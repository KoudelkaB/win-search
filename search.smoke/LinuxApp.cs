using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace search.Smoke
{
    [CollectionDefinition(nameof(LinuxAppCollection))]
    public sealed class LinuxAppCollection : ICollectionFixture<LinuxApp> { }

    /// <summary>
    /// The published app started once for the Linux smoke tests on an X display (xvfb-run
    /// or a desktop), with its own data folder. It is driven by real keys only; what it did
    /// is read from the clipboard, the disk and its windows - no accessibility bus needed.
    /// </summary>
    public sealed class LinuxApp : IAsyncLifetime
    {
        public const string Title = "^File Search Manager$";

        /// <summary>Why the tests are skipped here, null when they run</summary>
        public string Missing { get; private set; }
        public string Root { get; private set; }
        public string Data { get; private set; }
        public string Work { get; private set; }
        public string Token { get; private set; }
        public string Before { get; private set; }
        public string Window { get; private set; }
        public Process Process { get; private set; }

        /// <summary>The keyboard is in the results, not in the filter box (Right Shift switches)</summary>
        bool inResults;

        public async ValueTask InitializeAsync()
        {
            if ((Missing = X11.Missing()) != null) return;
            Token = "smk" + Guid.NewGuid().ToString("N")[..12];
            Root = Path.Combine(Path.GetTempPath(), "win-search-smoke-" + Token);
            Data = Path.Combine(Root, "data");
            Work = PublishedApp.WorkFolder(Token);
            Directory.CreateDirectory(Work);
            Before = Path.Combine(Work, $"{Token}-before.txt");
            File.WriteAllText(Before, $"smoke {Token} before");
            PublishedApp.PrepareData(Data, Work);

            var exe = PublishedApp.Executable;
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) };
            start.Environment[PublishedApp.DataFolderVariable] = Data;
            Process = Process.Start(start);
            Window = await WaitFor(() => X11.FindWindow(Title, Process.Id), TimeSpan.FromSeconds(60), "the main window");
        }

        public ValueTask DisposeAsync()
        {
            if (Process == null) return ValueTask.CompletedTask;
            if (!Process.HasExited)
            {
                X11.Close(Window);
                if (!Process.WaitForExit(15000)) Process.Kill(entireProcessTree: true);
            }
            Process.Dispose();
            PublishedApp.Delete(Root);
            PublishedApp.Delete(Work);
            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// Real keys go to the focused window, whichever program it belongs to: nothing is
        /// sent before the app's window has the focus. Skipped on a desktop somebody works at,
        /// a failure in CI.
        /// </summary>
        public void Keys(string keys)
        {
            Focus();
            X11.Key(keys);
        }

        public void Type(string text)
        {
            Focus();
            X11.Type(text);
        }

        void Focus()
        {
            for (var attempt = 0; attempt < 10 && X11.ActiveWindow() != Window; attempt++)
            {
                X11.Activate(Window);
                System.Threading.Thread.Sleep(200);
            }
            if (X11.ActiveWindow() == Window) return;
            const string reason = "The app's window could not get the keyboard focus, so no key was sent.";
            if (Environment.GetEnvironmentVariable("CI") is { Length: > 0 }) Assert.Fail(reason);
            Assert.Skip(reason);
        }

        /// <summary>Type a new filter (the keyboard goes to the filter box first)</summary>
        public void Filter(string text)
        {
            ToFilter();
            Keys("ctrl+a");
            Type(text);
        }

        public void ToFilter()
        {
            if (!inResults) return;
            Keys("Shift_R");
            inResults = false;
        }

        public void ToResults()
        {
            if (inResults) return;
            Keys("Shift_R");
            inResults = true;
        }

        /// <summary>
        /// The name of the row the keyboard selects in the results (Down, then N copies the
        /// name) - the filter shows a single row; null while there is none
        /// </summary>
        public string SelectedName()
        {
            const string none = "-none-";
            X11.SetClipboard(none);
            ToResults();
            Keys("Down");
            Keys("n");
            System.Threading.Thread.Sleep(300);
            var text = X11.Clipboard()?.Trim();
            return string.IsNullOrEmpty(text) || text == none ? null : text;
        }

        public static async Task<T> WaitFor<T>(Func<T> probe, TimeSpan timeout, string what) where T : class
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < timeout)
            {
                if (probe() is { } value) return value;
                await Task.Delay(250);
            }
            throw new TimeoutException($"Waited {timeout.TotalSeconds:0} s for {what}");
        }

        public static Task WaitUntil(Func<bool> condition, TimeSpan timeout, string what)
            => WaitFor(() => condition() ? "yes" : null, timeout, what);
    }
}
