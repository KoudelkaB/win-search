using System;
using System.Diagnostics;

namespace search
{
    /// <summary>
    /// Every program started for the user - a document in its application, a file manager,
    /// a terminal, a dropped-on program - goes through here. The UI tests take the starts
    /// over and only record them, so no test ever runs a real program.
    /// </summary>
    internal static class ProcessLauncher
    {
        /// <summary>Replaces the start (tests); null starts the process</summary>
        internal static Func<ProcessStartInfo, Process> Override;

        public static Process Start(ProcessStartInfo start) =>
            Override != null ? Override(start) : Process.Start(start);
    }
}
