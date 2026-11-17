using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace search.Smoke
{
    /// <summary>
    /// The published build on an X display: the walk of the work folder's file system, live
    /// changes, the clipboard of the file managers, rename on disk, help and a clean exit.
    /// Each test leaves the keyboard in the filter box, where the next one expects it.
    /// </summary>
    [Collection(nameof(LinuxAppCollection))]
    public sealed class LinuxSmokeTests
    {
        readonly LinuxApp app;
        static readonly TimeSpan Load = TimeSpan.FromMinutes(5), Live = TimeSpan.FromSeconds(30);

        public LinuxSmokeTests(LinuxApp app)
        {
            Assert.SkipWhen(app.Missing != null, app.Missing ?? "");
            this.app = app;
        }

        [Fact]
        public async Task AFileThatExistedBeforeTheStartIsFound()
        {
            var name = Path.GetFileName(app.Before);
            app.Filter(name);

            //Found once the walk of the drive is in: until then the filter shows nothing
            await LinuxApp.WaitFor(() => app.SelectedName() == name ? name : null, Load, "the file in the results");
            app.ToFilter();
            Assert.True(File.Exists(Path.Combine(app.Data, "log.txt")));
        }

        [Fact]
        public async Task AFileCreatedWhileRunningIsFoundAfterF12()
        {
            await Loaded();
            var live = Path.Combine(app.Work, $"{app.Token}-live.txt");
            app.Filter(Path.GetFileName(live));

            File.WriteAllText(live, "live");
            //Without the root service nobody watches the drives (the app looks again when its
            //window is activated); F12 walks them again at once
            app.Keys("F12");

            await LinuxApp.WaitFor(() => app.SelectedName() == Path.GetFileName(live) ? "found" : null, Load, "the new file");
            app.ToFilter();
        }

        [Fact]
        public async Task CopyPutsTheFileOnTheClipboardOfTheFileManagers()
        {
            await Loaded();
            app.Filter(Path.GetFileName(app.Before));
            await LinuxApp.WaitFor(() => app.SelectedName() == Path.GetFileName(app.Before) ? "row" : null, Live, "the file's row");

            app.Keys("ctrl+c");

            var uri = new Uri(app.Before).AbsoluteUri;
            await LinuxApp.WaitUntil(() => X11.Clipboard("x-special/gnome-copied-files").Replace("\r", "") == $"copy\n{uri}",
                TimeSpan.FromSeconds(10), "the GNOME file list on the clipboard");
            app.ToFilter();
        }

        [Fact]
        public async Task F2RenamesTheFileOnDisk()
        {
            await Loaded();
            var file = Path.Combine(app.Work, $"{app.Token}-rename.txt");
            File.WriteAllText(file, "rename me");
            app.Filter(Path.GetFileName(file));
            //Nobody watches the drives without the root service: F12 walks them again
            app.Keys("F12");
            await LinuxApp.WaitFor(() => app.SelectedName() == Path.GetFileName(file) ? "row" : null, Load, "the file's row");

            app.Keys(X11.F2);
            // F12 may still be publishing rows. Wait until the inline editor owns the
            // keyboard and exposes its selected stem, instead of typing after a fixed delay.
            string copied = null;
            try
            {
                await LinuxApp.WaitUntil(() =>
                {
                    app.Keys("ctrl+c");
                    copied = X11.Clipboard();
                    return copied == Path.GetFileNameWithoutExtension(file);
                }, TimeSpan.FromSeconds(10), "the selected name in the rename editor");
            }
            catch (TimeoutException error)
            {
                var diagnostics = Path.Combine(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")
                    ?? Environment.CurrentDirectory, "TestResults", "Diagnostics");
                Directory.CreateDirectory(diagnostics);
                try { PublishedApp.Run("xwd", $"-root -silent -out {Path.Combine(diagnostics, "rename.xwd")}"); }
                catch (Exception captureError) { Console.WriteLine(captureError.Message); }
                var log = Path.Combine(app.Data, "log.txt");
                if (File.Exists(log)) File.Copy(log, Path.Combine(diagnostics, "app.log"), overwrite: true);
                foreach (var trace in Directory.GetFiles(app.Data, "*.log"))
                    File.Copy(trace, Path.Combine(diagnostics, Path.GetFileName(trace)), overwrite: true);
                throw new TimeoutException($"{error.Message}; clipboard: {copied}; files: {string.Join(", ", Directory.GetFiles(app.Work))}", error);
            }
            //The name without its extension is selected in the editor: typing replaces it
            app.Type($"{app.Token}-renamed");
            app.Keys("Return");

            var renamed = Path.Combine(app.Work, $"{app.Token}-renamed.txt");
            await LinuxApp.WaitUntil(() => File.Exists(renamed) && !File.Exists(file), TimeSpan.FromSeconds(10), "the renamed file");
            app.ToFilter();
        }

        [Fact]
        public async Task F1OpensTheHelp()
        {
            app.Keys("F1");

            var help = await LinuxApp.WaitFor(() => X11.FindWindow("^Help$", app.Process.Id), TimeSpan.FromSeconds(20), "the help window");
            X11.Close(help);
            await LinuxApp.WaitUntil(() => X11.FindWindow("^Help$", app.Process.Id) == null, TimeSpan.FromSeconds(10), "the help to close");
            //Back in the main window the keys reach the filter again (X11 left nothing focused)
            await Loaded();
        }

        /// <summary>
        /// L opens the file in Log explorer, the program of the build (logexplorer/): its
        /// window is titled with the file and shows the file's rows. A build made without
        /// Rust has no Log explorer - a skip here, a failure in the workflows (CI).
        /// </summary>
        [Fact]
        public async Task LOpensTheFileInTheNativeLogExplorer()
        {
            var program = Path.Combine(Path.GetDirectoryName(PublishedApp.Executable), "LogExplorer", "logexplorer");
            if (!File.Exists(program))
            {
                const string reason = "This build has no Log explorer (LogExplorer/logexplorer): build logexplorer/ with cargo first";
                if (Environment.GetEnvironmentVariable("CI") is { Length: > 0 }) Assert.Fail(reason);
                Assert.Skip(reason);
            }
            await Loaded();
            var name = Path.GetFileName(app.Before);
            app.Filter(name);
            //Reading the name leaves the keyboard on the file's row
            await LinuxApp.WaitFor(() => app.SelectedName() == name ? "row" : null, Live, "the file's row");

            app.Keys("l");

            var title = $"^{System.Text.RegularExpressions.Regex.Escape(name)} - Log Explorer$";
            var window = await LinuxApp.WaitFor(() => X11.FindWindow(title), TimeSpan.FromSeconds(30), "the Log explorer window");
            try
            {
                var expected = File.ReadAllText(app.Before).Replace("\r\n", "\n").TrimEnd('\n');
                await LinuxApp.WaitUntil(() =>
                {
                    X11.Activate(window);
                    X11.Key("ctrl+a");
                    X11.Key("ctrl+c");
                    return X11.Clipboard().Replace("\r\n", "\n").TrimEnd('\n') == expected;
                }, TimeSpan.FromSeconds(20), "the rows of the file in Log explorer");
            }
            finally
            {
                X11.Close(window);
            }
            await LinuxApp.WaitUntil(() => X11.FindWindow(title) == null, TimeSpan.FromSeconds(10), "Log explorer to close");
            //Back in the main window the keys reach the filter again
            await Loaded();
        }

        /// <summary>The walk is in: the file made before the start is found</summary>
        async Task Loaded()
        {
            app.Filter(Path.GetFileName(app.Before));
            await LinuxApp.WaitFor(() => app.SelectedName() == Path.GetFileName(app.Before) ? "loaded" : null, Load, "the index load");
            app.ToFilter();
        }
    }

    /// <summary>A start of its own, closed through its window</summary>
    public sealed class LinuxExitSmokeTests
    {
        [Fact]
        public async Task ClosingTheWindowEndsTheAppAndKeepsItsSettings()
        {
            var session = new LinuxApp();
            await session.InitializeAsync();
            try
            {
                Assert.SkipWhen(session.Missing != null, session.Missing ?? "");

                X11.Close(session.Window);

                Assert.True(session.Process.WaitForExit(30000), "The app did not end in 30 s");
                Assert.Equal(0, session.Process.ExitCode);
                Assert.True(File.Exists(Path.Combine(session.Data, "window-layout.json")));
            }
            finally { await session.DisposeAsync(); }
        }
    }
}
