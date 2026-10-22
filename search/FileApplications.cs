using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Input;
using search.Models;

namespace search
{
    // One catalogue drives both Open with and the O/A keyboard sequences.
    internal static class FileApplications
    {
        static HashSet<string> Extensions(params string[] extensions) => new(extensions, StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> Code = Extensions("", ".txt", ".log", ".csv", ".ini", ".cfg", ".conf",
            ".json", ".jsonc", ".xml", ".yaml", ".yml", ".toml", ".md", ".rst", ".sql", ".html", ".htm",
            ".css", ".scss", ".less", ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".vue", ".svelte",
            ".php", ".phtml", ".py", ".pyw", ".ipynb", ".c", ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp", ".hxx",
            ".cs", ".csx", ".vb", ".fs", ".fsx", ".xaml", ".razor", ".cshtml", ".csproj", ".vbproj", ".fsproj",
            ".vcxproj", ".props", ".targets", ".sln", ".slnx", ".slnf", ".java", ".kt", ".kts", ".gradle",
            ".go", ".rs", ".rb", ".r", ".lua", ".pl", ".swift", ".dart", ".scala", ".clj", ".ex", ".exs",
            ".sh", ".ps1", ".psm1", ".psd1", ".bat", ".cmd", ".cmake", ".pro", ".pri", ".qrc", ".ui",
            ".prn", ".resx", ".DotSettings", ".gitignore", ".gitattributes", ".editorconfig", ".env", ".svg");
        static readonly HashSet<string> Web = Extensions(".htm", ".html", ".xml", ".json", ".pdf", ".svg", ".png",
            ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".txt");
        static readonly HashSet<string> Pdf = Extensions(".pdf");
        static readonly HashSet<string> Documents = Extensions(".pdf", ".xps", ".oxps", ".djvu", ".djv", ".epub", ".mobi", ".cbz", ".cbr");
        static readonly HashSet<string> Images = Extensions(".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".ico", ".avif");
        static readonly HashSet<string> Media = Extensions(".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".mpeg", ".mpg",
            ".m4v", ".mp3", ".flac", ".wav", ".ogg", ".m4a", ".aac", ".wma", ".opus");
        static readonly HashSet<string> Archives = Extensions(".zip", ".7z", ".rar", ".tar", ".gz", ".gzip", ".tgz",
            ".bz2", ".bzip2", ".tbz", ".tbz2", ".xz", ".txz", ".lz", ".lzip", ".iso", ".cab", ".zst");

        internal sealed class Application
        {
            public string Name { get; }
            public Key[] Keys { get; }
            public string[] ExecutableNames { get; }
            public bool Compare { get; private set; }
            public bool Directories { get; private set; }
            public bool Separate { get; private set; }
            public string Arguments { get; private set; }
            readonly HashSet<string> extensions;

            public Application(string name, Key[] keys, HashSet<string> extensions, params string[] executables)
            {
                Name = name;
                Keys = keys;
                this.extensions = extensions;
                ExecutableNames = executables;
            }

            public Application WithDirectories() { Directories = true; return this; }
            public Application Separately() { Separate = true; return this; }
            public Application AsComparison(bool directories, string arguments = null)
            {
                Compare = true;
                Directories = directories;
                Arguments = arguments;
                return this;
            }

            public string Executable => Apps.GetPath(ExecutableNames);
            public string CommandFor(string executable) => string.IsNullOrEmpty(Arguments) ? executable : executable + "\0" + Arguments;

            public bool Supports(IReadOnlyList<INode> nodes)
                => nodes.Count > 0 && (!Compare || nodes.Count == 2 && nodes.All(n => n.IsDirectory == nodes[0].IsDirectory)) &&
                   nodes.All(n => n.IsDirectory ? Directories : extensions == null || extensions.Contains(Path.GetExtension(n.Name)));

            public bool Available(IReadOnlyList<INode> nodes, Func<Application, string> resolve = null)
                => Supports(nodes) && !string.IsNullOrWhiteSpace((resolve ?? (a => a.Executable))(this));
        }

        static Application App(string name, Key key, HashSet<string> extensions, params string[] executables)
            => new(name, new[] { key }, extensions, executables);
        static Application App(string name, Key group, Key key, HashSet<string> extensions, params string[] executables)
            => new(name, new[] { group, key }, extensions, executables);

        internal static readonly Application[] All =
        {
            App("Chrome", Key.C, Web, "chrome.exe", "google-chrome", "google-chrome-stable"),
            App("Firefox", Key.F, Web, "firefox.exe", "firefox"),
            App("Edge", Key.E, Web, "msedge.exe", "microsoft-edge", "microsoft-edge-stable"),
            App("Opera", Key.O, Web, "opera.exe", "opera"),
            App("Internet Explorer", Key.I, Web, "iexplore.exe"),
            App("Adobe Reader", Key.A, Pdf, "AcroRd32.exe", "Acrobat.exe"),
            App("Visual Studio", Key.V, Code, "devenv.exe").WithDirectories(),
            App("Visual Studio Code", Key.D, Code, "Code.exe", "Code - Insiders.exe", "code", "code-insiders").WithDirectories(),
            App("Antigravity", Key.Y, Code, "antigravity.exe", "antigravity").WithDirectories(),

            App("Rider", Key.J, Key.R, Code, "rider64.exe", "rider.exe", "rider-aarch64.exe", "rider").WithDirectories(),
            App("PhpStorm", Key.J, Key.P, Code, "phpstorm64.exe", "phpstorm.exe", "phpstorm-aarch64.exe", "phpstorm").WithDirectories(),
            App("CLion", Key.J, Key.C, Code, "clion64.exe", "clion.exe", "clion-aarch64.exe", "clion").WithDirectories(),
            App("PyCharm", Key.J, Key.Y, Code, "pycharm64.exe", "pycharm.exe", "pycharm-aarch64.exe", "pycharm", "pycharm-professional", "pycharm-community").WithDirectories(),
            App("IntelliJ IDEA", Key.J, Key.I, Code, "idea64.exe", "idea.exe", "idea-aarch64.exe", "idea", "intellij-idea-ultimate", "intellij-idea-community").WithDirectories(),
            App("WebStorm", Key.J, Key.W, Code, "webstorm64.exe", "webstorm.exe", "webstorm-aarch64.exe", "webstorm").WithDirectories(),
            App("GoLand", Key.J, Key.G, Code, "goland64.exe", "goland.exe", "goland-aarch64.exe", "goland").WithDirectories(),
            App("RustRover", Key.J, Key.U, Code, "rustrover64.exe", "rustrover.exe", "rustrover-aarch64.exe", "rustrover").WithDirectories(),
            App("Android Studio", Key.J, Key.A, Code, "studio64.exe", "studio.exe", "android-studio", "studio").WithDirectories(),
            App("Code::Blocks", Key.J, Key.B, Code, "codeblocks.exe", "codeblocks"),
            App("Qt Creator", Key.J, Key.Q, Code, "qtcreator.exe", "qtcreator"),

            App("Notepad++", Key.N, Key.T, null, "notepad++.exe"),
            App("Sublime Text", Key.N, Key.S, null, "sublime_text.exe", "subl", "sublime_text").WithDirectories(),
            App("Cursor", Key.N, Key.C, Code, "Cursor.exe", "cursor").WithDirectories(),
            App("VSCodium", Key.N, Key.D, Code, "VSCodium.exe", "codium").WithDirectories(),
            App("PSPad", Key.N, Key.P, null, "PSPad.exe"),
            App("UltraEdit", Key.N, Key.U, null, "uedit64.exe", "uedit32.exe", "uex"),
            App("EditPlus", Key.N, Key.I, null, "editplus.exe"),
            App("Kate", Key.N, Key.K, null, "kate.exe", "kate"),
            App("Geany", Key.N, Key.G, null, "geany.exe", "geany"),
            App("gVim", Key.N, Key.V, null, "gvim.exe", "gvim"),
            App("Emacs", Key.N, Key.E, null, "runemacs.exe", "emacs"),

            App("Brave", Key.Q, Key.B, Web, "brave.exe", "brave-browser", "brave"),
            App("Vivaldi", Key.Q, Key.V, Web, "vivaldi.exe", "vivaldi", "vivaldi-stable"),
            App("Chromium", Key.Q, Key.C, Web, "chromium.exe", "chromium", "chromium-browser"),
            App("Waterfox", Key.Q, Key.W, Web, "waterfox.exe", "waterfox"),
            App("LibreWolf", Key.Q, Key.L, Web, "librewolf.exe", "librewolf"),

            App("SumatraPDF", Key.U, Key.S, Documents, "SumatraPDF.exe").Separately(),
            App("Foxit PDF Reader", Key.U, Key.F, Pdf, "FoxitPDFReader.exe", "FoxitReader.exe").Separately(),
            App("PDF-XChange Editor", Key.U, Key.P, Pdf, "PDFXEdit.exe").Separately(),
            App("Okular", Key.U, Key.O, Documents, "okular.exe", "okular").Separately(),
            App("IrfanView", Key.U, Key.I, Images, "i_view64.exe", "i_view32.exe").Separately(),
            App("XnView", Key.U, Key.X, Images, "xnviewmp.exe", "xnview.exe", "xnviewmp").Separately(),
            App("FastStone Image Viewer", Key.U, Key.T, Images, "FSViewer.exe").Separately(),
            App("VLC", Key.U, Key.V, Media, "vlc.exe", "vlc"),
            App("mpv", Key.U, Key.M, Media, "mpv.exe", "mpv"),
            App("MPC-HC", Key.U, Key.H, Media, "mpc-hc64.exe", "mpc-hc.exe"),

            App("7-Zip", Key.Z, Key.D7, null, "7zFM.exe").WithDirectories().Separately(),
            App("WinRAR", Key.Z, Key.R, Archives, "WinRAR.exe").WithDirectories().Separately(),
            App("PeaZip", Key.Z, Key.P, Archives, "peazip.exe", "peazip").WithDirectories().Separately(),
            App("Bandizip", Key.Z, Key.B, Archives, "Bandizip.exe").WithDirectories().Separately(),

            App("WinMerge", Key.M, Key.W, null, "WinMergeU.exe").AsComparison(true),
            App("KDiff3", Key.M, Key.K, null, "kdiff3.exe", "kdiff3").AsComparison(true),
            App("Meld", Key.M, Key.L, null, "Meld.exe", "meld").AsComparison(true),
            App("Beyond Compare", Key.M, Key.B, null, "BCompare.exe", "bcompare").AsComparison(true),
            App("DiffMerge", Key.M, Key.D, null, "sgdm.exe", "diffmerge").AsComparison(true),
            // Named apart from the plain entry: both can be offered for the same two files.
            App("Visual Studio Code (diff)", Key.M, Key.V, null, "Code.exe", "Code - Insiders.exe", "code", "code-insiders").AsComparison(false, "--diff")
        };

        internal static readonly (Key Key, string Name)[] Groups =
        {
            (Key.J, "Development environments"), (Key.N, "Text editors"), (Key.Q, "More web browsers"),
            (Key.U, "Viewers"), (Key.Z, "Archive managers"), (Key.M, "Diff / merge tools")
        };

        // The numeric keypad types the same digit as the main block.
        static Key Digit(Key key) => key is >= Key.NumPad0 and <= Key.NumPad9 ? Key.D0 + (key - Key.NumPad0) : key;

        internal static Application Find(IEnumerable<Key> keys)
        {
            var sequence = keys.Select(Digit).ToArray();
            return All.FirstOrDefault(a => a.Keys.SequenceEqual(sequence));
        }
        internal static Application Find(IEnumerable<Key> keys, IReadOnlyList<INode> nodes, Func<Application, string> resolve = null)
        {
            var sequence = keys.ToArray();
            var application = Find(sequence);
            if (application != null) return application;
            // A single-key application is chosen by its key alone: a stray extra key must not
            // fall through to the system default application.
            if (sequence.Length > 1) return All.FirstOrDefault(a => a.Keys.Length == 1 && a.Keys[0] == sequence[0]);
            if (sequence.Length != 1 || !Groups.Any(g => g.Key == sequence[0])) return null;
            var available = GroupApplications(sequence[0]).Where(a => a.Available(nodes, resolve)).Take(2).ToArray();
            return available.Length == 1 ? available[0] : null;
        }

        static IEnumerable<Application> GroupApplications(Key key) => All.Where(a => a.Keys.Length == 2 && a.Keys[0] == key);

        internal static Key[] ShortcutFor(Application application, IReadOnlyList<INode> nodes, Func<Application, string> resolve = null)
            => application.Keys.Length == 2 && Find(new[] { application.Keys[0] }, nodes, resolve) == application
                ? new[] { application.Keys[0] } : application.Keys;
        internal static Application FindDiff(IReadOnlyList<INode> nodes, Func<Application, string> resolve = null)
            => All.FirstOrDefault(a => a.Compare && a.Available(nodes, resolve));

        internal static CommandTree DiffCommand(CommandTree.Command action, Func<Application, string> resolve = null)
            => (Key.D, "Compare in diff tool", n => FindDiff(n.ToArray(), resolve) != null, action);
        internal static CommandTree[] Commands(Func<Application, string> resolve = null)
        {
            CommandTree Command(Application app) => new(app.Keys[^1], app.Name)
            {
                Visible = n => app.Available(n.ToArray(), resolve)
            };
            var commands = All.Where(a => a.Keys.Length == 1).Select(Command).ToList();
            foreach (var group in Groups)
            {
                var applications = GroupApplications(group.Key).ToArray();
                var command = new CommandTree(group.Key, group.Name);
                var singleCommands = applications.ToDictionary(a => a, a => new CommandTree(group.Key, a.Name));
                command.Add(applications.Select(Command).ToArray());
                command.ResolveForNodes = n =>
                {
                    var selection = n.ToArray();
                    var available = applications.Where(a => a.Available(selection, resolve)).Take(2).ToArray();
                    return available.Length switch
                    {
                        0 => null,
                        1 => singleCommands[available[0]],
                        _ => command
                    };
                };
                commands.Add(command);
            }
            return commands.ToArray();
        }
    }
}
