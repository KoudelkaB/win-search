using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace search
{
    /// <summary>
    /// Sets up the Linux file system service from Settings, as root through polkit (pkexec) -
    /// one administrator prompt per change, like the Windows installer's Modify.
    /// <list type="bullet">
    /// <item>The .rpm and .deb packages install the app, root-owned, in PackageFolder and ship
    /// the service's unit: the service runs these very files, turned on at install. Settings -
    /// of any copy of the app - only turns it off and on (systemctl); the package manager
    /// updates and removes it.</item>
    /// <item>The portable tar.gz (or a build) can be anywhere, also where its user can change
    /// it - root must never run such files. install-service.sh copies it, root-owned, to
    /// InstallFolder for the service; uninstall-service.sh removes both. Updating the app
    /// leaves that copy on the old version until Settings updates it.</item>
    /// </list>
    /// </summary>
    internal static class LinuxServiceSetup
    {
        public enum Change { Install, Remove, TurnOn, TurnOff }
        public enum Outcome { Done, Cancelled, Failed }

        /// <summary>Where the packages install the app, and their unit; tests point them elsewhere</summary>
        internal static string PackageFolder = "/usr/lib/file-search-manager";
        internal static string PackageUnit = "/usr/lib/systemd/system/file-search-manager.service";
        /// <summary>The copy of a portable app the service runs (install-service.sh); tests point it elsewhere</summary>
        internal static string InstallFolder = "/opt/file-search-manager";
        /// <summary>This app's folder - the scripts and the files install-service.sh copies</summary>
        internal static string AppFolder = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        const string AppAssembly = "FileSearchManager.dll";
        const string Unit = "file-search-manager.service";

        /// <summary>Settings can set the service up: Linux, outside a sandbox the service would refuse</summary>
        public static bool Supported => OperatingSystem.IsLinux() && !Shell.IsFlatpak;

        /// <summary>
        /// The service is the .rpm or .deb package's: it is turned off and on only - whichever
        /// copy of the app asks (the portable scripts refuse to touch it)
        /// </summary>
        public static bool FromPackage => File.Exists(PackageUnit);

        static string InstallScript => Path.Combine(AppFolder, "install-service.sh");
        static string UninstallScript => Path.Combine(AppFolder, "uninstall-service.sh");

        /// <summary>The app runs from the service's own copy - removing the service would delete it</summary>
        public static bool RunsFromInstallFolder => string.Equals(AppFolder, InstallFolder, StringComparison.Ordinal);

        /// <summary>Version of the files the service runs: the package's, or the portable app's copy; null without them</summary>
        public static string InstalledVersion => VersionOf(Path.Combine(FromPackage ? PackageFolder : InstallFolder, AppAssembly));

        /// <summary>Version of this app</summary>
        public static string AppVersion => VersionOf(Path.Combine(AppFolder, AppAssembly));

        /// <summary>The service runs another build than this app (null versions are not compared)</summary>
        public static bool IsOutdated(string installed, string app) => installed != null && app != null && installed != app;

        /// <summary>
        /// The versions as shown: without the commit after "+" - unless that is all that differs
        /// </summary>
        public static (string Installed, string App) Shown(string installed, string app)
        {
            static string Short(string v) => v?.Split('+')[0];
            return Short(installed) == Short(app) ? (installed, app) : (Short(installed), Short(app));
        }

        static string VersionOf(string assembly)
        {
            try { return File.Exists(assembly) ? FileVersionInfo.GetVersionInfo(assembly).ProductVersion : null; }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// The service is turned off (systemctl is-enabled says disabled) - rather than turned on
        /// and not running. Asks systemctl, so never on the UI thread.
        /// </summary>
        public static bool IsTurnedOff()
        {
            var systemctl = Shell.Which("systemctl");
            if (systemctl == null) return false;
            try
            {
                var start = new ProcessStartInfo(systemctl) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("is-enabled");
                start.ArgumentList.Add(Unit);
                using var process = Process.Start(start);
                var output = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(3000)) { process.Kill(); return false; }
                return output.Result.Trim() == "disabled";
            }
            catch (Exception) { return false; }
        }

        /// <summary>What root runs for the change: the program and its arguments</summary>
        internal static string[] Command(Change change, string systemctl = "systemctl") => change switch
        {
            //pkexec runs a program by its absolute path, with a clean environment
            Change.Install => new[] { "/bin/sh", InstallScript, AppFolder },
            Change.Remove => new[] { "/bin/sh", UninstallScript },
            Change.TurnOn => new[] { systemctl, "enable", "--now", Unit },
            _ => new[] { systemctl, "disable", "--now", Unit }
        };

        /// <summary>The command an administrator can run in a terminal instead</summary>
        public static string ManualCommand(Change change) => change switch
        {
            Change.Install => $"sudo sh {Quote(InstallScript)} {Quote(AppFolder)}",
            Change.Remove => $"sudo sh {Quote(UninstallScript)}",
            Change.TurnOn => $"sudo systemctl enable --now {Unit}",
            _ => $"sudo systemctl disable --now {Unit}"
        };

        static string Quote(string path) => "'" + path.Replace("'", "'\\''") + "'";

        /// <summary>
        /// Make the change as root. Blocks until it is made, the administrator prompt included -
        /// never on the UI thread.
        /// </summary>
        /// <returns>The outcome and, when it failed, why</returns>
        public static (Outcome Outcome, string Error) Run(Change change)
        {
            var script = change switch { Change.Install => InstallScript, Change.Remove => UninstallScript, _ => null };
            if (script != null && !File.Exists(script)) return (Outcome.Failed, $"{script} is missing");
            var systemctl = Shell.Which("systemctl");
            if (script == null && systemctl == null) return (Outcome.Failed, "systemctl is missing");
            var pkexec = Shell.Which("pkexec");
            if (pkexec == null) return (Outcome.Failed, L.Format("ServiceNoPkexec", ManualCommand(change)));
            var start = new ProcessStartInfo(pkexec)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in Command(change, systemctl)) start.ArgumentList.Add(argument);
            try
            {
                using var process = Process.Start(start);
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                $"service {change}: exit {process.ExitCode}\n{output.Result}{error.Result}".Debug();
                return process.ExitCode switch
                {
                    0 => (Outcome.Done, null),
                    126 => (Outcome.Cancelled, null), //pkexec: the prompt was dismissed
                    var code => (Outcome.Failed, LastLine(error.Result) ?? LastLine(output.Result) ?? $"exit code {code}")
                };
            }
            catch (Exception e) { return (Outcome.Failed, e.Message); }
        }

        /// <summary>
        /// After the service was installed or turned on: wait until its socket is there, so that
        /// the state shown next is the running service rather than "not running"
        /// </summary>
        public static void WaitForSocket(TimeSpan timeout)
        {
            var until = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (!Models.LinuxService.LinuxServiceClient.Installed && Environment.TickCount64 < until) Thread.Sleep(100);
        }

        static string LastLine(string text)
            => text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
    }
}
