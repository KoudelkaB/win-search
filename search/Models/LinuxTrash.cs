using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace search.Models
{
    /// <summary>
    /// The freedesktop.org trash (https://specifications.freedesktop.org/trash-spec/) - the
    /// one Nautilus, Dolphin, Thunar and "gio trash" share, so items moved there can be
    /// restored from the desktop's own Trash. A file on the home file system goes to
    /// ~/.local/share/Trash; anything on another mount goes to that mount's
    /// $topdir/.Trash/$uid (when the administrator prepared it) or $topdir/.Trash-$uid.
    /// The item is always renamed, never copied: moving gigabytes across devices to
    /// "delete" them is exactly what the spec forbids.
    /// </summary>
    internal static class LinuxTrash
    {
        [DllImport("libc", EntryPoint = "getuid")]
        static extern uint GetUid();

        /// <summary>Override for tests: the home trash directory</summary>
        internal static Func<string> HomeTrashOverride;

        public static string HomeTrash()
        {
            if (HomeTrashOverride != null) return HomeTrashOverride();
            //Inside Flatpak XDG_DATA_HOME points into the sandbox; the desktop's trash is the host one
            var dataHome = Environment.GetEnvironmentVariable("FLATPAK_ID") == null
                ? Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                : null;
            if (string.IsNullOrEmpty(dataHome))
                dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(dataHome, "Trash");
        }

        public static void Move(string path)
        {
            var full = Path.GetFullPath(path).TrimEnd('/');
            if (full.Length == 0 || !File.Exists(full) && !Directory.Exists(full) && !IsDanglingLink(full))
                throw new FileNotFoundException($"'{path}' does not exist.", path);

            var (trash, topDir) = TrashFor(full);
            var files = Path.Combine(trash, "files");
            var info = Path.Combine(trash, "info");
            Directory.CreateDirectory(files);
            Directory.CreateDirectory(info);
            TrySetPrivate(trash);

            var name = Path.GetFileName(full);
            var original = topDir == null ? full : Path.GetRelativePath(topDir, full);
            for (var attempt = 1; ; attempt++)
            {
                var candidate = attempt == 1 ? name : UniqueName(name, attempt);
                var infoPath = Path.Combine(info, candidate + ".trashinfo");
                FileStream stream;
                try
                {
                    //The .trashinfo file is the reservation of the name (created exclusively)
                    stream = new FileStream(infoPath, FileMode.CreateNew, FileAccess.Write);
                }
                catch (IOException) when (File.Exists(infoPath))
                {
                    continue;
                }
                using (stream)
                {
                    var text = "[Trash Info]\n"
                        + "Path=" + Escape(original) + "\n"
                        + "DeletionDate=" + DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "\n";
                    var bytes = Encoding.UTF8.GetBytes(text);
                    stream.Write(bytes, 0, bytes.Length);
                }
                var target = Path.Combine(files, candidate);
                if (File.Exists(target) || Directory.Exists(target))
                {
                    //A leftover without its info file - keep it and try the next name
                    File.Delete(infoPath);
                    continue;
                }
                try
                {
                    if (Directory.Exists(full) && !IsLink(full)) Directory.Move(full, target);
                    else File.Move(full, target);
                }
                catch
                {
                    try { File.Delete(infoPath); } catch { }
                    throw;
                }
                return;
            }
        }

        static bool IsLink(string path)
        {
            try { return new FileInfo(path).LinkTarget != null; }
            catch { return false; }
        }

        static bool IsDanglingLink(string path) => IsLink(path);

        static string UniqueName(string name, int attempt)
        {
            var extension = Path.GetExtension(name);
            var stem = extension.Length > 0 && extension.Length < name.Length
                ? name[..^extension.Length] : name;
            if (stem == name) extension = "";
            return $"{stem}.{attempt}{extension}";
        }

        /// <summary>
        /// The trash directory for the item and the top directory its relative path is
        /// written against (null for the home trash, which records absolute paths)
        /// </summary>
        static (string Trash, string TopDir) TrashFor(string full)
        {
            var home = HomeTrash();
            var homeMount = FsRoots.MountPointOf(ExistingAncestor(home));
            var itemMount = FsRoots.MountPointOf(full);
            if (string.Equals(homeMount, itemMount, StringComparison.Ordinal))
                return (home, null);

            var uid = GetUidSafe();
            var adminTrash = Path.Combine(itemMount, ".Trash");
            try
            {
                var dir = new DirectoryInfo(adminTrash);
                //Usable only when it is a real directory with the sticky bit (spec, method 1)
                if (dir.Exists && dir.LinkTarget == null
                    && (File.GetUnixFileMode(adminTrash) & UnixFileMode.StickyBit) != 0)
                {
                    var own = Path.Combine(adminTrash, uid);
                    Directory.CreateDirectory(own);
                    return (own, itemMount);
                }
            }
            catch { }
            var userTrash = Path.Combine(itemMount, ".Trash-" + uid);
            try
            {
                Directory.CreateDirectory(userTrash);
                return (userTrash, itemMount);
            }
            catch (Exception e)
            {
                throw new IOException($"'{full}' cannot be moved to the trash: no writable trash on {itemMount} ({e.Message}).", e);
            }
        }

        static string GetUidSafe()
        {
            try { return GetUid().ToString(CultureInfo.InvariantCulture); }
            catch { return Environment.UserName; }
        }

        static string ExistingAncestor(string path)
        {
            var current = path;
            while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
                current = Path.GetDirectoryName(current);
            return string.IsNullOrEmpty(current) ? "/" : current;
        }

        static void TrySetPrivate(string directory)
        {
            try
            {
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch { }
        }

        /// <summary>Percent-encode like a URL path, keeping '/' (spec: RFC 2396 escaping)</summary>
        internal static string Escape(string path)
        {
            var sb = new StringBuilder(path.Length);
            foreach (var b in Encoding.UTF8.GetBytes(path))
            {
                var c = (char)b;
                if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
                    || c is '/' or '-' or '_' or '.' or '~' or '!' or '*' or '\'' or '(' or ')')
                    sb.Append(c);
                else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
