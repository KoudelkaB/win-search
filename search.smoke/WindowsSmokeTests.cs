#if WINDOWS_HOST
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace search.Smoke
{
    /// <summary>
    /// The published build on the real desktop: the index of the system drive, live changes,
    /// the system clipboard, rename on disk, help and the WebView2 apps
    /// </summary>
    [Collection(nameof(WindowsAppCollection))]
    public sealed class WindowsSmokeTests : IDisposable
    {
        readonly WindowsApp app;
        static readonly TimeSpan Live = TimeSpan.FromSeconds(20);

        public WindowsSmokeTests(WindowsApp app)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows desktop only");
            this.app = app;
            app.Reset();
        }

        public void Dispose() => app.Reset();

        string BeforeName => Path.GetFileName(app.Before);

        [Fact]
        public void TheBuildStartsAndIndexesTheSystemDrive()
        {
            Assert.Equal("File Search Manager", app.Main.Title);
            Assert.True(app.LoadedFiles > 1000, $"Only {app.LoadedFiles} files indexed");
            Assert.NotEmpty(app.Rows);
            //Its own data folder: settings and the log of this run, nothing of the user's
            Assert.True(File.Exists(Path.Combine(app.Data, "log.txt")));
        }

        [Fact]
        public async Task AFileThatExistedBeforeTheStartIsFound()
        {
            await app.ShowOnly(app.Token, names => names.Contains(BeforeName));

            Assert.Equal(app.Work, WindowsApp.FolderOf(app.Row(BeforeName)), ignoreCase: true);
            //Only this run's items match its token
            Assert.All(app.RowNames, name => Assert.Contains(app.Token, name));
        }

        [Fact]
        public async Task AFileCreatedWhileRunningAppearsAndLeavesWhenDeleted()
        {
            await app.ShowOnly(app.Token, names => names.Contains(BeforeName));
            var live = Path.Combine(app.Work, $"{app.Token}-live-{Guid.NewGuid():N}.txt");

            File.WriteAllText(live, "live");
            await WindowsApp.WaitUntil(() => app.Row(Path.GetFileName(live)) != null, Live, "the new file's row");

            File.Delete(live);
            await WindowsApp.WaitUntil(() => app.Row(Path.GetFileName(live)) == null, Live, "the deleted file's row to go");
        }

        [Theory]
        [InlineData(VirtualKeyShort.KEY_C, 1)]
        [InlineData(VirtualKeyShort.KEY_X, 2)]
        public async Task CopyAndCutPutTheFileOnTheSystemClipboardForExplorer(VirtualKeyShort key, int dropEffect)
        {
            await app.ShowOnly(app.Token, names => names.Contains(BeforeName));
            await app.SelectRow(BeforeName);

            app.Press(VirtualKeyShort.CONTROL, key);

            await WindowsApp.WaitUntil(() => SystemClipboard.Files().SequenceEqual(new[] { app.Before }, StringComparer.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(10), "the file on the clipboard");
            Assert.Equal(dropEffect, SystemClipboard.DropEffect());
            //A cut moves nothing until it is pasted
            Assert.True(File.Exists(app.Before));
        }

        [Fact]
        public async Task AFileCopiedInExplorerPastesIntoTheSelectedFolder()
        {
            var source = Path.Combine(app.Root, $"{app.Token}-from-explorer.txt");
            File.WriteAllText(source, "pasted");
            var dest = Directory.CreateDirectory(Path.Combine(app.Work, $"{app.Token}-dest-{Guid.NewGuid():N}")).FullName;
            await app.ShowOnly(app.Token, names => names.Contains(Path.GetFileName(dest)));
            await app.SelectRow(Path.GetFileName(dest));
            SystemClipboard.SetFiles(new[] { source });

            app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
            var dialog = await app.Opened("FileTransferDialog");
            Assert.True(dialog.FindFirstDescendant(cf => cf.ByAutomationId("CopyAction")).AsRadioButton().IsChecked);
            dialog.FindFirstDescendant(cf => cf.ByAutomationId("OkButton")).AsButton().Invoke();

            var pasted = Path.Combine(dest, Path.GetFileName(source));
            await WindowsApp.WaitUntil(() => File.Exists(pasted), Live, "the pasted file");
            Assert.Equal("pasted", File.ReadAllText(pasted));
            Assert.True(File.Exists(source));
        }

        [Fact]
        public async Task F2RenamesTheFileOnDisk()
        {
            var file = Path.Combine(app.Work, $"{app.Token}-rename.txt");
            File.WriteAllText(file, "rename me");
            await app.ShowOnly(app.Token, names => names.Contains(Path.GetFileName(file)));
            await app.SelectRow(Path.GetFileName(file));

            app.Press(VirtualKeyShort.F2);
            var editor = await WindowsApp.WaitFor(() => app.Grid.FindFirstDescendant(cf => cf.ByAutomationId("InlineNameEditor")
                .And(cf.ByControlType(ControlType.Edit)))?.AsTextBox(), TimeSpan.FromSeconds(10), "the name editor");
            Assert.Equal(Path.GetFileName(file), editor.Text);
            //The name without its extension is selected: typing replaces it
            app.Type($"{app.Token}-renamed");
            app.Press(VirtualKeyShort.RETURN);

            var renamed = Path.Combine(app.Work, $"{app.Token}-renamed.txt");
            await WindowsApp.WaitUntil(() => File.Exists(renamed) && !File.Exists(file), TimeSpan.FromSeconds(10), "the renamed file");
            await WindowsApp.WaitUntil(() => app.Row(Path.GetFileName(renamed)) != null, Live, "the renamed row");
        }

        [Fact]
        public async Task F1OpensTheHelp()
        {
            app.Press(VirtualKeyShort.F1);

            var help = await app.Opened("HelpWindow");
            await WindowsApp.WaitUntil(() => help.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Length > 10,
                TimeSpan.FromSeconds(10), "the help text");
        }

        [Fact]
        public async Task TheDrivesDialogListsEveryDrive()
        {
            app.ButtonNamed("Drives").Invoke();

            var dialog = await app.Opened("DrivesWindow");
            var boxes = await WindowsApp.WaitFor(() =>
            {
                var found = dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox));
                return found.Length == DriveInfo.GetDrives().Length ? found : null;
            }, TimeSpan.FromSeconds(10), "a check box per drive");
            //Only the drive of the temporary folder is indexed in this run
            var temp = Path.GetPathRoot(Path.GetTempPath());
            var indexed = Assert.Single(boxes, b => b.AsCheckBox().IsChecked == true);
            Assert.StartsWith(temp, indexed.Name, StringComparison.OrdinalIgnoreCase);
            dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)).First(b => b.Name == "Cancel").AsButton().Invoke();
            await WindowsApp.WaitUntil(() => app.OtherWindows().Length == 0, TimeSpan.FromSeconds(10), "the dialog to close");
        }

        [Fact]
        public async Task TheCalculatorComputesInItsWebView()
        {
            var button = app.Main.FindFirstDescendant(cf => cf.ByAutomationId("calculatorButton"));
            Assert.SkipWhen(button == null || button.IsOffscreen, "No WebView2 runtime: the calculator is hidden");
            button.AsButton().Invoke();

            var calculator = await app.Opened("WebAppWindow");
            AutomationElement Key(string name) => calculator.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName(name)));
            await WindowsApp.WaitFor(() => Key("7"), TimeSpan.FromSeconds(30), "the calculator's keys");
            foreach (var name in new[] { "7", "Add", "2", "Calculate" }) Key(name).AsButton().Invoke();

            await WindowsApp.WaitUntil(() => ResultOf(calculator) == "9", TimeSpan.FromSeconds(10), "the result 9");
        }

        /// <summary>
        /// Chromium lets no web page read a file in AppData (Temp included) through its handle;
        /// the app serves those itself - archive entries and the text of documents open from Temp
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task TheLogExplorerShowsTheFileInItsWebView(bool inTemp)
        {
            var file = inTemp ? Path.Combine(app.Root, $"{app.Token}-temp.txt") : app.Before;
            var text = $"smoke {app.Token} {(inTemp ? "temp" : "before")}";
            if (inTemp) File.WriteAllText(file, text);
            var name = Path.GetFileName(file);
            await app.ShowOnly(app.Token, names => names.Contains(name));
            await app.SelectRow(name);

            app.Press(VirtualKeyShort.KEY_L);

            var explorer = await app.Opened("WebAppWindow", TimeSpan.FromSeconds(30));
            await WindowsApp.WaitUntil(() => explorer.FindAllDescendants()
                .Any(e => e.Properties.Name.ValueOrDefault?.Contains(text) == true),
                TimeSpan.FromSeconds(30), "the file's text in the Log explorer");
            Assert.DoesNotContain(explorer.FindAllDescendants(), e => e.Properties.Name.ValueOrDefault?.StartsWith("Could not read") == true);
        }

        /// <summary>The text of the calculator's result output (its HTML id is "result")</summary>
        static string ResultOf(AutomationElement calculator)
        {
            var output = calculator.FindFirstDescendant(cf => cf.ByAutomationId("result"));
            if (output == null) return null;
            var text = output.Name;
            if (string.IsNullOrWhiteSpace(text))
                text = string.Concat(output.FindAllDescendants().Select(e => e.Properties.Name.ValueOrDefault));
            return text?.Trim();
        }
    }
}
#endif
