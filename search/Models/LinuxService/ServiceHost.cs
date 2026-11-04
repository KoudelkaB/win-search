using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace search.Models.LinuxService
{
    /// <summary>
    /// The service's socket server. Any local user may connect; the kernel tells who did
    /// (SO_PEERCRED), and that decides the rest:
    /// - administrators (PeerIdentity.IsAdmin) asking for every file get the changes
    ///   unfiltered and may request a walk of a root, which runs here with the service's
    ///   rights and streams each item described for that user;
    /// - everyone else gets only changes in folders their own process could list
    ///   (IAccessCheck) and no walk - their app walks the disks itself.
    /// Requests name only mount points from the mount table, nothing else is accepted.
    /// Each client is served on its own thread; changes are queued per client and written by
    /// another, so a slow client never holds up the monitor or the other clients - one that
    /// falls too far behind gets a Rescan of its roots instead of its backlog, and nothing more
    /// is queued for it until that is sent, so one that stopped reading holds a bounded queue.
    /// A user gets a bounded number of connections, counted from the accept, and a new one
    /// must say hello in time.
    /// </summary>
    internal sealed class ServiceHost : IDisposable
    {
        const int MaxQueuedChanges = 200_000, MaxChangesPerFrame = 4096, MaxNodesPerFrame = 8192;
        /// <summary>An app holds one watch connection and one per running walk</summary>
        const int MaxConnectionsPerUser = 32;

        readonly string socketPath;
        readonly IChangeMonitor monitor;
        readonly Func<IReadOnlyCollection<string>> roots;
        readonly Func<Socket, UserIdentity> peerOf;
        readonly Func<UserIdentity, bool> isAdmin;
        readonly Func<UserIdentity, IAccessCheck> accessFor;
        readonly bool ownModeSupported;
        readonly List<Session> sessions = new();
        /// <summary>Open connections per user id - counted from the accept, before the first word</summary>
        readonly Dictionary<uint, int> connections = new();
        readonly HashSet<uint> full = new();
        Socket listener;
        volatile bool disposed;

        int walks;

        /// <summary>How long a new connection may take to say hello</summary>
        internal int HelloTimeoutMs { get; init; } = 10_000;

        public ServiceHost(string socketPath, IChangeMonitor monitor, Func<IReadOnlyCollection<string>> roots,
            Func<Socket, UserIdentity> peerOf, Func<UserIdentity, bool> isAdmin,
            Func<UserIdentity, IAccessCheck> accessFor, bool ownModeSupported)
        {
            this.socketPath = socketPath;
            this.monitor = monitor;
            this.roots = roots;
            this.peerOf = peerOf;
            this.isAdmin = isAdmin;
            this.accessFor = accessFor;
            this.ownModeSupported = ownModeSupported;
        }

        public void Start()
        {
            if (File.Exists(socketPath)) File.Delete(socketPath);
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            //Every local user may connect - who it is decides what it gets
            File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead
                | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
            listener.Listen(16);
            monitor.Changed += Deliver;
            new Thread(Accept) { IsBackground = true, Name = "service accept" }.Start();
        }

        void Accept()
        {
            while (!disposed)
            {
                Socket client;
                try { client = listener.Accept(); }
                catch when (disposed) { return; }
                catch (Exception e)
                {
                    LinuxService.Log($"accept failed: {e.Message}");
                    Thread.Sleep(100);
                    continue;
                }
                //Counted before it gets a thread: one that never says a word holds its slot
                //only until the hello times out, and a user's slots are that user's alone
                if (PeerIdentity.UidOf(client) is not { } uid || !Admit(uid))
                {
                    client.Dispose();
                    continue;
                }
                new Thread(() =>
                {
                    try { Serve(client); }
                    finally { Release(uid); }
                }) { IsBackground = true, Name = "service client" }.Start();
            }
        }

        bool Admit(uint uid)
        {
            lock (connections)
            {
                connections.TryGetValue(uid, out var open);
                if (open >= MaxConnectionsPerUser)
                {
                    if (full.Add(uid)) //Logged once until one closes
                        LinuxService.Log($"uid {uid} has {MaxConnectionsPerUser} connections - refusing more");
                    return false;
                }
                connections[uid] = open + 1;
                return true;
            }
        }

        void Release(uint uid)
        {
            lock (connections)
            {
                full.Remove(uid);
                if (--connections[uid] == 0) connections.Remove(uid);
            }
        }

        void Deliver(IReadOnlyList<RawChange> changes)
        {
            Session[] current;
            lock (sessions) current = sessions.ToArray();
            foreach (var session in current) session.Post(changes);
        }

        void Serve(Socket socket)
        {
            Session session = null;
            try
            {
                using var stream = new NetworkStream(socket, ownsSocket: true);
                socket.ReceiveTimeout = HelloTimeoutMs;
                var hello = ServiceProtocol.ReadFrame(stream, ServiceProtocol.MaxRequestFrame);
                if (hello == null) return;
                socket.ReceiveTimeout = 0; //A watching client may stay quiet for good
                ushort version;
                byte requested;
                using (var r = ServiceProtocol.Open(hello, out var type))
                {
                    if (type != ServiceProtocol.Hello) return;
                    version = r.ReadUInt16();
                    requested = r.ReadByte();
                }
                var user = peerOf(socket);
                if (user == null) return;
                var admin = isAdmin(user);
                var mode = requested == ServiceProtocol.ModeAll && admin ? ServiceProtocol.ModeAll : ServiceProtocol.ModeOwn;
                session = new Session(stream, user, mode,
                    mode == ServiceProtocol.ModeOwn && ownModeSupported ? accessFor(user) : null);
                lock (sessions) sessions.Add(session);
                var welcome = ServiceProtocol.Begin(ServiceProtocol.Welcome);
                welcome.Writer.Write(ServiceProtocol.Version);
                welcome.Writer.Write(mode);
                welcome.Writer.Write(admin);
                welcome.Writer.Write(ownModeSupported);
                session.Send(welcome);
                if (version != ServiceProtocol.Version) return;
                while (!disposed)
                {
                    var frame = ServiceProtocol.ReadFrame(stream, ServiceProtocol.MaxRequestFrame);
                    if (frame == null) return;
                    using var r = ServiceProtocol.Open(frame, out var type);
                    var root = type is ServiceProtocol.Watch or ServiceProtocol.Walk ? r.ReadString() : null;
                    var known = root != null && roots().Contains(root);
                    switch (type)
                    {
                        case ServiceProtocol.Watch:
                            var error = !known ? "not a mounted file system"
                                : mode == ServiceProtocol.ModeOwn && !ownModeSupported
                                    ? "access checks are not supported on this architecture"
                                : session.Roots.Count >= 256 ? "too many roots"
                                : monitor.Subscribe(root);
                            if (error == null) session.Watch(root);
                            var result = ServiceProtocol.Begin(ServiceProtocol.WatchResult);
                            result.Writer.Write(root);
                            result.Writer.Write(error ?? "");
                            session.Send(result);
                            break;
                        case ServiceProtocol.Walk:
                            if (mode != ServiceProtocol.ModeAll) EndWalk(session, "walks are for administrators");
                            else if (!known) EndWalk(session, "not a mounted file system");
                            else
                            {
                                Interlocked.Increment(ref walks);
                                try { Walk(session, root); }
                                finally
                                {
                                    if (Interlocked.Decrement(ref walks) == 0) ReleaseWalkMemory();
                                }
                            }
                            break;
                        default:
                            return; //Not ours - drop the connection
                    }
                }
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or EndOfStreamException
                or InvalidDataException)
            {
                //The client went away or spoke nonsense
            }
            catch (Exception e)
            {
                LinuxService.Log($"client failed: {e}");
            }
            finally
            {
                if (session != null)
                {
                    lock (sessions) sessions.Remove(session);
                    session.Dispose();
                }
            }
        }

        static void EndWalk(Session session, string error)
        {
            var end = ServiceProtocol.Begin(ServiceProtocol.WalkEnd);
            end.Writer.Write(error == null);
            end.Writer.Write(error ?? "");
            session.Send(end);
        }

        /// <summary>
        /// A walk holds its whole tree until the folder totals are known - gigabytes for
        /// millions of files - and the idle service allocates nothing that would start the
        /// collection to free it: give that memory back once no walk runs
        /// </summary>
        static void ReleaseWalkMemory()
            => GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        /// <summary>
        /// Walk the root with the service's rights, describing every item for the client, and
        /// stream it - the same walk the app runs itself, folder totals included
        /// </summary>
        static void Walk(Session session, string root)
        {
            var user = session.User;
            var nodes = DirectoryWalker.Walk(new DirectoryInfo(root), TimeSpan.FromSeconds(15), CancellationToken.None,
                () => new LinuxDirectoryReader(user, utcTimes: true));
            var chunk = ServiceProtocol.Begin(ServiceProtocol.WalkNodes);
            var count = 0;
            var counter = chunk.Buffer.Position;
            chunk.Writer.Write(0); //Node count, patched before sending
            void Flush()
            {
                chunk.Writer.Flush();
                var end = chunk.Buffer.Position;
                chunk.Buffer.Position = counter;
                chunk.Writer.Write(count);
                chunk.Writer.Flush();
                chunk.Buffer.Position = end;
                session.Send(chunk);
                chunk = ServiceProtocol.Begin(ServiceProtocol.WalkNodes);
                count = 0;
                chunk.Writer.Write(0);
            }
            foreach (var node in nodes)
            {
                var path = node.FullName;
                ServiceMetadata metadata;
                if (string.Equals(PathText.TrimEndSeparators(path), PathText.TrimEndSeparators(root), StringComparison.Ordinal)
                    && LinuxDirectoryReader.StatPath(root, out _) is { } rootStat)
                {
                    //The root node comes from the managed DirectoryInfo: describe it for the client too
                    var snapshot = rootStat.ToSnapshot(user, utc: true);
                    metadata = new ServiceMetadata(snapshot.Attributes, node.Size, snapshot.LastChangeTime.Ticks, node.Count);
                }
                else
                {
                    var time = node.LastChangeTime;
                    if (time.Kind == DateTimeKind.Local) time = time.ToUniversalTime();
                    metadata = new ServiceMetadata(node.Attributes, node.Size, time.Ticks, node.IsDirectory ? node.Count : 0);
                }
                chunk.Writer.Write(path);
                ServiceProtocol.WriteMetadata(chunk.Writer, metadata);
                if (++count >= MaxNodesPerFrame) Flush();
            }
            if (count > 0) Flush();
            chunk.Writer.Dispose();
            chunk.Buffer.Dispose();
            EndWalk(session, null);
        }

        /// <summary>Changes waiting for all clients together</summary>
        internal long QueuedChanges
        {
            get { lock (sessions) return sessions.Sum(s => (long)s.Queued); }
        }

        public void Dispose()
        {
            disposed = true;
            monitor.Changed -= Deliver;
            try { listener?.Dispose(); } catch { }
            try { if (File.Exists(socketPath)) File.Delete(socketPath); } catch { }
            Session[] current;
            lock (sessions) current = sessions.ToArray();
            foreach (var session in current) session.Dispose();
        }

        /// <summary>One connected client: its identity, mode, roots and the changes waiting for it</summary>
        sealed class Session : IDisposable
        {
            readonly Stream stream;
            readonly object writeLock = new();
            readonly IAccessCheck access;
            readonly BlockingCollection<IReadOnlyList<RawChange>> queue = new();
            readonly HashSet<string> roots = new(StringComparer.Ordinal);
            int queued, overflowed;

            public readonly UserIdentity User;
            public readonly byte Mode;

            public Session(Stream stream, UserIdentity user, byte mode, IAccessCheck access)
            {
                this.stream = stream;
                User = user;
                Mode = mode;
                this.access = access;
                new Thread(Write) { IsBackground = true, Name = $"service uid {user.Uid}" }.Start();
            }

            public int Queued => Volatile.Read(ref queued);

            public IReadOnlyCollection<string> Roots
            {
                get { lock (roots) return roots.ToArray(); }
            }

            public void Watch(string root)
            {
                lock (roots) roots.Add(root);
            }

            public void Send((MemoryStream Buffer, BinaryWriter Writer) message)
            {
                lock (writeLock) ServiceProtocol.Send(stream, message);
            }

            /// <summary>Queue a batch for this client (the monitor's thread is the only caller)</summary>
            public void Post(IReadOnlyList<RawChange> changes)
            {
                List<RawChange> mine;
                lock (roots)
                {
                    if (roots.Count == 0) return;
                    mine = changes.Where(c => roots.Contains(c.Root)).ToList();
                }
                if (mine.Count == 0) return;
                //Too far behind: the rescan on its way covers this batch too
                if (Volatile.Read(ref overflowed) != 0) return;
                if (Interlocked.Add(ref queued, mine.Count) > MaxQueuedChanges)
                {
                    Interlocked.Add(ref queued, -mine.Count);
                    //Nothing is queued until the writer has sent the rescan - it may be stuck
                    //writing to a client that stopped reading. The empty batch wakes it.
                    if (Interlocked.Exchange(ref overflowed, 1) == 0) Enqueue(Array.Empty<RawChange>());
                    return;
                }
                Enqueue(mine);
            }

            void Enqueue(IReadOnlyList<RawChange> batch)
            {
                try { queue.Add(batch); } catch (InvalidOperationException) { } //Closing
            }

            void Write()
            {
                try
                {
                    foreach (var batch in queue.GetConsumingEnumerable())
                    {
                        Interlocked.Add(ref queued, -batch.Count);
                        if (Volatile.Read(ref overflowed) != 0)
                        {
                            //Too far behind: what is queued is worth less than a rescan
                            while (queue.TryTake(out var dropped)) Interlocked.Add(ref queued, -dropped.Count);
                            //What is queued from now on happened after the rescan's walk starts
                            Volatile.Write(ref overflowed, 0);
                            foreach (var root in Roots)
                                SendChanges(root, new[] { ServiceChange.Of(ServiceProtocol.Rescan) });
                            continue;
                        }
                        foreach (var group in Describe(batch).GroupBy(c => c.Root))
                        {
                            var list = group.Select(c => c.Change).ToList();
                            for (var i = 0; i < list.Count; i += MaxChangesPerFrame)
                                SendChanges(group.Key, list.GetRange(i, Math.Min(MaxChangesPerFrame, list.Count - i)));
                        }
                    }
                }
                catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
                {
                    //The client went away - its reading thread ends the session
                }
            }

            void SendChanges(string root, IReadOnlyList<ServiceChange> changes)
            {
                var message = ServiceProtocol.Begin(ServiceProtocol.Changes);
                message.Writer.Write(root);
                message.Writer.Write(changes.Count);
                foreach (var change in changes) ServiceProtocol.WriteChange(message.Writer, change);
                Send(message);
            }

            /// <summary>The batch as this client may see it, each item described for its user</summary>
            List<(string Root, ServiceChange Change)> Describe(IReadOnlyList<RawChange> batch)
                => DescribeFor(batch, User, Mode == ServiceProtocol.ModeOwn ? access : null);

            public void Dispose()
            {
                queue.CompleteAdding();
                access?.Dispose();
                try { stream.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// Describe a batch for one client. With an access check (a client that sees only its
        /// own files) an item counts as seen when its folder is one the user could list: a
        /// change elsewhere is dropped, a rename from a hidden folder becomes the item's
        /// appearance (with its content to index) and one into a hidden folder its deletion.
        /// A folder whose permissions may have changed tells whether the user can list it now -
        /// the content the client indexed, or could not, may have to follow.
        /// </summary>
        internal static List<(string Root, ServiceChange Change)> DescribeFor(IReadOnlyList<RawChange> batch,
            UserIdentity user, IAccessCheck access)
        {
            Dictionary<string, bool> visible = null;
            if (access != null)
            {
                var folders = new HashSet<string>(StringComparer.Ordinal);
                foreach (var c in batch)
                {
                    if (FolderOf(c.Path) is { } a) folders.Add(a);
                    if (FolderOf(c.OldPath) is { } b) folders.Add(b);
                    if (c.Kind == ServiceProtocol.Upsert && c.AccessChanged && c.Stat is { IsDirectory: true }) folders.Add(c.Path);
                }
                var list = folders.ToList();
                var answers = access.CanList(list);
                visible = new Dictionary<string, bool>(StringComparer.Ordinal);
                for (var i = 0; i < list.Count; i++) visible[list[i]] = answers[i];
            }
            bool Sees(string path) => visible == null || FolderOf(path) is not { } folder || visible[folder];

            var appeared = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<(string, ServiceChange)>(batch.Count);
            foreach (var c in batch)
                switch (c.Kind)
                {
                    case ServiceProtocol.Upsert when Sees(c.Path) && c.Stat is { } stat:
                        var snapshot = stat.ToSnapshot(user, utc: true);
                        var content = c.IndexContent || appeared.Contains(c.Path) ? ServiceProtocol.ContentNew
                            : c.AccessChanged && stat.IsDirectory && visible != null && visible.TryGetValue(c.Path, out var listable)
                                ? listable ? ServiceProtocol.ContentVisible : ServiceProtocol.ContentHidden
                            : ServiceProtocol.ContentUnchanged;
                        result.Add((c.Root, new ServiceChange(ServiceProtocol.Upsert, c.Path, null, content,
                            new ServiceMetadata(snapshot.Attributes, snapshot.Size, snapshot.LastChangeTime.Ticks, 0))));
                        break;
                    case ServiceProtocol.Delete when Sees(c.Path):
                        result.Add((c.Root, ServiceChange.Of(ServiceProtocol.Delete, c.Path)));
                        break;
                    case ServiceProtocol.Rename:
                        var from = Sees(c.OldPath);
                        var to = Sees(c.Path);
                        if (from && to)
                            result.Add((c.Root, ServiceChange.Of(ServiceProtocol.Rename, c.Path, c.OldPath)));
                        else if (from)
                            result.Add((c.Root, ServiceChange.Of(ServiceProtocol.Delete, c.OldPath)));
                        else if (to)
                            appeared.Add(c.Path); //Its Upsert follows - with the content to index
                        break;
                    case ServiceProtocol.Rescan:
                        result.Add((c.Root, ServiceChange.Of(ServiceProtocol.Rescan)));
                        break;
                }
            return result;
        }

        static string FolderOf(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/") return null;
            var folder = Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(folder) ? null : folder;
        }
    }

    /// <summary>
    /// FileSearchManager --service: the optional root service behind live changes and the
    /// every-file view on Linux. Started by systemd (installer/linux/install-service.sh).
    /// </summary>
    internal static class LinuxService
    {
        [DllImport("libc")] static extern uint geteuid();

        public static void Log(string text) => Console.Error.WriteLine(text);

        public static int Run()
        {
            if (!OperatingSystem.IsLinux() || geteuid() != 0)
            {
                Log("The file system service runs as root (systemd unit file-search-manager.service).");
                return 1;
            }
            if (!LinuxDirectoryReader.IsSupported)
            {
                Log("getdents64 + statx unavailable - the service needs glibc 2.30+ and Linux 4.11+.");
                return 1;
            }
            var runtimeDirectory = Path.GetDirectoryName(ServiceProtocol.SocketPath);
            Directory.CreateDirectory(runtimeDirectory);
            var privateMounts = !SameMountNamespaceAsInit();
            if (!privateMounts) Log("Not in a private mount namespace: btrfs subvolumes will not be watched.");

            using var monitor = new FanotifyMonitor(runtimeDirectory, privateMounts);
            using var host = new ServiceHost(ServiceProtocol.SocketPath, monitor,
                () => FsRoots.AllPaths().ToArray(), PeerIdentity.Of, PeerIdentity.IsAdmin,
                user => new UserAccess(user), UserAccess.Supported);
            host.Start();
            Log($"File Search Manager service listening on {ServiceProtocol.SocketPath}");

            using var stop = new ManualResetEventSlim();
            using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Set(); });
            using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; stop.Set(); });
            stop.Wait();
            Log("File Search Manager service stopping");
            return 0;
        }

        static bool SameMountNamespaceAsInit()
        {
            try
            {
                //"mnt:[4026531841]" - the namespace's identity
                return new FileInfo("/proc/self/ns/mnt").LinkTarget == new FileInfo("/proc/1/ns/mnt").LinkTarget;
            }
            catch { return true; } //Unknown: assume shared and do not mount anything
        }
    }
}
