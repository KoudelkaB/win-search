using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using search.Models.LinuxService;

namespace search
{
    /// <summary>
    /// The app's settings. For now the Linux file system service: its state, setting it up
    /// (an administrator prompt each change - see LinuxServiceSetup), and whether an
    /// administrator sees every file. A setup change takes effect at once - Cancel only
    /// discards the every-file choice.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        /// <summary>What Settings shows about the service</summary>
        /// <param name="Installed">Version of the files the service runs; null without them</param>
        /// <param name="App">Version of this app</param>
        /// <param name="FromPackage">The service is the .rpm or .deb package's</param>
        /// <param name="TurnedOff">The service is installed and turned off (not merely stopped)</param>
        internal sealed record ServiceState(ServiceStatus Status, bool Admin, string Installed, string App,
            bool FromPackage = false, bool TurnedOff = false);

        /// <summary>The every-file choice changed: every drive is scanned and watched anew</summary>
        public bool ShowAllFilesChanged { get; private set; }
        /// <summary>The service was installed, updated, removed, turned on or off</summary>
        public bool ServiceChanged { get; private set; }
        bool busy;

        public SettingsWindow() : this(loadState: true) { }

        /// <param name="loadState">Ask the service for its state (tests set it themselves)</param>
        internal SettingsWindow(bool loadState)
        {
            InitializeComponent();
            showAllFiles.IsChecked = LinuxServiceSettingsStore.ShowAllFiles;
            ToolTip.SetShowOnDisabled(removeService, true);
            //A running change ends first - its prompt may still be on the screen
            Closing += (_, e) => { if (busy) e.Cancel = true; };
            if (Shell.IsFlatpak)
            {
                serviceState.Text = L.Text("ServiceFlatpak");
                serviceButtons.IsVisible = false;
                showAllFiles.IsVisible = false;
            }
            else if (loadState) LoadServiceState();
        }

        /// <summary>
        /// The service's state - asked on its own thread: connecting to a service that hangs
        /// must not freeze the dialog
        /// </summary>
        void LoadServiceState()
        {
            showAllFiles.IsEnabled = false;
            foreach (var button in SetupButtons) button.IsEnabled = false;
            new Thread(() =>
            {
                var status = LinuxServiceClient.Status;
                var state = new ServiceState(status,
                    Admin: status is ServiceStatus.AllFiles or ServiceStatus.OwnFiles && LinuxServiceClient.IsAdmin,
                    Installed: LinuxServiceSetup.InstalledVersion,
                    App: LinuxServiceSetup.AppVersion,
                    FromPackage: LinuxServiceSetup.FromPackage,
                    TurnedOff: status == ServiceStatus.NotRunning && LinuxServiceSetup.IsTurnedOff());
                Dispatcher.UIThread.Post(() => ShowServiceState(state));
            })
            { IsBackground = true, Name = "service state" }.Start();
        }

        Button[] SetupButtons => new[] { installService, updateService, removeService, turnOnService, turnOffService };

        internal void ShowServiceState(ServiceState state)
        {
            var status = state.Status;
            var running = status is ServiceStatus.AllFiles or ServiceStatus.OwnFiles;
            serviceState.Text = state.TurnedOff ? L.Text("ServiceTurnedOff") : ServiceStateText(status, state.Admin);
            //The package's service runs the package's files: nothing is copied. Another build of
            //the app than the package's still learns which version the service runs.
            serviceCopy.IsVisible = !state.FromPackage;
            var outdated = LinuxServiceSetup.IsOutdated(state.Installed, state.App);
            serviceVersion.IsVisible = outdated;
            if (outdated)
            {
                var (shownInstalled, shownApp) = LinuxServiceSetup.Shown(state.Installed, state.App);
                serviceVersion.Text = L.Format("ServiceOutdated", shownInstalled, shownApp);
            }
            var present = status != ServiceStatus.NotInstalled;
            //The package's service: on and off only - the package manager updates and removes it
            installService.IsVisible = !state.FromPackage && !present;
            updateService.IsVisible = !state.FromPackage && present && (outdated || status == ServiceStatus.NotRunning);
            removeService.IsVisible = !state.FromPackage && present;
            turnOnService.IsVisible = state.FromPackage && !running;
            //Turned on but failing keeps restarting: it can be turned off too
            turnOffService.IsVisible = state.FromPackage && !state.TurnedOff;
            foreach (var button in SetupButtons) button.IsEnabled = !busy;
            removeService.IsEnabled = !busy && !LinuxServiceSetup.RunsFromInstallFolder;
            ToolTip.SetTip(removeService, LinuxServiceSetup.RunsFromInstallFolder ? L.Text("ServiceRemoveRunning") : null);
            //Not installed or not running: the choice waits for the service.
            //Running: only an administrator may see every file.
            showAllFiles.IsEnabled = state.Admin || !running;
        }

        internal static string ServiceStateText(ServiceStatus status, bool admin) => status switch
        {
            ServiceStatus.NotInstalled => L.Text("ServiceNotInstalled"),
            ServiceStatus.NotRunning => L.Text("ServiceNotRunning"),
            ServiceStatus.AllFiles => L.Text("ServiceAllFiles"),
            _ => L.Text("ServiceOwnFiles") + (admin ? "" : " " + L.Text("ServiceNotAdmin"))
        };

        //Install and Update run the same script: it replaces the service's copy of the app
        async void Install_Click(object sender, RoutedEventArgs e) => await Run(LinuxServiceSetup.Change.Install);
        async void Remove_Click(object sender, RoutedEventArgs e) => await Run(LinuxServiceSetup.Change.Remove);
        async void TurnOn_Click(object sender, RoutedEventArgs e) => await Run(LinuxServiceSetup.Change.TurnOn);
        async void TurnOff_Click(object sender, RoutedEventArgs e) => await Run(LinuxServiceSetup.Change.TurnOff);

        async Task Run(LinuxServiceSetup.Change change)
        {
            busy = true;
            foreach (var button in SetupButtons) button.IsEnabled = false;
            okButton.IsEnabled = cancelButton.IsEnabled = false;
            serviceMessage.Text = L.Text("ServiceWorking");
            serviceMessage.IsVisible = true;
            var (outcome, error) = await Task.Run(() =>
            {
                var result = LinuxServiceSetup.Run(change);
                if (result.Outcome == LinuxServiceSetup.Outcome.Done)
                {
                    if (change is LinuxServiceSetup.Change.Install or LinuxServiceSetup.Change.TurnOn)
                        LinuxServiceSetup.WaitForSocket(TimeSpan.FromSeconds(5));
                    //A new service, or none: connect anew instead of waiting out the retry pause
                    LinuxServiceClient.RetryNow();
                }
                return result;
            });
            busy = false;
            okButton.IsEnabled = cancelButton.IsEnabled = true;
            if (outcome == LinuxServiceSetup.Outcome.Done) ServiceChanged = true;
            serviceMessage.Text = outcome switch
            {
                LinuxServiceSetup.Outcome.Cancelled => L.Text("ServiceCancelled"),
                LinuxServiceSetup.Outcome.Failed => L.Format("ServiceFailed", error),
                _ => null
            };
            serviceMessage.IsVisible = serviceMessage.Text != null;
            LoadServiceState();
        }

        void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (showAllFiles.IsVisible && showAllFiles.IsChecked is { } showAll
                && showAll != LinuxServiceSettingsStore.ShowAllFiles)
            {
                LinuxServiceSettingsStore.Save(showAll);
                ShowAllFilesChanged = true;
            }
            Close(true);
        }

        void Cancel_Click(object sender, RoutedEventArgs e) => Close(false);
    }
}
