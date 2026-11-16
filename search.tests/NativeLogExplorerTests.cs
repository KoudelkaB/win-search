using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using Xunit;

namespace search.Tests
{
    /// <summary>
    /// Log explorer as the native program of the build (logexplorer/): its command line says
    /// what the HTML page is told through window.logExplorer.open.
    /// </summary>
    public class NativeLogExplorerTests
    {
        static readonly CultureInfo Czech = CultureInfo.GetCultureInfo("cs-CZ");

        static string[] Arguments(string[] files, LogExplorerLoad load)
            => NativeLogExplorer.Arguments(files, load, Czech, "/data/LogExplorer").ToArray();

        [Fact]
        public void FilesWithoutALoadAreOpenedSoThatTheUserIsAskedHowToCombineThem()
            => Assert.Equal(new[] { "--lang", "cs-CZ", "--data-dir", "/data/LogExplorer", "--", "/logs/a.log", "/logs/b.log" },
                Arguments(new[] { "/logs/a.log", "/logs/b.log" }, null));

        [Fact]
        public void ShowInFilesAppendsTheFilesAndStartsWithTheFilter()
        {
            Assert.Equal(new[] { "--lang", "cs-CZ", "--data-dir", "/data/LogExplorer", "--combine", "append", "--filter", "a\"b c", "--case-sensitive", "--", "/a.log" },
                Arguments(new[] { "/a.log" }, new LogExplorerLoad("a\"b c", true)));
            Assert.Equal(new[] { "--lang", "cs-CZ", "--data-dir", "/data/LogExplorer", "--combine", "append", "--filter", "x", "--", "/a.log" },
                Arguments(new[] { "/a.log" }, new LogExplorerLoad("x", false)));
            //A HEX search has no text to filter by: the files are appended all the same
            Assert.Equal(new[] { "--lang", "cs-CZ", "--data-dir", "/data/LogExplorer", "--combine", "append", "--", "/a.log" },
                Arguments(new[] { "/a.log" }, new LogExplorerLoad(null, false)));
        }

        [Fact]
        public void AFileNamedLikeAnOptionIsStillAFile()
        {
            var arguments = Arguments(new[] { "--filter" }, null);
            Assert.Equal(new[] { "--", "--filter" }, arguments[^2..]);
        }

        [Fact]
        public void ItAsksOnlyWhenTheFilesWouldNotFitInMemory()
        {
            const ulong memory = 16UL << 30;
            Assert.False(NativeLogExplorer.NeedsConfirmation(MainWindow.LogExplorerConfirmBytes * 10, memory));
            Assert.False(NativeLogExplorer.NeedsConfirmation(memory / 2, memory));
            Assert.True(NativeLogExplorer.NeedsConfirmation(memory / 2 + 1, memory));
            Assert.True(NativeLogExplorer.MemoryBytes > 0);
        }

        //On the UI thread like the tests of the main window: they replace the launcher too
        [AvaloniaFact]
        public void TheProgramOfTheBuildOpensLogExplorerAndABuildWithoutItFallsBackToThePage()
        {
            var folder = Path.Combine(Path.GetTempPath(), "win-search-native-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var program = Path.Combine(folder, OperatingSystem.IsWindows() ? "logexplorer.exe" : "logexplorer");
            var started = new List<ProcessStartInfo>();
            var (launcher, path) = (ProcessLauncher.Override, NativeLogExplorer.PathOverride);
            try
            {
                ProcessLauncher.Override = start => { started.Add(start); return null; };
                NativeLogExplorer.PathOverride = program;

                Assert.False(NativeLogExplorer.IsAvailable);
                Assert.False(NativeLogExplorer.TryOpen(new[] { "/a.log" }, null));
                Assert.Empty(started);

                File.WriteAllText(program, "");
                Assert.True(NativeLogExplorer.IsAvailable);
                Assert.True(Apps.LogExplorerAvailable);
                Assert.Equal(Apps.LogExplorer, Apps.TextViever);

                //Opening the page of Log explorer starts the program; the other pages stay pages
                WebAppWindow.Open(Apps.LogExplorer, new[] { "/logs/a.log" }, new LogExplorerLoad("ERROR", true));
                var start = Assert.Single(started);
                Assert.Equal(program, start.FileName);
                Assert.False(start.UseShellExecute);
                Assert.Equal(new[] { "--combine", "append", "--filter", "ERROR", "--case-sensitive", "--", "/logs/a.log" }, start.ArgumentList.Skip(4));
                Assert.Equal(new[] { "--lang", CultureInfo.CurrentUICulture.Name, "--data-dir" }, start.ArgumentList.Take(3));
                Assert.EndsWith(NativeLogExplorer.Folder, start.ArgumentList[3]);
            }
            finally
            {
                ProcessLauncher.Override = launcher;
                NativeLogExplorer.PathOverride = path;
                Directory.Delete(folder, true);
            }
        }
    }
}
