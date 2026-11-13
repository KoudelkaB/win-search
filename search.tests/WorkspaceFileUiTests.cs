using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Xunit;

namespace search.Tests
{
    /// <summary>
    /// Export and import of the pinned filters and targets, and the drives dialog. The test
    /// answers the system's file dialog itself; the settings files live in the test's folder.
    /// </summary>
    public class WorkspaceFileUiTests
    {
        [AvaloniaFact]
        public async Task ExportWritesThePinsAndTargetsToTheChosenFile()
        {
            using var scene = await UiScene.Open();
            await Pin(scene, "logs", "Work");
            await scene.AddTarget(scene.Folder);
            var path = Path.Combine(scene.Root, "out.winsearch-settings.json");
            FilePickerSaveOptions asked = null;
            scene.Window.PickExportFile = options => { asked = options; return Task.FromResult(path); };

            scene.Click(scene.ButtonOf(L.Text("Export")));

            await scene.Until(() => scene.Status == L.Format("SettingsExported", path));
            Assert.Equal("file-search-manager.winsearch-settings.json", asked.SuggestedFileName);
            var saved = WorkspaceSettingsStore.Import(path);
            Assert.Equal("Work", Assert.Single(saved.PinnedFilters).Name);
            Assert.Equal("logs", saved.PinnedFilters[0].Filter);
            Assert.Equal(scene.Folder, Assert.Single(saved.BasketTargets).Path, ignoreCase: true);
        }

        [AvaloniaFact]
        public async Task ACancelledSaveDialogWritesNothing()
        {
            using var scene = await UiScene.Open();
            var asked = 0;
            scene.Window.PickExportFile = _ => { asked++; return Task.FromResult<string>(null); };
            var status = scene.Status;

            scene.Click(scene.ButtonOf(L.Text("Export")));
            scene.Settle();

            Assert.Equal(1, asked);
            Assert.Equal(status, scene.Status);
            Assert.Empty(Directory.GetFiles(scene.Root));
        }

        [AvaloniaFact]
        public async Task ImportIntoAnEmptyWorkspaceTakesTheFileAtOnce()
        {
            using var scene = await UiScene.Open();
            var path = await Exported(scene);
            scene.Window.PickImportFile = _ => Task.FromResult(path);

            scene.Click(scene.ButtonOf(L.Text("Import")));

            await scene.Until(() => scene.Status == L.Format("SettingsImported", 1, 1));
            Assert.Equal("Work", Assert.Single(scene.Window.PinnedFilters).Name);
            Assert.Equal(scene.Folder, Assert.Single(scene.Window.BasketTargets).Path, ignoreCase: true);
            Assert.Empty(scene.Window.OwnedWindows);
        }

        [AvaloniaTheory]
        [InlineData("Yes", "Work")]
        [InlineData("No", "Mine")]
        public async Task ImportOverAWorkspaceAsksFirst(string answer, string kept)
        {
            using var scene = await UiScene.Open();
            var path = await Exported(scene);
            await Pin(scene, "mine", "Mine");
            scene.Window.PickImportFile = _ => Task.FromResult(path);

            scene.Click(scene.ButtonOf(L.Text("Import")));
            var question = await scene.Dialog<MessageDialog>();
            Assert.Contains(L.Format("ImportReplaceConfirm", 1, 0), Message(question));
            scene.Click(question, answer);

            await scene.Until(() => !scene.Window.OwnedWindows.Any(w => w.IsVisible));
            scene.Settle();
            Assert.Equal(kept, Assert.Single(scene.Window.PinnedFilters).Name);
        }

        [AvaloniaFact]
        public async Task AFileThatIsNotOursIsReportedAndChangesNothing()
        {
            using var scene = await UiScene.Open();
            var path = Path.Combine(scene.Root, "foreign.json");
            File.WriteAllText(path, "this is not a settings file");
            scene.Window.PickImportFile = _ => Task.FromResult(path);

            scene.Click(scene.ButtonOf(L.Text("Import")));

            var error = await scene.Dialog<MessageDialog>();
            Assert.Equal(L.Text("SettingsImportFailed"), error.Title);
            Assert.StartsWith(L.Text("SettingsInvalid"), Message(error));
            scene.Click(error, "OK");
            await scene.Until(() => !scene.Window.OwnedWindows.Any(w => w.IsVisible));
            Assert.Empty(scene.Window.PinnedFilters);
        }

