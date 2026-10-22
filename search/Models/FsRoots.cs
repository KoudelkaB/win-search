using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace search.Models
{
    /// <summary>
    /// One indexable file-system root - a drive ("C:\") on Windows, a mount point of a real
    /// file system ("/", "/home", "/media/usb") on Linux. Each root is scanned, watched and
    /// published on its own, exactly like a Windows drive.
    /// </summary>
    internal sealed record FsRoot(string Path, string Key, string Format, DriveType Type, string Source);

    /// <summary>
    /// Enumerates the roots and tells which one a path belongs to. On Windows that is the
    /// drive letter. On Linux mount points nest ("/home" inside "/"), so a path belongs to
    /// the deepest mount point above it; a walk of "/" stops at "/home" exactly as a walk
    /// of C: never enters D:. Pseudo file systems (proc, sysfs, tmpfs, squashfs snaps,
    /// container overlays, portals) are never roots.
    /// </summary>
    internal static class FsRoots
    {
        static readonly HashSet<string> PseudoFormats = new(StringComparer.OrdinalIgnoreCase)
        {
            "proc", "sysfs", "devtmpfs", "devpts", "tmpfs", "ramfs", "cgroup", "cgroup2", "pstore",
            "bpf", "debugfs", "tracefs", "securityfs", "configfs", "fusectl", "mqueue", "hugetlbfs",
            "autofs", "binfmt_misc", "efivarfs", "rpc_pipefs", "nsfs", "overlay", "squashfs",
            "selinuxfs", "nfsd", "devfs", "composefs", "fuse.portal", "fuse.gvfsd-fuse",
            "fuse.lxcfs", "fuse.snapfuse", "fuse.xdg-document-portal", "rootfs", "zfs_snapshots"
        };

        static readonly HashSet<string> NetworkFormats = new(StringComparer.OrdinalIgnoreCase)
        {
            "nfs", "nfs4", "cifs", "smb3", "smbfs", "ncpfs", "9p", "afs", "ceph", "glusterfs",
            "fuse.sshfs", "fuse.rclone", "davfs", "fuse.davfs", "fuse.s3fs", "fuse.gcsfuse", "lustre"
        };

        /// <summary>Formats indexed by default on Linux (local, real files)</summary>
        static readonly HashSet<string> LocalFormats = new(StringComparer.OrdinalIgnoreCase)
        {
            "ext2", "ext3", "ext4", "btrfs", "xfs", "zfs", "f2fs", "bcachefs", "jfs", "reiserfs",
            "ntfs", "ntfs3", "fuseblk", "exfat", "vfat", "hfsplus", "apfs", "nilfs2"
        };

        /// <summary>Mount points that are never roots, nor anything below them</summary>
        static readonly string[] SystemTrees = { "/proc", "/sys", "/dev", "/snap", "/var/lib/snapd",
            "/var/lib/docker", "/var/lib/containers", "/var/lib/flatpak/runtime", "/run/user", "/run/snapd",
            "/run/credentials", "/run/lock", "/run/netns", "/run/docker", "/run/host", "/tmp/.mount_" };

        static volatile Snapshot current;
        static long loadedAt;

        sealed class Snapshot
        {
            public FsRoot[] Roots = Array.Empty<FsRoot>();
            /// <summary>Roots sorted longest first - the first prefix match is the owner</summary>
            public string[] ByLength = Array.Empty<string>();
            /// <summary>Every mount point (pseudo ones included) - a walk never crosses them</summary>
            public HashSet<string> MountPoints = new(StringComparer.Ordinal);
        }

        /// <summary>Test hook: replaces the mount table (Linux format of /proc/self/mountinfo)</summary>
        internal static Func<string> ReadMountInfo = () => File.ReadAllText("/proc/self/mountinfo");

        /// <summary>
        /// Every current root. Re-reads the mount table (cheap) so drives plugged in since
        /// the last call appear.
        /// </summary>
        public static IReadOnlyList<FsRoot> All()
        {
            if (OperatingSystem.IsWindows())
                return DriveInfo.GetDrives().Select(d => WindowsRoot(d)).ToArray();
            return Load(force: true).Roots;
        }

        static FsRoot WindowsRoot(DriveInfo d)
        {
            var root = d.RootDirectory.FullName;
            return new FsRoot(root, root.TrimEnd(Path.DirectorySeparatorChar), null, d.DriveType, null);
        }

        public static IEnumerable<string> AllPaths() => All().Select(r => r.Path);

        static Snapshot Load(bool force = false)
        {
            var snapshot = current;
            var now = Environment.TickCount64;
            if (snapshot != null && !force && now - Interlocked.Read(ref loadedAt) < 5000) return snapshot;
            try { snapshot = Parse(ReadMountInfo()); }
            catch { snapshot ??= new Snapshot(); }
            current = snapshot;
            Interlocked.Exchange(ref loadedAt, now);
            return snapshot;
        }

        /// <summary>Forget the cached mount table (tests)</summary>
        internal static void Reset() => current = null;

        /// <summary>The roots and all mount points of a mount table (tests)</summary>
        internal static (IReadOnlyList<FsRoot> Roots, IReadOnlyCollection<string> MountPoints) ParseMountInfo(string mountInfo)
        {
            var snapshot = Parse(mountInfo);
            return (snapshot.Roots, snapshot.MountPoints);
        }

        /// <summary>
        /// The root owning a Linux path: the longest root that is the path or its ancestor;
        /// "/" when none is (rootsByLength must be sorted longest first)
        /// </summary>
        internal static string OwnerRoot(string path, IEnumerable<string> rootsByLength)
        {
            foreach (var root in rootsByLength)
                if (root == "/" || path.Length == root.Length && string.Equals(path, root, StringComparison.Ordinal)
                    || path.Length > root.Length && path[root.Length] == '/'
                        && path.StartsWith(root, StringComparison.Ordinal))
                    return root;
            return "/";
        }

        static Snapshot Parse(string mountInfo)
        {
            var result = new Snapshot();
            var roots = new List<FsRoot>();
            var seenFileSystems = new HashSet<string>(StringComparer.Ordinal);
            var entries = new List<(string MountPoint, string Device, string FsRoot, string Format, string Source)>();
            foreach (var line in mountInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.Split(' ');
                var dash = Array.IndexOf(fields, "-");
                if (fields.Length < 7 || dash < 6 || dash + 2 >= fields.Length) continue;
                var mountPoint = Unescape(fields[4]);
                entries.Add((mountPoint, fields[2], Unescape(fields[3]), fields[dash + 1], Unescape(fields[dash + 2])));
                result.MountPoints.Add(mountPoint);
            }
            //Shorter mount points first: of two mounts showing the same file system the
            //outer one stays a root and the inner duplicate (a bind mount) is skipped
            foreach (var e in entries.OrderBy(e => e.MountPoint.Length).ThenBy(e => e.MountPoint, StringComparer.Ordinal))
            {
                if (PseudoFormats.Contains(e.Format) || IsSystemPath(e.MountPoint)) continue;
                if (e.Format.StartsWith("fuse.", StringComparison.Ordinal) && !NetworkFormats.Contains(e.Format)
                    && !LocalFormats.Contains(e.Format) && !e.Format.Equals("fuse.ntfs-3g", StringComparison.Ordinal))
                    continue; //Virtual FUSE views (gvfs, portals, appimage mounts...)
                //The same file system (device + subtree) mounted twice is one root
                if (!seenFileSystems.Add(e.Device + "\u0000" + e.FsRoot)) continue;
                roots.Add(new FsRoot(e.MountPoint, e.MountPoint, e.Format, TypeOf(e.MountPoint, e.Format), e.Source));
            }
            result.Roots = roots.ToArray();
            result.ByLength = roots.Select(r => r.Path).OrderByDescending(p => p.Length).ToArray();
            return result;
        }

        static DriveType TypeOf(string mountPoint, string format)
        {
            if (NetworkFormats.Contains(format)) return DriveType.Network;
            if (format is "iso9660" or "udf") return DriveType.CDRom;
            if (mountPoint.StartsWith("/media/", StringComparison.Ordinal)
                || mountPoint.StartsWith("/run/media/", StringComparison.Ordinal))
                return DriveType.Removable;
            return LocalFormats.Contains(format) ? DriveType.Fixed : DriveType.Unknown;
        }

        static bool IsSystemPath(string mountPoint)
        {
            foreach (var tree in SystemTrees)
                if (mountPoint.Equals(tree, StringComparison.Ordinal)
                    || mountPoint.StartsWith(tree + "/", StringComparison.Ordinal)
                    || tree.EndsWith('_') && mountPoint.StartsWith(tree, StringComparison.Ordinal))
                    return true;
            return false;
        }

        /// <summary>mountinfo escapes space, tab, newline and backslash as octal (\040)</summary>
        static string Unescape(string value)
        {
            if (value.IndexOf('\\') < 0) return value;
            var sb = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] == '\\' && IsOctal(value, i + 1))
                {
                    sb.Append((char)Convert.ToInt32(value.Substring(i + 1, 3), 8));
                    i += 3;
                }
                else sb.Append(value[i]);
            }
            return sb.ToString();
        }

        static bool IsOctal(string s, int start)
        {
            if (start + 3 > s.Length) return false;
            for (var i = start; i < start + 3; i++)
                if (s[i] < '0' || s[i] > '7') return false;
            return true;
        }

        /// <summary>
        /// Root owning the path: the drive on Windows, the deepest root above it on Linux.
        /// A path outside every root (a pseudo file system) maps to "/".
        /// </summary>
        public static string RootOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (OperatingSystem.IsWindows())
            {
                try { return Path.GetPathRoot(path) is { Length: > 0 } r ? r : null; }
                catch { return null; }
            }
            if (path[0] != '/') return null;
            return OwnerRoot(path, Load().ByLength);
        }

        /// <summary>
        /// The mount point actually holding the path - pseudo file systems included (a
        /// tmpfs /tmp is its own device). Decides whether a rename can move the item or a
        /// copy is needed, and which trash directory belongs to it.
        /// </summary>
        public static string MountPointOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (OperatingSystem.IsWindows())
            {
                try { return Path.GetPathRoot(path); }
                catch { return null; }
            }
            string best = "/";
            foreach (var mountPoint in Load().MountPoints)
                if (mountPoint.Length > best.Length
                    && (path.Length == mountPoint.Length || path.Length > mountPoint.Length && path[mountPoint.Length] == '/')
                    && path.StartsWith(mountPoint, StringComparison.Ordinal))
                    best = mountPoint;
            return best;
        }

        /// <summary>The settings key of a root: "C:" on Windows, the mount point on Linux</summary>
        public static string KeyOf(string root)
        {
            if (string.IsNullOrEmpty(root)) return root ?? "";
            if (OperatingSystem.IsWindows()) return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return PathText.TrimEndSeparators(root);
        }

        /// <summary>The root path for a settings key ("C:" => "C:\")</summary>
        public static string RootForKey(string key)
            => OperatingSystem.IsWindows() && key?.Length == 2 && key[1] == ':' ? key + "\\" : key;

        /// <summary>True when path is itself one of the roots</summary>
        public static bool IsRoot(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (OperatingSystem.IsWindows())
                return string.IsNullOrEmpty(Path.GetDirectoryName(path));
            var normalized = PathText.TrimEndSeparators(path);
            return normalized == "/" || Load().ByLength.Contains(normalized, StringComparer.Ordinal);
        }

        /// <summary>
        /// True when a directory met during the walk of root is the mount point of another
        /// file system - it belongs to that root (or to none, for pseudo file systems)
        /// </summary>
        public static bool IsForeignMountPoint(string directory, string root)
        {
            if (OperatingSystem.IsWindows() || string.IsNullOrEmpty(directory)) return false;
            var normalized = PathText.TrimEndSeparators(directory);
            return !string.Equals(normalized, PathText.TrimEndSeparators(root), StringComparison.Ordinal)
                && Load().MountPoints.Contains(normalized);
        }

        public static FsRoot Find(string root)
        {
            if (string.IsNullOrEmpty(root)) return null;
            if (OperatingSystem.IsWindows())
            {
                try { return WindowsRoot(new DriveInfo(root)); }
                catch { return null; }
            }
            var normalized = PathText.TrimEndSeparators(root);
            return Load().Roots.FirstOrDefault(r => string.Equals(r.Path, normalized, StringComparison.Ordinal));
        }

        /// <summary>
        /// Drive type of a root without blocking on it: Windows asks the drive (a local
        /// query), Linux reads it from the mount table
        /// </summary>
        public static DriveType TypeOfRoot(string root)
        {
            if (OperatingSystem.IsWindows())
            {
                try { return new DriveInfo(root).DriveType; }
                catch { return DriveType.Unknown; }
            }
            return Find(root)?.Type ?? DriveType.Unknown;
        }

        /// <summary>File system name; Linux reads the mount table instead of statfs</summary>
        public static string FormatOf(string root)
        {
            if (OperatingSystem.IsWindows())
            {
                try { return new DriveInfo(root).DriveFormat; }
                catch { return null; }
            }
            return Find(root)?.Format;
        }

        /// <summary>
        /// Indexed unless the user decided otherwise. Windows: local NTFS (the MFT makes it
        /// instant). Linux: local real file systems, but not the boot partitions.
        /// </summary>
        public static bool DefaultEnabled(string root, DriveType type, string format)
        {
            if (type == DriveType.Network) return false;
            if (OperatingSystem.IsWindows())
                return string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase);
            var normalized = PathText.TrimEndSeparators(root ?? "");
            if (normalized is "/boot" or "/efi" || normalized.StartsWith("/boot/", StringComparison.Ordinal))
                return false;
            return format != null && LocalFormats.Contains(format) && type != DriveType.CDRom;
        }
    }
}
