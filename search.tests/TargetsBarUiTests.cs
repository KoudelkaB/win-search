using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using Xunit;

namespace search.Tests
{
    /// <summary>The targets bar: its buttons, the menus of the targets and sending files to them</summary>
    public class TargetsBarUiTests
    {
        [AvaloniaFact]
        public async Task AddButtonsTakeTheSelectedFolderOrTheFolderOfTheSelectedFile()
        {
            using var scene = await UiScene.Open();

            scene.ClickRow(scene.FolderNode);
            scene.Click(scene.ButtonOf(L.Text("AddName")));
            Assert.Equal(scene.Folder, Assert.Single(scene.Window.BasketTargets).Path, ignoreCase: true);

            scene.ClickRow(scene.FileNode);
            scene.Click(scene.ButtonOf(L.Text("AddFolder")));
            Assert.Equal(Path.GetDirectoryName(scene.File), scene.Window.BasketTargets[1].Path, ignoreCase: true);

            //Each target has its button, named by the folder
            scene.Settle();
            Assert.Equal(2, TargetButtons(scene).Length);
        }

        [AvaloniaFact]
        public async Task SendButtonCountsTheTargetsAndNeedsAtLeastOne()
        {
            using var scene = await UiScene.Open();
            var send = scene.Find<Button>("pasteToBasketButton");
            var text = scene.Find<TextBlock>("pasteToBasketText");
            Assert.False(send.IsEnabled);
            Assert.EndsWith("(0)…", text.Text);

            await AddTarget(scene, scene.Folder);

            Assert.True(send.IsEnabled);
            Assert.EndsWith("(1)…", text.Text);
        }

        [AvaloniaFact]
        public async Task SendButtonAsksForTheActionAndCopiesTheClipboardFile()
        {
            using var scene = await UiScene.Open();
            await AddTarget(scene, scene.Folder);
            await CopyFile(scene);

            scene.Click(scene.Find<Button>("pasteToBasketButton"));
            await scene.Confirm(FileTransferAction.Copy);

            await scene.Until(() => File.Exists(Path.Combine(scene.Folder, "a.txt")));
            Assert.True(File.Exists(scene.File));
        }

        [AvaloniaFact]
        public async Task AltVSendsAtOnceWithoutTheDialog()
        {
            using var scene = await UiScene.Open();
            await AddTarget(scene, scene.Folder);
            await CopyFile(scene);

            scene.Keys(Key.LeftAlt, Key.V);

            await scene.Until(() => File.Exists(Path.Combine(scene.Folder, "a.txt")));
            Assert.Empty(scene.Window.OwnedWindows.OfType<FileTransferDialog>());
        }