        [AvaloniaFact]
        public async Task AltEAndAltIOpenTheSameDialogs()
        {
            using var scene = await UiScene.Open();
            int exports = 0, imports = 0;
            scene.Window.PickExportFile = _ => { exports++; return Task.FromResult<string>(null); };
            scene.Window.PickImportFile = _ => { imports++; return Task.FromResult<string>(null); };
            scene.Click(scene.Find<AutoCompleteTextBox>("filterTextBox"));

            scene.Keys(Key.LeftAlt, Key.E);
            scene.Keys(Key.LeftAlt, Key.I);

            Assert.Equal(1, exports);
            Assert.Equal(1, imports);
        }

        [AvaloniaFact]
        public async Task DrivesOkWithoutAChangeRescansNothing()
        {
            using var scene = await UiScene.Open();

            scene.Click(scene.ButtonOf(L.Text("Drives")));
            var dialog = await scene.Dialog<DrivesWindow>();
            var choices = dialog.GetVisualDescendants().OfType<CheckBox>()
                .Select(b => (DrivesWindow.DriveChoice)b.DataContext).ToArray();
            await scene.Until(() => choices.All(c => c.Probed));
            scene.Click(dialog, "OK");

            await scene.Until(() => !dialog.IsVisible);
            Assert.Empty(dialog.ChangedRoots);
            //What is written down is what was shown
            var after = DriveSelectionStore.Load();
            Assert.All(choices.Where(c => after.Drives.ContainsKey(c.Key)),
                c => Assert.Equal(c.IsChecked, after.Drives[c.Key]));
        }

        [AvaloniaFact]
        public async Task ADriveToggledAndCancelledStaysAsItWas()
        {
            using var scene = await UiScene.Open();
            var before = File.Exists(SelectionFile) ? File.ReadAllText(SelectionFile) : null;

            scene.Click(scene.ButtonOf(L.Text("Drives")));
            var dialog = await scene.Dialog<DrivesWindow>();
            var box = dialog.GetVisualDescendants().OfType<CheckBox>().First();
            await scene.Until(() => ((DrivesWindow.DriveChoice)box.DataContext).Probed);
            scene.Settle();
            var was = box.IsChecked;
            var at = box.TranslatePoint(new Point(8, box.Bounds.Height / 2), dialog).Value;
            dialog.MouseMove(at);
            dialog.MouseDown(at, MouseButton.Left);
            dialog.MouseUp(at, MouseButton.Left);
            scene.Settle();
            Assert.NotEqual(was, box.IsChecked);
            scene.Click(dialog, "Cancel");

            await scene.Until(() => !dialog.IsVisible);
            Assert.Empty(dialog.ChangedRoots);
            Assert.Equal(before, File.Exists(SelectionFile) ? File.ReadAllText(SelectionFile) : null);
        }

        static string SelectionFile => UserDataPaths.For("drive-selection.json");

        /// <summary>The settings of one pin and one target, exported to a file; the window emptied</summary>
        static async Task<string> Exported(UiScene scene)
        {
            await Pin(scene, "logs", "Work");
            await scene.AddTarget(scene.Folder);
            var path = Path.Combine(scene.Root, "saved.winsearch-settings.json");
            scene.Window.PickExportFile = _ => Task.FromResult(path);
            scene.Click(scene.ButtonOf(L.Text("Export")));
            await scene.Until(() => File.Exists(path));
            scene.Window.PinnedFilters.Clear();
            scene.Window.BasketTargets.Clear();
            scene.Settle();
            return path;
        }

        static async Task Pin(UiScene scene, string filter, string name)
        {
            var box = scene.Find<AutoCompleteTextBox>("filterTextBox");
            scene.Click(box);
            scene.Retype(filter);
            if (box.IsListOpen) scene.Keys(Key.Escape);
            scene.Click(scene.ButtonOf(L.Text("Pin")));
            await scene.Until(() => scene.Window.PinnedFilters.Any(p => p.IsEditing));
            await scene.Until(() => scene.Find<ItemsControl>("pinnedItems").GetVisualDescendants()
                .OfType<TextBox>().Any(t => t.IsKeyboardFocusWithin));
            scene.Retype(name);
            scene.Keys(Key.Enter);
            Assert.Equal(name, scene.Window.PinnedFilters.Last().Name);
        }

        static string Message(Window dialog) =>
            dialog.GetVisualDescendants().OfType<SelectableTextBlock>().Single().Text;
    }
}
