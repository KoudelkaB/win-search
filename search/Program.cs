using System;
using System.Security.Principal;

namespace search
{
    /// <summary>
    /// Entry point and process-role selection.
    /// The UI always runs unelevated; privileged work is done by the WinSearchService
    /// service (MFT reads) or by an optional elevated broker copy of this process
    /// (one UAC prompt at startup, see Broker).
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// True when this process itself runs with administrator rights
        /// (e.g. dev mode started from an admin shell, or the --broker instance)
        /// </summary>
        public static bool IsProcessElevated { get; private set; }

        /// <summary>
        /// Role tag for logging
        /// </summary>
        public static string Role { get; private set; } = "UI";

        [STAThread]
        public static void Main(string[] args)
        {
            using (var identity = WindowsIdentity.GetCurrent())
                IsProcessElevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

            if (args.Length == 2 && args[0] == "--broker")
            {
                // Elevated headless helper spawned by the unelevated UI
                Role = "Broker";
                Broker.RunServer(args[1]);
                return;
            }

            if (args.Length == 1 && args[0].Equals("UI", StringComparison.OrdinalIgnoreCase))
            {
                // Dev mode: UI only, no broker spawn
                App.Main();
                return;
            }

            // Normal start: offer one optional UAC prompt for the elevated broker;
            // declining it leaves the app fully functional (per-task prompts, service/walk indexing)
            Broker.StartClient();
            try
            {
                App.Main();
            }
            finally
            {
                Broker.Stop();
            }
        }
    }
}
