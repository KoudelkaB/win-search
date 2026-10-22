using System;
using System.Security.Principal;
using Avalonia;

// PropertyChanged.Fody weaves only the models: every Avalonia control implements INotifyPropertyChanged
// through AvaloniaObject and must keep its own change notification.
[assembly: PropertyChanged.FilterType("search.Models.")]

namespace search
{
    /// <summary>
    /// Entry point and process-role selection.
    /// On Windows the UI always runs unelevated; privileged work is done by the
    /// WinSearchService service (MFT reads) or by an optional elevated broker copy of this
    /// process (one UAC prompt at startup, see Broker). Linux has neither: the drives are
    /// walked with the user's own rights.
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
        public static bool OpenHelpRequested { get; private set; }

        [STAThread]
        public static int Main(string[] args)
        {
            if (OperatingSystem.IsWindows())
            {
                using (var identity = WindowsIdentity.GetCurrent())
                    IsProcessElevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

                if (args.Length == 2 && args[0] == "--broker")
                {
                    // Elevated headless helper spawned by the unelevated UI
                    Role = "Broker";
                    Broker.RunServer(args[1]);
                    return 0;
                }
            }
            else
            {
                IsProcessElevated = Environment.UserName == "root";
            }

            var devMode = args.Length == 1 && args[0].Equals("UI", StringComparison.OrdinalIgnoreCase);
            if (args.Length == 1 && args[0].Equals("--help", StringComparison.OrdinalIgnoreCase))
                OpenHelpRequested = true;

            // Normal Windows start: offer one optional UAC prompt for the elevated broker, but
            // only when something at startup needs it - with the WinSearchService serving the
            // $MFT, nothing does, and the offer moves to the first action that actually requires
            // admin rights. Declining leaves the app fully functional (per-task prompts,
            // service/walk indexing). A help launch just shows the help window, so skip the
            // elevation offer for it. Dev mode ("UI") never spawns the broker.
            if (OperatingSystem.IsWindows() && !devMode && !OpenHelpRequested
                && ElevationSettingsStore.ShouldStartHelperAtLaunch())
                Broker.StartClient();
            try
            {
                return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            finally
            {
                if (OperatingSystem.IsWindows()) Broker.Stop();
            }
        }

        // Also used by the Avalonia designer
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .With(new X11PlatformOptions { WmClass = "file-search-manager" })
                .LogToTrace();
    }
}
