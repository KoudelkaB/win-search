#if WINDOWS_HOST
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Xunit;

namespace search.Smoke
{
    [CollectionDefinition(nameof(WindowsAppCollection))]
    public sealed class WindowsAppCollection : ICollectionFixture<WindowsApp> { }

    /// <summary>
    /// The published app started once for the Windows smoke tests, with its own data folder
    /// and a work folder of files named after this run's token, driven through UI Automation.
    /// The keyboard and the mouse are real: leave them alone while the tests run.
    /// </summary>
    public sealed class WindowsApp : IAsyncLifetime
    {
        public string Root { get; private set; }
        public string Data { get; private set; }
        public string Work { get; private set; }
        /// <summary>Unique to this run - a filter for it shows only the run's own files</summary>
        public string Token { get; private set; }
        /// <summary>A file that exists before the app starts</summary>
        public string Before { get; private set; }
        public long LoadedFiles { get; private set; }

        public UIA3Automation Automation { get; private set; }
        public Application App { get; private set; }
        public Window Main { get; private set; }

        (uint Format, byte[] Data)[] clipboard;
        Process process;

        public async ValueTask InitializeAsync()
        {
            if (!OperatingSystem.IsWindows()) return;
            Token = "smk" + Guid.NewGuid().ToString("N")[..12];
            Root = Path.Combine(Path.GetTempPath(), "win-search-smoke-" + Token);
            Data = Path.Combine(Root, "data");
            //Not under %LOCALAPPDATA% (and so not in %TEMP%): WebView2 reads no file from there
            Work = PublishedApp.WorkFolder(Token);
            Directory.CreateDirectory(Work);
            Before = Path.Combine(Work, $"{Token}-before.txt");
            File.WriteAllText(Before, $"smoke {Token} before");
            PublishedApp.PrepareData(Data, Work);
            clipboard = SystemClipboard.Snapshot();

            var exe = PublishedApp.Executable;
            //"UI": never the elevated helper, so never a UAC prompt
            var start = new ProcessStartInfo(exe, "UI") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) };
            start.Environment[PublishedApp.DataFolderVariable] = Data;
            Automation = new UIA3Automation();
            //Started here, not by FlaUI: only the starting Process object can read the exit code
            process = Process.Start(start);
            App = Application.Attach(process.Id);
            Main = App.GetMainWindow(Automation, TimeSpan.FromSeconds(60))
                ?? throw new TimeoutException("The main window did not appear in 60 s");
            LoadedFiles = await WaitFor(() => LoadedCount(), TimeSpan.FromMinutes(5), "the index load");
        }

        /// <summary>Close the main window as a user does; the exit code, null when the app did not end in time</summary>
        public int? CloseMainWindow(TimeSpan timeout)
        {
            Main.Close();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds)) return null;

            return process.ExitCode;
        }

        public ValueTask DisposeAsync()
        {
            if (!OperatingSystem.IsWindows() || App == null) return ValueTask.CompletedTask;
            try
            {
                foreach (var window in OtherWindows()) window.Close();
                using var process = Process.GetProcessById(App.ProcessId);
                Main.Close();
                if (!process.WaitForExit(15000)) App.Kill();
            }
            catch (ArgumentException) { } //Exited already
            catch { try { App.Kill(); } catch { } }
            App.Dispose();
            Automation.Dispose();
            SystemClipboard.Restore(clipboard);
            PublishedApp.Delete(Root);
            PublishedApp.Delete(Work);
            return ValueTask.CompletedTask;
        }

        #region Finding things

        public TextBox Filter => Main.FindFirstDescendant(cf => cf.ByAutomationId("filterTextBox"))
            .FindFirstChild(cf => cf.ByAutomationId("TextBox")).AsTextBox();

        public TextBox Find => Main.FindFirstDescendant(cf => cf.ByAutomationId("findTextBox"))
            .FindFirstChild(cf => cf.ByAutomationId("TextBox")).AsTextBox();

        public AutomationElement Grid => Main.FindFirstDescendant(cf => cf.ByAutomationId("filesView"));

        public AutomationElement[] Rows => Grid.FindAllChildren(cf => cf.ByControlType(ControlType.DataItem));

        /// <summary>The cells of a row: marker, Name, Size, Count, Changed, Folder</summary>
        public static AutomationElement[] Cells(AutomationElement row) => row.FindAllChildren(cf => cf.ByClassName("DataGridCell"));

        public static string NameOf(AutomationElement row) => Cells(row)[1].Name;

        public static string FolderOf(AutomationElement row) => Cells(row)[5].Name;

        public string[] RowNames => Rows.Select(NameOf).ToArray();

        public AutomationElement Row(string name) => Rows.FirstOrDefault(r => NameOf(r) == name);

        public Button ButtonNamed(string text) => Main.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .FirstOrDefault(b => b.Name.Contains(text, StringComparison.Ordinal))?.AsButton();

        public string[] Texts => Main.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(t => t.Name).ToArray();

        /// <summary>Files of the index as the status bar reports them after a load, 0 before</summary>
        long LoadedCount()
        {
            var loaded = Texts.Select(t => Regex.Match(t, @"^Loaded ([\d\s  ]+) files")).FirstOrDefault(m => m.Success);
            return loaded == null ? 0 : long.Parse(Regex.Replace(loaded.Groups[1].Value, @"\D", ""));
        }

        /// <summary>The app's windows besides the main one: dialogs, help, web apps</summary>
        public Window[] OtherWindows() => App.GetAllTopLevelWindows(Automation)
            .Where(w => w.Properties.ProcessId.ValueOrDefault == App.ProcessId && !w.Equals(Main))
            //Avalonia shows the windows it owns (dialogs, help, web apps) under the main window
            .Concat(Main.FindAllChildren(cf => cf.ByControlType(ControlType.Window)).Select(w => w.AsWindow()))
            .Concat(Main.ModalWindows)
            .GroupBy(w => w.Properties.NativeWindowHandle.ValueOrDefault).Select(g => g.First())
            .ToArray();

        /// <summary>A window of the app of this class (MainWindow's class names: HelpWindow, WebAppWindow…)</summary>
        public Task<Window> Opened(string className, TimeSpan? timeout = null) => WaitFor(
            () => OtherWindows().FirstOrDefault(w => w.ClassName == className),
            timeout ?? TimeSpan.FromSeconds(20), $"a {className}");

        #endregion

        #region Doing things

        /// <summary>Show only the rows the filter matches; the grid follows the typed text</summary>
        public async Task<string[]> ShowOnly(string filter, Func<string[], bool> until)
        {
            Filter.Text = filter;
            //The grid publishes the filter's rows in steps (and moves them when a change
            //arrives): wait for the rows asked for, then for a moment without a change
            string[] last = null;
            return await WaitFor(() =>
            {
                var names = RowNames;
                var settled = last != null && names.SequenceEqual(last);
                last = names;
                return settled && until(names) ? names : null;
            }, TimeSpan.FromSeconds(30), $"the rows of the filter \"{filter}\"", poll: 400);
        }

        /// <summary>Select the row of this name with a real click on its Name cell; the grid then has the keyboard</summary>
        public async Task SelectRow(string name)
        {
            await WaitUntil(() =>
            {
                Select(Row(name) ?? throw new InvalidOperationException($"No row {name}"));
                //The click landed on the row asked for: it is the one selection
                return Texts.Contains("Selected: 1");
            }, TimeSpan.FromSeconds(15), $"the row {name} selected");
        }

        /// <summary>Click the row's Name cell - a real click, so the grid has the keyboard</summary>
        public void Select(AutomationElement row)
        {
            Foreground();
            Cells(row)[1].Click();
            Wait.UntilInputIsProcessed();
        }

        /// <summary>Real keys, to the app only</summary>
        public void Press(params VirtualKeyShort[] keys)
        {
            Foreground();
            Keyboard.TypeSimultaneously(keys);
            Wait.UntilInputIsProcessed();
        }

        /// <summary>Real typing, to the app only</summary>
        public void Type(string text)
        {
            Foreground();
            Keyboard.Type(text);
            Wait.UntilInputIsProcessed();
        }

        /// <summary>
        /// Real input goes to the foreground window, whichever program it belongs to. Nothing
        /// is sent before the app's own window is in front - a test that cannot get it there
        /// is skipped on a desktop somebody works at, and fails in CI.
        /// </summary>
        void Foreground()
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (ForegroundIsOurs()) return;
                ForegroundWindow.Bring(Main.Properties.NativeWindowHandle.Value);
                System.Threading.Thread.Sleep(200);
            }
            if (ForegroundIsOurs()) return;
            const string reason = "The app's window could not be brought to the front, so no key or click was sent: "
                + "another window keeps the foreground. Leave the desktop alone while the smoke tests run.";
            if (Environment.GetEnvironmentVariable("CI") is { Length: > 0 }) Assert.Fail(reason);
            Assert.Skip(reason);
        }

        /// <summary>The foreground window is the app's main window</summary>
        bool ForegroundIsOurs() => ForegroundWindow.Handle() == Main.Properties.NativeWindowHandle.Value;

        /// <summary>
        /// Back to the plain start for the next test: no dialog or web app open, the work
        /// folder's filter gone - through UI Automation, without a key or a click
        /// </summary>
        public void Reset()
        {
            foreach (var window in OtherWindows()) window.Close();
            Filter.Text = "";
        }

        public static async Task<T> WaitFor<T>(Func<T> probe, TimeSpan timeout, string what, int poll = 100)
        {
            var watch = Stopwatch.StartNew();
            Exception last = null;
            while (watch.Elapsed < timeout)
            {
                try
                {
                    var value = probe();
                    if (value is not null && !EqualityComparer<T>.Default.Equals(value, default)) return value;
                }
                //The tree changes under the probe; a skip or a failure of the test still ends it
                catch (Exception e) when (e.GetType().Namespace?.StartsWith("Xunit") != true) { last = e; }
                await Task.Delay(poll);
            }
            throw new TimeoutException($"Waited {timeout.TotalSeconds:0} s for {what}" + (last == null ? "" : $" (last error: {last.Message})"));
        }

        public static Task WaitUntil(Func<bool> condition, TimeSpan timeout, string what)
            => WaitFor(() => condition() ? (object)true : null, timeout, what);

        #endregion
    }
}
#endif
