using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using static search.Models.SearchModel;

namespace search
{
    public static class Apps
    {
        static readonly ConcurrentDictionary<string, string> paths = new(StringComparer.OrdinalIgnoreCase);
        static int generation;

        public static string GetPath(params string[] names) => GetPath(name => FindExe(name), names);

        internal static string GetPath(Func<string, string> find, params string[] names)
        {
            var key = string.Join("\"", names);
            if (paths.TryGetValue(key, out var cached)) return cached;
            var started = Volatile.Read(ref generation);
            string executable = null;
            foreach (var name in names)
            {
                executable = find(name);
                if (!string.IsNullOrWhiteSpace(executable)) break;
                executable = null;
            }
            // Misses are cached too: every index publication clears the cache, so an executable
            // appearing as another drive finishes loading is found then. A lookup that raced
            // a publication may have searched the previous index and is not stored.
            lock (paths)
                if (started == generation) paths[key] = executable;
            return executable;
        }

        // Paths come exclusively from the executable index. Re-resolve cached paths after a reload.
        internal static void RefreshInstalledApplications()
        {
            lock (paths)
            {
                generation++;
                paths.Clear();
            }
        }

        /// <summary>
        /// Built-in HTML apps (WebApps next to the exe). Open() shows these in a WebAppWindow
        /// instead of starting a process.
        /// </summary>
        public const string LogExplorer = "LogExplorer.html";
        public const string HexEditor = "HexEditorAndComparer.html";

        /// <summary>
        /// Get text viever path  - built-in Log explorer (needs the WebView2 runtime)
        /// </summary>
        /// <returns></returns>
        public static string TextViever => WebAppWindow.IsAvailable ? LogExplorer : Notepad;

        /// <summary>
        /// Get application for editing
        /// </summary>
        /// <returns></returns>
        public static string TextEditor => GetPath("notepad++.exe", "sublime_text.exe", "kate.exe", "geany.exe") ?? Notepad;

        // Never null: before the system drive is indexed the shell still resolves the name.
        // A null application would open (run) the file itself instead of editing it.
        public static string Notepad => GetPath("notepad.exe") ?? "notepad";

        public static string Powershell => "powershell";

        public static string Cmd => "cmd";

        public static string Explorer => "explorer";

        public static string WebBrowser => GetPath("firefox.exe", "chrome.exe", "msedge.exe", "brave.exe", "vivaldi.exe", "opera.exe", "chromium.exe", "waterfox.exe", "librewolf.exe", "iexplore.exe", "safari.exe");

        /// <summary>
        /// 
        /// </summary>
        public static string GhostPcl => GetPath("gpcl6win64.exe", "gpcl6win32.exe");

        public static string GhostScript => GetPath("gswin64c.exe", "gswin32c.exe");

        public static string GhostXps => GetPath("gxpswin64.exe", "gxpswin32.exe");

        public static string SevenZip => GetPath("7z.exe", "7zz.exe");

        /// <summary>
        /// Command line for displaying poscript file
        /// </summary>
        public static string DisplayPostScript => GhostScript == null ? TextViever : $"{GhostScript}\0-sDEVICE=display -r72";

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
                "PDF" => WebBrowser ?? TextViever,
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
                ".pdf" => GetPath("SumatraPDF.exe", "AcroRd32.exe", "Acrobat.exe", "FoxitPDFReader.exe", "FoxitReader.exe", "PDFXEdit.exe", "okular.exe") ?? WebBrowser ?? TextViever,
                ".htm" or ".html" or ".xml" or ".json" => WebBrowser ?? TextViever,
                _ => TextViever
            };
        }
    }
}
