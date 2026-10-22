using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using search.Models;
using Xunit;

namespace search.Tests
{
    public class FileApplicationsTests
    {
        static INode FileAt(string path) => new FileNode(path);
        static INode DirectoryAt(string path) => new DirectoryFileNode(path,
            new NodeMetadataSnapshot(true, 0, DateTime.MinValue));
        // Hints name a digit key by its digit, not "D7".
        static string Shown(Key key) => key is >= Key.D0 and <= Key.D9 ? (key - Key.D0).ToString() : key.ToString();

        [AvaloniaFact]
        public void HintsHideMissingApplicationsAndEmptyCategoriesAndRefreshWhenInstalled()
        {
            RunSta(() =>
            {
                var installed = new HashSet<string>();
                string Resolve(FileApplications.Application app) => installed.Contains(app.Name) ? @"C:\Tools\app.exe" : null;
                var commander = new KeysCommander { nodes = () => new[] { FileAt(@"Q:\src\app.php") } };
                commander.Commands.Add((Key.O, "Open in", FileApplications.Commands(Resolve)));
                commander.KeyPressed(Key.O);
                Assert.DoesNotContain("<J>", commander.Text);
                Assert.DoesNotContain("<N>", commander.Text);
                Assert.DoesNotContain("<M>", commander.Text);

                installed.Add("PhpStorm");
                commander.OnChange();
                Assert.Contains("<J>", commander.Text);
                commander.KeyPressed(Key.J);
                Assert.Contains("PhpStorm", commander.Text);
                Assert.DoesNotContain("CLion", commander.Text);
                installed.Clear();
                commander.OnChange();
                Assert.DoesNotContain("PhpStorm", commander.Text);
            });
        }

