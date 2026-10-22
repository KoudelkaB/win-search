using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using search.Models;
using IOPath = System.IO.Path;

namespace search
{
    /// <summary>The result grid: rows, sorting, mouse, drag and drop, context menu, keyboard</summary>
    public partial class MainWindow
    {
        readonly Dictionary<DataGridColumn, string> columnHeaders = new();

        void ConfigureGrid()
        {
            foreach (var column in filesView.Columns)
                columnHeaders[column] = column.Header as string ?? "";
            filesView.LoadingRow += (_, e) =>
            {
                realizedRows.Add(e.Row);
                ApplyRowStyle(e.Row);
            };
            filesView.UnloadingRow += (_, e) => realizedRows.Remove(e.Row);
            filesView.Sorting += ListViewColumnHeader_Click;
            filesView.AddHandler(KeyDownEvent, ListView_KeyDown, RoutingStrategies.Tunnel);
            filesView.AddHandler(KeyUpEvent, ListView_PreviewKeyUp, RoutingStrategies.Tunnel);
            filesView.AddHandler(PointerPressedEvent, FilesView_PointerPressed, RoutingStrategies.Tunnel);
            filesView.AddHandler(PointerMovedEvent, FilesView_PointerMoved, RoutingStrategies.Tunnel);
            filesView.AddHandler(PointerReleasedEvent, FilesView_PointerReleased, RoutingStrategies.Tunnel);
            filesView.AddHandler(DoubleTappedEvent, FilesView_DoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
            filesView.ContextMenu = BuildFilesContextMenu();
            //Inline rename editors live in the Name cell template
            filesView.AddHandler(KeyDownEvent, InlineNameEditor_KeyDown, RoutingStrategies.Bubble, handledEventsToo: false);
            filesView.AddHandler(LostFocusEvent, InlineNameEditor_LostFocus, RoutingStrategies.Bubble);
        }

        /// <summary>
        /// Row colour: green for found, red for searched-but-not-found, blue for directories.
        /// Cells bind their text colour to the row's Foreground.
        /// </summary>
        void ApplyRowStyle(DataGridRow row)
        {
            if (row.DataContext is not INode node)
            {
                row.Foreground = PlainBrush;
                return;
            }
            var found = Model.FoundIn(node);
            row.Foreground = found == true ? FoundBrush
                : found == false ? MissingBrush
                : node.IsDirectory ? DirectoryBrush
                : PlainBrush;
        }

        /// <summary>
        /// Make one realized row re-read every value it displays. INode raises no change
        /// notifications (it must not - the per-node memory budget), so a directory whose size
        /// changed while its row kept its position (the drive root is always the largest row of
        /// a size sort) would show its old aggregate until something reset the whole list.
        /// Every cell's content re-binds by having its data context cleared and inherited again.
        /// The row being renamed inline is left alone - rebuilding it would drop the editor.
        /// </summary>
        internal void RefreshRow(DataGridRow row, object renaming = null)
        {
            var item = row.DataContext;
            if (item == null) return;
            if (renaming != null && ReferenceEquals(item, renaming)) return;
            RefreshCells(filesView, row);
            ApplyRowStyle(row);
        }

        internal static void RefreshCells(DataGrid grid, DataGridRow row)
        {
            foreach (var column in grid.Columns)
            {
                if (column.GetCellContent(row) is not Control content) continue;
                content.DataContext = null;
                content.ClearValue(DataContextProperty);
            }
        }

        DataGridRow RowFor(INode node)
            => realizedRows.FirstOrDefault(r => ReferenceEquals(r.DataContext, node));

        static DataGridRow RowFromSource(object source)
            => (source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true);

        /// <summary>"Name" or "Folder" when the element lies in that cell, otherwise null</summary>
        static string ContextTargetColumn(object source)
        {
            for (var current = source as Visual; current != null; current = current.GetVisualParent())
                if (current is Control { Tag: string tag } && tag is "Name" or "Folder") return tag;
            return null;
        }

        static Border CellBorder(object source)
        {
            for (var current = source as Visual; current != null; current = current.GetVisualParent())
            {
                if (current is Border { Tag: string tag } border && tag is "Name" or "Folder") return border;
                if (current is DataGridRow) return null;
            }
            return null;
        }

        void ScrollResultsToBoundary(bool end)
        {
            var items = Model.Items;
            if (items.Count == 0) return;
            var target = end ? items[^1] : items[0];
            filesView.ScrollIntoView(target, null);
            GridSelection.SetCurrentRow(filesView, end ? items.Count - 1 : 0);
        }

        #region Mouse: drag source, double click, right click

        void FilesView_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            var point = e.GetCurrentPoint(filesView);
            var row = RowFromSource(e.Source);
            var column = ContextTargetColumn(e.Source);
            if (point.Properties.IsRightButtonPressed)
            {
                contextTargetColumn = column;
                if (row?.DataContext is not INode n) return;
                //CTRL + right click => open the file by its default application
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    filters.Used(filterTextBox.Text);
                    _ = Open(n);
                    e.Handled = true;
                }
                else if (!row.IsSelected)
                {
                    filesView.SelectedItems.Clear();
                    filesView.SelectedItem = n;
                }
                return;
            }
            if (!point.Properties.IsLeftButtonPressed || e.ClickCount != 1) return;
            if (inlineRenameEditor != null && (e.Source as Visual)?.FindAncestorOfType<TextBox>(includeSelf: true) == inlineRenameEditor)
                return;
            if (row?.DataContext is not INode node || column == null) return;

