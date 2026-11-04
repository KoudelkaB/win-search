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
    /// <summary>Which folders a client may see - the real check, or a fake in tests</summary>
    internal interface IAccessCheck : IDisposable
    {
        /// <summary>For each folder: could the user list it (reach it, read and search it)</summary>
        bool[] CanList(IReadOnlyList<string> folders);
    }

    /// <summary>
    /// Answers "could this user list that folder" as the kernel would for the user's own
    /// process - ACLs included - by asking it as the user: a dedicated thread
    /// switches its own file-system identity (setfsuid, setfsgid and, through the raw system
    /// call, its supplementary groups - glibc's setgroups would change every thread of the
    /// service), calls faccessat2, and switches back. With a non-zero file-system user the
    /// kernel also drops CAP_DAC_READ_SEARCH and CAP_DAC_OVERRIDE from that thread, so the
    /// service's privileges do not leak into the answer. Anything else restricting the user's
    /// process (SELinux, AppArmor, Landlock, a sandbox's namespaces) is not part of the
    /// answer - which is why only clients nothing confines are served
    /// (PeerIdentity.ConfinementOf). An identity that
    /// cannot be assumed, or an architecture without the known system call numbers, answers
    /// no to everything; a thread that cannot switch back ends the service.
    /// </summary>
    internal sealed unsafe class UserAccess : IAccessCheck
    {
        const int AT_FDCWD = -100, AT_EACCESS = 0x200, R_OK = 4, X_OK = 1, SYS_faccessat2 = 439;

        [DllImport("libc", SetLastError = true)] static extern long syscall(long number, long a, long b, long c, long d);
        [DllImport("libc")] static extern int setfsuid(uint uid);
        [DllImport("libc")] static extern int setfsgid(uint gid);
        [DllImport("libc", SetLastError = true)] static extern int getgroups(int size, uint* list);

        /// <summary>setgroups for one thread only - its number differs by architecture</summary>
        static readonly long SysSetgroups = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => 116,
            Architecture.Arm64 => 159,
            _ => -1
        };

        public static bool Supported => OperatingSystem.IsLinux() && SysSetgroups > 0;

        sealed class Work
        {
            public IReadOnlyList<string> Folders;
            public bool[] Result;
            public readonly ManualResetEventSlim Done = new(false);
        }

        readonly UserIdentity user;
        readonly BlockingCollection<Work> work = new();
        readonly Thread thread;

        public UserAccess(UserIdentity user)
        {
            this.user = user;
            thread = new Thread(Loop) { IsBackground = true, Name = $"access uid {user.Uid}" };
            thread.Start();
        }

        public bool[] CanList(IReadOnlyList<string> folders)
        {
            if (folders.Count == 0) return Array.Empty<bool>();
            var item = new Work { Folders = folders };
            try { work.Add(item); }
            catch (InvalidOperationException) { return new bool[folders.Count]; } //Disposed
            item.Done.Wait();
            return item.Result;
        }

        void Loop()
        {
            //What this thread switches back to
            var ownUid = (uint)setfsuid(uint.MaxValue);
            var ownGid = (uint)setfsgid(uint.MaxValue);
            var ownGroups = Groups();
            foreach (var item in work.GetConsumingEnumerable())
            {
                var result = new bool[item.Folders.Count];
                try
                {
                    if (Become(user.Uid, user.Gid, user.Groups))
                        for (var i = 0; i < result.Length; i++)
                            result[i] = CanListNow(item.Folders[i]);
                }
                finally
                {
                    if (!Become(ownUid, ownGid, ownGroups))
                        Environment.FailFast("The access check could not restore the service's identity.");
                    item.Result = result;
                    item.Done.Set();
                }
            }
        }

        static bool CanListNow(string folder)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(folder + "\0");
            fixed (byte* p = bytes)
                return syscall(SYS_faccessat2, AT_FDCWD, (long)p, R_OK | X_OK, AT_EACCESS) == 0;
        }

        /// <summary>Switch this thread's file-system identity; true when it really changed</summary>
        static bool Become(uint uid, uint gid, uint[] groups)
        {
            if (SysSetgroups <= 0) return false;
            if (!SameSet(Groups(), groups))
                fixed (uint* list = groups.Length > 0 ? groups : new uint[1])
                    if (syscall(SysSetgroups, groups.Length, (long)list, 0, 0) != 0) return false;
            setfsgid(gid);
            setfsuid(uid);
            //An invalid id changes nothing and returns the current one
            return (uint)setfsuid(uint.MaxValue) == uid && (uint)setfsgid(uint.MaxValue) == gid
                && SameSet(Groups(), groups);
        }

        static bool SameSet(uint[] a, uint[] b) => a.Distinct().OrderBy(g => g).SequenceEqual(b.Distinct().OrderBy(g => g));

        static uint[] Groups()
        {
            var n = getgroups(0, null);
            if (n <= 0) return Array.Empty<uint>();
            var list = new uint[n];
            fixed (uint* p = list) n = getgroups(n, p);
            return n > 0 ? list.AsSpan(0, n).ToArray() : Array.Empty<uint>();
        }

        public void Dispose()
        {
            work.CompleteAdding();
            thread.Join(2000);
        }
    }

    /// <summary>Who is on the other end of the service socket</summary>
    internal static unsafe class PeerIdentity
    {
        const int SOL_SOCKET = 1, SO_PEERCRED = 17, SO_PEERSEC = 31, SO_PEERPIDFD = 77;

        [DllImport("libc", SetLastError = true)] static extern IntPtr getpwuid(uint uid);
        [DllImport("libc", SetLastError = true)] static extern IntPtr getgrnam([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport("libc", SetLastError = true)]
        static extern int getgrouplist([MarshalAs(UnmanagedType.LPUTF8Str)] string user, uint group, uint* groups, int* count);
        [DllImport("libc")] static extern int close(int fd);

        static readonly object nss = new(); //getpwuid and getgrnam return shared buffers
        static readonly HashSet<string> refused = new(StringComparer.Ordinal);

        /// <summary>The connected process's user id (SO_PEERCRED); null when it cannot be told</summary>
        public static uint? UidOf(Socket socket)
        {
            var credentials = new byte[12]; //struct ucred: pid, uid, gid
            try
            {
                return socket.GetRawSocketOption(SOL_SOCKET, SO_PEERCRED, credentials) == 12
                    ? BitConverter.ToUInt32(credentials, 4) : null;
            }
            catch (SocketException) { return null; }
        }

        /// <summary>
        /// The connected process's user (SO_PEERCRED - the kernel's word, not the client's)
        /// with the groups the system gives that user; null when it cannot be told, or for a
        /// confined process (see ConfinementOf)
        /// </summary>
        public static UserIdentity Of(Socket socket)
        {
            var credentials = new byte[12]; //struct ucred: pid, uid, gid
            if (socket.GetRawSocketOption(SOL_SOCKET, SO_PEERCRED, credentials) != 12) return null;
            var uid = BitConverter.ToUInt32(credentials, 4);
            var gid = BitConverter.ToUInt32(credentials, 8);
            if (ConfinementOf(socket) is { } confinement)
            {
                lock (refused)
                    if (refused.Add($"{uid} {confinement}")) //Once: its app retries every few seconds
                        LinuxService.Log($"refusing a client of uid {uid} - {confinement}: what it may see is not checked here");
                return null;
            }
            lock (nss)
            {
                var passwd = getpwuid(uid);
                if (passwd == IntPtr.Zero) return new UserIdentity(uid, gid, Array.Empty<uint>());
                var name = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(passwd)); //pw_name comes first
                var count = 64;
                while (true)
                {
                    var groups = new uint[count];
                    int found = count;
                    fixed (uint* p = groups)
                        if (getgrouplist(name, gid, p, &found) >= 0)
                            return new UserIdentity(uid, gid, groups.AsSpan(0, found).ToArray());
                    if (found <= count || count > 65536) return new UserIdentity(uid, gid, Array.Empty<uint>());
                    count = found;
                }
            }
        }

        /// <summary>The connected process's security label (SO_PEERSEC); null without a security module labelling it</summary>
        public static string SecurityLabel(Socket socket)
        {
            var label = new byte[4096];
            try
            {
                var length = socket.GetRawSocketOption(SOL_SOCKET, SO_PEERSEC, label);
                return length <= 0 ? null : System.Text.Encoding.UTF8.GetString(label, 0, length).TrimEnd('\0', '\n', ' ');
            }
            catch (SocketException) { return null; } //ENOPROTOOPT: none
        }

        /// <summary>
        /// Why the connected process is confined, so that it is not served; null when it is not. The
        /// access check switches only the user and groups, so its answers - and every file
        /// for an administrator - are right only for a process nothing else restricts: the
        /// user's ordinary programs. Each way a process - any of its threads - can be
        /// restricted beyond its user shows: a security module's label (IsUnconfined),
        /// no_new_privs - without which an unprivileged process can apply neither Landlock
        /// nor a seccomp filter, and which bubblewrap and Flatpak set - and other mounts or
        /// another root than init's: its own mount namespace (a sandbox or container that
        /// hides or replaces folders) or a chroot. Checked once, when it connects: the
        /// connection is then the client's, like any file it opened - Landlock too restricts
        /// connecting, not a connection made before. Left out: a privileged process
        /// restricting itself with Landlock or seccomp, which the kernel lets do without
        /// no_new_privs.
        /// </summary>
        /// <param name="host">The process whose mounts and root are the system's (init)</param>
        internal static string ConfinementOf(Socket socket, string host = "/proc/1")
        {
            var label = SecurityLabel(socket);
            if (!IsUnconfined(label)) return $"its security label is {label}";
            //The process is checked through /proc by its number: a pidfd (Linux 6.5+) keeps
            //it, so that a process that ends meanwhile cannot lend its number to another
            var pidfd = -1;
            try
            {
                var fd = new byte[4];
                if (socket.GetRawSocketOption(SOL_SOCKET, SO_PEERPIDFD, fd) == 4) pidfd = BitConverter.ToInt32(fd);
            }
            catch (SocketException) { } //Older kernel: only the number, as connected
            try
            {
                var pid = pidfd >= 0 ? PidOf(pidfd) : PeerPid(socket);
                if (pid <= 0) return "it ended";
                var confinement = ConfinementOfProcess(pid, host);
                if (pidfd >= 0 && PidOf(pidfd) != pid) return "it ended"; //Its number may be another's now
                return confinement;
            }
            finally
            {
                if (pidfd >= 0) close(pidfd);
            }
        }

        /// <summary>
        /// A process's confinement apart from its label - see ConfinementOf. Every thread
        /// counts: Landlock, a seccomp filter, no_new_privs, a mount namespace and a root can
        /// each be one thread's own, and any thread may be the one that connected. A thread
        /// that ended (a zombie leader included) is passed over - it runs nothing any more.
        /// </summary>
        internal static string ConfinementOfProcess(int pid, string host = "/proc/1")
        {
            const string cannot = "its process cannot be inspected";
            string[] threads;
            try { threads = Directory.GetDirectories($"/proc/{pid}/task"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return cannot; }
            var hostRoot = RootMountsOf(host);
            var running = 0;
            foreach (var thread in threads)
            {
                string status;
                try { status = File.ReadAllText(thread + "/status"); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (Directory.Exists(thread)) return cannot;
                    continue; //Ended meanwhile
                }
                if (Field(status, "State:") is ['Z' or 'X', ..]) continue;
                if (Field(status, "NoNewPrivs:") != "0") return "it has no_new_privs (Landlock, seccomp or a sandbox)";
                //The socket's label is the connecting thread's - an AppArmor hat or an SELinux
                //setcon is another thread's alone
                string label;
                try { label = File.ReadAllText(thread + "/attr/current").TrimEnd('\0', '\n', ' '); }
                catch (UnauthorizedAccessException) { return cannot; }
                catch (IOException) { label = null; } //No security module labels processes - or it ended
                if (!IsUnconfined(label)) return $"its security label is {label}";
                var root = RootMountsOf(thread);
                if (root == null || root != hostRoot)
                {
                    if (!Directory.Exists(thread)) continue;
                    return "it sees other mounts or another root (a sandbox, container or chroot)";
                }
                running++;
            }
            return running == 0 ? "it ended" : null;
        }

        /// <summary>A /proc status line's value; null without that line</summary>
        static string Field(string status, string name)
        {
            foreach (var line in status.Split('\n'))
                if (line.StartsWith(name, StringComparison.Ordinal)) return line.Substring(name.Length).Trim();
            return null;
        }

        /// <summary>
        /// The ids of the mounts a process sees on "/". A mount namespace has its own copy of
        /// every mount, with its own id, and a chroot sees "/" on another mount or none of
        /// these. Unlike /proc/PID/ns and /proc/PID/root, mountinfo needs no ptrace right -
        /// which the service does not have (CAP_SYS_PTRACE).
        /// </summary>
        static string RootMountsOf(string process)
        {
            try
            {
                var ids = new List<string>();
                foreach (var line in File.ReadLines(process + "/mountinfo"))
                {
                    var fields = line.Split(' ');
                    if (fields.Length > 4 && fields[4] == "/") ids.Add(fields[0]); //Mount id, then (5th) mount point
                }
                return ids.Count == 0 ? null : string.Join(",", ids);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }

        /// <summary>The process a pidfd stands for; -1 once it ended</summary>
        static int PidOf(int pidfd)
        {
            try
            {
                foreach (var line in File.ReadLines($"/proc/self/fdinfo/{pidfd}"))
                    if (line.StartsWith("Pid:", StringComparison.Ordinal))
                        return int.TryParse(line.AsSpan(4).Trim(), out var pid) ? pid : -1;
            }
            catch (IOException) { }
            return -1;
        }

        static int PeerPid(Socket socket)
        {
            var credentials = new byte[12];
            return socket.GetRawSocketOption(SOL_SOCKET, SO_PEERCRED, credentials) == 12 ? BitConverter.ToInt32(credentials, 0) : -1;
        }

        /// <summary>
        /// A security module's label of an ordinary program: SELinux unconfined_t, AppArmor
        /// unconfined, or none at all. Any other - a sandboxed program, a confined user - has
        /// a policy that is checked for the service, which it lets do anything, not for it.
        /// </summary>
        internal static bool IsUnconfined(string label)
        {
            if (string.IsNullOrEmpty(label)) return true; //No security module labels processes
            //AppArmor: no profile, or one that only names the program ("code (unconfined)")
            if (label == "unconfined" || label.EndsWith(" (unconfined)", StringComparison.Ordinal)) return true;
            //SELinux: user:role:type:level
            var parts = label.Split(':');
            return parts.Length >= 3 && parts[2] == "unconfined_t";
        }

        /// <summary>
        /// An administrator - root, or a member of the group that may use sudo (wheel on
        /// Fedora and Arch, sudo on Debian and Ubuntu, admin on older Ubuntu) - may see every file
        /// </summary>
        public static bool IsAdmin(UserIdentity user)
        {
            if (user.Uid == 0) return true;
            lock (nss)
                foreach (var name in new[] { "wheel", "sudo", "admin" })
                {
                    var group = getgrnam(name);
                    //struct group: gr_name, gr_passwd, then gr_gid
                    if (group != IntPtr.Zero && user.IsMember((uint)Marshal.ReadInt32(group, 2 * IntPtr.Size)))
                        return true;
                }
            return false;
        }
    }
}
