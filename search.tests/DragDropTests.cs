using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using search.Models;
using Xunit;

namespace search.Tests
{
    /// <summary>
    /// Drag and drop through the real window: the platform's raw drag events on one side,
    /// files on the disk on the other. Only the pointer of the operating system is missing.
    /// </summary>
    public class DragDropTests
    {
        #region Drops from outside onto the targets bar

        [AvaloniaFact]
        public async Task FilesOverTheTargetsBarShowBothDropZones()
        {
            using var scene = await UiScene.Open();
            var overlay = scene.Window.FindControl<Grid>("BasketDropOverlay");
            Assert.False(overlay.IsVisible);

            var data = await scene.Files(scene.Folder);
            scene.Drag(RawDragEventType.DragEnter, scene.Center("TargetBar"), data);
            scene.Drag(RawDragEventType.DragOver, scene.Center("TargetBar"), data);

            Assert.True(overlay.IsVisible);
            Assert.Empty(scene.Window.BasketTargets);
        }

        [AvaloniaFact]
        public async Task DropOnTheAddZoneAddsFoldersButNoPlainFiles()
        {
            using var scene = await UiScene.Open();
            var data = await scene.Files(scene.Folder, scene.File);
            scene.Drag(RawDragEventType.DragEnter, scene.Center("TargetBar"), data);
            scene.Drag(RawDragEventType.DragOver, scene.Center("TargetBar"), data);

            var zone = scene.Center("BasketAddZone");
            scene.Drag(RawDragEventType.DragOver, zone, data);
            Assert.Equal(0.65, scene.Window.FindControl<Border>("BasketAddZone").Opacity);
            scene.Drag(RawDragEventType.Drop, zone, data);

            var target = Assert.Single(scene.Window.BasketTargets);
            Assert.Equal(scene.Folder, target.Path, ignoreCase: true);
            Assert.Equal(BasketTargetKind.Folder, target.Kind);
            Assert.Equal(1, scene.Window.FindControl<Border>("BasketAddZone").Opacity);
            Assert.False(scene.Window.FindControl<Grid>("BasketDropOverlay").IsVisible);
        }

        [AvaloniaFact]
        public async Task DropOnTheTargetsBoxAddsATargetOnce()
        {
            using var scene = await UiScene.Open();
            var data = await scene.Files(scene.Folder);
            for (var i = 0; i < 2; i++)
            {
                var box = scene.Center("BasketItemsZone");
                //Away from the buttons of the targets already there
                var at = new Point(box.X + scene.Window.FindControl<Border>("BasketItemsZone").Bounds.Width / 2 - 20, box.Y);
                scene.Drag(RawDragEventType.DragEnter, at, data);
                scene.Drag(RawDragEventType.DragOver, at, data);
                scene.Drag(RawDragEventType.Drop, at, data);
            }
            Assert.Single(scene.Window.BasketTargets);
        }

        [AvaloniaFact]
        public async Task UseZoneRefusesTheDragWithoutAnyTarget()
        {
            using var scene = await UiScene.Open();
            var data = await scene.Files(scene.File);
            scene.Drag(RawDragEventType.DragEnter, scene.Center("TargetBar"), data);
            scene.Drag(RawDragEventType.DragOver, scene.Center("TargetBar"), data);

            var effect = scene.Drag(RawDragEventType.DragOver, scene.Center("BasketUseZone"), data);

            Assert.Equal(DragDropEffects.None, effect);
            Assert.Equal(1, scene.Window.FindControl<Border>("BasketUseZone").Opacity);
        }

        [AvaloniaFact]
        public async Task DropOnATargetButtonTransfersIntoThatTarget()
        {
            using var scene = await UiScene.Open();
            var add = await scene.Files(scene.Folder);
            var box = scene.Center("BasketItemsZone");
            scene.Drag(RawDragEventType.DragEnter, box, add);
            scene.Drag(RawDragEventType.Drop, box, add);
            Assert.Single(scene.Window.BasketTargets);

            var button = scene.Window.FindControl<ItemsControl>("basketItems")
                .GetVisualDescendants().OfType<Button>().Single();
            var at = scene.Center(button);
            var data = await scene.Files(scene.File);
            scene.Drag(RawDragEventType.DragEnter, at, data);
            Assert.Equal(DragDropEffects.Move, scene.Drag(RawDragEventType.DragOver, at, data));
            scene.Drag(RawDragEventType.Drop, at, data);

            //Still one target: the drop on a button must not add the source's folder
            Assert.Single(scene.Window.BasketTargets);
            await scene.Confirm(FileTransferAction.Move);
            await scene.Until(() => File.Exists(Path.Combine(scene.Folder, "a.txt")));
            Assert.False(File.Exists(scene.File));
        }

        #endregion

        #region Drops onto result cells

        [AvaloniaFact]
        public async Task FolderRowHighlightsUnderTheDragAndClearsWhenItLeaves()
        {
            using var scene = await UiScene.Open();
            var data = await scene.Files(scene.File);
            var cell = scene.CellBorder(scene.FolderNode, "Name");
            var before = cell.Background;

            scene.Drag(RawDragEventType.DragEnter, scene.Center(cell), data);
            var effect = scene.Drag(RawDragEventType.DragOver, scene.Center(cell), data);
            Assert.Equal(DragDropEffects.Move, effect); //The same volume
            Assert.NotSame(before, cell.Background);

            scene.Drag(RawDragEventType.DragLeave, scene.Center(cell), data);
            Assert.Same(before, cell.Background);
        }

