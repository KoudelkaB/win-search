using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using search.Models.LinuxService;
using Xunit;

namespace search.Tests
{
    /// <summary>
    /// What Settings offers for each state of the Linux file system service. One class: the
    /// tests change LinuxServiceSetup's folders, which no other test reads.
    /// </summary>
    public class SettingsWindowTests
    {
        const string Old = "0.9.6+1111111", New = "0.9.7+2222222";

        static SettingsWindow Show(ServiceStatus status, bool admin, string installed, string app,
            bool fromPackage = false, bool turnedOff = false)
        {
            var window = new SettingsWindow(loadState: false);
            window.ShowServiceState(new SettingsWindow.ServiceState(status, admin, installed, app, fromPackage, turnedOff));
            return window;
        }

        static readonly string[] SetupButtons = { "installService", "updateService", "removeService", "turnOnService", "turnOffService" };

        /// <summary>The setup buttons shown, in their order</summary>
        static string[] Shown(Window window) => System.Array.FindAll(SetupButtons, name => Button(window, name).IsVisible);

        static Button Button(Window window, string name) => window.FindControl<Button>(name);

        [AvaloniaFact]
        public void WithoutTheServiceOnlyInstallIsOffered()
        {
            var window = Show(ServiceStatus.NotInstalled, admin: false, installed: null, app: New);
            Assert.Equal(new[] { "installService" }, Shown(window));
            Assert.True(Button(window, "installService").IsEnabled);
            Assert.False(window.FindControl<TextBlock>("serviceVersion").IsVisible);
            //The portable app's service runs a copy - Settings says so
            Assert.True(window.FindControl<TextBlock>("serviceCopy").IsVisible);
            //The every-file choice waits for the service
            Assert.True(window.FindControl<CheckBox>("showAllFiles").IsEnabled);
        }

        [AvaloniaFact]
        public void AnOlderServiceOffersUpdateAndShowsBothVersions()
        {
            var window = Show(ServiceStatus.OwnFiles, admin: false, installed: Old, app: New);
            Assert.Equal(new[] { "updateService", "removeService" }, Shown(window));
            var version = window.FindControl<TextBlock>("serviceVersion");
            Assert.True(version.IsVisible);
            Assert.Equal(L.Format("ServiceOutdated", "0.9.6", "0.9.7"), version.Text);
            Assert.EndsWith(L.Text("ServiceNotAdmin"), window.FindControl<TextBlock>("serviceState").Text);
            //Only an administrator may switch every file on while the service runs
            Assert.False(window.FindControl<CheckBox>("showAllFiles").IsEnabled);
        }

        [AvaloniaFact]
        public void ACurrentRunningServiceOffersOnlyRemove()
        {
            var window = Show(ServiceStatus.AllFiles, admin: true, installed: New, app: New);
            Assert.Equal(new[] { "removeService" }, Shown(window));
            Assert.True(Button(window, "removeService").IsEnabled);
            Assert.True(window.FindControl<CheckBox>("showAllFiles").IsEnabled);
        }

        [AvaloniaFact]
        public void AServiceThatDoesNotAnswerCanBeInstalledAgain()
        {
            var window = Show(ServiceStatus.NotRunning, admin: false, installed: New, app: New);
            Assert.Equal(new[] { "updateService", "removeService" }, Shown(window));
        }

        [AvaloniaFact]
        public void ThePackagesServiceIsOnlyTurnedOff()
        {
            var window = Show(ServiceStatus.AllFiles, admin: true, installed: null, app: New, fromPackage: true);
            Assert.Equal(new[] { "turnOffService" }, Shown(window));
            //It runs the package's files: no copy, no version to compare
            Assert.False(window.FindControl<TextBlock>("serviceCopy").IsVisible);
            Assert.False(window.FindControl<TextBlock>("serviceVersion").IsVisible);
        }

        [AvaloniaFact]
        public void ThePackagesServiceTurnedOffIsTurnedOnAgain()
        {
            var window = Show(ServiceStatus.NotRunning, admin: false, installed: null, app: New, fromPackage: true, turnedOff: true);
            Assert.Equal(new[] { "turnOnService" }, Shown(window));
            Assert.Equal(L.Text("ServiceTurnedOff"), window.FindControl<TextBlock>("serviceState").Text);
            Assert.True(window.FindControl<CheckBox>("showAllFiles").IsEnabled);
        }

        [AvaloniaFact]
        public void ThePackagesServiceTurnedOnButFailingCanBeStartedOrTurnedOff()
        {
            var window = Show(ServiceStatus.NotRunning, admin: false, installed: null, app: New, fromPackage: true);
            Assert.Equal(new[] { "turnOnService", "turnOffService" }, Shown(window));
            Assert.Equal(L.Text("ServiceNotRunning"), window.FindControl<TextBlock>("serviceState").Text);
        }