        [AvaloniaTheory]
        [InlineData(Key.J, "PhpStorm", "app.php", 1)]
        [InlineData(Key.N, "Notepad++", "app.txt", 1)]
        [InlineData(Key.Q, "Brave", "app.html", 1)]
        [InlineData(Key.U, "SumatraPDF", "app.pdf", 1)]
        [InlineData(Key.Z, "WinRAR", "app.zip", 1)]
        [InlineData(Key.M, "Beyond Compare", "app.txt", 2)]
        public void EveryCategoryHidesCollapsesOrExpandsAndExecutesItsSingleApplication(Key category, string name, string file, int count)
        {
            RunSta(() =>
            {
                foreach (var prefix in new[] { Key.O, Key.A })
                {
                    var selection = Enumerable.Range(0, count).Select(i => FileAt(@"Q:\" + i + file)).ToArray();
                    var installed = new HashSet<string>();
                    string Resolve(FileApplications.Application app) => installed.Contains(app.Name) ? "indexed.exe" : null;
                    var application = FileApplications.All.Single(a => a.Name == name && a.Keys[0] == category);
                    var other = FileApplications.All.First(a => a.Keys.Length == 2 && a.Keys[0] == category && a != application && a.Supports(selection));
                    string executed = null;
                    var commander = new KeysCommander { nodes = () => selection };
                    commander.Commands.Add((prefix, "Open in", (nodes, args) =>
                        executed = FileApplications.Find(args, nodes.ToArray(), Resolve)?.Name, FileApplications.Commands(Resolve)));
                    commander.KeyPressed(prefix);
                    Assert.DoesNotContain($"<{category}>", commander.Text);
                    Assert.Null(FileApplications.Find(new[] { category }, selection, Resolve));

                    installed.Add(name);
                    commander.OnChange();
                    Assert.Contains($"<{category}> {name}", commander.Text);
                    Assert.DoesNotContain($"<{category}> {name} ›", commander.Text);
                    Assert.Equal(new[] { category }, FileApplications.ShortcutFor(application, selection, Resolve));
                    commander.KeyPressed(category);
                    Assert.Contains($"<{prefix}><{category}>", commander.Text);
                    Assert.DoesNotContain($"<{application.Keys[1]}>", commander.Text);
                    commander.KeyReleased(category);
                    commander.KeyReleased(prefix);
                    Assert.Equal(name, executed);

                    executed = null;
                    installed.Add(other.Name);
                    commander.KeyPressed(prefix);
                    var groupName = L.Text(FileApplications.Groups.Single(g => g.Key == category).Name);
                    Assert.Contains($"<{category}> {groupName} ›", commander.Text);
                    Assert.Null(FileApplications.Find(new[] { category }, selection, Resolve));
                    Assert.Equal(application.Keys, FileApplications.ShortcutFor(application, selection, Resolve));
                    commander.KeyPressed(category);
                    Assert.Contains($"<{Shown(application.Keys[1])}> {name}", commander.Text);
                    Assert.Contains($"<{Shown(other.Keys[1])}> {other.Name}", commander.Text);
                    commander.KeyPressed(application.Keys[1]);
                    commander.KeyReleased(application.Keys[1]);
                    commander.KeyReleased(category);
                    commander.KeyReleased(prefix);
                    Assert.Equal(name, executed);

                    installed.Clear();
                    commander.KeyPressed(prefix);
                    Assert.DoesNotContain($"<{category}>", commander.Text);
                    commander.Cancel();
                }
            });
        }

        [AvaloniaFact]
        public void CategoryCollapseUpdatesWhenSelectionChanges()
        {
            RunSta(() =>
            {
                var selection = new[] { FileAt(@"Q:\file.txt") };
                var commander = new KeysCommander { nodes = () => selection };
                string Resolve(FileApplications.Application app) => app.Name is "Notepad++" or "Sublime Text" ? "indexed.exe" : null;
                commander.Commands.Add((Key.O, "Open in", FileApplications.Commands(Resolve)));
                commander.KeyPressed(Key.O);
                Assert.Contains($"<N> {L.Text("Text editors")} ›", commander.Text);
                selection = new[] { DirectoryAt(@"Q:\project") };
                commander.OnChange();
                Assert.Contains("<N> Sublime Text", commander.Text);
                Assert.DoesNotContain("<N> Sublime Text ›", commander.Text);
                Assert.Equal("Sublime Text", FileApplications.Find(new[] { Key.N }, selection, Resolve).Name);
                selection = Array.Empty<INode>();
                commander.OnChange();
                Assert.DoesNotContain("<N>", commander.Text);
            });
        }
        [AvaloniaFact]
        public void DiffHintRequiresAnInstalledToolThatSupportsTheEntireSelection()
        {
            RunSta(() =>
            {
                var selection = new[] { FileAt(@"Q:\left.bin"), FileAt(@"Q:\right.bin") };
                var installed = "";
                string Resolve(FileApplications.Application app) => app.Name == installed ? @"C:\Tools\app.exe" : null;
                var commander = new KeysCommander { nodes = () => selection };
                commander.Commands.Add(FileApplications.DiffCommand((n, a) => { }, Resolve));
                commander.OnChange();
                Assert.DoesNotContain("<D>", commander.Text);

                installed = "Visual Studio Code (diff)";
                commander.OnChange();
                Assert.Contains("<D>", commander.Text);
                var diff = FileApplications.FindDiff(selection, Resolve);
                Assert.Equal("Visual Studio Code (diff)", diff.Name);
                Assert.Equal("app.exe\0--diff", diff.CommandFor("app.exe"));

                selection = new[] { DirectoryAt(@"Q:\left"), DirectoryAt(@"Q:\right") };
                commander.OnChange();
                Assert.DoesNotContain("<D>", commander.Text);
                installed = "Beyond Compare";
                commander.OnChange();
                Assert.Contains("<D>", commander.Text);
                selection = new[] { DirectoryAt(@"Q:\left"), FileAt(@"Q:\right.bin") };
                commander.OnChange();
                Assert.DoesNotContain("<D>", commander.Text);
                selection = new[] { FileAt(@"Q:\one.txt") };
                commander.OnChange();
                Assert.DoesNotContain("<D>", commander.Text);
            });
        }

        [AvaloniaFact]
        public void NestedKeyboardSequenceResolvesTheChosenApplication()
        {
            var root = new CommandTree();
            root.Add((Key.O, "Open in", (nodes, args) => { }, FileApplications.Commands(_ => "installed.exe")));
            var command = root.Find(new[] { Key.O, Key.J, Key.P });
            Assert.NotNull(command.cmd);
            Assert.Equal("PhpStorm", FileApplications.Find(command.arg).Name);
            Assert.Null(FileApplications.Find(new[] { Key.J }));
            Assert.Equal("7-Zip", FileApplications.Find(new[] { Key.Z, Key.NumPad7 }).Name);
            // A stray key after a single-key application still opens that application.
            Assert.Equal("Chrome", FileApplications.Find(new[] { Key.C, Key.X }, Array.Empty<INode>()).Name);
            Assert.Equal(FileApplications.All.Length,
                FileApplications.All.Select(app => string.Join(',', app.Keys)).Distinct().Count());
        }

        [AvaloniaTheory]
        [InlineData("app.PHP", "PhpStorm")]
        [InlineData("app.cpp", "CLion")]
        [InlineData("app.cs", "Rider")]
        [InlineData("app.rs", "RustRover")]
        [InlineData("app.go", "GoLand")]
        [InlineData("app.py", "PyCharm")]
        [InlineData("app.kt", "IntelliJ IDEA")]
        [InlineData("README", "Visual Studio Code")]
        public void DevelopmentToolsSupportSourceFiles(string file, string application)
        {
            var app = FileApplications.All.First(a => a.Name == application && !a.Compare);
            Assert.True(app.Supports(new[] { FileAt(@"Q:\src\" + file) }));
        }

        [AvaloniaFact]
        public void OpenWithFiltersMixedSelectionsAndAllowsProjectDirectories()
        {
            var sumatra = FileApplications.All.First(a => a.Name == "SumatraPDF");
            Assert.True(sumatra.Supports(new[] { FileAt(@"Q:\one.pdf"), FileAt(@"Q:\two.epub") }));
            Assert.False(sumatra.Supports(new[] { FileAt(@"Q:\one.pdf"), FileAt(@"Q:\two.cpp") }));
            Assert.False(sumatra.Supports(new[] { DirectoryAt(@"Q:\docs") }));
            Assert.True(FileApplications.Find(new[] { Key.J, Key.R }).Supports(new[] { DirectoryAt(@"Q:\project") }));
            Assert.True(FileApplications.Find(new[] { Key.N, Key.T }).Supports(new[] { FileAt(@"Q:\custom.unknown") }));
            Assert.True(FileApplications.Find(new[] { Key.Z, Key.R }).Supports(new[] { FileAt(@"Q:\archive.rar") }));
        }

        [AvaloniaFact]
        public void DetectionUsesOnlyIndexPathsCachesUntilIndexPublicationAndRefreshesAfterIt()
        {
            var name = Guid.NewGuid().ToString("N") + ".exe";
            string indexed = null;
            var lookups = 0;
            string Find(string executable) { lookups++; return indexed; }
            Assert.Null(Apps.GetPath(Find, name));
            indexed = @"Q:\Indexed\first.exe";
            Assert.Null(Apps.GetPath(Find, name));
            Assert.Equal(1, lookups); // A cached miss performs no further lookup.
            Apps.RefreshInstalledApplications();
            Assert.Equal(indexed, Apps.GetPath(Find, name));
            Assert.Equal(2, lookups);
            Assert.Equal(indexed, Apps.GetPath(Find, name));
            Assert.Equal(2, lookups); // A cache hit performs no further lookup.
            indexed = @"Q:\Indexed\moved.exe";
            Apps.RefreshInstalledApplications();
            Assert.Equal(indexed, Apps.GetPath(Find, name));
            indexed = null;
            Apps.RefreshInstalledApplications();
            Assert.Null(Apps.GetPath(Find, name));
        }
        [AvaloniaFact]
        public void ComparisonKeepsOneFilePerWorkbook()
        {
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "win-search-tests-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(folder);
            try
            {
                INode Workbook(string name, params string[] sheets)
                {
                    var path = System.IO.Path.Combine(folder, name);
                    using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
                        for (var i = 0; i < sheets.Length; i++)
                        {
                            using var writer = new System.IO.StreamWriter(zip.CreateEntry($"xl/worksheets/sheet{i + 1}.xml").Open());
                            writer.Write("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" +
                                $"<row><c t=\"inlineStr\"><is><t>{sheets[i]}</t></is></c></row></sheetData></worksheet>");
                        }
                    return FileAt(path);
                }
                var selection = new[] { Workbook("left.xlsx", "alpha", "beta"), Workbook("right.xlsx", "gamma", "delta") };

                // A diff tool takes exactly one file per side, however many sheets a workbook has.
                var compared = SearchModel.TextNodes(true, selection).ToArray();
                Assert.Equal(2, compared.Length);
                Assert.Equal(new[] { "left.xlsx.txt", "right.xlsx.txt" }, compared.Select(n => n.Name));
                var left = System.IO.File.ReadAllText(compared[0].FullName);
                Assert.True(left.IndexOf("alpha", StringComparison.Ordinal) < left.IndexOf("beta", StringComparison.Ordinal));
                Assert.DoesNotContain("gamma", left);
                Assert.Contains("delta", System.IO.File.ReadAllText(compared[1].FullName));

                Assert.Equal(4, SearchModel.TextNodes(false, selection).Count());
            }
            finally { System.IO.Directory.Delete(folder, true); }
        }

        //[AvaloniaFact] already runs the test on the Avalonia UI thread
        static void RunSta(Action action) => action();
    }
}
