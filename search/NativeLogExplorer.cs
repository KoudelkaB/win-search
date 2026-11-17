using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

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
        /// The command line for the files: several files with a load are one appended source
        /// (never a question how to combine them), the filter of the load starts the pipeline.
        /// Profiles are kept with the other data of the app, the UI speaks its language.
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
            // After "--" a file whose name starts with dashes is still a file
            arguments.Add("--");
            arguments.AddRange(files);
            return arguments;
        }

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
                WorkingDirectory = Path.GetDirectoryName(ProgramPath)
            };
            foreach (var argument in Arguments(files, load, CultureInfo.CurrentUICulture, UserDataPaths.For(Folder)))
                start.ArgumentList.Add(argument);
            ProcessLauncher.Start(start);
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
