using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace search
{
    public partial class App : Application
    {
        //To be deleted on App closeup
        static readonly int pid = Environment.ProcessId;
        static readonly string dirName = "search.";
        static readonly string clipboardDirName = "search.clipboard.";
        static readonly long processStartTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        public static readonly string TempFolder = Path.Combine(Path.GetTempPath(), $"{dirName}{pid}.{processStartTicks}");
        public static readonly string ClipboardTempFolder = Path.Combine(Path.GetTempPath(), $"{clipboardDirName}{pid}.{processStartTicks}");

        /// <summary>
        /// Create unique temporal folder in App temp directory (will be deleted automatically)
        /// </summary>
        public static string CreateTempFolder()
            => CreateTempFolder(TempFolder);

        public static string CreateClipboardTempFolder()
            => CreateTempFolder(ClipboardTempFolder);

        static string CreateTempFolder(string root)
        {
            var path = Path.Combine(root, $"{DateTime.Now:MMdd-HHmmss}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return path;
        }

        static App()
        {
            StorageMaintenance.RunStartupCleanup(Path.GetTempPath(), TempFolder, DateTime.UtcNow);
        }

        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            //Commands run as async void handlers - an exception there bypasses every local
            //try/catch and would kill the whole app; report it and keep running instead
            Dispatcher.UIThread.UnhandledException += (o, e) =>
            {
                $"UI exception: {e.Exception}".Debug();
                e.Handled = true;
                _ = MessageDialog.Show(Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d ? d.MainWindow : null,
                    e.Exception.Message, L.Text("CommandFailed"), MessageDialogButtons.Ok, MessageDialogIcon.Error);
            };

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                //Secondary HTML/help windows must not keep the application running alone.
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                desktop.Exit += (_, e) => OnExit(e.ApplicationExitCode);
                var saved = LanguageSettingsStore.Load();
                if (!string.IsNullOrWhiteSpace(saved))
                {
                    ApplyCulture(saved);
                    desktop.MainWindow = new MainWindow();
                }
                else
                {
                    // Language is a per-user preference. A supported system language is
                    // selected silently; ask only when there is no matching translation.
                    var initial = Languages.ForSystemCulture(CultureInfo.CurrentUICulture);
                    if (!string.IsNullOrWhiteSpace(initial))
                    {
                        TrySaveLanguage(initial);
                        ApplyCulture(initial);
                        desktop.MainWindow = new MainWindow();
                    }
                    else
                    {
                        //The picker is the first window; it must not end the application
                        var shutdownMode = desktop.ShutdownMode;
                        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                        var picker = new LanguageSelectionWindow(initial);
                        picker.Closed += (_, __) =>
                        {
                            var selected = picker.Accepted ? picker.SelectedCulture ?? "en" : "en";
                            TrySaveLanguage(selected);
                            ApplyCulture(selected);
                            var main = new MainWindow();
                            desktop.MainWindow = main;
                            main.Show();
                            desktop.ShutdownMode = shutdownMode;
                        };
                        picker.Show();
                    }
                }
            }
            base.OnFrameworkInitializationCompleted();
        }

        static void TrySaveLanguage(string culture)
        {
            try { LanguageSettingsStore.Save(culture); }
            catch (Exception e) { $"saving language failed: {e.Message}".Debug(); }
        }

        static void OnExit(int code)
        {
            WebAppWindow.Shutdown();
            $"App exiting with {code}".Debug();
            //Best-effort removal of this run's temp folder; anything locked here
            //is swept by the stale-folder cleanup on the next start
            StorageMaintenance.TryDeleteDirectory(TempFolder);
            //Files the app put on the clipboard stay for other applications to paste
            StorageMaintenance.CleanupClipboardMaterializations(ClipboardTempFolder, FileClipboard.LastSetFiles);
        }

        internal static void ApplyCulture(string name)
        {
            L.CaptureRegional();
            var culture = CultureInfo.GetCultureInfo(name);
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        /// <summary>The application's main window, null before it exists (tests)</summary>
        internal static Window MainWindowOrNull
            => Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d ? d.MainWindow : null;

        /// <summary>Restart with the same arguments (language change)</summary>
        internal static void Restart()
        {
            var start = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false };
            // Carry the original arguments over, but drop --help so a language change made from
            // a help-launched instance does not reopen the help window after the restart.
            var args = Environment.GetCommandLineArgs();
            for (var i = 1; i < args.Length; i++)
                if (!args[i].Equals("--help", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(args[i]);
            Process.Start(start);
            if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }
    }
}
