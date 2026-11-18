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
    /// Log explorer, the program of the build (logexplorer/): its command line says which
    /// files to open, how to combine them and what to filter them by.
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
        public void FilesBeyondAWindowsCommandLineGoThroughTheStandardInput()
        {
            //400 paths of 90 characters pass the 32 767 characters of CreateProcessW
            var files = Enumerable.Range(0, 400).Select(i => $"/logs/{new string('x', 80)}/{i:D3}.log").ToArray();
            Assert.True(NativeLogExplorer.ListOnStandardInput(files));
            var arguments = Arguments(files, new LogExplorerLoad("ERROR", false));
            Assert.Equal(new[] { "--filter", "ERROR", "--files0-from", "-" }, arguments[^4..]);
            Assert.True(arguments.Sum(a => a.Length + 3) < 1000);
            //Each name ended by NUL, which no path holds - a line break may be in a name
            Assert.Equal("/a b.log\0/line\nbreak.log\0",
                System.Text.Encoding.UTF8.GetString(NativeLogExplorer.StandardInputList(new[] { "/a b.log", "/line\nbreak.log" })));
            Assert.False(NativeLogExplorer.ListOnStandardInput(files[..10]));
        }

        [Fact]
        public void ItAsksOnlyWhenTheFilesWouldNotFitInMemory()
        {
            const ulong memory = 16UL << 30;
            Assert.False(NativeLogExplorer.NeedsConfirmation(1UL << 30, memory));
            Assert.False(NativeLogExplorer.NeedsConfirmation(memory / 2, memory));
            Assert.True(NativeLogExplorer.NeedsConfirmation(memory / 2 + 1, memory));
            Assert.True(NativeLogExplorer.MemoryBytes > 0);
        }

        //On the UI thread like the tests of the main window: they replace the launcher too
        [AvaloniaFact]
        public void TheProgramOfTheBuildOpensLogExplorerAndABuildWithoutItHasNone()
        {
            var folder = Path.Combine(Path.GetTempPath(), "win-search-native-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var program = Path.Combine(folder, OperatingSystem.IsWindows() ? "logexplorer.exe" : "logexplorer");
            var started = new List<ProcessStartInfo>();
            var (launcher, path, open) = (ProcessLauncher.Override, NativeLogExplorer.PathOverride, NativeLogExplorer.OpenOverride);
            try
            {
                ProcessLauncher.Override = start => { started.Add(start); return null; };
                NativeLogExplorer.PathOverride = program;
                NativeLogExplorer.OpenOverride = null;

                //Without the program the text viewer is the editor, and L is not offered
                Assert.False(NativeLogExplorer.IsAvailable);
                Assert.False(Apps.LogExplorerAvailable);
                Assert.NotEqual(Apps.LogExplorer, Apps.TextViever);
                Assert.False(NativeLogExplorer.Open(new[] { "/a.log" }));
                Assert.Empty(started);

                File.WriteAllText(program, "");
                Assert.True(NativeLogExplorer.IsAvailable);
                Assert.True(Apps.LogExplorerAvailable);
                Assert.Equal(Apps.LogExplorer, Apps.TextViever);

                Assert.True(NativeLogExplorer.Open(new[] { "/logs/a.log" }, new LogExplorerLoad("ERROR", true)));
                var start = Assert.Single(started);
                Assert.Equal(program, start.FileName);
                Assert.False(start.UseShellExecute);
                Assert.Equal(new[] { "--combine", "append", "--filter", "ERROR", "--case-sensitive", "--", "/logs/a.log" }, start.ArgumentList.Skip(4));
                Assert.Equal(new[] { "--lang", CultureInfo.CurrentUICulture.Name, "--data-dir" }, start.ArgumentList.Take(3));
                Assert.EndsWith(NativeLogExplorer.Folder, start.ArgumentList[3]);
                Assert.False(start.RedirectStandardInput);

                //A long list is written to the standard input of the program
                started.Clear();
                var files = Enumerable.Range(0, 400).Select(i => $"/logs/{new string('x', 80)}/{i:D3}.log").ToArray();
                Assert.True(NativeLogExplorer.Open(files));
                start = Assert.Single(started);
                Assert.True(start.RedirectStandardInput);
                Assert.Equal(new[] { "--files0-from", "-" }, start.ArgumentList.Skip(4));
            }
            finally
            {
                ProcessLauncher.Override = launcher;
                NativeLogExplorer.PathOverride = path;
                NativeLogExplorer.OpenOverride = open;
                Directory.Delete(folder, true);
            }
        }
    }
}
