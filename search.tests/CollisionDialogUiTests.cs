using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace search.Tests
{
    /// <summary>
    /// A transfer onto an item that already exists: the question, and what each answer does
    /// to the files. Started the way a user starts it - a drop on a folder row.
    /// </summary>
    public class CollisionDialogUiTests
    {
        [AvaloniaFact]
        public async Task TheQuestionNamesTheExistingDestination()
        {
            using var scene = await UiScene.Open();
            var existing = await Collide(scene, FileTransferAction.Move);

            var dialog = await scene.Dialog<FileCollisionDialog>();

            Assert.Equal(existing, dialog.FindControl<SelectableTextBlock>("Destination").Text);
            scene.Click(dialog, "Cancel");
            await scene.Idle();
        }

        [AvaloniaFact]
        public async Task OverwriteReplacesTheDestination()
        {
            using var scene = await UiScene.Open();
            var existing = await Collide(scene, FileTransferAction.Move);

            scene.Click(await scene.Dialog<FileCollisionDialog>(), "Overwrite");

            await scene.Until(() => !File.Exists(scene.File));
            await scene.Idle();
            Assert.Equal("content", File.ReadAllText(existing));
            Assert.Single(Directory.GetFiles(scene.Folder));
        }

        [AvaloniaFact]
        public async Task SkipLeavesBothFilesAndSaysSo()
        {
            using var scene = await UiScene.Open();
            var existing = await Collide(scene, FileTransferAction.Move);

            scene.Click(await scene.Dialog<FileCollisionDialog>(), "Skip");

            await scene.Until(() => scene.Status.Contains(L.Format("TransferSkipped", 1)));
            Assert.Equal("content", File.ReadAllText(scene.File));
            Assert.Equal("old", File.ReadAllText(existing));
        }

        [AvaloniaFact]
        public async Task RenameKeepsTheOldFileBesideTheNewOne()
        {
            using var scene = await UiScene.Open();
            var existing = await Collide(scene, FileTransferAction.Move);

            scene.Click(await scene.Dialog<FileCollisionDialog>(), "Rename");

            await scene.Until(() => Directory.GetFiles(scene.Folder).Length == 2);
            await scene.Idle();
            Assert.False(File.Exists(scene.File));
            Assert.Equal("old", File.ReadAllText(existing));
            var renamed = Directory.GetFiles(scene.Folder).Single(f => f != existing);
            Assert.Equal("content", File.ReadAllText(renamed));
            Assert.Equal(".txt", Path.GetExtension(renamed));
        }

        [AvaloniaFact]
        public async Task CancelStopsTheWholeTransfer()
        {
            using var scene = await UiScene.Open();
            File.WriteAllText(Path.Combine(scene.Folder, "a.txt"), "old");
            scene.DropAt(scene.Center(scene.CellBorder(scene.FolderNode, "Name")),
                await scene.Files(scene.File, scene.Other));
            await scene.Confirm(FileTransferAction.Move);

            scene.Click(await scene.Dialog<FileCollisionDialog>(), "Cancel");

            await scene.Until(() => scene.Status.Contains(L.Format("CancelledAfter", 0, 2)));
            Assert.True(File.Exists(scene.File));
            //The second file had no collision, yet it must not be moved after the cancel
            Assert.True(File.Exists(scene.Other));
            Assert.Equal("old", File.ReadAllText(Path.Combine(scene.Folder, "a.txt")));
        }

        [AvaloniaTheory]
        [InlineData(true, 1)]
        [InlineData(false, 2)]
        public async Task ApplyToAllAsksOnlyOnce(bool applyToAll, int questions)
        {
            using var scene = await UiScene.Open();
            File.WriteAllText(Path.Combine(scene.Folder, "a.txt"), "old");
            File.WriteAllText(Path.Combine(scene.Folder, "b.txt"), "old");
            scene.DropAt(scene.Center(scene.CellBorder(scene.FolderNode, "Name")),
                await scene.Files(scene.File, scene.Other));
            await scene.Confirm(FileTransferAction.Move);

            var asked = 0;
            for (; asked < 3; asked++)
            {
                await scene.Until(() => scene.Window.OwnedWindows.OfType<FileCollisionDialog>().Any(d => d.IsVisible)
                    || !File.Exists(scene.Other));
                var dialog = scene.Window.OwnedWindows.OfType<FileCollisionDialog>().SingleOrDefault(d => d.IsVisible);
                if (dialog == null) break;
                if (applyToAll)
                {
                    scene.Click(dialog, "ApplyToAll");
                    Assert.True(dialog.ShouldApplyToAll);
                }
                scene.Click(dialog, "Overwrite");
                await scene.Until(() => !dialog.IsVisible);
            }

            Assert.Equal(questions, asked);
            Assert.Equal("content", File.ReadAllText(Path.Combine(scene.Folder, "a.txt")));
            Assert.Equal("other", File.ReadAllText(Path.Combine(scene.Folder, "b.txt")));
            Assert.False(File.Exists(scene.File));
        }

        /// <summary>Drop a.txt on the folder that already has one, and accept the action</summary>
        static async Task<string> Collide(UiScene scene, FileTransferAction action)
        {
            var existing = Path.Combine(scene.Folder, "a.txt");
            File.WriteAllText(existing, "old");
            scene.DropAt(scene.Center(scene.CellBorder(scene.FolderNode, "Name")), await scene.Files(scene.File));
            await scene.Confirm(action);
            return existing;
        }
    }
}
