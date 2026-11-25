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
        public void AsAnAdministratorItGetsOneCommandLineAndALongListThroughAPipe()
        {
            //What starts it elevated (the elevated helper, UAC, pkexec) takes one string
            var commandLine = NativeLogExplorer.ElevatedCommandLine(new[] { @"C:\Windows\Panther\setupact.log", @"C:\my logs\a.log" }, null,
                Czech, @"C:\Users\me\AppData\Local\win-search\LogExplorer", @"C:\Temp\files.lst");
            Assert.Equal(@"--lang cs-CZ --data-dir C:\Users\me\AppData\Local\win-search\LogExplorer -- C:\Windows\Panther\setupact.log ""C:\my logs\a.log""",
                commandLine);
            if (OperatingSystem.IsWindows())
                Assert.Equal(new[] { "--lang", "cs-CZ", "--data-dir", @"C:\Users\me\AppData\Local\win-search\LogExplorer", "--",
                    @"C:\Windows\Panther\setupact.log", @"C:\my logs\a.log" }, WindowsArguments(commandLine));

            //Too many for a command line: the list of the standard input comes through a pipe
            var files = Enumerable.Range(0, 400).Select(i => $"/logs/{new string('x', 80)}/{i:D3}.log").ToArray();
            Assert.EndsWith(@" --files0-from \\.\pipe\WinSearch-1",
                NativeLogExplorer.ElevatedCommandLine(files, null, Czech, null, @"\\.\pipe\WinSearch-1"));

            //Root on Linux keeps its own profiles
            Assert.Equal(new[] { "--lang", "cs-CZ", "--", "/var/log/secure" },
                NativeLogExplorer.Arguments(new[] { "/var/log/secure" }, null, Czech, null));
        }

        [Fact]
        public async System.Threading.Tasks.Task AnElevatedProgramReadsTheListFromThePipeAsAFile()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "The pipe is how Windows hands the list over");
            //Beyond the buffer of a pipe: written while the program reads
            var files = Enumerable.Range(0, 4000).Select(i => $@"C:\logs\{new string('x', 80)}\{i:D4}.log").ToArray();
            var list = NativeLogExplorer.StandardInputList(files);
            string listFrom = null;
            NativeLogExplorer.ServeList(list, name => listFrom = name, TimeSpan.FromSeconds(30));
            Assert.StartsWith(@"\\.\pipe\WinSearch-", listFrom);

            //Opened by its name and read to the end, as Log explorer reads --files0-from
            using var read = new MemoryStream();
            using (var pipe = new FileStream(listFrom, FileMode.Open, FileAccess.Read))
                await pipe.CopyToAsync(read, TestContext.Current.CancellationToken);
            Assert.Equal(list, read.ToArray());

            //Nobody else gets it: the pipe is gone once read
            Assert.ThrowsAny<IOException>(() => new FileStream(listFrom, FileMode.Open, FileAccess.Read).Dispose());

            //Nobody came (the UAC question declined): the pipe goes after the wait
            NativeLogExplorer.ServeList(list, name => listFrom = name, TimeSpan.FromMilliseconds(100));
            await System.Threading.Tasks.Task.Delay(1000, TestContext.Current.CancellationToken);
            Assert.ThrowsAny<IOException>(() => new FileStream(listFrom, FileMode.Open, FileAccess.Read).Dispose());
        }

        [Fact]
        public void QuotedArgumentsComeBackAsTheyWere()
        {
            Assert.Equal("plain", Shell.Quote("plain"));
            Assert.Equal(@"""a b""", Shell.Quote("a b"));
            Assert.Equal(@"""""", Shell.Quote(""));
            Assert.Equal(@"""a\""b""", Shell.Quote("a\"b"));
            //A trailing backslash would escape the closing quote; inside a name it is itself
            Assert.Equal(@"""C:\my dir\\""", Shell.Quote(@"C:\my dir\"));
            Assert.Equal(@"""a\\\""b c""", Shell.Quote(@"a\""b c"));
            Assert.Equal(@"""C:\a b\c""", Shell.Quote(@"C:\a b\c"));
            if (!OperatingSystem.IsWindows()) return;
            var values = new[] { "plain", "a b", "", "a\"b", @"C:\my dir\", @"a\""b c", @"C:\a b\c", @"\\server\share\x y\\", "\t" };
            Assert.Equal(values, WindowsArguments(string.Join(" ", values.Select(Shell.Quote))));
        }

        /// <summary>The arguments a Windows program reads from the command line</summary>
        static string[] WindowsArguments(string commandLine)
        {
            //The program name is parsed by other rules: one without quotes leads
            var list = CommandLineToArgvW("program " + commandLine, out var count);
            try
            {
                return Enumerable.Range(1, count - 1)
                    .Select(i => System.Runtime.InteropServices.Marshal.PtrToStringUni(
                        System.Runtime.InteropServices.Marshal.ReadIntPtr(list, i * IntPtr.Size)))
                    .ToArray();
            }
            finally
            {
                LocalFree(list);
            }
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr memory);

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
