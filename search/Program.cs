using System;
using System.Security.Principal;
using Avalonia;
using Avalonia.Logging;

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

        /// <summary>The environment variable naming another folder for all user data</summary>
        public const string DataFolderVariable = "WIN_SEARCH_DATA";

        [STAThread]
        public static int Main(string[] args)
        {
            // An automated run (the smoke tests over the published build) keeps its settings,
            // histories, index caches and logs in a folder of its own, apart from the user's
            if (Environment.GetEnvironmentVariable(DataFolderVariable) is { Length: > 0 } data)
                UserDataPaths.UseRootForCurrentProcess(data);

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
                if (args.Length == 1 && args[0] == "--service")
                {
                    // The optional root service behind live changes and the every-file view
                    // (systemd unit from installer/linux/install-service.sh) - no UI at all
                    Role = "Service";
                    return Models.LinuxService.LinuxService.Run();
                }
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
        {
            var builder = AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .With(new X11PlatformOptions { WmClass = "file-search-manager" })
                .LogToTrace();
            // A Wayland session gets the native backend: through XWayland, GNOME shows its plain
            // arrow for the whole drag of an X11 application. Only where the session names its
            // compositor - without WAYLAND_DISPLAY libwayland would reach for wayland-0 and put
            // a run meant for an X server (Xvfb in the smoke tests) on the desktop. Unsetting it
            // runs the app through XWayland again.
            if (OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
                builder = builder
                    .With(new WaylandPlatformOptions { AppId = "io.github.koudelkab.FileSearchManager" })
                    .UseWaylandWithFallback();
            // Hide the IBus "Destroy is not implemented" error logged on every window close
            if (Logger.Sink is { } sink and not AvaloniaLogFilter)
                Logger.Sink = new AvaloniaLogFilter(sink);
            return builder;
        }
    }
}