        [AvaloniaTheory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task SeveralTargetsAreConfirmedBeforeAnythingIsSent(bool confirmed)
        {
            using var scene = await UiScene.Open();
            var second = Directory.CreateDirectory(Path.Combine(scene.Root, "dest2")).FullName;
            await AddTarget(scene, scene.Folder);
            await AddTarget(scene, second);
            await CopyFile(scene);

            scene.Click(scene.Find<Button>("pasteToBasketButton"));
            var question = await scene.Dialog<MessageDialog>();
            scene.Click(question, confirmed ? "OK" : "Cancel");

            if (confirmed)
            {
                await scene.Confirm(FileTransferAction.Copy);
                await scene.Until(() => File.Exists(Path.Combine(scene.Folder, "a.txt"))
                    && File.Exists(Path.Combine(second, "a.txt")));
            }
            else
            {
                await scene.Until(() => !scene.Window.OwnedWindows.Any(w => w.IsVisible));
                Assert.Empty(Directory.GetFileSystemEntries(scene.Folder));
                Assert.Empty(Directory.GetFileSystemEntries(second));
            }
        }

        [AvaloniaFact]
        public async Task DropOnTheUseZoneSendsToTheTarget()
        {
            using var scene = await UiScene.Open();
            await AddTarget(scene, scene.Folder);
            var data = await scene.Files(scene.File);
            scene.Drag(RawDragEventType.DragEnter, scene.Center("TargetBar"), data);
            scene.Drag(RawDragEventType.DragOver, scene.Center("TargetBar"), data);
            Assert.EndsWith("(1)", scene.Find<TextBlock>("BasketUseText").Text);

            var zone = scene.Center("BasketUseZone");
            Assert.Equal(DragDropEffects.Move, scene.Drag(RawDragEventType.DragOver, zone, data));
            scene.Drag(RawDragEventType.Drop, zone, data);
            await scene.Confirm(FileTransferAction.Move);

            await scene.Until(() => File.Exists(Path.Combine(scene.Folder, "a.txt")) && !File.Exists(scene.File));
        }

        [AvaloniaFact]
        public async Task ATargetThatIsGoneIsNotSentTo()
        {
            using var scene = await UiScene.Open();
            await AddTarget(scene, scene.Folder);
            await CopyFile(scene);
            Directory.Delete(scene.Folder);

            scene.Keys(Key.LeftAlt, Key.V);

            await scene.Until(() => scene.Status == L.Text("NoSourcesOrTargets"));
            Assert.False(Directory.Exists(scene.Folder));
        }

        [AvaloniaFact]
        public async Task TargetMenuFiltersToTheTargetRemovesItAndClearsAll()
        {
            using var scene = await UiScene.Open();
            var second = Directory.CreateDirectory(Path.Combine(scene.Root, "dest2")).FullName;
            await AddTarget(scene, scene.Folder);
            await AddTarget(scene, second);

            TargetMenu(scene, 0, "FilterTarget");
            var filter = scene.Find<AutoCompleteTextBox>("filterTextBox");
            Assert.Equal($"\"{scene.Window.BasketTargets[0].Path}\"", filter.Text);
            Assert.True(filter.IsKeyboardFocusWithin);

            TargetMenu(scene, 0, "RemoveTarget");
            Assert.Equal(second, Assert.Single(scene.Window.BasketTargets).Path, ignoreCase: true);

            await AddTarget(scene, scene.Folder);
            TargetMenu(scene, 0, "ClearAllTargets");
            Assert.Empty(scene.Window.BasketTargets);
        }

        [AvaloniaFact]
        public async Task TheBarsOwnMenuClearsTheTargets()
        {
            using var scene = await UiScene.Open();
            await AddTarget(scene, scene.Folder);
            var bar = scene.Find<Border>("TargetBar");
            var label = bar.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == L.Text("Targets"));

            scene.Click(label, MouseButton.Right);
            scene.Choose(bar.ContextMenu, L.Text("ClearTargets"));

            Assert.Empty(scene.Window.BasketTargets);
        }

        [AvaloniaFact]
        public async Task TargetsSurviveIntoTheNextWindow()
        {
            using var scene = await UiScene.Open();
            await AddTarget(scene, scene.Folder);

            var next = new MainWindow(startFileSystem: false);
            try { Assert.Equal(scene.Folder, Assert.Single(next.BasketTargets).Path, ignoreCase: true); }
            finally { next.Close(); }
        }

        /// <summary>Add a folder as a target the way a user does: a drop into the targets box</summary>
        static async Task AddTarget(UiScene scene, string folder)
        {
            var before = scene.Window.BasketTargets.Count;
            var box = scene.Find<Border>("BasketItemsZone");
            var at = scene.Center(box);
            //Its free right end, away from the buttons of the targets already there
            scene.DropAt(new Avalonia.Point(at.X + box.Bounds.Width / 2 - 20, at.Y), await scene.Files(folder));
            Assert.Equal(before + 1, scene.Window.BasketTargets.Count);
            scene.Settle();
        }

        static async Task CopyFile(UiScene scene)
        {
            scene.ClickRow(scene.FileNode);
            scene.Ctrl(Key.C);
            await scene.Until(() => FileClipboard.LastSetFiles.Count == 1 && scene.Status.Length > 0
                && FileClipboard.GetFiles(scene.Window).GetAwaiter().GetResult().Length == 1);
        }

        static Button[] TargetButtons(UiScene scene) => scene.Find<ItemsControl>("basketItems")
            .GetVisualDescendants().OfType<Button>().ToArray();

        static void TargetMenu(UiScene scene, int index, string item)
        {
            scene.Settle();
            var button = TargetButtons(scene)[index];
            scene.Click(button, MouseButton.Right);
            scene.Choose(button.ContextMenu, L.Text(item));
        }
    }
}
