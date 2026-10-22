using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace search.Models
{
    /// <summary>
    /// Linux change source for one root, built directly on inotify instead of
    /// FileSystemWatcher. inotify watches single directories, so a whole file system needs
    /// one watch per directory - and the kernel's per-user watch limit is shared with every
    /// other program (IDEs, file managers, sync clients). This watcher therefore:
    /// - spends at most half of the user's limit across all roots and stops adding watches
    ///   when that budget is used (shallow directories first, so the visible part of the
    ///   tree stays live; deeper ones refresh with the next scan),
    /// - never follows symbolic links or crosses into another mount point,
    /// - pairs IN_MOVED_FROM/IN_MOVED_TO by cookie into one rename (a moved folder keeps
    ///   its indexed subtree), and reports a move out of the watched tree as a delete,
    /// - walks a directory that appears (created, or moved in from outside) and reports
    ///   its existing content, because entries made before its watch existed raise nothing,
    /// - asks for a rescan of the root when the kernel queue overflows.
    /// </summary>
    internal sealed class InotifyWatcher : IDisposable
    {
        const uint IN_MODIFY = 0x2, IN_ATTRIB = 0x4, IN_CLOSE_WRITE = 0x8, IN_MOVED_FROM = 0x40,
            IN_MOVED_TO = 0x80, IN_CREATE = 0x100, IN_DELETE = 0x200, IN_DELETE_SELF = 0x400,
            IN_MOVE_SELF = 0x800, IN_UNMOUNT = 0x2000, IN_Q_OVERFLOW = 0x4000, IN_IGNORED = 0x8000,
            IN_ONLYDIR = 0x01000000, IN_DONT_FOLLOW = 0x02000000, IN_EXCL_UNLINK = 0x04000000,
            IN_ISDIR = 0x40000000;
        const int IN_NONBLOCK = 0x800, IN_CLOEXEC = 0x80000;
        const int EAGAIN = 11, EINTR = 4, ENOSPC = 28;
        const uint WatchMask = IN_MODIFY | IN_ATTRIB | IN_CLOSE_WRITE | IN_MOVED_FROM | IN_MOVED_TO
            | IN_CREATE | IN_DELETE | IN_DELETE_SELF | IN_MOVE_SELF | IN_ONLYDIR | IN_DONT_FOLLOW | IN_EXCL_UNLINK;

        [StructLayout(LayoutKind.Sequential)]
        struct PollFd { public int Fd; public short Events; public short Revents; }

        [DllImport("libc", SetLastError = true)] static extern int inotify_init1(int flags);
        [DllImport("libc", SetLastError = true)]
        static extern int inotify_add_watch(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mask);
        [DllImport("libc", SetLastError = true)] static extern int inotify_rm_watch(int fd, int wd);
        [DllImport("libc", SetLastError = true)] static extern unsafe nint read(int fd, byte* buffer, nint count);
        [DllImport("libc", SetLastError = true)] static extern unsafe int poll(PollFd* fds, nuint count, int timeout);
        [DllImport("libc", SetLastError = true)] static extern int close(int fd);

        static readonly object budgetLock = new();
        static int budgetLeft = -1;
        static bool budgetReported;

        readonly string root;
        readonly Action<FsEvent> report;
        readonly Action<DriveScanReason> rescan;
        readonly int fd;
        readonly object gate = new();
        readonly Dictionary<int, string> paths = new();
        readonly Dictionary<string, int> watches = new(StringComparer.Ordinal);
        readonly Thread thread;
        volatile bool disposed;
        int watchCount;

        /// <summary>Directories left unwatched because the watch budget ran out</summary>
        public int Unwatched { get; private set; }
        public int WatchCount => Volatile.Read(ref watchCount);

        InotifyWatcher(string root, int fd, Action<FsEvent> report, Action<DriveScanReason> rescan)
        {
            this.root = PathText.TrimEndSeparators(root);
            this.fd = fd;
            this.report = report;
            this.rescan = rescan;
            WatchTree(this.root, announce: false);
            thread = new Thread(Loop) { IsBackground = true, Name = "inotify " + this.root, Priority = ThreadPriority.BelowNormal };
            thread.Start();
        }

        /// <summary>
        /// Start watching the root; null when inotify is unavailable (not Linux, or the
        /// per-user instance limit is reached) - the caller then uses FileSystemWatcher.
        /// All initial watches exist when this returns, so a scan started afterwards
        /// cannot miss a change made in between.
        /// </summary>
        public static InotifyWatcher TryStart(string root, Action<FsEvent> report, Action<DriveScanReason> rescan)
        {
            if (!OperatingSystem.IsLinux()) return null;
            int fd;
            try { fd = inotify_init1(IN_NONBLOCK | IN_CLOEXEC); }
            catch { return null; }
            if (fd < 0)
            {
                $"inotify_init1 failed for {root}: errno {Marshal.GetLastPInvokeError()}".Debug();
                return null;
            }
            try { return new InotifyWatcher(root, fd, report, rescan); }
            catch (Exception e)
            {
                $"inotify watcher on {root} failed: {e.Message}".Debug();
                close(fd);
                return null;
            }
        }

        static int ReadMaxUserWatches()
        {
            try
            {
                var text = File.ReadAllText("/proc/sys/fs/inotify/max_user_watches").Trim();
                if (int.TryParse(text, out var value) && value > 0) return value;
            }
            catch { }
            return 8192;
        }

        /// <summary>Take one watch from the process-wide budget</summary>
        static bool TakeBudget()
        {
            lock (budgetLock)
            {
                if (budgetLeft < 0) budgetLeft = Math.Max(1024, ReadMaxUserWatches() / 2);
                if (budgetLeft == 0) return false;
                budgetLeft--;
                return true;
            }
        }

        static void ReturnBudget(int count)
        {
            if (count <= 0) return;
            lock (budgetLock)
                if (budgetLeft >= 0) budgetLeft += count;
        }

        static readonly EnumerationOptions DirectoryOptions = new()
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint //Symbolic links are never followed
        };

        /// <summary>
        /// Watch the directory and everything below it, breadth first. With announce the
        /// entries found are reported as created - their own events happened before the
        /// watches existed.
        /// </summary>
        void WatchTree(string top, bool announce)
        {
            var queue = new Queue<string>();
            queue.Enqueue(top);
            var first = true;
            while (queue.Count > 0 && !disposed)
            {
                var dir = queue.Dequeue();
                if (!first && FsRoots.IsForeignMountPoint(dir, root)) continue;
                first = false;
                if (!AddWatch(dir)) continue;
                try
                {
                    if (announce)
                        foreach (var file in Directory.EnumerateFiles(dir, "*", DirectoryOptions))
                            report(new FsEvent(WatcherChangeTypes.Created, file));
                    foreach (var sub in Directory.EnumerateDirectories(dir, "*", DirectoryOptions))
                    {
                        if (announce) report(new FsEvent(WatcherChangeTypes.Created, sub));
                        queue.Enqueue(sub);
                    }
                }
                catch { } //Unreadable directory - nothing below it can be watched either
            }
        }

        bool AddWatch(string dir)
        {
            lock (gate)
            {
                if (watches.ContainsKey(dir)) return true;
                if (!TakeBudget())
                {
                    Unwatched++;
                    ReportBudgetExhausted();
                    return false;
                }
                var wd = inotify_add_watch(fd, dir, WatchMask);
                if (wd < 0)
                {
                    ReturnBudget(1);
                    var errno = Marshal.GetLastPInvokeError();
                    if (errno == ENOSPC)
                    {
                        //The user's limit is spent by other programs - stop asking for more
                        lock (budgetLock) budgetLeft = 0;
                        Unwatched++;
                        ReportBudgetExhausted();
                    }
                    return false;
                }
                //The same directory reached under a second name (bind mount) gets the same wd
                if (paths.TryGetValue(wd, out var known) && known != dir)
                {
                    ReturnBudget(1);
                    return true;
                }
                paths[wd] = dir;
                watches[dir] = wd;
                watchCount++;
                return true;
            }
        }

        static void ReportBudgetExhausted()
        {
            if (budgetReported) return;
            budgetReported = true;
            StorageMaintenance.AppendDiagnostic(
                "inotify watch budget exhausted - deeper directories refresh only with the next scan (F12)");
        }

        void ForgetTree(string dir)
        {
            lock (gate)
            {
                var prefix = dir + "/";
                var gone = new List<string>();
                foreach (var path in watches.Keys)
                    if (path == dir || path.StartsWith(prefix, StringComparison.Ordinal)) gone.Add(path);
                foreach (var path in gone)
                {
                    var wd = watches[path];
                    watches.Remove(path);
                    paths.Remove(wd);
                    inotify_rm_watch(fd, wd);
                    watchCount--;
                }
                ReturnBudget(gone.Count);
            }
        }

        void ForgetWatch(int wd)
        {
            lock (gate)
            {
                if (!paths.TryGetValue(wd, out var path)) return;
                paths.Remove(wd);
                if (watches.TryGetValue(path, out var current) && current == wd) watches.Remove(path);
                watchCount--;
                ReturnBudget(1);
            }
        }

        /// <summary>A watched directory moved inside the tree: its watches follow it</summary>
        void MoveTree(string oldDir, string newDir)
        {
            lock (gate)
            {
                var prefix = oldDir + "/";
                var moved = new List<(string Old, string New, int Wd)>();
                foreach (var (path, wd) in watches)
                    if (path == oldDir) moved.Add((path, newDir, wd));
                    else if (path.StartsWith(prefix, StringComparison.Ordinal))
                        moved.Add((path, newDir + path.Substring(oldDir.Length), wd));
                foreach (var m in moved) watches.Remove(m.Old);
                foreach (var m in moved)
                {
                    watches[m.New] = m.Wd;
                    paths[m.Wd] = m.New;
                }
            }
        }

        string PathOf(int wd)
        {
            lock (gate) return paths.TryGetValue(wd, out var path) ? path : null;
        }

        unsafe void Loop()
        {
            var buffer = new byte[256 * 1024];
            //A move out of the tree has no IN_MOVED_TO - it is a delete once the pair cannot
            //complete anymore (the kernel queues both halves back to back)
            var pendingMoves = new Dictionary<uint, (string Path, bool IsDirectory, long At)>();
            while (!disposed)
            {
                var pfd = new PollFd { Fd = fd, Events = 1 };
                var ready = poll(&pfd, 1, pendingMoves.Count > 0 ? 50 : 500);
                if (disposed) break;
                if (ready < 0 && Marshal.GetLastPInvokeError() != EINTR) break;
                if (ready <= 0)
                {
                    FlushMoves(pendingMoves, force: true);
                    continue;
                }
                nint length;
                fixed (byte* p = buffer) length = read(fd, p, buffer.Length);
                if (length < 0)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    if (errno == EAGAIN || errno == EINTR) continue;
                    break;
                }
                var offset = 0;
                while (offset + 16 <= length)
                {
                    var wd = BitConverter.ToInt32(buffer, offset);
                    var mask = BitConverter.ToUInt32(buffer, offset + 4);
                    var cookie = BitConverter.ToUInt32(buffer, offset + 8);
                    var nameLength = (int)BitConverter.ToUInt32(buffer, offset + 12);
                    var name = nameLength > 0 ? DecodeName(buffer, offset + 16, nameLength) : null;
                    offset += 16 + nameLength;
                    try { Handle(wd, mask, cookie, name, pendingMoves); }
                    catch (Exception e) { $"inotify event on {root} failed: {e.Message}".Debug(); }
                }
                FlushMoves(pendingMoves, force: false);
            }
            FlushMoves(pendingMoves, force: true);
        }

        static string DecodeName(byte[] buffer, int start, int length)
        {
            var end = Array.IndexOf(buffer, (byte)0, start, length);
            return Encoding.UTF8.GetString(buffer, start, (end < 0 ? start + length : end) - start);
        }

        void FlushMoves(Dictionary<uint, (string Path, bool IsDirectory, long At)> pending, bool force)
        {
            if (pending.Count == 0) return;
            var now = Environment.TickCount64;
            List<uint> done = null;
            foreach (var (cookie, move) in pending)
            {
                if (!force && now - move.At < 50) continue;
                (done ??= new()).Add(cookie);
                if (move.IsDirectory) ForgetTree(move.Path);
                report(new FsEvent(WatcherChangeTypes.Deleted, move.Path));
            }
            if (done != null) foreach (var cookie in done) pending.Remove(cookie);
        }

        void Handle(int wd, uint mask, uint cookie, string name,
            Dictionary<uint, (string Path, bool IsDirectory, long At)> pendingMoves)
        {
            if ((mask & IN_Q_OVERFLOW) != 0)
            {
                $"inotify queue overflow on {root} => rescan".Debug();
                rescan(DriveScanReason.WatcherOverflow);
                return;
            }
            if ((mask & IN_IGNORED) != 0)
            {
                ForgetWatch(wd);
                return;
            }
            var dir = PathOf(wd);
            if (dir == null) return;
            var isDirectory = (mask & IN_ISDIR) != 0;
            if (name == null)
            {
                //Events about the watched directory itself
                if ((mask & (IN_DELETE_SELF | IN_MOVE_SELF | IN_UNMOUNT)) != 0 && dir == root)
                    rescan(DriveScanReason.ManualRefresh);
                return;
            }
            var path = dir == "/" ? "/" + name : dir + "/" + name;

            if ((mask & IN_MOVED_FROM) != 0)
            {
                pendingMoves[cookie] = (path, isDirectory, Environment.TickCount64);
                return;
            }
            if ((mask & IN_MOVED_TO) != 0)
            {
                if (pendingMoves.Remove(cookie, out var from))
                {
                    if (from.IsDirectory) MoveTree(from.Path, path);
                    report(new FsEvent(WatcherChangeTypes.Renamed, path, from.Path));
                }
                else
                {
                    //Moved in from outside the watched tree: new to the index, content included
                    report(new FsEvent(WatcherChangeTypes.Created, path));
                    if (isDirectory) WatchTree(path, announce: true);
                }
                return;
            }
            if ((mask & IN_CREATE) != 0)
            {
                report(new FsEvent(WatcherChangeTypes.Created, path));
                if (isDirectory && !FsRoots.IsForeignMountPoint(path, root)) WatchTree(path, announce: true);
                return;
            }
            if ((mask & IN_DELETE) != 0)
            {
                if (isDirectory) ForgetTree(path);
                report(new FsEvent(WatcherChangeTypes.Deleted, path));
                return;
            }
            if ((mask & (IN_MODIFY | IN_ATTRIB | IN_CLOSE_WRITE)) != 0)
                report(new FsEvent(WatcherChangeTypes.Changed, path));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (thread != null && thread.IsAlive && Thread.CurrentThread != thread) thread.Join(2000);
            lock (gate)
            {
                ReturnBudget(watchCount);
                watchCount = 0;
                paths.Clear();
                watches.Clear();
            }
            close(fd);
        }
    }
}
