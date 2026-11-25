using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using Xunit;

namespace search.Tests
{
    /// <summary>The app started again: the elevated helper and a restart</summary>
    public class SelfStartTests
    {
        /// <summary>
        /// F5 runs "dotnet FileSearchManager.dll": the helper was dotnet.exe with --broker, which
        /// only flashed an error in a console window while the app waited for it with its UI
        /// </summary>
        [Fact]
        public void UnderDotnetTheAppStartsAsItsOwnProgram()
        {
            var folder = Path.Combine(Path.GetTempPath(), "fsm-self-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var windows = OperatingSystem.IsWindows();
                var dotnet = windows ? @"C:\Program Files\dotnet\dotnet.exe" : "/usr/share/dotnet/dotnet";
                var dll = Path.Combine(folder, "FileSearchManager.dll");
                var exe = Path.Combine(folder, windows ? "FileSearchManager.exe" : "FileSearchManager");
                File.WriteAllText(dll, "");

                //Built without its program: dotnet runs the dll
                Starts(dotnet, new[] { dll }, Program.Self(dotnet, dll));

                File.WriteAllText(exe, "");
                Starts(exe, Array.Empty<string>(), Program.Self(dotnet, dll));
                //Started as its own program it is that one
                Starts(exe, Array.Empty<string>(), Program.Self(exe, dll));
                //Single-file: no assembly path
                Starts(exe, Array.Empty<string>(), Program.Self(exe, ""));
            }
            finally { Directory.Delete(folder, true); }
        }

        /// <summary>
        /// A alone on a log without the helper: the shell has no "runas" for a document (No
        /// application is associated with the specified file for this operation), which ended
        /// unhandled in the command of the key. An elevated copy of the app opens it instead.
        /// </summary>
        //On the UI thread like the tests of the main window: they replace the launcher too
        [AvaloniaFact]
        public void ADocumentAsAnAdministratorOpensThroughAnElevatedCopyOfTheApp()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Linux opens a document as root in Log explorer");
            Assert.SkipWhen(Program.IsProcessElevated, "Elevated, the file opens as it is");
            var log = @"C:\Windows\Panther\setupact.log";
            var started = new List<ProcessStartInfo>();
            var launcher = ProcessLauncher.Override;
            try
            {
                ProcessLauncher.Override = start =>
                {
                    lock (started) started.Add(start);
                    //The helper is not started here; the shell knows no runas for the log
                    if (start.FileName == log) throw new Win32Exception(1155);
                    return null;
                };

                log.Open("", @"C:\Windows\Panther", elevated: true);
            }
            finally { ProcessLauncher.Override = launcher; }

            var (program, leading) = Program.Self();
            //The helper offered first (unless it was before): this app, never dotnet.exe alone
            Assert.All(started.Where(s => s.Arguments.Contains("--broker")), s => Assert.Equal(program, s.FileName));
            var open = started.Last();
            Assert.Equal("runas", open.Verb);
            Assert.Equal(program, open.FileName);
            Assert.Equal(string.Join(" ", leading.Select(Shell.Quote).Concat(new[] { "--open", log, @"C:\Windows\Panther" })), open.Arguments);
            Assert.Equal("runas", started[^2].Verb);
            Assert.Equal(log, started[^2].FileName);
        }

        static void Starts(string program, string[] leading, (string Program, string[] Leading) self)
        {
            Assert.Equal(program, self.Program);
            Assert.Equal(leading, self.Leading);
        }
    }
}