        [AvaloniaFact]
        public void TheServicesOwnCopyCannotRemoveItself()
        {
            var folder = LinuxServiceSetup.AppFolder;
            LinuxServiceSetup.AppFolder = LinuxServiceSetup.InstallFolder;
            try
            {
                var window = Show(ServiceStatus.AllFiles, admin: true, installed: New, app: New);
                Assert.True(Button(window, "removeService").IsVisible);
                Assert.False(Button(window, "removeService").IsEnabled);
                Assert.Equal(L.Text("ServiceRemoveRunning"), ToolTip.GetTip(Button(window, "removeService")));
            }
            finally { LinuxServiceSetup.AppFolder = folder; }
        }

        [Theory]
        [InlineData("0.9.6+aaa", "0.9.7+bbb", "0.9.6", "0.9.7")]
        [InlineData("0.9.7+aaa", "0.9.7+bbb", "0.9.7+aaa", "0.9.7+bbb")] //Only the commit differs: keep it
        [InlineData(null, "0.9.7+bbb", null, "0.9.7")]
        public void VersionsAreShownWithoutTheCommitUnlessThatIsTheDifference(string installed, string app,
            string shownInstalled, string shownApp)
            => Assert.Equal((shownInstalled, shownApp), LinuxServiceSetup.Shown(installed, app));

        [Theory]
        [InlineData("0.9.6+a", "0.9.7+b", true)]
        [InlineData("0.9.7+a", "0.9.7+a", false)]
        [InlineData(null, "0.9.7+a", false)] //Not installed
        [InlineData("0.9.7+a", null, false)] //This app's version is unknown
        public void OnlyAnotherKnownVersionIsOutdated(string installed, string app, bool outdated)
            => Assert.Equal(outdated, LinuxServiceSetup.IsOutdated(installed, app));

        [Fact]
        public void TheManualCommandQuotesThePaths()
        {
            var folder = LinuxServiceSetup.AppFolder;
            LinuxServiceSetup.AppFolder = "/home/user/My Apps/it's here";
            try
            {
                Assert.Equal("sudo sh '/home/user/My Apps/it'\\''s here/install-service.sh' '/home/user/My Apps/it'\\''s here'",
                    LinuxServiceSetup.ManualCommand(LinuxServiceSetup.Change.Install));
                Assert.Equal("sudo sh '/home/user/My Apps/it'\\''s here/uninstall-service.sh'",
                    LinuxServiceSetup.ManualCommand(LinuxServiceSetup.Change.Remove));
                //The script gets the folder as one argument, quotes and all
                Assert.Equal(new[] { "/bin/sh", "/home/user/My Apps/it's here/install-service.sh", "/home/user/My Apps/it's here" },
                    LinuxServiceSetup.Command(LinuxServiceSetup.Change.Install));
            }
            finally { LinuxServiceSetup.AppFolder = folder; }
        }

        [Fact]
        public void TheServiceIsTurnedOnAndOffWithSystemctl()
        {
            Assert.Equal(new[] { "/usr/bin/systemctl", "enable", "--now", "file-search-manager.service" },
                LinuxServiceSetup.Command(LinuxServiceSetup.Change.TurnOn, "/usr/bin/systemctl"));
            Assert.Equal(new[] { "/usr/bin/systemctl", "disable", "--now", "file-search-manager.service" },
                LinuxServiceSetup.Command(LinuxServiceSetup.Change.TurnOff, "/usr/bin/systemctl"));
            Assert.Equal("sudo systemctl disable --now file-search-manager.service",
                LinuxServiceSetup.ManualCommand(LinuxServiceSetup.Change.TurnOff));
        }

        [AvaloniaFact]
        public void AnotherCopyOfTheAppTurnsThePackagesServiceOffToo()
        {
            //A portable or development build next to the package: the scripts would refuse
            //to update or remove the package's service, so only on and off are offered - and
            //which version the service runs
            var window = Show(ServiceStatus.AllFiles, admin: true, installed: Old, app: New, fromPackage: true);
            Assert.Equal(new[] { "turnOffService" }, Shown(window));
            Assert.True(window.FindControl<TextBlock>("serviceVersion").IsVisible);
        }

        [Fact]
        public void TheServiceIsThePackagesWhileItsUnitIsInstalled()
        {
            var (unit, folder) = (LinuxServiceSetup.PackageUnit, LinuxServiceSetup.PackageFolder);
            var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fsm-unit-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                LinuxServiceSetup.PackageUnit = System.IO.Path.Combine(temp, "file-search-manager.service");
                LinuxServiceSetup.PackageFolder = temp;
                Assert.False(LinuxServiceSetup.FromPackage);
                System.IO.Directory.CreateDirectory(temp);
                System.IO.File.WriteAllText(LinuxServiceSetup.PackageUnit, "");
                Assert.True(LinuxServiceSetup.FromPackage);
                //Its version is the package's files' - none here
                Assert.Null(LinuxServiceSetup.InstalledVersion);
            }
            finally
            {
                (LinuxServiceSetup.PackageUnit, LinuxServiceSetup.PackageFolder) = (unit, folder);
                if (System.IO.Directory.Exists(temp)) System.IO.Directory.Delete(temp, recursive: true);
            }
        }
    }
}
