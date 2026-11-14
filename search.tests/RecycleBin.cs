using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using search.Models;

namespace search.Tests
{
    /// <summary>
    /// A test's own items in the Recycle Bin (the trash on Linux): found by their original
    /// path under the test's folder and removed for good. Nothing else in the bin is touched.
    /// </summary>
    internal static class RecycleBin
    {
        /// <summary>
        /// Remove the items recycled from under the folder; how many there were, null when this
        /// process cannot read the bin (a sandbox)
        /// </summary>
        public static int? Purge(string root)
        {
            var folder = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return OperatingSystem.IsWindows() ? PurgeWindows(folder) : PurgeTrash(folder);
        }

        /// <summary>
        /// $Recycle.Bin\{user SID} of the drive: each item is a $R… file or folder with a $I…
        /// file beside it that records where the item came from
        /// </summary>
        static int? PurgeWindows(string folder)
        {
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            if (sid == null) return null;
            var bin = Path.Combine(Path.GetPathRoot(folder), "$Recycle.Bin", sid);
            //No folder: nothing was recycled on this drive - or a sandbox hides the folder
            //(SeesWhatIsRecycled tells which). Directory.Exists would say false for both.
            string[] infos;
            try { infos = Directory.GetFiles(bin, "$I*"); }
            catch (DirectoryNotFoundException) { return 0; }
            catch (UnauthorizedAccessException) { return null; }
            var removed = 0;
            foreach (var info in infos)
            {
                string original;
                try { original = OriginalPath(File.ReadAllBytes(info)); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if (original == null || !original.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) continue;
                var item = Path.Combine(bin, "$R" + Path.GetFileName(info)[2..]);
                if (Directory.Exists(item)) Directory.Delete(item, recursive: true);
                else if (File.Exists(item)) File.Delete(item);
                File.Delete(info);
                removed++;
            }
            return removed;
        }

        /// <summary>
        /// Whether this process finds in the bin what is recycled: a file of its own goes there
        /// through the shell, independently of the app's code, and is looked for (and removed).
        /// False where a sandbox hides the bin - the file then stays in it, named for what it
        /// is - and on a drive whose bin deletes for good. The Linux trash of a test is in its
        /// own folder: always seen.
        /// </summary>
        public static bool SeesWhatIsRecycled(string root)
        {
            if (!OperatingSystem.IsWindows()) return true;
            var folder = Directory.CreateDirectory(Path.Combine(root, "bin-check")).FullName;
            var probe = Path.Combine(folder, "win-search-tests-recycle-bin-check.txt");
            File.WriteAllText(probe, "A test checked whether it sees the Recycle Bin. This file may be deleted.");
            var operation = new FileOperation
            {
                Function = FO_DELETE,
                From = probe + "\0", //A list ended by two nulls: the marshaller adds the second
                Flags = FOF_SILENT | FOF_NOCONFIRMATION | FOF_ALLOWUNDO | FOF_NOERRORUI
            };
            if (SHFileOperation(ref operation) != 0 || File.Exists(probe)) return false;
            return Purge(folder) == 1;
        }

        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct FileOperation
        {
            public IntPtr Window;
            public uint Function;
            public string From;
            public string To;
            public ushort Flags;
            public bool AnyAborted;
            public IntPtr NameMappings;
            public string ProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHFileOperation(ref FileOperation operation);

        /// <summary>
        /// The original path in a $I file: Windows 10 writes version 2 (the path's length,
        /// then the path), older versions a fixed 260-character field
        /// </summary>
        static string OriginalPath(byte[] info)
        {
            if (info.Length < 28) return null;
            if (BitConverter.ToInt64(info, 0) == 2)
            {
                var chars = BitConverter.ToInt32(info, 24);
                return Encoding.Unicode.GetString(info, 28, Math.Min(chars * 2, info.Length - 28)).TrimEnd('\0');
            }
            return Encoding.Unicode.GetString(info, 24, Math.Min(520, info.Length - 24)).Split('\0')[0];
        }

        /// <summary>
        /// The trash the test moved its items to (see UiScene.Recycles): files/ holds the items,
        /// info/*.trashinfo where each came from
        /// </summary>
        static int? PurgeTrash(string folder)
        {
            var trash = LinuxTrash.HomeTrash();
            var info = Path.Combine(trash, "info");
            if (!Directory.Exists(info)) return 0;
            var removed = 0;
            foreach (var entry in Directory.EnumerateFiles(info, "*.trashinfo"))
            {
                var line = File.ReadLines(entry).FirstOrDefault(l => l.StartsWith("Path=", StringComparison.Ordinal));
                if (line == null) continue;
                var original = Uri.UnescapeDataString(line["Path=".Length..]);
                if (!original.StartsWith(folder, StringComparison.Ordinal)) continue;
                var item = Path.Combine(trash, "files", Path.GetFileNameWithoutExtension(entry));
                if (Directory.Exists(item)) Directory.Delete(item, recursive: true);
                else if (File.Exists(item) || new FileInfo(item).LinkTarget != null) File.Delete(item);
                File.Delete(entry);
                removed++;
            }
            return removed;
        }
    }
}
