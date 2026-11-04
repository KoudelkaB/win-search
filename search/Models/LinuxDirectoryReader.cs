using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace search.Models
{
    /// <summary>
    /// Lists one directory for the Linux walk straight from the kernel: getdents64 for the
    /// names, then one statx per entry relative to the directory's descriptor. Compared with
    /// DirectoryInfo.EnumerateFileSystemInfos this skips the FileSystemInfo objects and the
    /// kernel's resolution of every entry's full path. Each getdents64 batch (64 KB, about
    /// two thousand names) is stat-ed in inode order, which keeps the stat calls local in
    /// the inode tables (ext4/XFS) and the btrfs tree with a cold cache, and is added before
    /// the next batch is read - like the managed enumeration, a listing that hangs on a dead
    /// mount keeps everything read before.
    /// The nodes match what FileNode.Create(FileSystemInfo) builds from the managed
    /// enumeration (attributes as .NET computes them on Unix, lstat size and local mtime),
    /// so the rest of the app cannot tell the two apart.
    /// One instance per walker thread: it owns the buffers it reuses for every directory.
    /// Not sealed: tests override GetDents and StatAt to simulate hung and interrupted calls.
    /// </summary>
    internal unsafe class LinuxDirectoryReader
    {
        const int AT_SYMLINK_NOFOLLOW = 0x100, AT_NO_AUTOMOUNT = 0x800, AT_FDCWD = -100;
        const uint STATX_TYPE = 0x1, STATX_MODE = 0x2, STATX_UID = 0x8, STATX_GID = 0x10,
            STATX_MTIME = 0x40, STATX_SIZE = 0x200;
        const uint StatMask = STATX_TYPE | STATX_MODE | STATX_UID | STATX_GID | STATX_MTIME | STATX_SIZE;
        const int S_IFMT = 0xF000, S_IFDIR = 0x4000, S_IFLNK = 0xA000;
        const int ENOENT = 2, EINTR = 4, ENOTDIR = 20;
        const int O_CLOEXEC = 0x80000;
        internal const byte DT_UNKNOWN = 0, DT_DIR = 4, DT_REG = 8, DT_LNK = 10;
        const int DirentHeader = 19; //d_ino u64 @0, d_off s64 @8, d_reclen u16 @16, d_type u8 @18, d_name @19
        const int BufferSize = 64 * 1024;

        /// <summary>struct statx of linux/stat.h - the same layout on every architecture</summary>
        [StructLayout(LayoutKind.Explicit, Size = 256)]
        internal struct Statx
        {
            [FieldOffset(0)] public uint Mask;
            [FieldOffset(20)] public uint Uid;
            [FieldOffset(24)] public uint Gid;
            [FieldOffset(28)] public ushort Mode;
            [FieldOffset(40)] public ulong Size;
            [FieldOffset(112)] public long MTimeSec;
            [FieldOffset(120)] public uint MTimeNsec;
        }

        internal struct Entry : IComparable<Entry>
        {
            public ulong Ino;
            public int NameOffset;
            public int NameLength;
            public byte Type;
            public int CompareTo(Entry other) => Ino.CompareTo(other.Ino);
        }

        [DllImport("libc", SetLastError = true)]
        static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
        [DllImport("libc", SetLastError = true)] static extern int close(int fd);
        [DllImport("libc", SetLastError = true)] static extern nint getdents64(int fd, byte* buffer, nuint count);
        [DllImport("libc", SetLastError = true)]
        static extern int statx(int dirfd, byte* path, int flags, uint mask, Statx* buffer);
        [DllImport("libc")] static extern uint geteuid();
        [DllImport("libc")] static extern uint getegid();
        [DllImport("libc", SetLastError = true)] static extern int getgroups(int size, uint* list);

        static readonly Lazy<TimedCheck> support = new(() => new TimedCheck(Probe, TimeSpan.FromSeconds(2), "getdents64 + statx probe"));
        static uint euid, egid;
        static uint[] groups = Array.Empty<uint>();
        static int openFlags, NoFollowFlag;

        readonly byte[] buffer = new byte[BufferSize];
        byte[] names = new byte[16 * 1024];
        int namesLength;
        Entry[] entries = new Entry[256];
        int count;
        char[] chars = new char[256];

        /// <summary>
        /// True on Linux when the C library exports getdents64 and statx (glibc 2.30+) and the
        /// kernel (4.11+) and any seccomp filter let them run; otherwise the walk keeps using
        /// the managed enumeration. Every drive's walk asks this before its stall watchdog
        /// exists, so the check runs on its own thread and answers within two seconds of its
        /// start even should it hang - false until it does answer.
        /// </summary>
        public static bool IsSupported => OperatingSystem.IsLinux() && support.Value.Passed;

        static bool Probe()
        {
            try
            {
                //O_DIRECTORY | O_NOFOLLOW differ by architecture (asm/fcntl.h), O_CLOEXEC does not
                var (directory, noFollow) = RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.X64 or Architecture.X86 or Architecture.RiscV64
                        or Architecture.LoongArch64 or Architecture.S390x => (0x10000, 0x20000),
                    Architecture.Arm64 or Architecture.Arm or Architecture.Ppc64le => (0x4000, 0x8000),
                    _ => (0, 0)
                };
                if (directory == 0) return false;
                openFlags = directory | noFollow | O_CLOEXEC; //O_RDONLY = 0
                NoFollowFlag = noFollow;

                //The whole path once: a wrong flag or a blocked call must fall back to the
                //managed enumeration, never make every directory look unreadable. On /proc,
                //which lives in memory: a probe on a real file system could hang on a dead
                //disk or network mount and hold up every walk waiting for this answer.
                Statx sx;
                var proc = stackalloc byte[] { (byte)'/', (byte)'p', (byte)'r', (byte)'o', (byte)'c', 0 };
                if (statx(AT_FDCWD, proc, AT_SYMLINK_NOFOLLOW, StatMask, &sx) != 0)
                    return Unsupported($"statx errno {Marshal.GetLastPInvokeError()}"); //ENOSYS, EPERM from seccomp, no /proc
                if ((sx.Mode & S_IFMT) != S_IFDIR) return Unsupported("/proc is not a directory");
                var fd = open("/proc", openFlags);
                if (fd < 0) return Unsupported($"open errno {Marshal.GetLastPInvokeError()}");
                try
                {
                    var probe = new byte[BufferSize];
                    fixed (byte* p = probe)
                        if (getdents64(fd, p, (nuint)probe.Length) <= 0) //"." and ".." at least
                            return Unsupported($"getdents64 errno {Marshal.GetLastPInvokeError()}");
                }
                finally
                {
                    close(fd);
                }
                //A link must not open: /proc/self is one
                fd = open("/proc/self", openFlags);
                if (fd >= 0)
                {
                    close(fd);
                    return Unsupported("O_NOFOLLOW has no effect");
                }

                euid = geteuid();
                egid = getegid();
                var n = getgroups(0, null);
                if (n > 0)
                {
                    var list = new uint[n];
                    fixed (uint* p = list) n = getgroups(n, p);
                    groups = n > 0 ? list.AsSpan(0, n).ToArray() : Array.Empty<uint>();
                }
                return true;
            }
            catch (Exception e) //EntryPointNotFoundException (glibc before 2.30, musl), DllNotFoundException...
            {
                return Unsupported(e.GetType().Name);
            }
        }

        static bool Unsupported(string reason)
        {
            $"getdents64 + statx listing unavailable ({reason}) - the walk uses the managed enumeration".Debug();
            return false;
        }

        /// <summary>getdents64 - a virtual seam for tests</summary>
        internal virtual nint GetDents(int fd, byte* buffer, nuint count) => getdents64(fd, buffer, count);

        /// <summary>statx - a virtual seam for tests</summary>
        internal virtual int StatAt(int dirfd, byte* path, int flags, uint mask, Statx* buffer)
            => statx(dirfd, path, flags, mask, buffer);

        /// <summary>
        /// Call add for every entry of the directory, until it returns false. False when the
        /// directory cannot be opened (unreadable, vanished, replaced by a symbolic link) - it
        /// is then skipped, like the managed enumeration with IgnoreInaccessible does.
        /// Entries that vanish between the listing and their stat are left out. An entry that
        /// cannot be stat-ed (a directory readable but not searchable) ends the listing, as
        /// the managed enumeration throws there and the walk loses the rest of the directory.
        /// A read error ends it too, keeping what was added - the managed enumeration throws.
        /// </summary>
        /// <param name="followLink">Open the directory even when its path is a symbolic link (the walk's root)</param>
        public bool List(string directory, bool followLink, Func<FileNode, bool> add)
        {
            var flags = followLink ? openFlags & ~NoFollowFlag : openFlags;
            int fd;
            while ((fd = open(directory, flags)) < 0 && Marshal.GetLastPInvokeError() == EINTR) { }
            if (fd < 0) return false;
            try
            {
                var prefix = directory.EndsWith('/') ? directory : directory + "/";
                fixed (byte* p = buffer)
                    while (true)
                    {
                        var read = GetDents(fd, p, (nuint)buffer.Length);
                        if (read == 0) break;
                        if (read < 0)
                        {
                            if (Marshal.GetLastPInvokeError() == EINTR) continue;
                            break;
                        }
                        ParseBatch(buffer.AsSpan(0, (int)read));
                        if (!AddBatch(fd, prefix, add)) break;
                    }
                return true;
            }
            finally
            {
                close(fd);
            }
        }

        /// <summary>Stat and add the parsed batch in inode order; false ends the listing</summary>
        bool AddBatch(int fd, string prefix, Func<FileNode, bool> add)
        {
            entries.AsSpan(0, count).Sort();
            fixed (byte* namePtr = names)
                for (var i = 0; i < count; i++)
                {
                    var node = Stat(fd, prefix, namePtr, in entries[i], out var failed);
                    if (failed) return false;
                    if (node != null && !add(node)) return false;
                }
            return true;
        }

        /// <summary>
        /// StatAt, repeated when a signal interrupts it - a FUSE or network file system can
        /// return EINTR. (.NET repeats its stat and open too, but not the lstat of a listed
        /// entry: there the managed enumeration throws and loses the rest of the directory.)
        /// </summary>
        int StatRetrying(int dirfd, byte* path, int flags, uint mask, Statx* buffer)
        {
            int result;
            while ((result = StatAt(dirfd, path, flags, mask, buffer)) != 0
                && Marshal.GetLastPInvokeError() == EINTR) { }
            return result;
        }

        /// <summary>The entry's node; null when it vanished since the listing</summary>
        /// <param name="failed">It exists but cannot be stat-ed</param>
        FileNode Stat(int fd, string prefix, byte* namePtr, in Entry entry, out bool failed)
        {
            failed = false;
            Statx sx;
            var name = namePtr + entry.NameOffset;
            if (StatRetrying(fd, name, AT_SYMLINK_NOFOLLOW | AT_NO_AUTOMOUNT, StatMask, &sx) != 0)
            {
                failed = Marshal.GetLastPInvokeError() is not (ENOENT or ENOTDIR);
                return null;
            }

            var type = sx.Mode & S_IFMT;
            var isLink = type == S_IFLNK;
            var isDirectory = type == S_IFDIR;
            //.NET reports a link by its own size, time and owner, but with the target's
            //directory flag and permissions (FileStatus.RefreshCaches)
            var mode = sx.Mode;
            if (isLink)
            {
                Statx target;
                if (StatRetrying(fd, name, AT_NO_AUTOMOUNT, STATX_TYPE | STATX_MODE, &target) == 0)
                {
                    isDirectory = (target.Mode & S_IFMT) == S_IFDIR;
                    mode = (ushort)(S_IFLNK | (target.Mode & 0xFFF));
                }
            }

            var hidden = entry.NameLength > 0 && names[entry.NameOffset] == (byte)'.';
            var attributes = Attributes(mode, sx.Uid, sx.Gid, isLink, isDirectory, hidden);
            return FileNode.Create(Combine(prefix, entry),
                new NodeMetadataSnapshot(attributes, isDirectory ? 0 : sx.Size, LocalTime(sx.MTimeSec, sx.MTimeNsec)));
        }

        /// <summary>FileStatus.GetAttributes of .NET on Unix</summary>
        internal static FileAttributes Attributes(int mode, uint uid, uint gid, bool isLink, bool isDirectory, bool hidden)
        {
            FileAttributes attributes = default;
            if (IsReadOnly(mode, uid, gid)) attributes |= FileAttributes.ReadOnly;
            if (isLink) attributes |= FileAttributes.ReparsePoint;
            if (isDirectory) attributes |= FileAttributes.Directory;
            if (hidden) attributes |= FileAttributes.Hidden;
            return attributes != default ? attributes : FileAttributes.Normal;
        }

        /// <summary>
        /// FileStatus.IsModeReadOnlyCore of .NET on Unix: readable but not writable by the
        /// class (owner, group, other) the effective user falls in
        /// </summary>
        internal static bool IsReadOnly(int mode, uint uid, uint gid)
        {
            var user = (mode & 0x100) != 0 && (mode & 0x80) == 0;
            var group = (mode & 0x20) != 0 && (mode & 0x10) == 0;
            var other = (mode & 0x4) != 0 && (mode & 0x2) == 0;
            if (user == group && group == other) return user;
            if (uid == euid) return user;
            if (group == user) return group;
            return IsMemberOfGroup(gid) ? group : other;
        }

        static bool IsMemberOfGroup(uint gid) => gid == egid || Array.IndexOf(groups, gid) >= 0;

        /// <summary>FileSystemInfo.LastWriteTime: the Unix time converted to local time</summary>
        internal static DateTime LocalTime(long seconds, uint nanoseconds)
        {
            //Outside DateTimeOffset's range FromUnixTimeSeconds throws and FileNode keeps the default
            if (seconds < -62_135_596_800L || seconds > 253_402_300_799L) return default;
            return DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(nanoseconds / 100).UtcDateTime.ToLocalTime();
        }

        /// <summary>prefix + the entry's name, decoded from UTF-8 like the managed enumeration does</summary>
        string Combine(string prefix, in Entry entry)
        {
            var bytes = names.AsSpan(entry.NameOffset, entry.NameLength);
            if (chars.Length < bytes.Length) chars = new char[bytes.Length]; //UTF-16 never needs more units than UTF-8 bytes
            var length = Encoding.UTF8.GetChars(bytes, chars);
            return string.Concat(prefix.AsSpan(), chars.AsSpan(0, length));
        }

        /// <summary>
        /// The entries of one getdents64 result: its linux_dirent64 records without "." and
        /// "..". A malformed record ends the buffer.
        /// </summary>
        internal void ParseBatch(ReadOnlySpan<byte> records)
        {
            count = 0;
            namesLength = 0;
            var offset = 0;
            while (offset + DirentHeader < records.Length)
            {
                var length = BitConverter.ToUInt16(records.Slice(offset + 16, 2));
                if (length <= DirentHeader || offset + length > records.Length) break;
                var record = records.Slice(offset, length);
                offset += length;

                var name = record.Slice(DirentHeader);
                var end = name.IndexOf((byte)0);
                if (end >= 0) name = name.Slice(0, end);
                if (name.Length == 0 || name[0] == '.' && (name.Length == 1 || name.Length == 2 && name[1] == '.'))
                    continue;

                if (count == entries.Length) Array.Resize(ref entries, count * 2);
                //Names are kept NUL-terminated - statx takes them as C strings
                if (namesLength + name.Length + 1 > names.Length)
                    Array.Resize(ref names, Math.Max(names.Length * 2, namesLength + name.Length + 1));
                name.CopyTo(names.AsSpan(namesLength));
                names[namesLength + name.Length] = 0;
                entries[count++] = new Entry
                {
                    Ino = BitConverter.ToUInt64(record),
                    NameOffset = namesLength,
                    NameLength = name.Length,
                    Type = record[18]
                };
                namesLength += name.Length + 1;
            }
        }

        /// <summary>Test view of the entries of the last ParseBatch</summary>
        internal (ulong Ino, byte Type, string Name)[] CollectedEntries()
        {
            var result = new (ulong, byte, string)[count];
            for (var i = 0; i < count; i++)
                result[i] = (entries[i].Ino, entries[i].Type,
                    Encoding.UTF8.GetString(names, entries[i].NameOffset, entries[i].NameLength));
            return result;
        }
    }

    /// <summary>
    /// A one-time check on its own thread whose askers wait for it only until its deadline:
    /// a check that hangs answers false then - and its real answer should it ever finish.
    /// </summary>
    internal sealed class TimedCheck
    {
        readonly ManualResetEventSlim done = new(false); //Never disposed: a hung check still holds it
        readonly long deadline;
        readonly string name;
        volatile bool passed;
        int reported;

        public TimedCheck(Func<bool> check, TimeSpan limit, string name)
        {
            this.name = name;
            deadline = Environment.TickCount64 + (long)limit.TotalMilliseconds;
            new Thread(() =>
            {
                try { passed = check(); }
                catch { }
                finally { done.Set(); }
            })
            { IsBackground = true, Name = name }.Start();
        }

        /// <summary>The check's result; false while it runs past its deadline</summary>
        public bool Passed
        {
            get
            {
                if (done.Wait((int)Math.Clamp(deadline - Environment.TickCount64, 0, int.MaxValue))) return passed;
                if (Interlocked.Exchange(ref reported, 1) == 0)
                    $"{name} still runs past its time limit - false until it answers".Debug();
                return false;
            }
        }
    }
}
