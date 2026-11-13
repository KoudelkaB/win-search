using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using search.Models;
using Xunit;

namespace search.Tests
{
    /// <summary>
    /// The windows and dialogs opened from the main one, and the small controls around the
    /// results. What would rescan the drives or restart the application is only cancelled.
    /// </summary>
    public class WindowsUiTests
    {
        [AvaloniaFact]
        public async Task DrivesButtonListsEveryDriveAndCancelChangesNothing()
        {
            using var scene = await UiScene.Open();

            scene.Click(scene.ButtonOf(L.Text("Drives")));
            var dialog = await scene.Dialog<DrivesWindow>();

            var boxes = dialog.GetVisualDescendants().OfType<CheckBox>().ToArray();
            Assert.Equal(FsRoots.All().Count(), boxes.Length);
            Assert.NotEmpty(boxes);
            //Each drive is probed on its own thread and then says what it is
            await scene.Until(() => boxes.All(b => ((DrivesWindow.DriveChoice)b.DataContext).Probed));
            Assert.DoesNotContain(boxes, b => ((DrivesWindow.DriveChoice)b.DataContext).Details == L.Text("Checking"));

            scene.Click(dialog, "Cancel");
            await scene.Until(() => !scene.Window.OwnedWindows.Any());
            Assert.Empty(dialog.ChangedRoots);
        }

        [AvaloniaFact]
        public async Task SettingsButtonOpensTheSettingsWhereThereIsSomethingToSet()
        {
            using var scene = await UiScene.Open();

            var button = scene.Find<Button>("settingsButton");
            //Only the Linux service is set there so far
            Assert.Equal(System.OperatingSystem.IsLinux(), button.IsEffectivelyVisible);
            if (!button.IsEffectivelyVisible) return;

            scene.Click(button);
            var dialog = await scene.Dialog<SettingsWindow>();

            Assert.Equal(L.Text("Settings"), dialog.Title);
            dialog.Close();
            await scene.Until(() => !scene.Window.OwnedWindows.Any());
        }

        [AvaloniaFact]
        public async Task LanguageButtonOffersTheLanguagesWithTheCurrentOneChosen()
        {
            using var scene = await UiScene.Open();

            scene.Click(scene.Window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "🌐")));
            var dialog = await scene.Dialog<LanguageSelectionWindow>();

