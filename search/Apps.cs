using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using static search.Models.SearchModel;

namespace search
{
    public static class Apps
    {
        static readonly ConcurrentDictionary<string, string> paths = new(StringComparer.OrdinalIgnoreCase);
        static int generation;

        /// <summary>
        /// The first installed program of the names. Windows looks the .exe names up in the
        /// file index; Linux searches the PATH (of the host when sandboxed) for the names
        /// without an extension - so one list can name both.
        /// </summary>
        public static string GetPath(params string[] names) => GetPath(name => FindExe(name), names);

        internal static string GetPath(Func<string, string> find, params string[] names)
        {
            names = ForPlatform(names);
            if (names.Length == 0) return null;
            var key = string.Join("\"", names);
            if (paths.TryGetValue(key, out var cached)) return cached;
            var started = Volatile.Read(ref generation);
            string executable = null;
            foreach (var name in names)
            {
                executable = OperatingSystem.IsWindows() ? find(name) : Shell.Which(name);
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

        /// <summary>Windows programs are the ".exe" names, Linux programs the others</summary>
        internal static string[] ForPlatform(string[] names)
            => names.Where(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == OperatingSystem.IsWindows()).ToArray();

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
        public const string Calculator = "calculator.html";

        /// <summary>
        /// Get text viewer path - built-in Log explorer (needs a web view or a browser)
        /// </summary>
        public static string TextViever => WebAppWindow.IsAvailable ? LogExplorer : Notepad;

        /// <summary>
        /// Get application for editing
        /// </summary>
        public static string TextEditor => GetPath("notepad++.exe", "sublime_text.exe", "kate.exe", "geany.exe",
            "gnome-text-editor", "gedit", "kate", "kwrite", "mousepad", "xed", "pluma", "featherpad", "geany", "code") ?? Notepad;

        // Never null: before the system drive is indexed the shell still resolves the name.
        // A null application would open (run) the file itself instead of editing it.
        public static string Notepad => OperatingSystem.IsWindows()
            ? GetPath("notepad.exe") ?? "notepad"
            : GetPath("gnome-text-editor", "gedit", "kwrite", "kate", "mousepad", "xed", "pluma", "featherpad") ?? "xdg-open";

        public static string Powershell => OperatingSystem.IsWindows() ? "powershell" : GetPath("pwsh") ?? "pwsh";

        public static string Cmd => "cmd";

        public static string Explorer => OperatingSystem.IsWindows()
            ? "explorer"
            : GetPath("nautilus", "dolphin", "nemo", "thunar", "caja", "pcmanfm-qt", "pcmanfm") ?? "xdg-open";

        public static string WebBrowser => GetPath("firefox.exe", "chrome.exe", "msedge.exe", "brave.exe", "vivaldi.exe", "opera.exe", "chromium.exe", "waterfox.exe", "librewolf.exe", "iexplore.exe", "safari.exe",
            "firefox", "google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "brave-browser", "microsoft-edge", "vivaldi", "opera", "librewolf", "epiphany");

        public static string GhostPcl => GetPath("gpcl6win64.exe", "gpcl6win32.exe", "gpcl6");

        public static string GhostScript => GetPath("gswin64c.exe", "gswin32c.exe", "gs");

        public static string GhostXps => GetPath("gxpswin64.exe", "gxpswin32.exe", "gxps");

        public static string SevenZip => GetPath("7z.exe", "7zz.exe", "7z", "7zz", "7za");

        /// <summary>
        /// Command line for displaying poscript file
        /// </summary>
        public static string DisplayPostScript => GhostScript == null ? TextViever : $"{GhostScript}\0-sDEVICE=display -r72";

        /// <summary>
        /// Get application for viewing prn files
        /// </summary>
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
                ".pdf" => GetPath("SumatraPDF.exe", "AcroRd32.exe", "Acrobat.exe", "FoxitPDFReader.exe", "FoxitReader.exe", "PDFXEdit.exe", "okular.exe",
                    "evince", "okular", "atril", "xreader", "papers", "zathura", "mupdf") ?? WebBrowser ?? TextViever,
                ".htm" or ".html" or ".xml" or ".json" => WebBrowser ?? TextViever,
                _ => TextViever
            };
        }
    }
}
