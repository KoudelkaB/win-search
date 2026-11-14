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

            app.Keys("F2");
            await Task.Delay(500);
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
