using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace search
{
    /// <summary>
    /// What a Log explorer window starts with: the files appended as one source, filtered to
    /// the lines containing Filter (none when empty)
    /// </summary>
    internal sealed record LogExplorerLoad(string Filter, bool CaseSensitive);

    /// <summary>
    /// Log explorer (logexplorer/, Rust): a program of its own in the LogExplorer folder next
    /// to the app. It keeps the text of the files once, so files of gigabytes fit. The
    /// releases ship it; a build made without Rust has none (search.csproj).
    /// </summary>
    internal static class NativeLogExplorer
    {
        internal const string Folder = "LogExplorer";

        /// <summary>Replaces the path of the program (tests); null is the one next to the app</summary>
        internal static string PathOverride;

        /// <summary>
        /// Replaces starting the program (the UI tests record what would open: the files, the
        /// load and whether as an administrator); null starts it
        /// </summary>
        internal static Action<string[], LogExplorerLoad, bool> OpenOverride;

        public static string ProgramPath => PathOverride
            ?? Path.Combine(AppContext.BaseDirectory, Folder, OperatingSystem.IsWindows() ? "logexplorer.exe" : "logexplorer");

        public static bool IsAvailable => OpenOverride != null || File.Exists(ProgramPath);

        /// <summary>
        /// Above this many characters of names the files go through the standard input
        /// instead of the command line - Windows caps a command line at 32 767 characters
        /// (CreateProcessW), 400 paths of 90 characters already pass it
        /// </summary>
        internal const int CommandLineFilesLimit = 8 * 1024;

        /// <summary>The files are too many for the command line: they are written to the standard input</summary>
        internal static bool ListOnStandardInput(string[] files)
            => files.Sum(file => (long)file.Length + 3) > CommandLineFilesLimit; //Quotes and a space each

        /// <summary>
        /// The command line for the files: several files with a load are one appended source
        /// (never a question how to combine them), the filter of the load starts the pipeline.
        /// Profiles are kept with the other data of the app (no <paramref name="dataFolder"/>:
        /// those of the program's own user), the UI speaks its language. A long list is read
        /// from the standard input (<see cref="StandardInputList"/>), or from the pipe
        /// <paramref name="listFrom"/> serving the same.
        /// </summary>
        internal static List<string> Arguments(string[] files, LogExplorerLoad load, CultureInfo culture, string dataFolder, string listFrom = "-")
        {
            var arguments = new List<string> { "--lang", culture.Name };
            if (dataFolder != null)
            {
                arguments.Add("--data-dir");
                arguments.Add(dataFolder);
            }
            if (load != null)
            {
                arguments.Add("--combine");
                arguments.Add("append");
                if (!string.IsNullOrEmpty(load.Filter))
                {
                    arguments.Add("--filter");
                    arguments.Add(load.Filter);
                    if (load.CaseSensitive) arguments.Add("--case-sensitive");
                }
            }
            if (ListOnStandardInput(files))
            {
                arguments.Add("--files0-from");
                arguments.Add(listFrom);
                return arguments;
            }
            // After "--" a file whose name starts with dashes is still a file
            arguments.Add("--");
            arguments.AddRange(files);
            return arguments;
        }

        /// <summary>The standard input of --files0-from: each name ended by NUL, which no path holds</summary>
        internal static byte[] StandardInputList(string[] files)
            => new UTF8Encoding(false).GetBytes(string.Concat(files.Select(file => file + "\0")));

        /// <summary>
        /// The command line of a start as an administrator, as one string: what starts it
        /// elevated (the elevated helper, the UAC question, pkexec) takes no list of arguments.
        /// A long list of files comes from <paramref name="listFrom"/>: the standard input,
        /// which pkexec passes on, or a pipe of its own (<see cref="ServeList"/>).
        /// </summary>
        internal static string ElevatedCommandLine(string[] files, LogExplorerLoad load, CultureInfo culture, string dataFolder, string listFrom)
            => string.Join(" ", Arguments(files, load, culture, dataFolder, listFrom).Select(Shell.Quote));

        /// <summary>How long a pipe with the list of files waits for the program once it was started</summary>
        static readonly TimeSpan ListWait = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Start the program with the files: without a load it asks how to combine several
        /// of them. <paramref name="elevated"/>: as an administrator, so that it reads files
        /// only administrators may. False when this build has no program.
        /// </summary>
        public static bool Open(string[] files, LogExplorerLoad load = null, bool elevated = false)
        {
            if (OpenOverride != null)
            {
                OpenOverride(files, load, elevated);
                return true;
            }
            if (!IsAvailable) return false;
            if (elevated && !Program.IsProcessElevated)
            {
                OpenElevated(files, load);
                return true;
            }
            var start = new ProcessStartInfo
            {
                FileName = ProgramPath,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(ProgramPath),
                RedirectStandardInput = ListOnStandardInput(files)
            };
            foreach (var argument in Arguments(files, load, CultureInfo.CurrentUICulture, UserDataPaths.For(Folder)))
                start.ArgumentList.Add(argument);
            var process = ProcessLauncher.Start(start);
            if (start.RedirectStandardInput && process != null) WriteList(process, StandardInputList(files));
            return true;
        }

        /// <summary>Off the UI thread: a list beyond the pipe buffer waits until the program reads it</summary>
        static void WriteList(Process process, byte[] list) => _ = Task.Run(() =>
        {
            try
            {
                using var input = process.StandardInput.BaseStream;
                input.Write(list);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                $"Log explorer did not take the list of files: {e.Message}".Debug();
            }
        });

        /// <summary>
        /// Start the program as an administrator: on Windows through the elevated helper (no
        /// question once it runs) or the UAC question, on Linux through pkexec. Elevated on
        /// Windows it is the same user, with the same profiles. Root on Linux keeps profiles of
        /// its own: written into the user's folder they would belong to root, and the user's
        /// own Log explorer could no longer save them.
        /// </summary>
        static void OpenElevated(string[] files, LogExplorerLoad load)
        {
            var culture = CultureInfo.CurrentUICulture;
            var folder = Path.GetDirectoryName(ProgramPath);
            var list = ListOnStandardInput(files) ? StandardInputList(files) : null;
            if (!OperatingSystem.IsWindows())
            {
                //pkexec hands its standard input over to the program
                var process = Shell.StartElevated(ProgramPath, ElevatedCommandLine(files, load, culture, null, "-"), folder, redirectInput: list != null);
                if (list != null && process != null) WriteList(process, list);
                return;
            }
            void Start(string listFrom) => ProgramPath.Open(
                ElevatedCommandLine(files, load, culture, UserDataPaths.For(Folder), listFrom), folder, elevated: true);
            //Neither the elevated helper nor the UAC question gives the program a standard input
            if (list == null) Start("-");
            else ServeList(list, Start, ListWait);
        }

        /// <summary>
        /// Hand a list of files over through a pipe of its own (Windows): the program, started
        /// by <paramref name="start"/> with the name of the pipe, opens it like a file and reads
        /// it to the end. The name cannot be guessed and no other program can serve a pipe of
        /// that name. The pipe waits for the program until <paramref name="wait"/> after the
        /// start - which may wait for the UAC question first - and goes when nobody came (the
        /// question was declined).
        /// </summary>
        internal static void ServeList(byte[] list, Action<string> start, TimeSpan wait)
        {
            var name = $"WinSearch-{Guid.NewGuid():N}";
            var pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var timeout = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                using (pipe)
                    try
                    {
                        await pipe.WaitForConnectionAsync(timeout.Token);
                        await pipe.WriteAsync(list);
                        //Closed sooner, what the program has not read yet would be lost
                        pipe.WaitForPipeDrain();
                    }
                    catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
                    {
                        $"Log explorer did not take the list of files: {e.Message}".Debug();
                    }
            });
            try
            {
                start($@"\\.\pipe\{name}");
            }
            finally
            {
                timeout.CancelAfter(wait);
            }
        }

        /// <summary>
        /// The text of the files is in memory once (plus a few bytes a line), so the question
        /// before loading is whether it fits: asked from half of the memory of the machine.
        /// </summary>
        internal static bool NeedsConfirmation(ulong bytes, ulong memoryBytes) => bytes > memoryBytes / 2;

        internal static ulong MemoryBytes => (ulong)Math.Max(0, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
    }
}
