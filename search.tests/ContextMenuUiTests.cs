using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace search.Tests
{
    /// <summary>The right-click menu of the results: what it acts on and what it offers</summary>
    public class ContextMenuUiTests
    {
        [AvaloniaFact]
        public async Task RightClickOnAnUnselectedRowSelectsItAndOpensTheMenu()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);

            scene.ClickRow(scene.FolderNode, button: MouseButton.Right);

            Assert.True(scene.Grid.ContextMenu.IsOpen);
            Assert.Same(scene.FolderNode, Assert.Single(scene.Selected));
        }

        [AvaloniaFact]
        public async Task RightClickInsideTheSelectionKeepsAllOfIt()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);
            scene.ClickRow(scene.OtherNode, held: RawInputModifiers.Control);

            scene.ClickRow(scene.FileNode, button: MouseButton.Right);

            Assert.True(scene.Grid.ContextMenu.IsOpen);
            Assert.Equal(2, scene.Selected.Length);
        }

        [AvaloniaTheory]
        //A folder can become a target by its name, a plain file only by its folder
        [InlineData(true, "Name", true, "add selected as targets")]
        [InlineData(false, "Name", false, null)]
        [InlineData(false, "Folder", true, "add parent Folders as targets")]
        public async Task AddTargetIsOfferedOnlyWhereTheCellCanBeATarget(bool folderRow, string column,
            bool offered, string caption)
        {
            using var scene = await UiScene.Open();

            scene.ClickRow(folderRow ? scene.FolderNode : scene.FileNode, column, MouseButton.Right);

            var captions = new[] { L.Caption("add selected as targets"), L.Caption("add parent Folders as targets") };
            var item = Items(scene).Single(i => captions.Contains(Text(i)));
            Assert.Equal(offered, item.IsVisible);
            if (offered) Assert.Equal(L.Caption(caption), Text(item));
        }

        [AvaloniaFact]
        public async Task AddTargetFromTheMenuAddsTheFolderAndIsNotOfferedAgain()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FolderNode, button: MouseButton.Right);

            Choose(scene, L.Caption("add selected as targets"));

            var target = Assert.Single(scene.Window.BasketTargets);
            Assert.Equal(scene.Folder, target.Path, ignoreCase: true);
            Assert.False(scene.Grid.ContextMenu.IsOpen);

            scene.ClickRow(scene.FolderNode, button: MouseButton.Right);
            Assert.False(Items(scene).Single(i => Text(i) == L.Caption("add selected as targets")).IsVisible);
        }

        [AvaloniaFact]
        public async Task CopyPathPutsTheFullPathsOfTheSelectionOnTheClipboard()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);
            scene.ClickRow(scene.OtherNode, held: RawInputModifiers.Control);
            scene.ClickRow(scene.OtherNode, button: MouseButton.Right);

            Choose(scene, L.Text("CopyPath"));

            var expected = scene.File + System.Environment.NewLine + scene.Other;
            await scene.Until(() => scene.Status == L.Format("CopiedToClipboard", expected));
            Assert.Equal(expected, await FileClipboard.GetText(scene.Window));
        }

        [AvaloniaFact]
        public async Task CopyAndPasteFromTheMenuTransferLikeTheKeys()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode, button: MouseButton.Right);
            Choose(scene, L.Text("Copy"));
            await scene.Until(() => FileClipboard.LastSetFiles.Count == 1);

            scene.ClickRow(scene.FolderNode, button: MouseButton.Right);
            Choose(scene, L.Text("Paste"));
            await scene.Confirm(FileTransferAction.Copy);

            await scene.Until(() => System.IO.File.Exists(System.IO.Path.Combine(scene.Folder, "a.txt")));
            Assert.True(System.IO.File.Exists(scene.File));
        }

        [AvaloniaFact]
        public async Task ArchiveAndSearchItemsFollowTheSelection()
        {
            using var scene = await UiScene.Open();

            scene.ClickRow(scene.FileNode, button: MouseButton.Right);

            var items = Items(scene).ToArray();
            Assert.True(items.Single(i => Text(i) == L.Text("Zip")).IsEnabled);
            //A text file is no archive, and no content search has run
            Assert.False(items.Single(i => Text(i) == L.Text("Unzip")).IsVisible);
            Assert.False(items.Single(i => Text(i) == L.Text("SelectFound")).IsVisible);
        }

        static MenuItem[] Items(UiScene scene) => scene.Grid.ContextMenu.Items.OfType<MenuItem>().ToArray();

        /// <summary>The caption of an item, with or without the key hint beside it</summary>
        static string Text(MenuItem item) => item.Header switch
        {
            string text => text,
            Grid grid => grid.Children.OfType<TextBlock>().First().Text,
            _ => null
        };

        /// <summary>Click an item of the open menu, in the popup the menu lives in</summary>
        static void Choose(UiScene scene, string caption)
        {
            Assert.True(scene.Grid.ContextMenu.IsOpen);
            var item = Items(scene).Single(i => Text(i) == caption);
            Assert.True(item.IsVisible && item.IsEffectivelyEnabled);
            var popup = TopLevel.GetTopLevel(item);
            Assert.NotNull(popup);
            var at = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), popup).Value;
            popup.MouseMove(at);
            popup.MouseDown(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            popup.MouseUp(at, MouseButton.Left);
            scene.Settle();
        }
    }
}
