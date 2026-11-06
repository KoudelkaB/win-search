using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace search.Models.LinuxService
{
    internal enum ServiceStatus { NotInstalled, NotRunning, OwnFiles, AllFiles }

    /// <summary>
    /// The app's side of the Linux file system service. One shared connection carries the
    /// changes of every watched root (FSChangeProcessor); a drive scan asks for a walk on a
    /// connection of its own (SearchModel.DriveEntries). Without the service - not installed,
    /// not running, an older protocol - every call answers null and the app watches nothing
    /// and walks the disks itself, as before.
    /// </summary>
    internal static class LinuxServiceClient
    {
        /// <summary>Tests point it at their own server</summary>
        internal static string SocketPath = ServiceProtocol.SocketPath;
        /// <summary>Ask for every file (the user's setting); the service grants it to administrators</summary>
        internal static Func<bool> RequestAllFiles = () => LinuxServiceSettingsStore.ShowAllFiles;
        const int ConnectTimeoutMs = 1000, ReplyTimeoutMs = 3000, RetryAfterMs = 5000;

        static readonly object gate = new();
        static WatchConnection connection;
        static long lastFailure; //Environment.TickCount64 of the last failed connect, 0 = none

        /// <summary>The unit install-service.sh writes - present while the service is installed</summary>
        internal static string UnitPath = "/etc/systemd/system/file-search-manager.service";

        /// <summary>The service is started (its socket exists - systemd removes it with the service)</summary>
        public static bool Installed => OperatingSystem.IsLinux() && File.Exists(SocketPath);

        public static ServiceStatus Status
        {
            get
            {
                if (!Installed) return File.Exists(UnitPath) ? ServiceStatus.NotRunning : ServiceStatus.NotInstalled;
                var c = Connection();
                return c == null ? ServiceStatus.NotRunning
                    : c.GrantedMode == ServiceProtocol.ModeAll ? ServiceStatus.AllFiles : ServiceStatus.OwnFiles;
            }
        }

        /// <summary>The user may see every file (root or a member of wheel/sudo/admin)</summary>
        public static bool IsAdmin => Connection()?.IsAdmin == true;

        /// <summary>The service answers - connecting when needed</summary>
        public static bool Available => Connection() != null;

        /// <summary>The shared watch connection; null without the service (retried after a pause)</summary>
        static WatchConnection Connection()
        {
            lock (gate)
            {
                if (connection is { Dead: false }) return connection;
                connection?.Dispose();
                connection = null;
                if (!Installed || lastFailure != 0 && Environment.TickCount64 - lastFailure < RetryAfterMs) return null;
                connection = WatchConnection.Open(RequestAllFiles() ? ServiceProtocol.ModeAll : ServiceProtocol.ModeOwn);
                if (connection == null) lastFailure = Environment.TickCount64;
                return connection;
            }
        }

        /// <summary>
        /// The service is there and has not refused this root yet - watching it again is worth
        /// a try (and the rescan that comes with it)
        /// </summary>
        public static bool WouldWatch(string root) => Connection() is { } c && !c.Refused(root);

        /// <summary>
        /// Watch a root through the service; null without it. lost runs once should the
        /// service go away later.
        /// </summary>
        public static IDisposable TryWatch(string root, Action<FsEvent> report, Action<DriveScanReason> rescan, Action lost)
            => Connection()?.Watch(root, report, rescan, lost);

        /// <summary>
        /// Every item of the root, walked by the service with its rights - only when the user
        /// asks for every file and may see them; null otherwise or when the walk fails (the
        /// app then walks the root itself)
        /// </summary>
        public static IReadOnlyCollection<INode> TryWalk(string root, CancellationToken cancellationToken)
        {
            if (!RequestAllFiles() || Connection() is not { GrantedMode: ServiceProtocol.ModeAll }) return null;
            try { return Walk(root, cancellationToken); }
            catch (Exception) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
            catch (Exception e)
            {
                $"service walk of {root} failed: {e.Message} - walking it here".Debug();
                return null;
            }
        }

        static List<FileNode> Walk(string root, CancellationToken cancellationToken)
        {
            using var socket = Connect();
            using var cancel = cancellationToken.Register(() => { try { socket.Dispose(); } catch { } });
            using var stream = new NetworkStream(socket, ownsSocket: false);
            var (mode, _, _) = Handshake(socket, stream, ServiceProtocol.ModeAll);
            if (mode != ServiceProtocol.ModeAll) throw new UnauthorizedAccessException("the service refused every-file access");
            socket.ReceiveTimeout = 0; //A big walk takes a while before its first chunk
            var request = ServiceProtocol.Begin(ServiceProtocol.Walk);
            request.Writer.Write(root);
            ServiceProtocol.Send(stream, request);

            //Millions of items: the chunks are decoded side by side as they come, and their
            //folder totals summed afterwards. On threads of their own, like the walker's: the
            //pool is busy while the app starts and adds threads only slowly.
            var decoderCount = Math.Max(2, Environment.ProcessorCount / 4);
            using var frames = new BlockingCollection<(int Index, byte[] Frame)>(decoderCount * 2);
            var chunks = new ConcurrentDictionary<int, Chunk>();
            Exception failure = null;
            var decoders = Enumerable.Range(0, decoderCount).Select(_ =>
            {
                var decoder = new Thread(() =>
                {
                    foreach (var (index, frame) in frames.GetConsumingEnumerable())
                        if (!cancellationToken.IsCancellationRequested) //Then only let the rest go
                            try { chunks[index] = Decode(frame); }
                            catch (Exception e) { Interlocked.CompareExchange(ref failure, e, null); } //Reported at the end
                })
                { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "service walk decoder" };
                decoder.Start();
                return decoder;
            }).ToArray();
            try
            {
                for (var count = 0; ; count++)
                {
                    var frame = ServiceProtocol.ReadFrame(stream, ServiceProtocol.MaxResponseFrame)
                        ?? throw new EndOfStreamException("the service closed the walk");
                    if (frame[0] == ServiceProtocol.WalkNodes)
                    {
                        frames.Add((count, frame), cancellationToken);
                        continue;
                    }
                    using var r = ServiceProtocol.Open(frame, out var type);
                    if (type != ServiceProtocol.WalkEnd) throw new InvalidDataException($"unexpected message {type} in a walk");
                    var ok = r.ReadBoolean();
                    var error = r.ReadString();
                    if (!ok) throw new IOException(error);
                    frames.CompleteAdding();
                    foreach (var decoder in decoders) decoder.Join();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (failure != null) throw new InvalidDataException($"a walk chunk could not be read: {failure.Message}", failure);
                    var nodes = Assemble(Enumerable.Range(0, count).Select(i => chunks[i]), cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    return nodes;
                }
            }
            finally
            {
                //Also when the walk failed: the decoders finish what they have before the
                //queue goes away under them
                frames.CompleteAdding();
                foreach (var decoder in decoders) decoder.Join();
            }
        }

        /// <summary>One chunk of a walk: its items, and the folder each run of them is in</summary>
        sealed record Chunk(List<(string Folder, int Start, int Count)> Groups, List<FileNode> Nodes);

        static Chunk Decode(byte[] frame)
        {
            using var r = ServiceProtocol.Open(frame, out _);
            var groups = new List<(string, int, int)>();
            var nodes = new List<FileNode>();
            for (var left = r.ReadInt32(); left > 0; left--)
            {
                var folder = r.ReadString();
                var count = r.ReadInt32();
                if (count < 0) throw new InvalidDataException("a negative item count");
                groups.Add((folder, nodes.Count, count));
                //The root's group (no folder) holds its whole path
                var prefix = folder.Length == 0 ? null : ServiceProtocol.PrefixOf(folder);
                for (var i = 0; i < count; i++)
                    nodes.Add(ToNode(prefix == null ? r.ReadString() : ServiceProtocol.ReadString(r, frame, prefix),
                        ServiceProtocol.ReadMetadata(r)));
            }
            return new Chunk(groups, nodes);
        }

        static List<FileNode> Assemble(IEnumerable<Chunk> chunks, CancellationToken cancellationToken)
        {
            var decoded = chunks.ToList();
            var nodes = new List<FileNode>(decoded.Sum(c => c.Nodes.Count));
            var totals = new DirectoryWalker.StreamedTotals();
            foreach (var chunk in decoded)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var (folder, start, count) in chunk.Groups)
                {
                    if (folder.Length == 0)
                    {
                        //The root itself, first and alone
                        if (nodes.Count > 0 || count != 1) throw new InvalidDataException("a second root in a walk");
                        totals.Root(chunk.Nodes[start]);
                        nodes.Add(chunk.Nodes[start]);
                        continue;
                    }
                    var pending = totals.FolderAt(folder);
                    for (var i = start; i < start + count; i++)
                    {
                        totals.Add(pending, chunk.Nodes[i]);
                        nodes.Add(chunk.Nodes[i]);
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            totals.Complete();
            return nodes;
        }

        /// <summary>A walked item as the app's own walk builds it, before the folder totals</summary>
        internal static FileNode ToNode(string path, ServiceMetadata metadata)
        {
            var node = FileNode.Create(path, metadata.ToSnapshot());
            if (metadata.IsDirectory)
            {
                node.AddSize(metadata.Size);
                node.AddCountDelta(metadata.Count);
            }
            return node;
        }

        /// <summary>Drop the connection - the every-file setting changed; the caller rescans and watches again</summary>
        public static void Reset()
        {
            lock (gate)
            {
                connection?.Dispose();
                connection = null;
                lastFailure = 0;
            }
        }

        internal static Socket Connect()
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                if (!socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath)).Wait(ConnectTimeoutMs))
                    throw new TimeoutException("the file system service does not answer");
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        /// <summary>Hello and its Welcome: the mode granted, whether the user is an administrator</summary>
        internal static (byte Mode, bool Admin, bool OwnSupported) Handshake(Socket socket, Stream stream, byte requested)
        {
            socket.ReceiveTimeout = ReplyTimeoutMs;
            var hello = ServiceProtocol.Begin(ServiceProtocol.Hello);
            hello.Writer.Write(ServiceProtocol.Version);
            hello.Writer.Write(requested);
            ServiceProtocol.Send(stream, hello);
            var frame = ServiceProtocol.ReadFrame(stream, ServiceProtocol.MaxResponseFrame)
                ?? throw new EndOfStreamException("the service closed the connection");
            using var r = ServiceProtocol.Open(frame, out var type);
            if (type != ServiceProtocol.Welcome) throw new InvalidDataException($"unexpected message {type}");
            var version = r.ReadUInt16();
            var mode = r.ReadByte();
            var admin = r.ReadBoolean();
            var ownSupported = r.ReadBoolean();
            if (version != ServiceProtocol.Version)
                throw new InvalidDataException($"service protocol {version}, app protocol {ServiceProtocol.Version}");
            return (mode, admin, ownSupported);
        }

        /// <summary>The connection carrying the changes of every watched root</summary>
        sealed class WatchConnection : IDisposable
        {
            readonly Socket socket;
            readonly NetworkStream stream;
            readonly object writeLock = new();
            readonly ConcurrentDictionary<string, RootWatch> roots = new(StringComparer.Ordinal);
            readonly ConcurrentDictionary<string, TaskCompletionSource<string>> pending = new(StringComparer.Ordinal);
            readonly ConcurrentDictionary<string, string> refused = new(StringComparer.Ordinal);
            volatile bool dead, disposed;

            /// <summary>The service would not watch the root (not a file system it can mark)</summary>
            public bool Refused(string root) => refused.ContainsKey(root);

            public byte GrantedMode { get; private set; }
            public bool IsAdmin { get; private set; }
            public bool Dead => dead;

            WatchConnection(Socket socket)
            {
                this.socket = socket;
                stream = new NetworkStream(socket, ownsSocket: false);
            }

            public static WatchConnection Open(byte mode)
            {
                Socket socket = null;
                try
                {
                    socket = Connect();
                    var c = new WatchConnection(socket);
                    var (granted, admin, ownSupported) = Handshake(socket, c.stream, mode);
                    if (granted == ServiceProtocol.ModeOwn && !ownSupported)
                        throw new NotSupportedException("the service cannot filter by access here");
                    socket.ReceiveTimeout = 0;
                    c.GrantedMode = granted;
                    c.IsAdmin = admin;
                    new Thread(c.Read) { IsBackground = true, Name = "file system service" }.Start();
                    $"file system service connected ({(granted == ServiceProtocol.ModeAll ? "every file" : "own files")})".Debug();
                    return c;
                }
                catch (Exception e)
                {
                    socket?.Dispose();
                    $"file system service unavailable: {e.Message}".Debug();
                    return null;
                }
            }

            public IDisposable Watch(string root, Action<FsEvent> report, Action<DriveScanReason> rescan, Action lost)
            {
                var watch = new RootWatch(this, root, report, rescan, lost);
                roots[root] = watch;
                var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                pending[root] = answer;
                try
                {
                    var request = ServiceProtocol.Begin(ServiceProtocol.Watch);
                    request.Writer.Write(root);
                    lock (writeLock) ServiceProtocol.Send(stream, request);
                }
                catch (Exception e)
                {
                    answer.TrySetResult(e.Message);
                    Fail();
                }
                var error = answer.Task.Wait(ReplyTimeoutMs) ? answer.Task.Result : "no answer";
                pending.TryRemove(new KeyValuePair<string, TaskCompletionSource<string>>(root, answer));
                if (error.Length == 0) return watch;
                Forget(watch);
                if (!dead) refused[root] = error;
                $"the file system service does not watch {root}: {error}".Debug();
                return null;
            }

            void Read()
            {
                try
                {
                    while (true)
                    {
                        var frame = ServiceProtocol.ReadFrame(stream, ServiceProtocol.MaxResponseFrame);
                        if (frame == null) break;
                        using var r = ServiceProtocol.Open(frame, out var type);
                        switch (type)
                        {
                            case ServiceProtocol.WatchResult:
                                var answered = r.ReadString();
                                var error = r.ReadString();
                                if (pending.TryGetValue(answered, out var answer)) answer.TrySetResult(error);
                                break;
                            case ServiceProtocol.Changes:
                                var root = r.ReadString();
                                var count = r.ReadInt32();
                                roots.TryGetValue(root, out var watch);
                                for (var i = 0; i < count; i++)
                                {
                                    var change = ServiceProtocol.ReadChange(r);
                                    watch?.Apply(change);
                                }
                                break;
                        }
                    }
                }
                catch (Exception e) when (!disposed)
                {
                    $"file system service connection failed: {e.Message}".Debug();
                }
                catch { }
                finally { Fail(); }
            }

            void Fail()
            {
                if (dead && disposed) return;
                dead = true;
                foreach (var answer in pending.Values) answer.TrySetResult("connection lost");
                foreach (var watch in roots.Values) watch.Lost();
                roots.Clear();
                try { socket.Dispose(); } catch { }
            }

            public void Forget(RootWatch watch)
                => roots.TryRemove(new KeyValuePair<string, RootWatch>(watch.Root, watch));

            public void Dispose()
            {
                //A reset: the caller watches again - the roots must not report themselves lost
                disposed = true;
                dead = true;
                foreach (var watch in roots.Values) watch.Dispose();
                try { socket.Dispose(); } catch { }
            }
        }

        /// <summary>One root's changes turned into the app's file-system events</summary>
        sealed class RootWatch : IDisposable
        {
            readonly WatchConnection connection;
            readonly Action<FsEvent> report;
            readonly Action<DriveScanReason> rescan;
            readonly Action lost;
            int ended;

            public readonly string Root;

            public RootWatch(WatchConnection connection, string root, Action<FsEvent> report,
                Action<DriveScanReason> rescan, Action lost)
            {
                this.connection = connection;
                Root = root;
                this.report = report;
                this.rescan = rescan;
                this.lost = lost;
            }

            public void Apply(ServiceChange change)
            {
                if (Volatile.Read(ref ended) != 0) return;
                foreach (var e in ToEvents(change))
                    if (e != null) report(e);
                    else rescan(DriveScanReason.WatcherOverflow);
            }

            public void Lost()
            {
                if (Interlocked.Exchange(ref ended, 1) == 0)
                    try { lost(); } catch { }
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref ended, 1);
                connection.Forget(this);
            }
        }

        /// <summary>
        /// The app's events for one service change; null stands for a rescan. What an item's
        /// state does to the index is decided when the model applies it (SearchModel
        /// .PlanServiceState), against the index as it is then - not here, while the
        /// changes before it may still wait in the queue.
        /// </summary>
        internal static IEnumerable<FsEvent> ToEvents(ServiceChange change)
        {
            switch (change.Kind)
            {
                case ServiceProtocol.Upsert:
                    yield return FsEvent.ServiceState(change.Path, change.Metadata.ToSnapshot(), change.Content switch
                    {
                        ServiceProtocol.ContentNew => FolderContent.Arrived,
                        ServiceProtocol.ContentVisible => FolderContent.Listable,
                        ServiceProtocol.ContentHidden => FolderContent.Unlistable,
                        _ => FolderContent.Unchanged
                    });
                    break;
                case ServiceProtocol.Delete:
                    yield return new FsEvent(WatcherChangeTypes.Deleted, change.Path);
                    break;
                case ServiceProtocol.Rename:
                    yield return new FsEvent(WatcherChangeTypes.Renamed, change.Path, change.OldPath);
                    break;
                case ServiceProtocol.Rescan:
                    yield return null;
                    break;
            }
        }
    }
}
