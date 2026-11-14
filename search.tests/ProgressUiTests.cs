using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace search.Tests
{
    /// <summary>
    /// The progress window of transfers and deletes: when it appears, what its Cancel does,
    /// and the Recycle Bin a confirmed delete moves the files to
    /// </summary>
    public class ProgressUiTests
    {
        const int Count = 20;

        [AvaloniaFact]
        public async Task AQuickTransferFinishesWithoutAProgressWindow()
        {
            using var scene = await UiScene.Open();
            var opened = 0;
            using var watch = Window.WindowOpenedEvent.AddClassHandler<TransferProgressWindow>((_, _) => opened++);

            scene.DropAt(scene.Center(scene.CellBorder(scene.FolderNode, "Name")), await scene.Files(scene.File));
            await scene.Confirm(FileTransferAction.Move);
            await scene.Until(() => File.Exists(Path.Combine(scene.Folder, "a.txt")));
            await scene.Idle();

            Assert.Equal(0, opened);
        }

        [AvaloniaFact]
        public async Task CancelInTheProgressWindowStopsTheTransfer()
        {
            using var scene = await UiScene.Open();
            var sources = Many(scene);
            scene.ShowProgressAtOnce();
            //A local copy outruns any pointer, and the headless input first lets all pending
            //work run: the button is pressed the way UI Automation presses it, as it appears
            TransferProgressWindow progress = null;
            using var watch = Window.WindowOpenedEvent.AddClassHandler<TransferProgressWindow>((w, _) =>
            {
                progress = w;
                var peer = ControlAutomationPeer.CreatePeerForElement(w.FindControl<Button>("CancelButton"));
                ((IInvokeProvider)peer).Invoke();
            });

            await Copy(scene, sources);

            await scene.Until(() => progress != null);
            Assert.Equal(L.Text("Copy"), progress.FindControl<TextBlock>("Operation").Text);
            var cancel = progress.FindControl<Button>("CancelButton");
            Assert.False(cancel.IsEnabled);
            Assert.Equal(L.Text("Cancelling"), cancel.Content);
            await scene.Until(() => !scene.Window.FileOperationInProgress && !progress.IsVisible);
            Assert.Equal($"{L.Text("Copy")}: {L.Format("CancelledAfter", 0, Count)}", scene.Status);
            Assert.Empty(Directory.GetFiles(scene.Folder));
            Assert.All(sources, s => Assert.True(File.Exists(s)));
        }

        [AvaloniaFact]
        public async Task ClosingTheProgressWindowCancelsAsItsButtonDoes()
        {
            using var scene = await UiScene.Open();
            scene.ShowProgressAtOnce();
            TransferProgressWindow progress = null;
            var stillOpen = false;
            using var watch = Window.WindowOpenedEvent.AddClassHandler<TransferProgressWindow>((w, _) =>
            {
                progress = w;
                Dispatcher.UIThread.Post(() =>
                {
                    w.Close();
                    //Kept open until the running item ends
                    stillOpen = w.IsVisible;
                }, DispatcherPriority.Send);
            });

            await Copy(scene, Many(scene));

            await scene.Until(() => progress != null && !progress.FindControl<Button>("CancelButton").IsEnabled);
            await scene.Until(() => !progress.IsVisible && !scene.Window.FileOperationInProgress);
            Assert.True(stillOpen);
            Assert.InRange(Directory.GetFiles(scene.Folder).Length, 0, Count - 1);
        }

        [AvaloniaFact]
        public async Task ACompletedTransferClosesItsProgressWindow()
        {
            using var scene = await UiScene.Open();
            scene.ShowProgressAtOnce();
            TransferProgressWindow progress = null;
            using var watch = Window.WindowOpenedEvent.AddClassHandler<TransferProgressWindow>((w, _) => progress = w);

            await Copy(scene, new[] { scene.File });

            await scene.Until(() => File.Exists(Path.Combine(scene.Folder, "a.txt")) && !scene.Window.FileOperationInProgress);
            Assert.NotNull(progress);
            await scene.Until(() => !progress.IsVisible);
            Assert.StartsWith($"{L.Text("Copy")}: ", scene.Status);
            Assert.DoesNotContain(L.Format("CancelledAfter", 0, 1), scene.Status);
        }

        [AvaloniaTheory]
        [InlineData(1, false)]
        [InlineData(2, true)]
        public async Task ADeleteCanBeCancelledOnlyBetweenItsItems(int items, bool cancellable)
        {
            using var scene = await UiScene.Open();
            scene.ShowProgressAtOnce();
            bool? enabled = null;
            object tip = null;
            using var watch = Window.WindowOpenedEvent.AddClassHandler<TransferProgressWindow>((w, _) =>
            {
                var button = w.FindControl<Button>("CancelButton");
                enabled = button.IsEnabled;
                tip = ToolTip.GetTip(button);
            });
            scene.ClickRow(scene.FileNode);
            if (items > 1) scene.ClickRow(scene.OtherNode, held: RawInputModifiers.Control);

            scene.Keys(Key.LeftShift, Key.Delete);

            await scene.Until(() => enabled != null && !scene.Window.FileOperationInProgress);
            Assert.Equal(cancellable, enabled);
            Assert.Equal(cancellable ? null : L.Text("DeleteNotCancellable"), tip);
            Assert.False(File.Exists(scene.File));
        }

        [AvaloniaFact]
        public async Task YesMovesTheFileToTheRecycleBin()
        {
            using var scene = await UiScene.Open();
            scene.Recycles();
            scene.ClickRow(scene.FileNode);

            scene.Keys(Key.Delete);
            scene.Click(await scene.Dialog<MessageDialog>(), "Yes");

            await scene.Until(() => !File.Exists(scene.File) && !scene.Window.FileOperationInProgress);
            Assert.Equal(L.Format("Recycled", FileOperationText.DescribeItems(new[] { scene.File })), scene.Status);
            Assert.True(File.Exists(scene.Other));
            //In the bin, not gone for good - and out of it again
            scene.AssertOneRecycled();
        }

        [AvaloniaFact]
        public async Task TheMenuRecyclesAFolderWithItsContent()
        {
            using var scene = await UiScene.Open();
            scene.Recycles();
            File.WriteAllText(Path.Combine(scene.Folder, "inner.txt"), "inside");
            scene.ClickRow(scene.FolderNode, button: MouseButton.Right);

            scene.Choose(scene.Grid.ContextMenu, L.Text("Recycle"));
            scene.Click(await scene.Dialog<MessageDialog>(), "Yes");

            await scene.Until(() => !Directory.Exists(scene.Folder) && !scene.Window.FileOperationInProgress);
            scene.AssertOneRecycled();
        }

        /// <summary>Files big enough that the copy of all of them outlasts a click</summary>
        static string[] Many(UiScene scene)
        {
            var dir = Directory.CreateDirectory(Path.Combine(scene.Root, "many")).FullName;
            var data = new byte[1 << 20];
            new Random(1).NextBytes(data);
            return Enumerable.Range(0, Count).Select(i =>
            {
                var path = Path.Combine(dir, $"f{i:00}.bin");
                File.WriteAllBytes(path, data);
                return path;
            }).ToArray();
        }

        /// <summary>Drop the files on the empty folder and choose Copy</summary>
        static async Task Copy(UiScene scene, string[] sources)
        {
            scene.DropAt(scene.Center(scene.CellBorder(scene.FolderNode, "Name")), await scene.Files(sources));
            var dialog = await scene.TransferDialog();
            scene.Click(dialog, "CopyAction");
            scene.Click(dialog, "OkButton");
        }
    }
}
