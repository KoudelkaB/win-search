using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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
        /// Replaces starting the program (the UI tests record what would open: the files and
        /// the load); null starts it
        /// </summary>
        internal static Action<string[], LogExplorerLoad> OpenOverride;

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
        /// Profiles are kept with the other data of the app, the UI speaks its language. A
        /// long list is read from the standard input (<see cref="StandardInputList"/>).
        /// </summary>
        internal static List<string> Arguments(string[] files, LogExplorerLoad load, CultureInfo culture, string dataFolder)
        {
            var arguments = new List<string> { "--lang", culture.Name, "--data-dir", dataFolder };
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
                arguments.Add("-");
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
        /// Start the program with the files: without a load it asks how to combine several
        /// of them. False when this build has no program.
        /// </summary>
        public static bool Open(string[] files, LogExplorerLoad load = null)
        {
            if (OpenOverride != null)
            {
                OpenOverride(files, load);
                return true;
            }
            if (!IsAvailable) return false;
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
            if (start.RedirectStandardInput && process != null)
            {
                //Off the UI thread: a list beyond the pipe buffer waits until the program reads it
                var list = StandardInputList(files);
                _ = Task.Run(() =>
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
            }
            return true;
        }

        /// <summary>
        /// The text of the files is in memory once (plus a few bytes a line), so the question
        /// before loading is whether it fits: asked from half of the memory of the machine.
        /// </summary>
        internal static bool NeedsConfirmation(ulong bytes, ulong memoryBytes) => bytes > memoryBytes / 2;

        internal static ulong MemoryBytes => (ulong)Math.Max(0, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
    }
}