            dragStart = e.GetPosition(filesView);
            dragPress = e;
            dragSourceNode = node;
            dragSourceColumn = column;
            dragStarted = false;

            // A press on an already selected row of an extended selection would collapse it.
            // Preserve it long enough to permit a multi-item drag; a plain click still
            // collapses it in the release handler below.
            preserveSelectionOnMouseUp =
                e.KeyModifiers == KeyModifiers.None &&
                row.IsSelected &&
                filesView.SelectedItems.Count > 1;
            if (preserveSelectionOnMouseUp)
            {
                filesView.Focus();
                e.Handled = true;
            }
        }

        async void FilesView_PointerMoved(object sender, PointerEventArgs e)
        {
            if (dragStart == null || dragSourceNode == null || dragPress == null
                || !e.GetCurrentPoint(filesView).Properties.IsLeftButtonPressed)
                return;

            var position = e.GetPosition(filesView);
            if (Math.Abs(position.X - dragStart.Value.X) < 4 && Math.Abs(position.Y - dragStart.Value.Y) < 4)
                return;

            var press = dragPress;
            var nodes = filesView.SelectedItems.Contains(dragSourceNode)
                ? SelectedNodes().ToArray()
                : new[] { dragSourceNode };
            var paths = dragSourceColumn == "Folder"
                ? nodes.Select(n => IOPath.GetDirectoryName(n.FullName))
                : nodes.Select(n => n.GetFileOrTempPath());
            var existingPaths = paths
                .Where(p => !string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p)))
                .Distinct(PathText.Comparer)
                .ToArray();

            dragStarted = true;
            preserveSelectionOnMouseUp = false;
            CancelMouseDrag(keepDragStarted: true);
            if (existingPaths.Length == 0)
            {
                Model.Status = L.Text("NothingToDrag");
                return;
            }

            try
            {
                var data = new DataTransfer();
                foreach (var path in existingPaths)
                {
                    IStorageItem item = Directory.Exists(path)
                        ? await StorageProvider.TryGetFolderFromPathAsync(path)
                        : await StorageProvider.TryGetFileFromPathAsync(path);
                    if (item != null) data.Add(DataTransferItem.CreateFile(item));
                }
                ShowBasketDropOverlay(pinned: true);
                await DragDrop.DoDragDropAsync(press, data,
                    DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            }
            catch (Exception ex)
            {
                Model.Status = L.Format("DragFailed", ex.Message);
            }
            finally
            {
                dragStarted = false;
                HideBasketDropOverlay();
            }
        }

        void FilesView_PointerReleased(object sender, PointerReleasedEventArgs e)
        {
            var clickedNode = dragSourceNode;
            if (preserveSelectionOnMouseUp && !dragStarted && clickedNode != null)
            {
                filesView.SelectedItems.Clear();
                filesView.SelectedItem = clickedNode;
                e.Handled = true;
            }
            CancelMouseDrag();
        }

        void CancelMouseDrag(bool keepDragStarted = false)
        {
            dragStart = null;
            dragPress = null;
            dragSourceNode = null;
            dragSourceColumn = null;
            preserveSelectionOnMouseUp = false;
            if (!keepDragStarted)
                dragStarted = false;
        }

        /// <summary>Name: filter into the folder (or open the file); Folder: show it in the file manager</summary>
        async void FilesView_DoubleTapped(object sender, TappedEventArgs e)
        {
            if (inlineRenameEditor != null || (e.Source as Visual)?.FindAncestorOfType<TextBox>(includeSelf: true) != null)
                return;
            var column = ContextTargetColumn(e.Source);
            if (column == null || RowFromSource(e.Source)?.DataContext is not INode n) return;
            CancelMouseDrag();
            filters.Used(filterTextBox.Text);
            e.Handled = true;
            if (column == "Name") await FilterFolders(n);
            else n.GetFileOrTempPath().Reveal();
        }

        #endregion

        #region Drop onto result cells

        void FileCell_DragOver(object sender, DragEventArgs e)
        {
            var sources = GetDropSources(e);
            var cell = CellBorder(e.Source);
            var executableTarget = TryGetExecutableDrop(cell, out _);
            var folderTarget = TryGetDropTargets(cell, out var targets);
            var validTarget = sources.Length > 0 && (executableTarget || folderTarget);
            if (cell == null || !validTarget)
            {
                ClearDropHighlight();
                e.DragEffects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            if (dropHoverBorder != cell)
            {
                ClearDropHighlight();
                dropHoverBorder = cell;
                dropHoverOriginalBackground = cell.Background;
                cell.Background = new SolidColorBrush(Color.FromArgb(90, 0, 120, 215));
            }
            var requested = executableTarget
                ? DragDropEffects.Copy
                : DropEffectFor(SuggestedTransferAction(e.KeyModifiers, sources, targets));
            e.DragEffects = CoerceDropEffect(e.DragEffects, requested);
            e.Handled = true;
        }

        void FileCell_DragLeave(object sender, DragEventArgs e) => ClearDropHighlight();

        async void FileCell_Drop(object sender, DragEventArgs e)
        {
            var cell = CellBorder(e.Source);
            ClearDropHighlight();
            var sources = GetDropSources(e);
            e.Handled = true;
            if (sources.Length == 0 || cell == null)
            {
                e.DragEffects = DragDropEffects.None;
                return;
            }

            if (TryGetExecutableDrop(cell, out var executable))
            {
                e.DragEffects = DragDropEffects.Copy;
                try
                {
                    StartProgramWith(executable, sources);
                    Model.Status = L.Format("ProgramStarted", executable, sources.Length);
                }
                catch (Exception ex)
                {
                    Model.Status = L.Format("ProgramStartFailed", executable, ex.Message);
                }
                return;
            }

            if (!TryGetDropTargets(cell, out var targets))
            {
                e.DragEffects = DragDropEffects.None;
                return;
            }
            var defaultAction = SuggestedTransferAction(e.KeyModifiers, sources, targets);
            e.DragEffects = CoerceDropEffect(e.DragEffects, DropEffectFor(defaultAction));
            if (await ChooseTransferAction(sources, targets.Length, defaultAction) is not { } action)
            {
                e.DragEffects = DragDropEffects.None;
                return;
            }
            await TransferPaths(sources, targets, action);
        }

        static string[] GetDropSources(DragEventArgs e)
        {
            try
            {
                return e.DataTransfer?.TryGetFiles() is { } items
                    ? items.Select(i => i.TryGetLocalPath())
                        .Where(p => !string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p)))
                        .Distinct(PathText.Comparer).ToArray()
                    : Array.Empty<string>();
            }
            catch { return Array.Empty<string>(); }
        }

        bool TryGetDropTargets(Border cell, out string[] targets)
        {
            targets = Array.Empty<string>();
            if (cell == null || RowFromSource(cell) is not { DataContext: INode hoveredNode } hovered)
                return false;

            var nodes = hovered.IsSelected
                ? SelectedNodes()
                : new[] { hoveredNode };
            targets = ((cell.Tag as string) == "Folder"
                    ? nodes.Select(n => IOPath.GetDirectoryName(n.FullName))
                    : nodes.Where(n => n.IsDirectory).Select(n => n.FullName))
                .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                .Distinct(PathText.Comparer)
                .ToArray();
            return targets.Length > 0;
        }

        static bool TryGetExecutableDrop(Border cell, out string executable)
        {
            executable = null;
            if (cell is not { Tag: "Name" } ||
                RowFromSource(cell) is not { DataContext: INode node } ||
                node.IsDirectory || !File.Exists(node.FullName) || !IsExecutable(node.FullName))
                return false;
            executable = node.FullName;
            return true;
        }

        /// <summary>
        /// A program: by extension on Windows, by its execute permission on Linux (scripts
        /// and AppImages included)
        /// </summary>
        static bool IsExecutable(string path)
        {
            if (OperatingSystem.IsWindows()) return ExecutableExtensions.Contains(IOPath.GetExtension(path));
            try
            {
                return File.Exists(path)
                    && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
            }
            catch { return false; }
        }

        static bool IsDescendant(string candidate, string directory)
        {
            var parent = IOPath.GetFullPath(directory).TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar);
            var child = IOPath.GetFullPath(candidate).TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar);
            return child.StartsWith(parent + IOPath.DirectorySeparatorChar, PathText.Comparison);
        }

        void ClearDropHighlight()
        {
            if (dropHoverBorder == null)
                return;
            dropHoverBorder.Background = dropHoverOriginalBackground ?? Brushes.Transparent;
            dropHoverBorder = null;
            dropHoverOriginalBackground = null;
        }

        async Task<FileTransferAction?> ChooseTransferAction(
            string[] sources,
            int destinationCount,
            FileTransferAction defaultAction)
        {
            var dialog = new FileTransferDialog(sources, destinationCount, defaultAction);
            return await dialog.ShowDialog<bool>(this) ? dialog.Action : null;
        }

        #endregion

        #region Context menu

        MenuItem openWithItem, addTargetItem, zipItem, zip7zItem, unzipItem;
        readonly List<Control> foundItems = new();

        static object MenuHeader(string text, string gesture)
        {
            if (string.IsNullOrEmpty(gesture)) return text;
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(new TextBlock { Text = text });
            var g = new TextBlock { Text = gesture, Foreground = Brushes.Gray, Margin = new Thickness(24, 0, 0, 0) };
            Grid.SetColumn(g, 1);
            grid.Children.Add(g);
            return grid;
        }

        MenuItem Item(string key, string gesture, Action click, bool capitalize = false)
        {
            var item = new MenuItem { Header = MenuHeader(capitalize ? L.Caption(key) : L.Text(key), gesture) };
            if (click != null) item.Click += (_, __) => click();
            return item;
        }

        ContextMenu BuildFilesContextMenu()
        {
            var menu = new ContextMenu();
            menu.Opening += FilesContextMenu_Opening;
            menu.Closed += (_, __) => contextTargetColumn = null;
            var showInFiles = Item("Show in files", "G", () => _ = ShowInFiles());
            var selectFound = Item("SelectFound", "S, G", SelectFound);
            var foundSeparator = new Separator();
            foundItems.AddRange(new Control[] { foundSeparator, showInFiles, selectFound });
            openWithItem = Item("OpenWith", null, null);
            addTargetItem = Item("AddTarget", null, ContextAddTarget);
            zipItem = Item("Zip", "Z", () => _ = Zip(ContextNodes(), Array.Empty<Key>()));
            zip7zItem = Item("Create7z", null, () => _ = Zip(ContextNodes(), new[] { Key.NumPad7 }));
            unzipItem = Item("Unzip", "U", () => _ = Unzip(ContextNodes(), Array.Empty<Key>()));
            menu.Items.Add(Item("Open", "O", () => _ = Open(null, ContextNodes())));
            menu.Items.Add(openWithItem);
            menu.Items.Add(Item("OpenFolder", null, () =>
            {
                foreach (var node in ContextNodes()) node.GetFileOrTempPath().Reveal();
            }));
            menu.Items.Add(foundSeparator);
            menu.Items.Add(showInFiles);
            menu.Items.Add(selectFound);
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Copy", "Ctrl+C", () => _ = Copy(ContextNodes(), Array.Empty<Key>())));
            menu.Items.Add(Item("Cut", "Ctrl+X", () => _ = Copy(ContextNodes(), Array.Empty<Key>(), true)));
            menu.Items.Add(Item("Paste", "Ctrl+V", () => _ = PasteWithDialog(ContextNodes())));
            menu.Items.Add(addTargetItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("CopyPath", null, () => _ = SetClipboardText(string.Join(Environment.NewLine, ContextNodes().Select(n => n.FullName)))));
            menu.Items.Add(Item("Rename", "F2", () => _ = Rename(ContextNodes(), new[] { Key.None })));
            menu.Items.Add(new Separator());
            menu.Items.Add(zipItem);
            menu.Items.Add(zip7zItem);
            menu.Items.Add(unzipItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Recycle", "Delete", () => _ = Delete(ContextNodes())));
            return menu;
        }

        INode[] ContextNodes() => SelectedNodes().ToArray();

        void FilesContextMenu_Opening(object sender, CancelEventArgs e)
        {
            var nodes = ContextNodes();
            var paths = contextTargetColumn == "Name"
                ? nodes.Select(NameTargetPath).ToArray()
                : contextTargetColumn == "Folder"
                    ? nodes.Select(x => IOPath.GetDirectoryName(x.FullName)).ToArray()
                    : Array.Empty<string>();
            var eligible = paths.Length > 0 && paths.All(IsEligibleTargetPath) &&
                paths.Any(path => !BasketTargets.Any(target =>
                    string.Equals(target.Path, path, PathText.Comparison)));
            addTargetItem.IsVisible = eligible;
            addTargetItem.Header = MenuHeader(L.Caption(contextTargetColumn == "Folder"
                ? "add parent Folders as targets"
                : "add selected as targets"), contextTargetColumn == "Folder" ? "Alt+F" : "Alt+N");
            PopulateOpenWith(nodes);
            foreach (var item in foundItems)
                item.IsVisible = Model.HasFound && (item is not MenuItem m || m != foundItems[1] || WebAppWindow.IsAvailable);
            var canZip = nodes.Length > 0 &&
                nodes.All(n => File.Exists(n.FullName) || Directory.Exists(n.FullName));
            zipItem.IsEnabled = canZip;
            zip7zItem.IsVisible = canZip && !string.IsNullOrWhiteSpace(Apps.SevenZip);
            unzipItem.IsVisible = nodes.Length > 0 && nodes.All(CanUnzip);
        }

        void PopulateOpenWith(INode[] nodes)
        {
            var openWith = openWithItem;
            openWith.Items.Clear();
            if (nodes.Length == 0)
            {
                openWith.IsEnabled = false;
                return;
            }

            var allFiles = nodes.All(n => !n.IsDirectory);
            var extensions = nodes.Select(n => IOPath.GetExtension(n.Name)).ToArray();
            void Add(string label, Key key, string executable, Func<string, bool> supports)
            {
                if (!allFiles || string.IsNullOrWhiteSpace(executable) || !extensions.All(supports))
                    return;
                var item = new MenuItem { Header = label };
                ToolTip.SetTip(item, executable);
                item.Click += async (_, __) => await OpenIn(nodes, new[] { key });
                openWith.Items.Add(item);
            }

            var diffApplication = FileApplications.FindDiff(nodes);
            var diffTool = diffApplication?.Executable;
            if (nodes.Length == 2 && !string.IsNullOrWhiteSpace(diffTool))
            {
                var diffItem = new MenuItem { Header = L.Text("Compare in diff tool") };
                ToolTip.SetTip(diffItem, diffTool);
                diffItem.Click += async (_, __) => await OpenDiff(nodes);
                openWith.Items.Add(diffItem);
            }
            if (allFiles && nodes.Length is 1 or 2 && WebAppWindow.IsAvailable)
            {
                var hexItem = new MenuItem { Header = L.Text("Hex editor / compare") };
                hexItem.Click += async (_, __) => await Open(Apps.HexEditor, nodes);
                openWith.Items.Add(hexItem);
            }

            foreach (var application in FileApplications.All)
            {
                // The default diff tool already has its "Compare in diff tool" entry above.
                if (application == diffApplication || !application.Supports(nodes)) continue;
                var executable = application.Executable;
                if (string.IsNullOrWhiteSpace(executable)) continue;
                var item = new MenuItem
                {
                    Header = MenuHeader(application.Name, "O " + string.Join(" ", FileApplications.ShortcutFor(application, nodes).Select(k =>
                        k is >= Key.D0 and <= Key.D9 ? ((int)k - (int)Key.D0).ToString() : k.ToString())))
                };
                ToolTip.SetTip(item, executable);
                item.Click += async (_, __) => await OpenApplication(application, nodes);
                openWith.Items.Add(item);
            }

            // The built-in Log explorer falls back to a text editor without a web view.
            Add(WebAppWindow.IsAvailable ? L.Text("Log explorer") : OperatingSystem.IsWindows() ? "Notepad" : L.Text("Edit"),
                Key.T, Apps.TextViever, TextExtensions.Contains);
            Add("Ghostscript", Key.G, Apps.GhostScript,
                extension => extension.Equals(".ps", StringComparison.OrdinalIgnoreCase) ||
                             extension.Equals(".eps", StringComparison.OrdinalIgnoreCase) ||
                             extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase));
            Add("GhostPCL", Key.P, Apps.GhostPcl,
                extension => extension.Equals(".pcl", StringComparison.OrdinalIgnoreCase) ||
                             extension.Equals(".pxl", StringComparison.OrdinalIgnoreCase) ||
                             extension.Equals(".px3", StringComparison.OrdinalIgnoreCase));
            Add("GhostXPS", Key.X, Apps.GhostXps,
                extension => extension.Equals(".xps", StringComparison.OrdinalIgnoreCase) ||
                             extension.Equals(".oxps", StringComparison.OrdinalIgnoreCase));

            if (allFiles && extensions.All(extension => extension.Equals(".prn", StringComparison.OrdinalIgnoreCase)))
            {
                var viewer = Apps.ViewerFor(nodes[0].FullName);
                if (!string.Equals(viewer, Apps.TextViever, StringComparison.OrdinalIgnoreCase))
                    Add(L.Text("PRN Viewer"), Key.R, viewer, _ => true);
            }
            openWith.IsEnabled = openWith.Items.Count > 0;
        }

        static bool IsEligibleTargetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)) ||
                IsTemporaryDragPath(path))
                return false;
            return GetBasketTargetKind(path) != BasketTargetKind.File;
        }

        static bool CanUnzip(INode node)
            => !node.IsDirectory && File.Exists(node.FullName) &&
               ArchiveExtensions.Contains(IOPath.GetExtension(node.Name));

        #endregion

        #region Keyboard

        // Keys pressed or released while Alt is involved: RightAlt maps to LeftAlt so AltGr
        // layouts get the same ALT commands, and the press/release pairs stay matched.
        static Key CommandKey(KeyEventArgs e)
        {
            var key = e.Key == Key.System ? Key.LeftAlt : e.Key;
            return key == Key.RightAlt ? Key.LeftAlt : key;
        }

        bool IsInlineEditorEvent(RoutedEventArgs e)
        {
            if (e.Source is not Visual source) return false;
            if (inlineRenameEditor != null && (source == inlineRenameEditor || inlineRenameEditor.IsVisualAncestorOf(source)))
                return true;
            if (source == inlineActionTextBox || inlineActionTextBox.IsVisualAncestorOf(source)) return true;
            return IsPinnedNameEditor(source, out var pinned) && pinned.DataContext is PinnedFilter { IsEditing: true };
        }

        void ListView_KeyDown(object sender, KeyEventArgs e)
        {
            if (IsInlineEditorEvent(e))
                return;
            var key = CommandKey(e);
            switch (key)
            {
                case Key.Up:
                case Key.Down:
                case Key.PageDown:
                case Key.PageUp:
                case Key.Left:
                case Key.Right:
                    // Grid navigation keys
                    if (filesViewCmd.IsReceivingCommandKeys) break;
                    return;
                case Key.F4 when e.KeyModifiers.HasFlag(KeyModifiers.Alt):
                    // Leave Alt+F4 to the system so the window can close
                    return;
                case Key.Tab:
                    return;
            }
            // Auto-repeats of a held key carry no new command information - processing them
            // would just spam the log (and watching one's own log file then loops forever)
            if (filesViewCmd.KeysDown().Contains(key))
            {
                e.Handled = true;
                return;
            }
            // Receive another command
            filesViewCmd.KeyPressed(key, e.KeySymbol);
            $"Key {key} pressed => Still down: {string.Join(", ", filesViewCmd.KeysDown().Select(x => x.ToString()))}".Debug();
            e.Handled = true;
        }

        void ListView_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (IsInlineEditorEvent(e))
                return;
            try
            {
                var key = CommandKey(e);
                e.Handled = filesViewCmd.KeyReleased(key);
                $"Key {key} released => Down: {string.Join(", ", filesViewCmd.KeysDown().Select(x => x.ToString()))}".Debug();
            }
            catch (Exception ex)
            {
                $"KeyDown exception {ex}".Debug();
                _ = MessageDialog.Show(this, $"{ex}", L.Text("CommandFailed"), MessageDialogButtons.Ok, MessageDialogIcon.Error);
            }
        }

        void Window_KeyDown(object sender, KeyEventArgs e)
        {
            currentModifiers = e.KeyModifiers;
            if (IsInlineEditorEvent(e))
                return;

            var modifiers = e.KeyModifiers;
            if (filesView.IsKeyboardFocusWithin && e.Key == Key.J &&
                (modifiers == KeyModifiers.Control ||
                 modifiers == (KeyModifiers.Control | KeyModifiers.Shift)))
            {
                if (!filesViewCmd.KeysDown().Contains(Key.J)) JumpToSelected(modifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
                return;
            }

            //ALT command chords work window-wide through the global commander - the same tree,
            //hints and behavior as in the result list (which feeds its own commander instead)
            if (!filesView.IsKeyboardFocusWithin)
            {
                var key = CommandKey(e);
                //Only pure LeftAlt starts a chord: AltGr (RightAlt/Ctrl+Alt) must keep typing
                //special characters into the text boxes, and F4/Space stay with the system
                //(close window, system menu)
                var starts = e.Key == Key.LeftAlt && modifiers == KeyModifiers.Alt;
                if ((starts || globalCmd.IsReceivingCommandKeys) && key != Key.F4 && key != Key.Space)
                {
                    if (!globalCmd.KeysDown().Contains(key)) globalCmd.KeyPressed(key, e.KeySymbol);
                    e.Handled = true;
                    return;
                }
            }
            switch (e.Key)
            {
                case Key.F1:
                    OpenHelp();
                    e.Handled = true;
                    return;
                case Key.F12:
                    //Refresh needs no selection => works window-wide, wherever the focus is
                    FSChangeProcessor.RefreshFromNFT();
                    e.Handled = true;
                    return;
                case Key.Escape:
                    // An active grid sequence owns Escape. Leave the event unhandled here so
                    // ListView_KeyDown can cancel the pending command instead of navigating
                    // the filter one level up.
                    if (filesView.IsKeyboardFocusWithin && filesViewCmd.IsReceivingCommandKeys) return;
                    //Let the suggestion popup close itself first
                    if (filterTextBox.IsListOpen || findTextBox.IsListOpen) return;
                    if (filesView.IsKeyboardFocusWithin && filesView.SelectedItems.Count > 0)
                    {
                        filesView.SelectedItems.Clear();
                        e.Handled = true;
                        return;
                    }
                    //Escape from current directory level up
                    filterTextBox.Text = new NodeFilter(filterTextBox.Text).Up().ToString();
                    break;
                case Key.Home:
                    if (!filesView.IsKeyboardFocusWithin) return;
                    ScrollResultsToBoundary(end: false);
                    e.Handled = true;
                    return;
                case Key.End:
                    if (!filesView.IsKeyboardFocusWithin) return;
                    ScrollResultsToBoundary(end: true);
                    e.Handled = true;
                    return;
                case Key.Left:
                    if (modifiers != KeyModifiers.Control) return;
                    filters.Add2History(filterTextBox.Text);
                    filterTextBox.Text = HistoryStep(filterTextBox.Text, filters.Backward);
                    break;
                case Key.Right:
                    if (modifiers != KeyModifiers.Control) return;
                    filterTextBox.Text = HistoryStep(filterTextBox.Text, filters.Forward);
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }

        void Window_KeyUp(object sender, KeyEventArgs e)
        {
            currentModifiers = e.KeyModifiers;
            if (IsInlineEditorEvent(e))
                return;
            //Complete the global commander's chord - the command runs when all keys are released
            if (!filesView.IsKeyboardFocusWithin && globalCmd.IsReceivingCommandKeys)
                e.Handled = globalCmd.KeyReleased(CommandKey(e));
        }

        /// <summary>
        /// History returns null at both of its ends. Assigning that null cleared the filter
        /// box, so one Ctrl+Left/Ctrl+Right too many wiped the current filter instead of
        /// simply staying where it is.
        /// </summary>
        internal static string HistoryStep(string current, string moved) => moved ?? current;

        #endregion

        #region Window layout

        private void ApplyWindowLayout()
        {
            pendingLayout = WindowLayoutStore.Load();
            if (pendingLayout == null)
                return;

            if (pendingLayout.Width > 0 && pendingLayout.Height > 0)
            {
                Width = pendingLayout.Width;
                Height = pendingLayout.Height;
            }

            if (!double.IsNaN(pendingLayout.Left) && !double.IsNaN(pendingLayout.Top))
            {
                var position = WindowLayoutStore.PixelPosition(pendingLayout);
                if (WindowLayoutStore.IsVisibleOnAnyScreen(Screens, position, pendingLayout))
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Position = position;
                }
            }

            WindowState = pendingLayout.WindowState == LayoutWindowState.Maximized
                ? WindowState.Maximized
                : WindowState.Normal;
        }

        private void ApplyColumnWidths(IReadOnlyList<double> widths)
        {
            var columns = filesView.Columns;
            var countIndex = columns.Select((column, index) => (column, index))
                .Where(x => (x.column.Tag as string) == nameof(INode.Count))
                .Select(x => x.index)
                .DefaultIfEmpty(-1)
                .First();
            //Layouts saved before Count had five positional widths. Leave the new column
            //at its default and shift Changed/Folder back onto their original saved values.
            var legacyWithoutCount = countIndex >= 0 && widths.Count == columns.Count - 1;
            for (var i = 0; i < columns.Count; i++)
            {
                if (legacyWithoutCount && i == countIndex) continue;
                var source = legacyWithoutCount && i > countIndex ? i - 1 : i;
                if (source >= widths.Count) break;
                var width = widths[source];
                //The Folder column fills the rest of the grid
                if (width > 0 && (columns[i].Tag as string) != nameof(INode.Folder))
                    columns[i].Width = new DataGridLength(width);
            }
        }

        private IReadOnlyList<double> CaptureColumnWidths()
            => filesView.Columns.Select(c => c.ActualWidth).ToArray();

        /// <summary>Currently filtered items</summary>
        IEnumerable<INode> Items => Model.Items;

        SearchModel Model => model;

        async void Window_Opened(object sender, EventArgs e)
        {
            filterTextBox.Focus();
            if (Program.OpenHelpRequested) OpenHelp();
            // UAC may have been accepted before this window subscribed or became visible.
            if (OperatingSystem.IsWindows() && Broker.ElevationAccepted) RestoreForegroundAfterElevation();
            await Model.Update("");
            ApplySavedColumnWidths();
        }

        private void ApplySavedColumnWidths()
        {
            if (pendingLayout?.ColumnWidths == null || pendingLayout.ColumnWidths.Count == 0)
                return;

            ApplyColumnWidths(pendingLayout.ColumnWidths);
            pendingLayout = null;
        }

        void Broker_StartupElevationAccepted()
        {
            try
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (IsVisible) RestoreForegroundAfterElevation();
                }, DispatcherPriority.ApplicationIdle);
            }
            catch (InvalidOperationException)
            {
                // The application is already shutting down.
            }
        }

        void RestoreForegroundAfterElevation()
        {
            if (elevationForegroundRestored) return;
            elevationForegroundRestored = true;
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

            // Activate normally first. If the system rejects that foreground request, briefly
            // enter the topmost z-order so the window is at least restored above other apps.
            Activate();
            if (!IsActive)
            {
                var wasTopmost = Topmost;
                Topmost = true;
                Activate();
                Topmost = wasTopmost;
            }
            Focus();
        }

        #endregion

        #region Sorting by column headers
        DataGridColumn sortedColumn;
        string displayedSort;

        internal static ListSortDirection HeaderSortDirection(string sort)
        {
            var ascending = sort[0] == '+';
            var sortBy = sort.Substring(1);

            // Numeric aggregate and date columns intentionally show their most useful
            // order first: largest/newest to smallest/oldest. Their '+' token therefore
            // maps to a descending value order, unlike text and content-result columns.
            if (sortBy == nameof(INode.Size) ||
                sortBy == nameof(INode.Count) ||
                sortBy == nameof(INode.LastChangeTime))
            {
                ascending = !ascending;
            }

            return ascending ? ListSortDirection.Ascending : ListSortDirection.Descending;
        }

        void ShowSortIndicator(string sort)
        {
            if (string.IsNullOrWhiteSpace(sort) || sort.Length < 2)
                return;

            var sortBy = sort.Substring(1);
            var column = filesView.Columns
                .FirstOrDefault(c => string.Equals(c.Tag as string, sortBy, StringComparison.Ordinal));
            if (column == null)
                return;

            if (sortedColumn != null && sortedColumn != column)
                sortedColumn.Header = columnHeaders.GetValueOrDefault(sortedColumn) ?? sortedColumn.Header;

            var up = HeaderSortDirection(sort) == ListSortDirection.Ascending;
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock { Text = columnHeaders.GetValueOrDefault(column) ?? column.Header as string });
            header.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Width = 7,
                Height = 9,
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Stretch = Stretch.Fill,
                Stroke = Brushes.Black,
                StrokeThickness = 1.25,
                StrokeLineCap = PenLineCap.Square,
                Data = Geometry.Parse(up ? "M 3.5,9 L 3.5,0 M 0,3.5 L 3.5,0 L 7,3.5" : "M 3.5,0 L 3.5,9 M 0,5.5 L 3.5,9 L 7,5.5")
            });
            column.Header = header;
            sortedColumn = column;
            displayedSort = sort;
        }

        async void ListViewColumnHeader_Click(object sender, DataGridColumnEventArgs e)
        {
            //The model sorts the whole index - never the grid's own copy of the window
            e.Handled = true;
            var c = Cursor;
            Cursor = new Cursor(StandardCursorType.Wait);
            try
            {
                // The sort field must be language-independent: the column header text is
                // localized, so the column's Tag carries the model's field name.
                var sortBy = e.Column?.Tag as string;
                if (string.IsNullOrEmpty(sortBy))
                    return;

                var sameColumn = displayedSort?.Length > 1 &&
                    string.Equals(displayedSort.Substring(1), sortBy, StringComparison.Ordinal);
                var sign = sameColumn && displayedSort[0] == '+' ? '-' : '+';
                var newSort = sign + sortBy;

                var watch = Stopwatch.StartNew();
                await Model.Update(newSort: newSort);
                var updateMs = watch.ElapsedMilliseconds;
                ShowSortIndicator(newSort);
                ResetSelectionCycle();

                await Dispatcher.UIThread.InvokeAsync(
                    () => ScrollResultsToBoundary(end: false),
                    DispatcherPriority.Loaded);
                $"column sort {newSort}: update {updateMs} ms, render {watch.ElapsedMilliseconds - updateMs} ms".Debug();
            }
            finally
            {
                Cursor = c;
            }
        }
        #endregion
    }

    /// <summary>
    /// Highlight of a pinned filter equal to the current filter text
    /// </summary>
    public class ActiveFilterBrushConverter : Avalonia.Data.Converters.IMultiValueConverter
    {
        static readonly IBrush Active = new ImmutableSolidColorBrush(Color.FromRgb(190, 220, 250));

        public object Convert(IList<object> values, Type targetType, object parameter, CultureInfo culture)
            => values.Count >= 2 && string.Equals(values[0] as string, values[1] as string, StringComparison.Ordinal)
                ? Active
                : Avalonia.AvaloniaProperty.UnsetValue;
    }
}
