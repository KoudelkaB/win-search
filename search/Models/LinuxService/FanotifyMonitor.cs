using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace search.Models.LinuxService
{
    /// <summary>A change the monitor found, before it is described for a client</summary>
    /// <param name="Stat">An Upsert's state after the change</param>
    /// <param name="IndexContent">A folder moved in from where nothing was watched: its content has no events</param>
    /// <param name="AccessChanged">A folder's attributes changed: who may list it may have too</param>
    internal readonly record struct RawChange(byte Kind, string Root, string Path, string OldPath, RawStat? Stat,
        bool IndexContent = false, bool AccessChanged = false);

    /// <summary>Where the service's changes come from - fanotify, or a fake in tests</summary>
    internal interface IChangeMonitor : IDisposable
    {
        /// <summary>Report changes below this root (a mount point); null when it works, else why not</summary>
        string Subscribe(string root);

        /// <summary>One batch of changes, in the order to apply them</summary>
        event Action<IReadOnlyList<RawChange>> Changed;
    }

    /// <summary>
    /// fanotify with one whole-file-system mark per watched file system: no per-folder watches
    /// and no limit, unlike inotify. Needs CAP_SYS_ADMIN (the mark) and CAP_DAC_READ_SEARCH
    /// (turning the reported folder handles back into paths).
    ///
    /// An event names the folder (a file handle) and the entry's name, nothing more - no size
    /// or time - and the kernel merges a file's events while they wait in the queue, so their
    /// order is lost. Each named entry is therefore stat-ed when the batch is processed and
    /// reported as its current state (Upsert), or as deleted - but only when the event says
    /// so: an entry that is just gone was renamed, and the rename reports it. Folder handles
    /// resolve to the folder's path at processing time too, so a batch reports its renames
    /// first - each named as it was when it happened (later renames of the folders above it
    /// undone) - and everything else after them: a change inside a folder renamed since then
    /// already names the new path, which must exist in the index when it is applied. (A
    /// rename that lands between reading a batch and processing it can still slip past this;
    /// the app's rename handling replaces what was added under the new name, and a rescan
    /// settles the rest.)
    ///
    /// btrfs: a subvolume mount cannot carry a file system mark (EXDEV). The service mounts
    /// the file system's top level (subvolid=5) in its private mount namespace instead - the
    /// mark there sees every subvolume - and maps the paths back through mountinfo's
    /// subvolume of each watched mount point.
    /// </summary>
    internal sealed unsafe class FanotifyMonitor : IChangeMonitor
    {
        const uint FAN_CLOEXEC = 0x1, FAN_NONBLOCK = 0x2, FAN_UNLIMITED_QUEUE = 0x10, FAN_UNLIMITED_MARKS = 0x20,
            FAN_REPORT_FID = 0x200, FAN_REPORT_DIR_FID = 0x400, FAN_REPORT_NAME = 0x800, FAN_REPORT_TARGET_FID = 0x1000;
        const uint FAN_MARK_ADD = 0x1, FAN_MARK_FILESYSTEM = 0x100;
        internal const ulong FAN_MODIFY = 0x2, FAN_ATTRIB = 0x4, FAN_CLOSE_WRITE = 0x8, FAN_CREATE = 0x100,
            FAN_DELETE = 0x200, FAN_DELETE_SELF = 0x400, FAN_MOVE_SELF = 0x800, FAN_Q_OVERFLOW = 0x4000,
            FAN_RENAME = 0x10000000, FAN_ONDIR = 0x40000000;
        internal const byte INFO_FID = 1, INFO_DFID_NAME = 2, INFO_DFID = 3, INFO_OLD_DFID_NAME = 10, INFO_NEW_DFID_NAME = 12;
        const ulong Mask = FAN_CREATE | FAN_DELETE | FAN_RENAME | FAN_ATTRIB | FAN_MODIFY | FAN_CLOSE_WRITE | FAN_ONDIR;
        const int AT_FDCWD = -100, O_RDONLY = 0, O_CLOEXEC = 0x80000, O_PATH = 0x200000, EAGAIN = 11, EINTR = 4;
        const ulong MS_RDONLY = 1, MS_NOSUID = 2, MS_NODEV = 4, MS_NOEXEC = 8;
        const short POLLIN = 1;

        [StructLayout(LayoutKind.Sequential)]
        struct PollFd { public int Fd; public short Events; public short Revents; }

        [DllImport("libc", SetLastError = true)] static extern int fanotify_init(uint flags, uint eventFlags);
        [DllImport("libc", SetLastError = true)]
        static extern int fanotify_mark(int fd, uint flags, ulong mask, int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport("libc", SetLastError = true)] static extern int open_by_handle_at(int mountFd, byte* handle, int flags);
        [DllImport("libc", SetLastError = true)] static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
        [DllImport("libc", SetLastError = true)] static extern int close(int fd);
        [DllImport("libc", SetLastError = true)] static extern nint read(int fd, byte* buffer, nint count);
        [DllImport("libc", SetLastError = true)] static extern int poll(PollFd* fds, nuint count, int timeout);
        [DllImport("libc", SetLastError = true)]
        static extern int statfs([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte* buffer);
        [DllImport("libc", SetLastError = true)]
        static extern nint readlink([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte* buffer, nint size);
        [DllImport("libc", SetLastError = true)]
        static extern int mount([MarshalAs(UnmanagedType.LPUTF8Str)] string source, [MarshalAs(UnmanagedType.LPUTF8Str)] string target,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string type, ulong flags, [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

        /// <summary>One watched file system: the mount its handles are resolved through and the roots it shows</summary>
        sealed class MarkedFs
        {
            public int MountFd;
            /// <summary>Where the resolving mount is in the service's view ("/" or the private btrfs top level)</summary>
            public string MountPath;
            /// <summary>The folder of the file system that mount shows</summary>
            public string MountFsRoot;
            /// <summary>Watched mount points and the folder of the file system each shows</summary>
            public readonly List<(string Root, string FsRoot)> Roots = new();
        }

        readonly object gate = new();
        readonly Dictionary<string, MarkedFs> byKey = new(StringComparer.Ordinal);
        readonly Dictionary<ulong, MarkedFs> byFsid = new();
        readonly string runtimeDirectory;
        readonly bool privateMounts;
        readonly int fd;
        readonly Thread thread;
        volatile bool disposed;

        public event Action<IReadOnlyList<RawChange>> Changed;

        /// <param name="runtimeDirectory">Where private btrfs top levels are mounted</param>
        /// <param name="privateMounts">The service runs in its own mount namespace (systemd PrivateMounts)</param>
        public FanotifyMonitor(string runtimeDirectory, bool privateMounts)
        {
            this.runtimeDirectory = runtimeDirectory;
            this.privateMounts = privateMounts;
            const uint flags = FAN_CLOEXEC | FAN_NONBLOCK | FAN_REPORT_DIR_FID | FAN_REPORT_NAME | FAN_REPORT_FID
                | FAN_REPORT_TARGET_FID;
            fd = fanotify_init(flags | FAN_UNLIMITED_QUEUE | FAN_UNLIMITED_MARKS, O_RDONLY | O_CLOEXEC);
            //A bounded queue where an unlimited one is not allowed: an overflow then rescans
            if (fd < 0) fd = fanotify_init(flags, O_RDONLY | O_CLOEXEC);
            if (fd < 0)
                throw new IOException($"fanotify_init failed: {Marshal.GetLastPInvokeErrorMessage()}");
            thread = new Thread(Loop) { IsBackground = true, Name = "fanotify" };
            thread.Start();
        }

        public string Subscribe(string root)
        {
            lock (gate)
            {
                FsRoots.MountEntry entry;
                try
                {
                    entry = FsRoots.ParseMountEntries(File.ReadAllText("/proc/self/mountinfo"))
                        .LastOrDefault(e => e.MountPoint == root);
                }
                catch (Exception e) { return $"mount table unreadable: {e.Message}"; }
                if (entry == null) return "not a mount point";

                var isSubvolume = entry.Format == "btrfs" && entry.FsRoot != "/";
                var key = entry.Format == "btrfs" ? "btrfs " + entry.Source : entry.Device;
                if (byKey.TryGetValue(key, out var marked))
                {
                    if (!marked.Roots.Contains((root, entry.FsRoot))) marked.Roots.Add((root, entry.FsRoot));
                    return null;
                }

                var mountPath = root;
                var mountFsRoot = entry.FsRoot;
                if (isSubvolume)
                {
                    if (!privateMounts)
                        return "a btrfs subvolume needs the service's own mount namespace (PrivateMounts=yes)";
                    mountPath = System.IO.Path.Combine(runtimeDirectory, "btrfs", key.Replace('/', '_').Replace(' ', '_'));
                    Directory.CreateDirectory(mountPath);
                    if (mount(entry.Source, mountPath, "btrfs", MS_RDONLY | MS_NOSUID | MS_NODEV | MS_NOEXEC, "subvolid=5") != 0
                        && mount(entry.Source, mountPath, "btrfs", MS_NOSUID | MS_NODEV | MS_NOEXEC, "subvolid=5") != 0)
                        return $"mounting the btrfs top level of {entry.Source} failed: {Marshal.GetLastPInvokeErrorMessage()}";
                    mountFsRoot = "/";
                }
                if (fanotify_mark(fd, FAN_MARK_ADD | FAN_MARK_FILESYSTEM, Mask, AT_FDCWD, mountPath) != 0)
                    return $"fanotify_mark failed: {Marshal.GetLastPInvokeErrorMessage()}";
                var mountFd = open(mountPath, O_RDONLY | O_CLOEXEC);
                if (mountFd < 0) return $"open failed: {Marshal.GetLastPInvokeErrorMessage()}";
                var fsid = Fsid(mountPath);
                if (fsid == null)
                {
                    close(mountFd);
                    return "statfs failed";
                }
                marked = new MarkedFs { MountFd = mountFd, MountPath = mountPath, MountFsRoot = mountFsRoot };
                marked.Roots.Add((root, entry.FsRoot));
                byKey[key] = marked;
                byFsid[fsid.Value] = marked;
                return null;
            }
        }

        /// <summary>statfs f_fsid - what fanotify reports as an event's file system (64-bit layout)</summary>
        static ulong? Fsid(string path)
        {
            if (IntPtr.Size != 8) return null;
            var buffer = stackalloc byte[256];
            if (statfs(path, buffer) != 0) return null;
            return BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(buffer + 56, 8));
        }

        void Loop()
        {
            var buffer = new byte[1 << 20];
            var events = new List<FanEvent>();
            while (!disposed)
            {
                var pollFd = new PollFd { Fd = fd, Events = POLLIN };
                if (poll(&pollFd, 1, 500) <= 0) continue;
                events.Clear();
                //Everything queued: a folder renamed within the batch then resolves consistently.
                //Bounded - a never-ending storm must still be processed piece by piece.
                fixed (byte* p = buffer)
                    while (events.Count < 100_000)
                    {
                        var length = read(fd, p, buffer.Length);
                        if (length > 0)
                        {
                            Parse(new ReadOnlySpan<byte>(p, (int)length), events);
                            continue;
                        }
                        if (length < 0 && Marshal.GetLastPInvokeError() == EINTR) continue;
                        break; //EAGAIN: drained
                    }
                if (events.Count == 0) continue;
                try
                {
                    var changes = Process(events);
                    if (changes.Count > 0) Changed?.Invoke(changes);
                }
                catch (Exception e) { LinuxService.Log($"processing {events.Count} events failed: {e}"); }
            }
        }

        internal readonly record struct FanRecord(byte Kind, ulong Fsid, int HandleType, byte[] Handle, string Name);
        internal readonly record struct FanEvent(ulong Mask, FanRecord[] Records);

        /// <summary>The events of one read: fanotify_event_metadata, then FID info records</summary>
        internal static void Parse(ReadOnlySpan<byte> buffer, List<FanEvent> into)
        {
            var offset = 0;
            while (offset + 24 <= buffer.Length)
            {
                var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset));
                var metaLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset + 6));
                if (length < 24 || metaLength < 24 || offset + length > buffer.Length) break;
                var mask = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(offset + 8));
                var eventFd = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset + 16));
                if (eventFd >= 0) close(eventFd); //Not opened in FID mode - never leak one
                var records = new List<FanRecord>(2);
                var p = offset + metaLength;
                while (p + 4 <= offset + length)
                {
                    var kind = buffer[p];
                    var recordLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(p + 2));
                    if (recordLength < 4 || p + recordLength > offset + length) break;
                    var body = buffer.Slice(p + 4, recordLength - 4);
                    if (kind is INFO_FID or INFO_DFID_NAME or INFO_DFID or INFO_OLD_DFID_NAME or INFO_NEW_DFID_NAME
                        && body.Length >= 16)
                    {
                        var fsid = BinaryPrimitives.ReadUInt64LittleEndian(body);
                        var handleBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(8));
                        var handleType = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(12));
                        if (16 + handleBytes <= body.Length)
                        {
                            string name = null;
                            if (kind is INFO_DFID_NAME or INFO_OLD_DFID_NAME or INFO_NEW_DFID_NAME)
                            {
                                var rest = body.Slice(16 + handleBytes);
                                var end = rest.IndexOf((byte)0);
                                name = Encoding.UTF8.GetString(end < 0 ? rest : rest.Slice(0, end));
                            }
                            records.Add(new FanRecord(kind, fsid, handleType, body.Slice(16, handleBytes).ToArray(), name));
                        }
                    }
                    p += recordLength;
                }
                into.Add(new FanEvent(mask, records.ToArray()));
                offset += length;
            }
        }

        List<RawChange> Process(List<FanEvent> events)
        {
            //A storm in one folder (rm -rf) names that folder in every event: resolve it once
            var folders = new Dictionary<string, (string Root, string Path)?>(StringComparer.Ordinal);
            (string Root, string Path)? Locate(FanRecord record)
            {
                var key = record.Fsid.ToString("x") + ":" + record.HandleType + ":" + Convert.ToHexString(record.Handle);
                if (!folders.TryGetValue(key, out var folder)) folders[key] = folder = LocateFolder(record);
                if (folder is not { } f) return null;
                return (f.Root, record.Name == "." ? f.Path : Join(f.Path, record.Name));
            }
            return BuildChanges(events, Locate, path => LinuxDirectoryReader.StatPath(path, out var exists) is { } stat
                ? (stat, true) : (null, exists), AllRoots);
        }

        IEnumerable<string> AllRoots()
        {
            lock (gate) return byKey.Values.SelectMany(m => m.Roots.Select(r => r.Root)).ToArray();
        }

        /// <summary>
        /// The changes of one batch: its renames first, in event order and each named as it
        /// was when it happened, then each named entry once in event order, stat-ed now -
        /// see the class summary
        /// </summary>
        /// <param name="locate">A record's (root, path); null outside every watched root</param>
        /// <param name="stat">The entry's state now; Exists without a state = there but unreadable</param>
        internal static List<RawChange> BuildChanges(IReadOnlyList<FanEvent> events,
            Func<FanRecord, (string Root, string Path)?> locate,
            Func<string, (RawStat? Stat, bool Exists)> stat, Func<IEnumerable<string>> allRoots)
        {
            var changes = new List<RawChange>();
            //Each path once: the index of its change, or -1 when it was gone (or unreadable) then
            var reported = new Dictionary<string, int>(StringComparer.Ordinal);
            void Report(string root, string path, bool deleted, bool indexContent = false, bool accessChanged = false)
            {
                if (reported.TryGetValue(path, out var at))
                {
                    if (at >= 0)
                    {
                        //Described already - with what this event adds
                        var c = changes[at];
                        if (c.Kind == ServiceProtocol.Upsert)
                            changes[at] = c with
                            {
                                IndexContent = c.IndexContent || indexContent,
                                AccessChanged = c.AccessChanged || accessChanged
                            };
                        return;
                    }
                    //Found gone by an earlier event that did not say why; this one may
                    if (!deleted) return;
                }
                var (state, exists) = stat(path);
                var index = -1;
                if (state != null)
                {
                    index = changes.Count;
                    changes.Add(new RawChange(ServiceProtocol.Upsert, root, path, null, state, indexContent, accessChanged));
                }
                else if (!exists && deleted)
                {
                    index = changes.Count;
                    changes.Add(new RawChange(ServiceProtocol.Delete, root, path, null, null));
                }
                reported[path] = index;
            }
            void ReportFolder(string root, string path)
            {
                //A folder's time changes with its content: keep it current (sorting by time
                //shows where programs write)
                var folder = System.IO.Path.GetDirectoryName(path);
                if (folder != null && IsUnder(folder, root)) Report(root, folder, deleted: false);
            }

            //Renames. Their folders resolve to where they are now, so a rename followed by a
            //rename of a folder above it (/A/x -> /A/y, then /A -> /B) reads /B/x -> /B/y,
            //which the index does not have yet. Undoing the later folder renames, last first,
            //names each as it was: /A/x -> /A/y, then /A -> /B.
            var renames = new List<((string Root, string Path)? From, (string Root, string Path)? To, bool IsDirectory)>();
            foreach (var e in events)
            {
                if ((e.Mask & FAN_RENAME) == 0) continue;
                var oldRecord = e.Records.FirstOrDefault(r => r.Kind == INFO_OLD_DFID_NAME);
                var newRecord = e.Records.FirstOrDefault(r => r.Kind == INFO_NEW_DFID_NAME);
                renames.Add((oldRecord.Handle == null ? null : locate(oldRecord),
                    newRecord.Handle == null ? null : locate(newRecord), (e.Mask & FAN_ONDIR) != 0));
            }
            var then = new (string From, string To)[renames.Count];
            var later = new List<(string Root, string From, string To)>(); //Folder renames after the current one, last first
            string AsThen((string Root, string Path) now)
            {
                var path = now.Path;
                foreach (var (root, from, to) in later)
                    if (root == now.Root && path.Length > to.Length && IsUnder(path, to))
                        path = Join(from, path.Substring(to.Length));
                return path;
            }
            for (var i = renames.Count - 1; i >= 0; i--)
            {
                var (from, to, isDirectory) = renames[i];
                then[i] = (from is { } a ? AsThen(a) : null, to is { } b ? AsThen(b) : null);
                if (isDirectory && from is { } f && to is { } t && f.Root == t.Root) later.Add((f.Root, then[i].From, then[i].To));
            }
            //What the renames leave to report - stat-ed now, so applied after all of them
            var afterRenames = new List<(string Root, string Path, bool IndexContent, bool Folder)>();
            for (var i = 0; i < renames.Count; i++)
            {
                var (from, to, isDirectory) = renames[i];
                if (from is { } a && to is { } b && a.Root == b.Root)
                {
                    changes.Add(new RawChange(ServiceProtocol.Rename, b.Root, then[i].To, then[i].From, null));
                    afterRenames.Add((b.Root, b.Path, false, false));
                }
                else
                {
                    //Moved out of sight, or in from where nothing was watched
                    if (from is { } gone) changes.Add(new RawChange(ServiceProtocol.Delete, gone.Root, then[i].From, null, null));
                    if (to is { } came) afterRenames.Add((came.Root, came.Path, isDirectory, false));
                }
                if (from is { } x) afterRenames.Add((x.Root, x.Path, false, true));
                if (to is { } y) afterRenames.Add((y.Root, y.Path, false, true));
            }
            foreach (var (root, path, indexContent, folder) in afterRenames)
                if (folder) ReportFolder(root, path);
                else Report(root, path, deleted: false, indexContent);

            var overflow = false;
            foreach (var e in events)
            {
                if ((e.Mask & FAN_Q_OVERFLOW) != 0)
                {
                    overflow = true;
                    continue;
                }
                if ((e.Mask & FAN_RENAME) != 0) continue;
                var record = e.Records.FirstOrDefault(r => r.Kind == INFO_DFID_NAME);
                if (record.Handle == null || locate(record) is not { } at) continue;
                //A folder's attributes: its permissions may have changed, and with them who may list it
                var accessChanged = (e.Mask & (FAN_ATTRIB | FAN_ONDIR)) == (FAN_ATTRIB | FAN_ONDIR);
                if (record.Name == ".")
                {
                    //An event of the folder itself: its attributes or times. Its deletion and
                    //moves are the parent's events.
                    if ((e.Mask & (FAN_DELETE_SELF | FAN_MOVE_SELF)) == 0)
                        Report(at.Root, at.Path, deleted: false, accessChanged: accessChanged);
                    continue;
                }
                Report(at.Root, at.Path, deleted: (e.Mask & FAN_DELETE) != 0, accessChanged: accessChanged);
                if ((e.Mask & (FAN_CREATE | FAN_DELETE)) != 0) ReportFolder(at.Root, at.Path);
            }
            if (overflow)
                foreach (var root in allRoots())
                    changes.Add(new RawChange(ServiceProtocol.Rescan, root, null, null, null));
            return changes;
        }

        static bool IsUnder(string path, string root)
            => root == "/" ? path.StartsWith('/') : path == root || path.StartsWith(root + "/", StringComparison.Ordinal);

        /// <summary>The watched root and path of the folder a record names</summary>
        (string Root, string Path)? LocateFolder(FanRecord record)
        {
            MarkedFs marked;
            List<(string Root, string FsRoot)> roots;
            lock (gate)
            {
                if (!byFsid.TryGetValue(record.Fsid, out marked)) return null;
                roots = marked.Roots.ToList();
            }
            var resolved = Resolve(marked.MountFd, record.HandleType, record.Handle);
            if (resolved == null) return null;
            return MapToRoot(resolved, marked.MountPath, marked.MountFsRoot, roots);
        }

        /// <summary>
        /// A path in the service's view of the resolving mount to the watched root showing it:
        /// the mount's file system folder plus the rest is a folder of the file system, which
        /// the root showing the deepest part of the file system above it maps to its own path
        /// </summary>
        internal static (string Root, string Path)? MapToRoot(string resolved, string mountPath, string mountFsRoot,
            IReadOnlyList<(string Root, string FsRoot)> roots)
        {
            if (!IsUnder(resolved, mountPath)) return null;
            var inFileSystem = Join(mountFsRoot, mountPath == "/" ? resolved : resolved.Substring(mountPath.Length));
            (string Root, string FsRoot)? best = null;
            foreach (var r in roots)
                if (IsUnder(inFileSystem, r.FsRoot) && (best == null || r.FsRoot.Length > best.Value.FsRoot.Length))
                    best = r;
            if (best == null) return null;
            var rest = best.Value.FsRoot == "/" ? inFileSystem : inFileSystem.Substring(best.Value.FsRoot.Length);
            return (best.Value.Root, Join(best.Value.Root, rest));
        }

        /// <summary>a + "/" + b without doubled or trailing separators ("/" + "" = "/")</summary>
        internal static string Join(string a, string b)
        {
            b = b.Trim('/');
            if (b.Length == 0) return a.Length == 0 ? "/" : a;
            return a.EndsWith('/') ? a + b : a + "/" + b;
        }

        /// <summary>The path of a reported folder, through the mount the file system was marked on</summary>
        static string Resolve(int mountFd, int handleType, byte[] handle)
        {
            var buffer = stackalloc byte[8 + handle.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(buffer, 4), (uint)handle.Length);
            BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(buffer + 4, 4), handleType);
            handle.CopyTo(new Span<byte>(buffer + 8, handle.Length));
            var opened = open_by_handle_at(mountFd, buffer, O_PATH | O_CLOEXEC);
            if (opened < 0) return null; //ESTALE: deleted meanwhile - its parent reports that
            try
            {
                var target = stackalloc byte[4096];
                var length = readlink($"/proc/self/fd/{opened}", target, 4096);
                if (length <= 0) return null;
                var path = Encoding.UTF8.GetString(target, (int)length);
                return path.EndsWith(" (deleted)", StringComparison.Ordinal) ? null : path;
            }
            finally
            {
                close(opened);
            }
        }

        public void Dispose()
        {
            disposed = true;
            thread.Join(2000);
            close(fd);
            lock (gate)
                foreach (var marked in byKey.Values) close(marked.MountFd);
        }
    }
}
