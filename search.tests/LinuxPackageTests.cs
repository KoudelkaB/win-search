using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace search.Tests
{
    /// <summary>
    /// The .rpm and .deb packages' scripts (installer/linux/package), run against a systemctl
    /// that only notes what it is asked: rpm passes counts (1 install, 2 update, 0 removal),
    /// dpkg actions ("configure" with the old version on an update, "remove", "upgrade").
    /// </summary>
    public sealed class LinuxPackageTests : IDisposable
    {
        const string Unit = "file-search-manager.service";
        readonly string dir = Path.Combine(Path.GetTempPath(), "fsm-package-" + Guid.NewGuid().ToString("N"));
        string Root => Path.Combine(dir, "root");
        string Log => Path.Combine(dir, "systemctl.log");

        public LinuxPackageTests()
        {
            Directory.CreateDirectory(Path.Combine(dir, "bin"));
            Directory.CreateDirectory(Root);
            var systemctl = Path.Combine(dir, "bin", "systemctl");
            //is-enabled answers as FAKE_ENABLED says; everything else succeeds
            File.WriteAllText(systemctl, "#!/bin/sh\necho \"$*\" >> \"$FAKE_LOG\"\n"
                + "[ \"$1\" != is-enabled ] || [ \"$FAKE_ENABLED\" = yes ]\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(systemctl, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public void Dispose()
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }

        static string Script(string name)
        {
            for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
            {
                var path = Path.Combine(folder.FullName, "installer", "linux", "package", name);
                if (File.Exists(path)) return path;
            }
            throw new FileNotFoundException(name);
        }

        /// <summary>What the script asked systemctl, in order</summary>
        string[] Run(string script, bool enabled, params string[] arguments)
        {
            var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardError = true };
            start.ArgumentList.Add(Script(script));
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["PATH"] = Path.Combine(dir, "bin") + ":/usr/bin:/bin";
            start.Environment["FAKE_LOG"] = Log;
            start.Environment["FAKE_ENABLED"] = enabled ? "yes" : "no";
            start.Environment["FSM_ROOT"] = Root;
            using var process = Process.Start(start);
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
            var calls = File.Exists(Log) ? File.ReadAllLines(Log) : Array.Empty<string>();
            File.Delete(Log);
            return calls;
        }

        [Theory]
        [InlineData("1")]                //rpm
        [InlineData("configure", "")]    //dpkg
        public void AFirstInstallTurnsTheServiceOn(params string[] arguments)
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "the packages are Linux ones");
            Assert.Equal(new[] { "daemon-reload", $"enable --now {Unit}" }, Run("postinstall.sh", false, arguments));
        }

        [Theory]
        [InlineData("2")]
        [InlineData("configure", "0.9.6")]
        public void AnUpdateKeepsTheChoiceAndRestartsARunningService(params string[] arguments)
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "the packages are Linux ones");
            //Turned off by the user: try-restart leaves a stopped service stopped
            Assert.Equal(new[] { "daemon-reload", $"try-restart {Unit}" }, Run("postinstall.sh", false, arguments));
        }

        [Theory]
        [InlineData(true, "1")]                   //rpm, first install
        [InlineData(false, "1")]
        [InlineData(true, "configure", "")]       //dpkg, first install
        [InlineData(false, "configure", "")]
        [InlineData(true, "configure", "0.9.6")]  //an update
        [InlineData(false, "configure", "0.9.6")]
        public void ThePortableAppsServiceGivesWayToThePackagesKeepingItsChoice(bool wasOn, params string[] arguments)
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "the packages are Linux ones");
            var unit = Path.Combine(Root, "etc", "systemd", "system", Unit);
            var copy = Path.Combine(Root, "opt", "file-search-manager");
            Directory.CreateDirectory(Path.GetDirectoryName(unit));
            Directory.CreateDirectory(copy);
            File.WriteAllText(unit, "[Service]\nExecStart=/opt/file-search-manager/FileSearchManager --service\n");
            File.WriteAllText(Path.Combine(copy, "FileSearchManager"), "");

            //Turned off by hand, it stays off - even at the package's first install
            var calls = Run("postinstall.sh", wasOn, arguments);

            Assert.Equal(new[] { $"is-enabled --quiet {Unit}", $"disable --now {Unit}", "daemon-reload",
                wasOn ? $"enable --now {Unit}" : $"try-restart {Unit}" }, calls);
            Assert.False(File.Exists(unit));
            Assert.False(Directory.Exists(copy));
        }

        [Fact]
        public void AUnitOfSomethingElseIsLeftAlone()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "the packages are Linux ones");
            //An administrator's own override of the unit, not install-service.sh's
            var unit = Path.Combine(Root, "etc", "systemd", "system", Unit);
            Directory.CreateDirectory(Path.GetDirectoryName(unit));
            File.WriteAllText(unit, "[Service]\nExecStart=/usr/lib/file-search-manager/FileSearchManager --service\n");

            Assert.Equal(new[] { "daemon-reload", $"try-restart {Unit}" }, Run("postinstall.sh", true, "2"));
            Assert.True(File.Exists(unit));
        }

        [Theory]
        [InlineData("0", true)]
        [InlineData("remove", true)]
        [InlineData("1", false)]        //rpm update
        [InlineData("upgrade", false)]  //dpkg update
        public void OnlyARemovalTurnsTheServiceOff(string argument, bool turnsOff)
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "the packages are Linux ones");
            Assert.Equal(turnsOff ? new[] { $"disable --now {Unit}" } : Array.Empty<string>(),
                Run("preremove.sh", false, argument));
            Assert.Equal(new[] { "daemon-reload" }, Run("postremove.sh", false, argument));
        }
    }
}
