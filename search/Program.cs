using System;
using System.Diagnostics;
using System.IO;
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

        /// <summary>
        /// This app as a program to start again - the elevated helper, a restart - with the
        /// arguments that come before its own. Run as "dotnet FileSearchManager.dll" (F5 in VS
        /// Code) the process is dotnet.exe: started with --broker alone it showed its error in
        /// a console window and the helper never came. The program built next to the dll is
        /// the app itself, a window program without a console; without one dotnet runs the dll.
        /// </summary>
        internal static (string Program, string[] Leading) Self(string processPath = null, string assembly = null)
        {
            processPath ??= Environment.ProcessPath;
            assembly ??= typeof(Program).Assembly.Location;
            if (string.IsNullOrEmpty(assembly)
                || !Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                return (processPath, Array.Empty<string>());
            var host = Path.ChangeExtension(assembly, OperatingSystem.IsWindows() ? ".exe" : null);
            return File.Exists(host) ? (host, Array.Empty<string>()) : (processPath, new[] { assembly });
        }

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
                if (args.Length == 3 && args[0] == "--open")
                {
                    // One document opened as an administrator without the helper (declined, or
                    // it failed): the shell has no "runas" for documents (Extensions.Open). This
                    // elevated copy opens it in its application and ends. With no window of its
                    // own it lets the shell show what goes wrong (no application for the file).
                    Role = "Open";
                    try
                    {
                        using var opened = Process.Start(new ProcessStartInfo(args[1])
                        {
                            UseShellExecute = true,
                            WorkingDirectory = args[2],
                            ErrorDialog = true
                        });
                        return 0;
                    }
                    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        return 1;
                    }
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

        /// <summary>A Linux desktop session on Wayland, where the app runs on Avalonia's Wayland backend</summary>
        internal static bool WaylandSession =>
            OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

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
            if (WaylandSession)
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
