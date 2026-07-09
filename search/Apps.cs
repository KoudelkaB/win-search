using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using static search.Models.SearchModel;

namespace search
{
    public static class Apps
    {
        static NonBlocking.ConcurrentDictionary<string, string> paths = new();
        public static string GetPath(params string[] names)
        {
            var key = string.Join("\"", names);
            return paths.GetOrAdd(key, x => FindExe(names))
            ?? (paths.TryRemove(new(key, null)) ? null : null); // Do not cache not found file => may be found later (especialy when called during fill up)
        }

        static string ProgramFilesDir => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        static string ProgramFilesDirX86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        /// <summary>
        /// Get text viever path  - default log reader
        /// </summary>
        /// <returns></returns>
        public static string TextViever => GetPath("logreader.exe") ?? "notepad";

        /// <summary>
        /// Get application for editing
        /// </summary>
        /// <returns></returns>
        public static string TextEditor => GetPath("notepad++.exe") ?? "notepad";

        public static string Powershell => "powershell";

        public static string Cmd => "cmd";

        public static string Explorer => "explorer";

        public static string VisualStudio => GetPath("devenv.exe");

        public static string VSCode => GetPath("code.exe", "Code.exe");

        public static string Antigravity => GetPath("antigravity.exe");

        public static string Chrome => GetPath("chrome.exe");

        public static string Firefox => GetPath("firefox.exe");

        public static string IExplore => GetPath("iexplore.exe");

        public static string Edge => GetPath("msedge.exe");

        public static string Opera => GetPath("opera.exe");

        public static string Safari => GetPath("safari.exe");

        public static string AdobeReader => GetPath("AcroRd32.exe", "Acrobat.exe");

        public static string WebBrowser => GetPath("firefox.exe", "chrome.exe", "msedge.exe", "iexplore.exe", "opera.exe", "safari.exe") ?? TextViever;

        /// <summary>
        /// 
        /// </summary>
        public static string GhostPcl => GetPath("gpcl6win64.exe", "gpcl6win32.exe");

        public static string GhostScript => GetPath("gswin64c.exe", "gswin32c.exe");

        public static string GhostXps => GetPath("gxpswin64.exe", "gxpswin32.exe");

        public static string SevenZip => GetPath("7z.exe", "7zz.exe");

        public static string SevenZipFileManager => GetPath("7zFM.exe");

        /// <summary>
        /// Command line for displaying poscript file
        /// </summary>
        public static string DisplayPostScript => GhostScript == null ? TextViever : $"{GhostScript}\0-sDEVICE=display -r72";

        /// <summary>
        /// Tool for comparing text files
        /// </summary>
        public static string DiffTool => GetPath("WinMergeU.exe", "KDiff3.exe", "Meld.exe");

        /// <summary>
        /// Get application for viewing prn files
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        static string PrnViewerFor(this string path)
        {
            //Switch by PJL LANGUAGE
            Span<byte> buf = stackalloc byte[1 << 12];
            using var fs = File.OpenRead(path);
            var header = buf.Slice(0, fs.Read(buf)); // Use only the bytes actually read
            var pjlLang = Encoding.ASCII.GetBytes("@PJL ENTER LANGUAGE").AsSpan();
            var lb = header.IndexOf(pjlLang);
            if (lb < 0) return null;
            lb += pjlLang.Length;
            var ln = header.Slice(lb).IndexOf((byte)'\n');
            if (ln < 0) ln = header.Length - lb; // No newline => take the rest of the header
            var language = Encoding.ASCII.GetString(header.Slice(lb, ln)).Trim('=', ' ').Trim().ToUpper();
            return language switch
            {
                "POSTSCRIPT" => DisplayPostScript,
                "PDF" => WebBrowser,
                var x when x.StartsWith("PCL", StringComparison.OrdinalIgnoreCase) && GhostPcl != null => $"{GhostPcl}\0-sDEVICE=display -r120",
                _ => null
            };
        }

        /// <summary>
        /// Path to installed application that can view this file type
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public static string ViewerFor(string path)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".prn")
            {
                try { return path.PrnViewerFor() ?? TextViever; }
                catch { return TextViever; }
            }
            return extension switch
            {
                ".ps" => DisplayPostScript,
                ".htm" or ".html" or ".xml" or ".json" or ".pdf" => WebBrowser,
                _ => TextViever
            };
        }
    }
}
