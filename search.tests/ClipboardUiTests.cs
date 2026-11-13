using System.IO;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Xunit;

namespace search.Tests
{
    /// <summary>Ctrl+C, Ctrl+X and Ctrl+V on the result rows, with real files behind them</summary>
    public class ClipboardUiTests
    {
        [AvaloniaTheory]
        [InlineData(Key.C, false, "CopiedToClipboard")]
        [InlineData(Key.X, true, "CutToClipboard")]
        public async Task CopyAndCutPutTheSelectedFileOnTheClipboard(Key key, bool cut, string status)
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);

            scene.Ctrl(key);
            await scene.Until(() => scene.Status == L.Format(status, FileOperationText.DescribeItems(new[] { scene.File })));

            var files = await FileClipboard.GetFiles(scene.Window);
            Assert.Equal(scene.File, Assert.Single(files), ignoreCase: true);
            Assert.Equal(cut, await FileClipboard.RequestsMove(scene.Window));
            //A cut moves nothing by itself
            Assert.True(File.Exists(scene.File));
        }

        [AvaloniaFact]
        public async Task CopyTakesTheWholeSelection()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);
            scene.ClickRow(scene.OtherNode, held: RawInputModifiers.Control);
            Assert.Equal(2, scene.Selected.Length);

            scene.Ctrl(Key.C);
            await scene.Until(() => scene.Status.Length > 0 && FileClipboard.LastSetFiles.Count == 2);

            Assert.Equal(2, (await FileClipboard.GetFiles(scene.Window)).Length);
        }

        [AvaloniaFact]
        public async Task CopiedFilePastesIntoTheSelectedFolderAndStays()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);
            scene.Ctrl(Key.C);
            await scene.Until(() => FileClipboard.LastSetFiles.Count == 1);

            scene.ClickRow(scene.FolderNode);
            scene.Ctrl(Key.V);
            await scene.Confirm(FileTransferAction.Copy);

            var pasted = Path.Combine(scene.Folder, "a.txt");
            await scene.Until(() => File.Exists(pasted));
            await scene.Idle();
            Assert.Equal("content", File.ReadAllText(pasted));
            Assert.True(File.Exists(scene.File));
            //Still on the clipboard for the next paste
            Assert.Single(await FileClipboard.GetFiles(scene.Window));
        }

        [AvaloniaFact]
        public async Task CutFilePastesAsAMoveAndLeavesTheClipboardEmpty()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);
            scene.Ctrl(Key.X);
            await scene.Until(() => FileClipboard.LastSetFiles.Count == 1);

            scene.ClickRow(scene.FolderNode);
            scene.Ctrl(Key.V);
            await scene.Confirm(FileTransferAction.Move);

            await scene.Until(() => File.Exists(Path.Combine(scene.Folder, "a.txt")) && !File.Exists(scene.File));
            //A second paste must not find the file that no longer is there
            await scene.Until(() => FileClipboard.GetFiles(scene.Window).GetAwaiter().GetResult().Length == 0);
        }

        [AvaloniaFact]
        public async Task CutPastedAsACopyStopsBeingACut()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);
            scene.Ctrl(Key.X);
            await scene.Until(() => FileClipboard.LastSetFiles.Count == 1);

            scene.ClickRow(scene.FolderNode);
            scene.Ctrl(Key.V);
            var dialog = await scene.TransferDialog();
            Assert.Equal(FileTransferAction.Move, dialog.Action);
            scene.Click(dialog, "CopyAction");
            scene.Click(dialog, "OkButton");

            await scene.Until(() => File.Exists(Path.Combine(scene.Folder, "a.txt")));
            Assert.True(File.Exists(scene.File));
            //Otherwise Explorer would still move the source away on its paste
            await scene.Until(() => !FileClipboard.RequestsMove(scene.Window).GetAwaiter().GetResult());
            Assert.Single(await FileClipboard.GetFiles(scene.Window));
        }

        [AvaloniaFact]
        public async Task PasteWithAnEmptyClipboardOnlyReportsIt()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FolderNode);

            scene.Ctrl(Key.V);

            await scene.Until(() => scene.Status == L.Text("NoClipboardFiles"));
            Assert.Empty(scene.Window.OwnedWindows);
        }

        [AvaloniaFact]
        public async Task CancelledPasteChangesNothing()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);
            scene.Ctrl(Key.X);
            await scene.Until(() => FileClipboard.LastSetFiles.Count == 1);

            scene.ClickRow(scene.FolderNode);
            scene.Ctrl(Key.V);
            scene.Click(await scene.TransferDialog(), "Cancel");
            await scene.Until(() => scene.Window.OwnedWindows.Count == 0);

            Assert.True(File.Exists(scene.File));
            Assert.Empty(Directory.GetFileSystemEntries(scene.Folder));
            Assert.True(await FileClipboard.RequestsMove(scene.Window));
        }
    }
}
