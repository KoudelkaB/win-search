using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
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
    /// The real main window over a small real directory - src\a.txt, src\b.txt and an empty
    /// dest - driven the way a user drives it: the mouse, the keys, drags, dialogs.
    /// </summary>
    internal sealed class UiScene : IDisposable
    {
        const DragDropEffects AllEffects = DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;

        public MainWindow Window { get; private set; }
        public string Root { get; private set; }
        public string File { get; private set; }
        public string Other { get; private set; }
        public string Folder { get; private set; }
        public INode FileNode { get; private set; }
        public INode OtherNode { get; private set; }
        public INode FolderNode { get; private set; }

        public DataGrid Grid => Window.FindControl<DataGrid>("filesView");
        public string Status => ((SearchModel)Window.DataContext).Status;

        /// <summary>The programs the window asked to start - none of them runs</summary>
        public System.Collections.Generic.List<System.Diagnostics.ProcessStartInfo> Launched { get; } = new();

        /// <summary>
        /// The web apps and the Log explorer the window asked to open: the page (Apps.LogExplorer
        /// for the program), its files, what Log explorer starts with
        /// </summary>
        public System.Collections.Generic.List<(string Page, string[] Files, LogExplorerLoad Load)> WebApps { get; } = new();

        readonly TimeSpan progressDelay = MainWindow.ProgressDialogDelay;

        public static async Task<UiScene> Open()
        {
            var root = Path.Combine(Path.GetTempPath(), "win-search-ui-" + Guid.NewGuid().ToString("N"));
            var scene = new UiScene
            {
                Root = root,
                File = Path.Combine(root, "src", "a.txt"),
                Other = Path.Combine(root, "src", "b.txt"),
                Folder = Path.Combine(root, "dest")
            };
            Directory.CreateDirectory(Path.Combine(root, "src"));
            Directory.CreateDirectory(scene.Folder);
            System.IO.File.WriteAllText(scene.File, "content");
            System.IO.File.WriteAllText(scene.Other, "other");
            //Nothing a test does starts a real program or a web view
            ProcessLauncher.Override = start => { scene.Launched.Add(start); return null; };
            WebAppWindow.OpenOverride = (page, files) => scene.WebApps.Add((page, files, null));
            NativeLogExplorer.OpenOverride = (files, load) => scene.WebApps.Add((Apps.LogExplorer, files, load));

            var window = new MainWindow(startFileSystem: false);
            scene.Window = window;
            //Pins and targets of the previous test come back from the saved workspace
            window.BasketTargets.Clear();
            window.PinnedFilters.Clear();
            scene.WatchAnswers();
            // The headless font is wide - room for the filter column
            window.Width = 2400;
            window.Height = 800;
            window.Show();
            //The headless clipboard outlives the window of the previous test
            await window.Clipboard.ClearAsync();
            var model = (SearchModel)window.DataContext;
            //The results the opening asked for would replace rows added sooner; the index is
            //shared with other tests, so they are not the rows of this scene
            Dispatcher.UIThread.RunJobs();
            await model.UserUpdatesSettled();
            Dispatcher.UIThread.RunJobs();
            model.Items.Clear();
            model.Items.Add(scene.FileNode = new PathNode(scene.File, FileAttributes.Normal));
            model.Items.Add(scene.OtherNode = new PathNode(scene.Other, FileAttributes.Normal));
            model.Items.Add(scene.FolderNode = new PathNode(scene.Folder, FileAttributes.Directory));
            Dispatcher.UIThread.RunJobs();
            return scene;
        }

        #region Drag and drop

        public async Task<DataTransfer> Files(params string[] paths)
        {
            var data = new DataTransfer();
            foreach (var path in paths)
            {
                IStorageItem item = Directory.Exists(path)
                    ? await Window.StorageProvider.TryGetFolderFromPathAsync(path)
                    : await Window.StorageProvider.TryGetFileFromPathAsync(path);
                Assert.NotNull(item);
                data.Add(DataTransferItem.CreateFile(item));
            }
            return data;
        }

        /// <summary>One raw drag event of the platform; the effect the window answered with</summary>
        public DragDropEffects Drag(RawDragEventType type, Point at, IDataTransfer data,
            RawInputModifiers held = RawInputModifiers.None)
        {
            answered = DragDropEffects.None;
            Window.DragDrop(at, type, data, AllEffects, held);
            Settle();
            if (type == RawDragEventType.DragOver)
            {
                //Over a new element the first move only enters it; as a real pointer
                //never stops after one event, the next one is the drag-over proper
                Window.DragDrop(at, type, data, AllEffects, held);
                Settle();
            }
            return answered;
        }

        /// <summary>Enter, hover and drop at one spot</summary>
        public void DropAt(Point at, IDataTransfer data, RawInputModifiers held = RawInputModifiers.None)
        {
            Drag(RawDragEventType.DragEnter, at, data, held);
            Drag(RawDragEventType.DragOver, at, data, held);
            Drag(RawDragEventType.Drop, at, data, held);
        }

        DragDropEffects answered;

        //The headless platform does not hand the answer back: read it where the event
        //ends its way up, after every handler of the window had its say
        void WatchAnswers()
        {
            foreach (var routed in new[] { DragDrop.DragEnterEvent, DragDrop.DragOverEvent, DragDrop.DropEvent })
                Window.AddHandler(routed, (_, e) => answered = e.DragEffects,
                    Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        }

        #endregion

        #region Mouse and keys

        /// <summary>
        /// Lay out and render what the last event changed (drop zones, menus): the next
        /// event is hit-tested against the rendered scene
        /// </summary>
        public void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        public Point Center(string name) => Center(Window.FindControl<Control>(name));

        public Point Center(Control control) =>
            control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), Window).Value;

        public Border CellBorder(INode node, string column)
        {
            var row = Grid.GetVisualDescendants().OfType<DataGridRow>()
                .Single(r => ReferenceEquals(r.DataContext, node));
            return row.GetVisualDescendants().OfType<Border>().Single(b => Equals(b.Tag, column));
        }

        public void Click(Point at, MouseButton button = MouseButton.Left,
            RawInputModifiers held = RawInputModifiers.None)
        {
            Window.MouseMove(at, held);
            Window.MouseDown(at, button, held);
            Dispatcher.UIThread.RunJobs();
            Window.MouseUp(at, button, held);
            Settle();
        }

        public void ClickRow(INode node, string column = "Name", MouseButton button = MouseButton.Left,
            RawInputModifiers held = RawInputModifiers.None) => Click(Center(CellBorder(node, column)), button, held);

        /// <summary>Ctrl + a key, the modifier going through the window as its own key</summary>
        public void Ctrl(Key key)
        {
            Window.KeyPress(Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.ControlLeft, null);
            Dispatcher.UIThread.RunJobs();
            Window.KeyPress(key, RawInputModifiers.Control, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
            Window.KeyRelease(key, RawInputModifiers.Control, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
            Window.KeyRelease(Key.LeftCtrl, RawInputModifiers.None, PhysicalKey.ControlLeft, null);
            Dispatcher.UIThread.RunJobs();
        }

        public INode[] Selected => Grid.SelectedItems.Cast<INode>().ToArray();

        /// <summary>
        /// A key sequence as the hints show it: the keys go down in their order and come up
        /// in the reverse one - the command runs when the last one is released
        /// </summary>
        public void Keys(params Key[] keys)
        {
            var held = RawInputModifiers.None;
            foreach (var key in keys)
            {
                held |= ModifierOf(key);
                Window.KeyPress(key, held, PhysicalKey.None, null);
                Dispatcher.UIThread.RunJobs();
            }
            foreach (var key in keys.Reverse())
            {
                held &= ~ModifierOf(key);
                Window.KeyRelease(key, held, PhysicalKey.None, null);
                Dispatcher.UIThread.RunJobs();
            }
            Settle();
        }

        /// <summary>
        /// A key held while the others are tapped one after another - how a value is typed
        /// into a sequence (F2, E, then the new extension), letters repeated included
        /// </summary>
        public void Hold(Key held, params Key[] taps)
        {
            var modifiers = ModifierOf(held);
            Window.KeyPress(held, modifiers, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
            foreach (var key in taps)
            {
                Window.KeyPress(key, modifiers | ModifierOf(key), PhysicalKey.None, null);
                Dispatcher.UIThread.RunJobs();
                Window.KeyRelease(key, modifiers, PhysicalKey.None, null);
                Dispatcher.UIThread.RunJobs();
            }
            Window.KeyRelease(held, RawInputModifiers.None, PhysicalKey.None, null);
            Settle();
        }

        /// <summary>The keys that type the text in a sequence: letters, digits, "." and ","</summary>
        public static Key[] KeysOf(string text) => text.Select(c => c switch
        {
            >= 'a' and <= 'z' => Key.A + (c - 'a'),
            >= '0' and <= '9' => Key.D0 + (c - '0'),
            '.' => Key.OemPeriod,
            ',' => Key.OemComma,
            _ => throw new ArgumentException($"No key types '{c}' in a sequence")
        }).ToArray();

        /// <summary>One more row of the results, for a file or folder made by the test</summary>
        public INode AddRow(string path)
        {
            var node = new PathNode(path, Directory.Exists(path) ? FileAttributes.Directory : FileAttributes.Normal);
            ((SearchModel)Window.DataContext).Items.Add(node);
            Settle();
            return node;
        }

        /// <summary>Add a target the way a user does: a drop into the targets box</summary>
        public async Task<BasketTarget> AddTarget(string path)
        {
            var before = Window.BasketTargets.Count;
            var box = Find<Border>("BasketItemsZone");
            var at = Center(box);
            //Its free right end, away from the buttons of the targets already there
            DropAt(new Point(at.X + box.Bounds.Width / 2 - 20, at.Y), await Files(path));
            Assert.Equal(before + 1, Window.BasketTargets.Count);
            Settle();
            return Window.BasketTargets[^1];
        }

        /// <summary>The button the targets bar shows for the target</summary>
        public Button TargetButton(BasketTarget target)
        {
            Settle();
            return Find<ItemsControl>("basketItems").GetVisualDescendants().OfType<Button>()
                .Single(b => ReferenceEquals(b.Tag, target));
        }

        bool recycles;

        /// <summary>
        /// The test moves its files to the Recycle Bin (the trash on Linux). They are taken
        /// out of it again at the end - only the items from this test's folder.
        /// </summary>
        public void Recycles()
        {
            recycles = true;
            //The Linux trash then lives in the test's folder, on its file system
            if (!OperatingSystem.IsWindows())
                LinuxTrash.HomeTrashOverride = () => Path.Combine(Root, ".trash");
        }

        /// <summary>Remove this test's items from the Recycle Bin for good; how many there were</summary>
        public int? PurgeRecycled() => RecycleBin.Purge(Root);

        /// <summary>
        /// The one item the test recycled is in the bin, and out of it again. Skipped where this
        /// process may not read the bin, or does not find there what it recycles itself (a
        /// sandbox hiding the bin) - the move itself was checked before. An item missing from a
        /// bin that works is a failure: the app deleted it for good.
        /// </summary>
        public void AssertOneRecycled()
        {
            var purged = PurgeRecycled();
            Assert.SkipWhen(purged == null, "This process cannot read the Recycle Bin, so the recycled item could not be checked or removed from it");
            if (purged != 1)
                Assert.SkipUnless(RecycleBin.SeesWhatIsRecycled(Root),
                    "This process does not find in the Recycle Bin what it recycles, so the recycled item could not be checked or removed from it");
            Assert.Equal(1, purged);
        }

        /// <summary>The progress window appears with the operation, not a second later</summary>
        public void ShowProgressAtOnce() => MainWindow.ProgressDialogDelay = TimeSpan.Zero;

        /// <summary>The program start names the path - as the program, an argument or its folder</summary>
        public static bool Mentions(System.Diagnostics.ProcessStartInfo start, string path) =>
            new[] { start.FileName, start.Arguments, start.WorkingDirectory }.Concat(start.ArgumentList)
                .Any(text => text != null && text.Contains(path, StringComparison.OrdinalIgnoreCase));

        static RawInputModifiers ModifierOf(Key key) => key switch
        {
            Key.LeftShift or Key.RightShift => RawInputModifiers.Shift,
            Key.LeftCtrl or Key.RightCtrl => RawInputModifiers.Control,
            Key.LeftAlt or Key.RightAlt => RawInputModifiers.Alt,
            _ => RawInputModifiers.None
        };

        /// <summary>Text typed into the focused box</summary>
        public void Type(string text)
        {
            Window.KeyTextInput(text);
            Settle();
        }

        /// <summary>Replace the whole text of the focused box by typing</summary>
        public void Retype(string text)
        {
            Ctrl(Key.A);
            Type(text);
        }

        public T Find<T>(string name) where T : Control => Window.FindControl<T>(name);

        public void Click(Control control, MouseButton button = MouseButton.Left) => Click(Center(control), button);

        /// <summary>The button of the window with this localized caption</summary>
        public Button ButtonOf(string caption) => Window.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(b.Content, caption) && b.IsEffectivelyVisible);

        /// <summary>Click an item of an open context menu, in the popup the menu lives in</summary>
        public void Choose(ContextMenu menu, string caption)
        {
            Assert.True(menu.IsOpen);
            var item = menu.Items.OfType<MenuItem>().Single(i => MenuText(i) == caption);
            Assert.True(item.IsVisible && item.IsEffectivelyEnabled);
            var popup = TopLevel.GetTopLevel(item);
            Assert.NotNull(popup);
            var at = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), popup).Value;
            popup.MouseMove(at);
            popup.MouseDown(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            popup.MouseUp(at, MouseButton.Left);
            Settle();
        }

        /// <summary>The caption of a menu item, with or without the key hint beside it</summary>
        public static string MenuText(MenuItem item) => item.Header switch
        {
            string text => text,
            Grid grid => grid.Children.OfType<TextBlock>().First().Text,
            _ => null
        };

        #endregion

        #region Dialogs

        public async Task<T> Dialog<T>() where T : Window
        {
            await Until(() => Window.OwnedWindows.OfType<T>().Any(d => d.IsVisible));
            return Window.OwnedWindows.OfType<T>().Single();
        }

        public Task<FileTransferDialog> TransferDialog() => Dialog<FileTransferDialog>();

        /// <summary>Accept the action dialog, which has to offer the expected action</summary>
        public async Task Confirm(FileTransferAction expected)
        {
            var dialog = await TransferDialog();
            Assert.Equal(expected, dialog.Action);
            Click(dialog, "OkButton");
        }

        /// <summary>Click a named control of a dialog, or its button of that localized caption</summary>
        public void Click(Window dialog, string name)
        {
            var control = dialog.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name)
                ?? dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, L.Text(name)));
            var at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), dialog).Value;
            dialog.MouseMove(at);
            dialog.MouseDown(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            dialog.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        public async Task Until(Func<bool> reached)
        {
            for (var i = 0; i < 200; i++)
            {
                Dispatcher.UIThread.RunJobs();
                //A file still being written is simply not there yet
                try { if (reached()) return; }
                catch (IOException) { }
                await Task.Delay(25);
            }
            Assert.Fail("The expected state was not reached in 5 s; status: " + Status + "; open: "
                + string.Join(", ", Window.OwnedWindows.Where(w => w.IsVisible).Select(w => $"{w.GetType().Name} \"{w.Title}\"")));
        }

        /// <summary>No dialog is left open and no transfer is still running</summary>
        public Task Idle() => Until(() => !Window.OwnedWindows.Any(w => w.IsVisible)
            && !Window.FileOperationInProgress && !string.IsNullOrEmpty(Status));

        #endregion

        public void Dispose()
        {
            if (Grid.ContextMenu is { IsOpen: true } menu) menu.Close();
            foreach (var owned in Window.OwnedWindows.ToArray()) owned.Close();
            Window.BasketTargets.Clear();
            Window.PinnedFilters.Clear();
            Window.Close();
            ProcessLauncher.Override = null;
            WebAppWindow.OpenOverride = null;
            NativeLogExplorer.OpenOverride = null;
            MainWindow.ProgressDialogDelay = progressDelay;
            if (recycles)
            {
                try { PurgeRecycled(); } catch { }
                LinuxTrash.HomeTrashOverride = null;
            }
            try { Directory.Delete(Root, recursive: true); } catch { }
        }

        internal sealed class PathNode(string path, FileAttributes attributes) : INode
        {
            public override string Name => Path.GetFileName(path);
            public override string FullName => path;
            public override FileAttributes Attributes { get; protected set; } = attributes;
            public override ulong Size { get; protected set; }
            public override DateTime LastChangeTime { get; protected set; }
        }
    }
}