            var list = dialog.GetVisualDescendants().OfType<ComboBox>().Single();
            Assert.Equal(Languages.All.Count, list.ItemCount);
            Assert.True(list.IsKeyboardFocusWithin);
            //Confirming the language already in use changes nothing (another one restarts the app)
            if (dialog.SelectedCulture == CultureInfo.CurrentUICulture.Name)
            {
                scene.Click(dialog, "OK");
                Assert.True(dialog.Accepted);
            }
            else dialog.Close();
            await scene.Until(() => !scene.Window.OwnedWindows.Any());
        }

        [AvaloniaFact]
        public async Task F1OpensTheHelpOfTheCurrentLanguage()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);

            scene.Keys(Key.F1);
            var help = await scene.Dialog<HelpWindow>();

            Assert.NotEmpty(help.GetVisualDescendants().OfType<SelectableTextBlock>()
                .Where(t => !string.IsNullOrWhiteSpace(t.Text) || t.Inlines?.Count > 0));
            //Not modal: the main window keeps working beside it
            scene.ClickRow(scene.OtherNode);
            Assert.Same(scene.OtherNode, Assert.Single(scene.Selected));
            help.Close();
        }

        [AvaloniaFact]
        public async Task HelpSearchBoxTakesTheFocusOnCtrlF()
        {
            using var scene = await UiScene.Open();
            scene.Keys(Key.F1);
            var help = await scene.Dialog<HelpWindow>();
            scene.Settle();

            help.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, null);
            scene.Settle();

            Assert.True(help.GetVisualDescendants().OfType<TextBox>().Any(t => t.IsKeyboardFocusWithin));
            help.Close();
        }

        [AvaloniaFact]
        public async Task ElevateAtStartToggleIsRememberedAtOnceWhereItExists()
        {
            using var scene = await UiScene.Open();
            var toggle = scene.Find<ToggleButton>("elevateAtStart");
            //There is no UAC helper outside Windows
            Assert.Equal(System.OperatingSystem.IsWindows(), toggle.IsEffectivelyVisible);
            if (!toggle.IsEffectivelyVisible) return;
            var before = ElevationSettingsStore.StartHelperAtLaunch();
            try
            {
                scene.Click(toggle);
                Assert.Equal(toggle.IsChecked == true, ElevationSettingsStore.StartHelperAtLaunch());
                var first = toggle.IsChecked;

                scene.Click(toggle);
                Assert.NotEqual(first, toggle.IsChecked);
                Assert.Equal(toggle.IsChecked == true, ElevationSettingsStore.StartHelperAtLaunch());
            }
            finally { ElevationSettingsStore.Save(before); }
        }

        [AvaloniaTheory]
        [InlineData(MessageDialogButtons.OkCancel, "OK", MessageDialogResult.Ok)]
        [InlineData(MessageDialogButtons.OkCancel, "Cancel", MessageDialogResult.Cancel)]
        [InlineData(MessageDialogButtons.YesNo, "Yes", MessageDialogResult.Yes)]
        [InlineData(MessageDialogButtons.YesNo, "No", MessageDialogResult.No)]
        [InlineData(MessageDialogButtons.Ok, "OK", MessageDialogResult.Ok)]
        public async Task MessageDialogAnswersWithTheClickedButton(MessageDialogButtons buttons, string clicked,
            MessageDialogResult expected)
        {
            using var scene = await UiScene.Open();

            var answer = MessageDialog.Show(scene.Window, "Question?", "Caption", buttons, MessageDialogIcon.Question);
            var dialog = await scene.Dialog<MessageDialog>();
            Assert.Equal("Caption", dialog.Title);
            Assert.Equal(buttons == MessageDialogButtons.Ok ? 1 : 2, dialog.GetVisualDescendants().OfType<Button>().Count());
            scene.Click(dialog, clicked);

            await scene.Until(() => answer.IsCompleted);
            Assert.Equal(expected, await answer);
        }

        [AvaloniaTheory]
        [InlineData(MessageDialogButtons.OkCancel, MessageDialogResult.Cancel)]
        [InlineData(MessageDialogButtons.YesNo, MessageDialogResult.No)]
        [InlineData(MessageDialogButtons.Ok, MessageDialogResult.Ok)]
        public async Task EscapeOrClosingAMessageDialogIsItsCautiousAnswer(MessageDialogButtons buttons,
            MessageDialogResult expected)
        {
            using var scene = await UiScene.Open();

            var escaped = MessageDialog.Show(scene.Window, "Question?", "Caption", buttons);
            (await scene.Dialog<MessageDialog>()).KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            await scene.Until(() => escaped.IsCompleted);
            Assert.Equal(expected, await escaped);

            var closed = MessageDialog.Show(scene.Window, "Question?", "Caption", buttons);
            (await scene.Dialog<MessageDialog>()).Close();
            await scene.Until(() => closed.IsCompleted);
            Assert.Equal(expected, await closed);
        }

        [AvaloniaFact]
        public async Task TheDefaultButtonOfADangerousQuestionIsNo()
        {
            using var scene = await UiScene.Open();

            var answer = MessageDialog.Show(scene.Window, "Delete?", "Caption", MessageDialogButtons.YesNo,
                MessageDialogIcon.Question, MessageDialogResult.No);
            var dialog = await scene.Dialog<MessageDialog>();
            scene.Settle();
            var focused = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.IsFocused);
            Assert.Equal(L.Text("No"), focused.Content);

            //Enter presses the focused button
            dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await scene.Until(() => answer.IsCompleted);
            Assert.Equal(MessageDialogResult.No, await answer);
        }

        [AvaloniaFact]
        public async Task AHeaderClickMovesTheSortArrowAndASecondOneTurnsIt()
        {
            using var scene = await UiScene.Open();
            DataGridColumn Column(string tag) => scene.Grid.Columns.Single(c => Equals(c.Tag, tag));
            bool HasArrow(string tag) => Column(tag).Header is StackPanel panel && panel.Children.OfType<Path>().Any();
            var initial = scene.Window.DisplayedSort;
            Assert.True(HasArrow("LastChangeTime"));
            try
            {
                ClickHeader(scene, Column("Size"));
                await scene.Until(() => scene.Window.DisplayedSort == "+Size");
                Assert.True(HasArrow("Size"));
                //The arrow moved: the old column has its plain caption back
                Assert.False(HasArrow("LastChangeTime"));
                Assert.Equal(L.Text("Changed"), Column("LastChangeTime").Header);

                ClickHeader(scene, Column("Size"));
                await scene.Until(() => scene.Window.DisplayedSort == "-Size");
                Assert.True(HasArrow("Size"));
            }
            finally
            {
                //The sort is remembered for the next window - leave it as it was found
                for (var i = 0; i < 2 && scene.Window.DisplayedSort != initial; i++)
                {
                    var before = scene.Window.DisplayedSort;
                    ClickHeader(scene, Column("LastChangeTime"));
                    await scene.Until(() => scene.Window.DisplayedSort != before);
                }
            }
        }

        static void ClickHeader(UiScene scene, DataGridColumn column)
        {
            scene.Settle();
            var header = scene.Grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
                .Single(h => ReferenceEquals(DataGridColumn.GetColumnContainingElement(h), column));
            scene.Click(header.TranslatePoint(new Point(header.Bounds.Width / 2, 12), scene.Window).Value);
        }
    }
}
