using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Xunit;

namespace search.Tests
{
    /// <summary>
    /// The key sequences of the result list, pressed on the real window with real files
    /// behind the rows. Sequences that start another program are left to their own tests.
    /// </summary>
    public class GridKeysUiTests
    {
        #region Text to the clipboard

        [AvaloniaFact]
        public async Task NamePathAndFolderKeysCopyTheirTextOfTheSelection()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);
            scene.ClickRow(scene.OtherNode, held: RawInputModifiers.Control);
            var nl = Environment.NewLine;

            scene.Keys(Key.N);
            await Clipboard(scene, "a.txt" + nl + "b.txt");
            scene.Keys(Key.P);
            await Clipboard(scene, scene.File + nl + scene.Other);
            scene.Keys(Key.F);
            var folder = Path.GetDirectoryName(scene.File);
            await Clipboard(scene, folder + nl + folder);
        }

        static Task Clipboard(UiScene scene, string expected) => scene.Until(() =>
            FileClipboard.GetText(scene.Window).GetAwaiter().GetResult() == expected
            && scene.Status == L.Format("CopiedToClipboard", expected));

        #endregion

        #region Selection

        [AvaloniaFact]
        public async Task SelectSequencesPickAllFoldersFilesAndTheInverse()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);

            scene.Keys(Key.S, Key.D);
            Assert.Same(scene.FolderNode, Assert.Single(scene.Selected));

            scene.Keys(Key.S, Key.F);
            Assert.Equal(2, scene.Selected.Length);
            Assert.DoesNotContain(scene.FolderNode, scene.Selected);

            scene.Keys(Key.S, Key.I);
            Assert.Same(scene.FolderNode, Assert.Single(scene.Selected));

            scene.Keys(Key.S, Key.A);
            Assert.Equal(3, scene.Selected.Length);
            //All selected: the same sequence takes the selection away again
            scene.Keys(Key.S, Key.A);
            Assert.Empty(scene.Selected);
        }

        [AvaloniaFact]
        public async Task CtrlASelectsAllAndEscapeClearsTheSelection()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);

            scene.Ctrl(Key.A);
            Assert.Equal(3, scene.Selected.Length);

            scene.Keys(Key.Escape);
            Assert.Empty(scene.Selected);
            //The filter stays as it was: this Escape only dropped the selection
            Assert.True(string.IsNullOrEmpty(scene.Find<AutoCompleteTextBox>("filterTextBox").Text));
        }

        [AvaloniaFact]
        public async Task TheStatusBarCountsTheSelectionAndItsLinkWalksThroughIt()
        {
            using var scene = await UiScene.Open();
            var link = scene.Window.GetVisualDescendants().OfType<HyperlinkButton>().Single();
            string Shown() => string.Concat(((TextBlock)link.Content).Inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));

            scene.ClickRow(scene.FileNode);
            scene.ClickRow(scene.FolderNode, held: RawInputModifiers.Control);
            Assert.Equal($"{L.Text("Selected")}: 2", Shown());

            scene.Click(link);
            Assert.EndsWith(" · 2/2", Shown());
            scene.Click(link);
            Assert.EndsWith(" · 1/2", Shown());
            scene.Click(link);
            Assert.EndsWith(" · 2/2", Shown());
            Assert.Equal(2, scene.Selected.Length);
        }

        [AvaloniaFact]
        public async Task RightShiftHandsTheKeyboardToTheFilter()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);
            Assert.True(scene.Grid.IsKeyboardFocusWithin);

            scene.Keys(Key.RightShift);

            Assert.True(scene.Find<AutoCompleteTextBox>("filterTextBox").IsKeyboardFocusWithin);
        }

        #endregion

        #region Delete

        [AvaloniaFact]
        public async Task DeleteAsksFirstAndNoKeepsTheFile()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);

            scene.Keys(Key.Delete);

            var dialog = await scene.Dialog<MessageDialog>();
            Assert.Equal(L.Text("Recycle"), dialog.Title);
            Assert.Contains(scene.File, dialog.GetVisualDescendants().OfType<SelectableTextBlock>().Single().Text);
            scene.Click(dialog, "No");
            await scene.Until(() => !scene.Window.OwnedWindows.Any());
            Assert.True(File.Exists(scene.File));
        }

        [AvaloniaFact]
        public async Task EscapeInTheDeleteQuestionMeansNo()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);
            scene.Keys(Key.Delete);
            var dialog = await scene.Dialog<MessageDialog>();

            dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

            await scene.Until(() => !scene.Window.OwnedWindows.Any());
            Assert.True(File.Exists(scene.File));
        }

        [AvaloniaFact]
        public async Task ShiftDeleteRemovesTheSelectionForGoodWithoutAsking()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);
            scene.ClickRow(scene.OtherNode, held: RawInputModifiers.Control);

            scene.Keys(Key.LeftShift, Key.Delete);

            await scene.Until(() => !File.Exists(scene.File) && !File.Exists(scene.Other));
            Assert.DoesNotContain(scene.Window.OwnedWindows, w => w is MessageDialog);
            Assert.True(Directory.Exists(scene.Folder));
        }

        #endregion

        #region Rename in place

        [AvaloniaFact]
        public async Task F2EditsTheNameInItsCellWithTheExtensionLeftUnselected()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);

            scene.Keys(Key.F2);
            var editor = await Editor(scene);

            Assert.Equal("a.txt", editor.Text);
            Assert.Equal("a", editor.SelectedText);
            Assert.True(editor.IsKeyboardFocusWithin);
        }

        [AvaloniaFact]
        public async Task TypedNameAndEnterRenameTheFile()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);
            scene.Keys(Key.F2);
            var editor = await Editor(scene);

            //Typing replaces the selected part - the extension stays
            scene.Type("renamed");
            Assert.Equal("renamed.txt", editor.Text);
            scene.Keys(Key.Enter);

            var renamed = Path.Combine(Path.GetDirectoryName(scene.File), "renamed.txt");
            Assert.True(File.Exists(renamed));
            Assert.False(File.Exists(scene.File));
            Assert.False(editor.IsVisible);
            Assert.Equal(L.Format("RenamedTo", "a.txt", "renamed.txt"), scene.Status);
            Assert.True(scene.Grid.IsKeyboardFocusWithin);
        }

        [AvaloniaFact]
        public async Task EscapeLeavesTheNameAlone()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);
            scene.Keys(Key.F2);
            var editor = await Editor(scene);
            scene.Type("renamed");

            scene.Keys(Key.Escape);

            Assert.False(editor.IsVisible);
            Assert.True(File.Exists(scene.File));
            //The Escape belonged to the editor: the row is still selected
            Assert.Same(scene.FileNode, Assert.Single(scene.Selected));
        }

        [AvaloniaTheory]
        [InlineData("b.txt", "AlreadyExists")]
        [InlineData("a?.txt", "InvalidName")]
        public async Task ANameThatCannotBeUsedKeepsTheEditorOpenAndMarked(string name, string reason)
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);
            scene.Keys(Key.F2);
            var editor = await Editor(scene);
            scene.Retype(name);

            scene.Keys(Key.Enter);

            Assert.True(editor.IsVisible);
            Assert.Equal(Brushes.IndianRed, editor.BorderBrush);
            Assert.Equal(reason == "AlreadyExists" ? L.Format(reason, name) : L.Text(reason), scene.Status);
            Assert.True(File.Exists(scene.File));
            Assert.Equal("other", File.ReadAllText(scene.Other));
            scene.Keys(Key.Escape);
        }

        [AvaloniaFact]
        public async Task F2OnSeveralRowsOnlySaysOneAtATime()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);
            scene.ClickRow(scene.OtherNode, held: RawInputModifiers.Control);

            scene.Keys(Key.F2);

            Assert.Equal(L.Text("RenameOneOnly"), scene.Status);
            Assert.DoesNotContain(scene.Grid.GetVisualDescendants().OfType<TextBox>(),
                t => t.Name == "InlineNameEditor" && t.IsVisible);
        }

        static async Task<TextBox> Editor(UiScene scene)
        {
            TextBox Visible() => scene.Grid.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(t => t.Name == "InlineNameEditor" && t.IsVisible);
            await scene.Until(() => Visible() != null);
            scene.Settle();
            return Visible();
        }

        #endregion

        #region New folder

        [AvaloniaFact]
        public async Task MAsksForANameAndCreatesTheFolderInTheSelectedOne()
        {
            using var scene = await UiScene.Open();
            var bar = scene.Find<Border>("inlineActionBar");
            scene.ClickRow(scene.FolderNode);

            scene.Keys(Key.M);
            await scene.Until(() => bar.IsVisible && scene.Find<TextBox>("inlineActionTextBox").IsKeyboardFocusWithin);
            Assert.Equal(L.Format("CreateFolderIn", scene.Folder), scene.Find<TextBlock>("inlineActionLabel").Text);
            scene.Type("new folder");
            scene.Keys(Key.Enter);

            Assert.True(Directory.Exists(Path.Combine(scene.Folder, "new folder")));
            Assert.False(bar.IsVisible);
            Assert.Equal(L.Format("CreatedItem", "new folder"), scene.Status);
            Assert.True(scene.Grid.IsKeyboardFocusWithin);
        }

        [AvaloniaFact]
        public async Task ForAFileTheNewFolderGoesBesideIt()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);
            scene.Keys(Key.M);
            await scene.Until(() => scene.Find<TextBox>("inlineActionTextBox").IsKeyboardFocusWithin);
            scene.Type("beside");

            scene.Click(scene.ButtonOf(L.Text("Create")));

            Assert.True(Directory.Exists(Path.Combine(Path.GetDirectoryName(scene.File), "beside")));
        }

        [AvaloniaFact]
        public async Task AnInvalidFolderNameIsMarkedAndCancelClosesTheBar()
        {
            using var scene = await UiScene.Open();
            var bar = scene.Find<Border>("inlineActionBar");
            var box = scene.Find<TextBox>("inlineActionTextBox");
            scene.ClickRow(scene.FolderNode);
            scene.Keys(Key.M);
            await scene.Until(() => box.IsKeyboardFocusWithin);
            scene.Type("a|b");

            scene.Keys(Key.Enter);
            Assert.True(bar.IsVisible);
            Assert.Equal(Brushes.IndianRed, box.BorderBrush);
            Assert.Equal(L.Text("InvalidName"), scene.Status);

            scene.Click(bar.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, L.Text("Cancel"))));
            Assert.False(bar.IsVisible);
            Assert.Empty(Directory.GetFileSystemEntries(scene.Folder));
        }

        [AvaloniaFact]
        public async Task EscapeClosesTheNewFolderBarWithoutCreatingAnything()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FolderNode);
            scene.Keys(Key.M);
            await scene.Until(() => scene.Find<TextBox>("inlineActionTextBox").IsKeyboardFocusWithin);
            scene.Type("never");

            scene.Keys(Key.Escape);

            Assert.False(scene.Find<Border>("inlineActionBar").IsVisible);
            Assert.Empty(Directory.GetFileSystemEntries(scene.Folder));
        }

        #endregion

        #region Paste without the dialog

        [AvaloniaFact]
        public async Task VPastesTheCopiedFileAtOnce()
        {
            using var scene = await UiScene.Open();
            await Copy(scene, Key.C);

            scene.ClickRow(scene.FolderNode);
            scene.Keys(Key.V);

            var pasted = Path.Combine(scene.Folder, "a.txt");
            await scene.Until(() => File.Exists(pasted) && scene.Status.Contains(L.Text("Copy")));
            Assert.Empty(scene.Window.OwnedWindows.OfType<FileTransferDialog>());
            Assert.True(File.Exists(scene.File));
        }

        [AvaloniaFact]
        public async Task XThenVMovesTheFile()
        {
            using var scene = await UiScene.Open();
            await Copy(scene, Key.X);

            scene.ClickRow(scene.FolderNode);
            scene.Keys(Key.V);

            await scene.Until(() => File.Exists(Path.Combine(scene.Folder, "a.txt")) && !File.Exists(scene.File));
        }

        [AvaloniaFact]
        public async Task VWithOOverwritesWithoutTheCollisionQuestion()
        {
            using var scene = await UiScene.Open();
            var existing = Path.Combine(scene.Folder, "a.txt");
            File.WriteAllText(existing, "old");
            await Copy(scene, Key.C);

            scene.ClickRow(scene.FolderNode);
            scene.Keys(Key.V, Key.O);

            await scene.Until(() => File.ReadAllText(existing) == "content");
            Assert.Empty(scene.Window.OwnedWindows.OfType<FileCollisionDialog>());
        }

        [AvaloniaFact]
        public async Task PlainVOntoAnExistingFileStillAsks()
        {
            using var scene = await UiScene.Open();
            var existing = Path.Combine(scene.Folder, "a.txt");
            File.WriteAllText(existing, "old");
            await Copy(scene, Key.C);

            scene.ClickRow(scene.FolderNode);
            scene.Keys(Key.V);

            scene.Click(await scene.Dialog<FileCollisionDialog>(), "Skip");
            await scene.Until(() => scene.Status.Contains(L.Format("TransferSkipped", 1)));
            Assert.Equal("old", File.ReadAllText(existing));
        }

        [AvaloniaFact]
        public async Task VWithHPastesAHardLinkToTheSameData()
        {
            using var scene = await UiScene.Open();
            await Copy(scene, Key.C);

            scene.ClickRow(scene.FolderNode);
            scene.Keys(Key.V, Key.H);

            var link = Path.Combine(scene.Folder, "a.txt");
            await scene.Until(() => File.Exists(link));
            await scene.Idle();
            //One file under two names: a change through one shows through the other
            File.AppendAllText(scene.File, "+more");
            Assert.Equal("content+more", File.ReadAllText(link));
        }

        static async Task Copy(UiScene scene, Key key)
        {
            scene.ClickRow(scene.FileNode);
            scene.Keys(key);
            await scene.Until(() => FileClipboard.LastSetFiles.Count == 1 && scene.Status.Length > 0
                && FileClipboard.GetFiles(scene.Window).GetAwaiter().GetResult().Length == 1);
        }

        #endregion

        #region Targets from the keyboard

        [AvaloniaFact]
        public async Task TAddsTheSelectedFolderAndTFTheFolderOfAFile()
        {
            using var scene = await UiScene.Open();

            scene.ClickRow(scene.FolderNode);
            scene.Keys(Key.T);
            Assert.Equal(scene.Folder, Assert.Single(scene.Window.BasketTargets).Path, ignoreCase: true);
            Assert.Equal(L.Format("TargetsAdded", 1), scene.Status);

            scene.ClickRow(scene.FileNode);
            scene.Keys(Key.T, Key.F);
            Assert.Equal(2, scene.Window.BasketTargets.Count);
            Assert.Equal(Path.GetDirectoryName(scene.File), scene.Window.BasketTargets[1].Path, ignoreCase: true);

            //A plain file is no target, and a target is never added twice
            scene.Keys(Key.T);
            scene.Keys(Key.T, Key.F);
            Assert.Equal(2, scene.Window.BasketTargets.Count);
            Assert.Equal(L.Text("NoNewTargets"), scene.Status);

            scene.Keys(Key.T, Key.C);
            Assert.Empty(scene.Window.BasketTargets);
        }

        [AvaloniaFact]
        public async Task AltSequencesDoTheSameFromTheResultList()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FolderNode);

            scene.Keys(Key.LeftAlt, Key.N);
            Assert.Single(scene.Window.BasketTargets);

            scene.ClickRow(scene.FileNode);
            scene.Keys(Key.LeftAlt, Key.F);
            Assert.Equal(2, scene.Window.BasketTargets.Count);

            scene.Keys(Key.LeftAlt, Key.C);
            Assert.Empty(scene.Window.BasketTargets);
        }

        #endregion

        #region Into a folder

        [AvaloniaFact]
        public async Task EnterOnAFolderFiltersIntoIt()
        {
            using var scene = await UiScene.Open();
            var filter = scene.Find<AutoCompleteTextBox>("filterTextBox");
            scene.ClickRow(scene.FolderNode);

            scene.Keys(Key.Enter);

            await scene.Until(() => !string.IsNullOrEmpty(filter.Text));
            Assert.Contains(scene.Folder, filter.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(filter.Text, scene.Window.FilterText);
        }

        [AvaloniaFact]
        public async Task DoubleClickOnAFolderNameFiltersIntoItToo()
        {
            using var scene = await UiScene.Open();
            var filter = scene.Find<AutoCompleteTextBox>("filterTextBox");
            var at = scene.Center(scene.CellBorder(scene.FolderNode, "Name"));

            scene.Click(at);
            scene.Click(at);

            await scene.Until(() => !string.IsNullOrEmpty(filter.Text));
            Assert.Contains(scene.Folder, filter.Text, StringComparison.OrdinalIgnoreCase);
        }

        #endregion
    }
}