        [AvaloniaFact]
        public async Task NameOfAPlainFileRefusesTheDragButItsFolderCellTakesIt()
        {
            using var scene = await UiScene.Open();
            var data = await scene.Files(scene.Other);

            var name = scene.CellBorder(scene.FileNode, "Name");
            scene.Drag(RawDragEventType.DragEnter, scene.Center(name), data);
            Assert.Equal(DragDropEffects.None, scene.Drag(RawDragEventType.DragOver, scene.Center(name), data));

            var folder = scene.CellBorder(scene.FileNode, "Folder");
            Assert.NotEqual(DragDropEffects.None, scene.Drag(RawDragEventType.DragOver, scene.Center(folder), data));
        }

        [AvaloniaTheory]
        [InlineData(RawInputModifiers.None, FileTransferAction.Move)]
        [InlineData(RawInputModifiers.Control, FileTransferAction.Copy)]
        [InlineData(RawInputModifiers.Shift, FileTransferAction.Move)]
        [InlineData(RawInputModifiers.Alt, FileTransferAction.SymbolicLink)]
        public async Task DropOnAFolderAsksWithTheActionOfTheHeldKey(RawInputModifiers held, FileTransferAction offered)
        {
            using var scene = await UiScene.Open();
            var data = await scene.Files(scene.File);
            var at = scene.Center(scene.CellBorder(scene.FolderNode, "Name"));
            scene.Drag(RawDragEventType.DragEnter, at, data, held);
            scene.Drag(RawDragEventType.DragOver, at, data, held);
            scene.Drag(RawDragEventType.Drop, at, data, held);

            var dialog = await scene.TransferDialog();
            Assert.Equal(offered, dialog.Action);
            scene.Click(dialog, "Cancel");
            await scene.Until(() => !scene.Window.OwnedWindows.Any());

            //Cancelled: nothing moved
            Assert.True(File.Exists(scene.File));
            Assert.Empty(Directory.GetFileSystemEntries(scene.Folder));
        }

        [AvaloniaFact]
        public async Task ConfirmedDropMovesTheFileIntoTheFolder()
        {
            using var scene = await UiScene.Open();
            var data = await scene.Files(scene.File);
            var at = scene.Center(scene.CellBorder(scene.FolderNode, "Name"));
            scene.Drag(RawDragEventType.DragEnter, at, data);
            scene.Drag(RawDragEventType.DragOver, at, data);
            scene.Drag(RawDragEventType.Drop, at, data);

            await scene.Confirm(FileTransferAction.Move);

            var moved = Path.Combine(scene.Folder, "a.txt");
            await scene.Until(() => File.Exists(moved));
            Assert.False(File.Exists(scene.File));
            Assert.Equal("content", File.ReadAllText(moved));
        }

        [AvaloniaFact]
        public async Task CopyChosenInTheDialogKeepsTheSource()
        {
            using var scene = await UiScene.Open();
            var data = await scene.Files(scene.File);
            var at = scene.Center(scene.CellBorder(scene.FolderNode, "Name"));
            scene.Drag(RawDragEventType.DragEnter, at, data);
            scene.Drag(RawDragEventType.Drop, at, data);

            var dialog = await scene.TransferDialog();
            scene.Click(dialog, "CopyAction");
            Assert.Equal(FileTransferAction.Copy, dialog.Action);
            scene.Click(dialog, "OkButton");

            await scene.Until(() => File.Exists(Path.Combine(scene.Folder, "a.txt")));
            Assert.True(File.Exists(scene.File));
        }

        #endregion

        #region The results as the drag source

        [AvaloniaFact]
        public async Task DraggingARowShowsTheDropZonesUntilTheRelease()
        {
            using var scene = await UiScene.Open();
            var overlay = scene.Window.FindControl<Grid>("BasketDropOverlay");
            var start = scene.Center(scene.CellBorder(scene.FolderNode, "Name"));

            scene.Window.MouseMove(start);
            scene.Window.MouseDown(start, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.False(overlay.IsVisible); //A press alone is no drag

            scene.Window.MouseMove(start + new Point(2, 2), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            Assert.False(overlay.IsVisible); //Nor is the tremble of a click

            scene.Window.MouseMove(start + new Point(30, 0), RawInputModifiers.LeftMouseButton);
            await scene.Until(() => overlay.IsVisible);

            var zone = scene.Center("BasketAddZone");
            scene.Window.MouseMove(zone, RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            scene.Window.MouseUp(zone, MouseButton.Left);
            await scene.Until(() => !overlay.IsVisible);

            var target = Assert.Single(scene.Window.BasketTargets);
            Assert.Equal(scene.Folder, target.Path, ignoreCase: true);
        }

        [AvaloniaFact]
        public async Task DraggingARowThatNoLongerExistsOnlyReportsIt()
        {
            using var scene = await UiScene.Open();
            File.Delete(scene.File);
            var start = scene.Center(scene.CellBorder(scene.FileNode, "Name"));

            scene.Window.MouseMove(start);
            scene.Window.MouseDown(start, MouseButton.Left);
            scene.Window.MouseMove(start + new Point(30, 0), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            scene.Window.MouseUp(start + new Point(30, 0), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(L.Text("NothingToDrag"), ((SearchModel)scene.Window.DataContext).Status);
            Assert.False(scene.Window.FindControl<Grid>("BasketDropOverlay").IsVisible);
        }

        #endregion
    }
}
