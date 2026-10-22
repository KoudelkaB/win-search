using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using search.Models;
using IOPath = System.IO.Path;

namespace search
{
    /// <summary>Pinned filters, workspace import/export and the target basket</summary>
    public partial class MainWindow
    {
        void PinFilter_Click(object sender, RoutedEventArgs e)
        {
            if (!FinishPinnedFilterEdit(commit: true)) return;
            var pinned = new PinnedFilter
            {
                Name = SuggestedFilterName(filterTextBox.Text),
                Filter = filterTextBox.Text ?? "",
                IsDraft = true
            };
            PinnedFilters.Add(pinned);
            BeginPinnedFilterEdit(pinned);
        }

        static string SuggestedFilterName(string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return L.Text("AllFiles");
            var value = filter.Trim().Trim('"').TrimEnd(PathText.Sep);
            var name = IOPath.GetFileName(value);
            return string.IsNullOrWhiteSpace(name) || name.IndexOfAny(IOPath.GetInvalidFileNameChars()) >= 0
                ? (filter.Length > 40 ? filter.Substring(0, 40) : filter)
                : name;
        }

        void PinnedFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: PinnedFilter pinned })
            {
                filters.Used(filterTextBox.Text);
                filterTextBox.Text = pinned.Filter;
                filterTextBox.Focus();
            }
        }

        static PinnedFilter PinnedFilterFromMenu(object sender) =>
            (sender as MenuItem)?.DataContext as PinnedFilter;

        void PinnedFilterUpdate_Click(object sender, RoutedEventArgs e)
        {
            var pinned = PinnedFilterFromMenu(sender);
            if (pinned == null) return;
            var index = PinnedFilters.IndexOf(pinned);
            if (index < 0) return;
            PinnedFilters[index] = new PinnedFilter { Name = pinned.Name, Filter = filterTextBox.Text ?? "" };
            SaveWorkspaceSettings();
        }

        void PinnedFilterRename_Click(object sender, RoutedEventArgs e)
        {
            var pinned = PinnedFilterFromMenu(sender);
            if (pinned == null) return;
            CancelPinnedFilterEdit();
            BeginPinnedFilterEdit(pinned);
        }

        void PinnedFilterRemove_Click(object sender, RoutedEventArgs e)
        {
            var pinned = PinnedFilterFromMenu(sender);
            if (pinned != null && PinnedFilters.Remove(pinned)) SaveWorkspaceSettings();
        }

        void BeginPinnedFilterEdit(PinnedFilter pinned)
        {
            editingPinnedFilter = pinned;
            editingPinnedOriginalName = pinned.Name;
            pinned.IsEditing = true;
            FocusPinnedFilterName(pinned);
        }

        bool PinnedFilterNameIsValid(PinnedFilter pinned, out string error)
        {
            if (string.IsNullOrWhiteSpace(pinned?.Name))
            {
                error = L.Text("EnterName");
                return false;
            }
            if (PinnedFilters.Any(x => !ReferenceEquals(x, pinned) &&
                string.Equals(x.Name?.Trim(), pinned.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                error = L.Text("PinnedNameExists");
                return false;
            }
            error = null;
            return true;
        }

        bool FinishPinnedFilterEdit(bool commit)
        {
            if (finishingPinnedEdit || editingPinnedFilter == null) return true;
            var pinned = editingPinnedFilter;
            if (commit && !PinnedFilterNameIsValid(pinned, out var error))
            {
                Model.Status = error;
                return false;
            }
            finishingPinnedEdit = true;
            try
            {
                if (!commit)
                {
                    if (pinned.IsDraft) PinnedFilters.Remove(pinned);
                    else pinned.Name = editingPinnedOriginalName;
                }
                else
                {
                    pinned.Name = pinned.Name.Trim();
                    pinned.IsDraft = false;
                    SaveWorkspaceSettings();
                }
                pinned.IsEditing = false;
                editingPinnedFilter = null;
                editingPinnedOriginalName = null;
                return true;
            }
            finally { finishingPinnedEdit = false; }
        }

        void CancelPinnedFilterEdit() => FinishPinnedFilterEdit(commit: false);

        static bool IsPinnedNameEditor(object source, out TextBox editor)
        {
            editor = (source as Visual)?.FindAncestorOfType<TextBox>(includeSelf: true);
            return editor != null && editor.Classes.Contains("pinnedName");
        }

        void PinnedFilterName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (IsPinnedNameEditor(e.Source, out var editor)) UpdatePinnedFilterNameValidation(editor);
        }

        void UpdatePinnedFilterNameValidation(TextBox editor)
        {
            if (editor.DataContext is not PinnedFilter pinned) return;
            var valid = PinnedFilterNameIsValid(pinned, out var error);
            if (valid) editor.ClearValue(TextBox.BorderBrushProperty);
            else editor.BorderBrush = Brushes.IndianRed;
            ToolTip.SetTip(editor, error ?? pinned.Filter);
        }

        void PinnedFilterName_KeyDown(object sender, KeyEventArgs e)
        {
            if (!IsPinnedNameEditor(e.Source, out _)) return;
            if (e.Key == Key.Enter)
            {
                if (FinishPinnedFilterEdit(commit: true)) filterTextBox.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelPinnedFilterEdit();
                filterTextBox.Focus();
                e.Handled = true;
            }
        }

        void PinnedFilterName_LostFocus(object sender, RoutedEventArgs e)
        {
            if (finishingPinnedEdit || editingPinnedFilter == null || !IsPinnedNameEditor(e.Source, out var editor)) return;
            if (!FinishPinnedFilterEdit(commit: true))
                Dispatcher.UIThread.Post(() => editor.Focus(), DispatcherPriority.Input);
        }

        /// <summary>Focus the name editor of the pinned filter once its template exists</summary>
        void FocusPinnedFilterName(PinnedFilter pinned) =>
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(pinned, editingPinnedFilter)) return;
                var editor = pinnedItems.GetVisualDescendants().OfType<TextBox>()
                    .FirstOrDefault(t => t.Classes.Contains("pinnedName") && ReferenceEquals(t.DataContext, pinned));
                if (editor == null) return;
                UpdatePinnedFilterNameValidation(editor);
                editor.Focus();
                editor.SelectAll();
            }, DispatcherPriority.Loaded);

        static FilePickerFileType SettingsFiles => new(L.Text("SettingsFileType"))
        {
            Patterns = new[] { "*.winsearch-settings.json", "*.json" },
            MimeTypes = new[] { "application/json" }
        };

        async void ExportWorkspace_Click(object sender, RoutedEventArgs e)
        {
            IStorageFile file;
            try
            {
                file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = L.Text("Export pinned filters and targets"),
                    SuggestedFileName = "file-search-manager.winsearch-settings.json",
                    DefaultExtension = ".json",
                    FileTypeChoices = new[] { SettingsFiles, new FilePickerFileType(L.Text("JsonFiles")) { Patterns = new[] { "*.json" } } },
                    ShowOverwritePrompt = true
                });
            }
            catch (Exception ex)
            {
                Model.Status = ex.Message;
                return;
            }
            var path = file?.TryGetLocalPath();
            if (path == null) return;
            try
            {
                WorkspaceSettingsStore.Export(CaptureWorkspaceSettings(), path);
                Model.Status = L.Format("SettingsExported", path);
            }
            catch (Exception ex)
            {
                await MessageDialog.Show(this, ex.Message, L.Text("SettingsExportFailed"), MessageDialogButtons.Ok, MessageDialogIcon.Error);
            }
        }

        async void ImportWorkspace_Click(object sender, RoutedEventArgs e)
        {
            IReadOnlyList<IStorageFile> files;
            try
            {
                files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = L.Text("Import pinned filters and targets"),
                    AllowMultiple = false,
                    FileTypeFilter = new[] { SettingsFiles, new FilePickerFileType(L.Text("AllFiles")) { Patterns = new[] { "*" } } }
                });
            }
            catch (Exception ex)
            {
                Model.Status = ex.Message;
                return;
            }
            var path = files?.FirstOrDefault()?.TryGetLocalPath();
            if (path == null) return;
            try
            {
                var imported = WorkspaceSettingsStore.Import(path);
                if ((PinnedFilters.Count > 0 || BasketTargets.Count > 0) &&
                    await MessageDialog.Show(this,
                        L.Format("ImportReplaceConfirm", PinnedFilters.Count, BasketTargets.Count),
                        L.Text("Import pinned filters and targets"), MessageDialogButtons.YesNo, MessageDialogIcon.Question) != MessageDialogResult.Yes)
                    return;
                ApplyWorkspaceSettings(imported);
                SaveWorkspaceSettings();
                Model.Status = L.Format("SettingsImported", PinnedFilters.Count, BasketTargets.Count);
            }
            catch (Exception ex)
            {
                //A file that is not ours gets a plain sentence first; the parser detail stays below it
                var message = ex is InvalidDataException or System.Text.Json.JsonException
                    ? $"{L.Text("SettingsInvalid")}\n\n{ex.Message}"
                    : ex.Message;
                await MessageDialog.Show(this, message, L.Text("SettingsImportFailed"), MessageDialogButtons.Ok, MessageDialogIcon.Error);
            }
        }

        void AddNameTargets_Click(object sender, RoutedEventArgs e) =>
            AddBasketTargets(SelectedNodes().Select(NameTargetPath));

        void AddFolderTargets_Click(object sender, RoutedEventArgs e) =>
            AddBasketTargets(SelectedNodes().Select(x => IOPath.GetDirectoryName(x.FullName)));

        void ContextAddTarget()
        {
            var nodes = ContextNodes();
            AddBasketTargets(contextTargetColumn == "Folder"
                ? nodes.Select(x => IOPath.GetDirectoryName(x.FullName))
                : nodes.Select(NameTargetPath));
        }

        static string NameTargetPath(INode node) =>
            node != null && (File.Exists(node.FullName) || Directory.Exists(node.FullName))
                ? node.FullName
                : null;

        void AddBasketTargets(IEnumerable<string> paths)
        {
            var added = 0;
            foreach (var rawPath in paths.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                string path;
                try { path = WorkspaceSettingsStore.NormalizePath(rawPath); }
                catch { continue; }
                if ((!File.Exists(path) && !Directory.Exists(path)) ||
                    BasketTargets.Any(x => string.Equals(x.Path, path, PathText.Comparison)))
                    continue;
                var kind = GetBasketTargetKind(path);
                if (kind == BasketTargetKind.File || IsTemporaryDragPath(path))
                    continue;
                BasketTargets.Add(new BasketTarget { Path = path, Kind = kind });
                added++;
            }
            if (added > 0) SaveWorkspaceSettings();
            Model.Status = added > 0 ? L.Format("TargetsAdded", added) : L.Text("NoNewTargets");
        }

        static BasketTargetKind GetBasketTargetKind(string path)
        {
            if (Directory.Exists(path)) return BasketTargetKind.Folder;
            if (ArchiveExtensions.Contains(IOPath.GetExtension(path))) return BasketTargetKind.Archive;
            if (IsExecutable(path)) return BasketTargetKind.Executable;
            return BasketTargetKind.File;
        }

        void ClearTargets_Click(object sender, RoutedEventArgs e)
        {
            if (BasketTargets.Count == 0) return;
            BasketTargets.Clear();
            SaveWorkspaceSettings();
        }

        static BasketTarget TargetFromMenu(object sender) => (sender as MenuItem)?.DataContext as BasketTarget;

        void TargetRemove_Click(object sender, RoutedEventArgs e)
        {
            var target = TargetFromMenu(sender);
            if (target != null && BasketTargets.Remove(target)) SaveWorkspaceSettings();
        }

        void TargetFilter_Click(object sender, RoutedEventArgs e)
        {
            var target = TargetFromMenu(sender);
            if (target == null) return;
            var folder = target.Kind == BasketTargetKind.Folder ? target.Path : IOPath.GetDirectoryName(target.Path);
            filterTextBox.Text = string.IsNullOrWhiteSpace(folder) ? target.Name : $"\"{folder}\"";
            filterTextBox.Focus();
        }

        void TargetOpen_Click(object sender, RoutedEventArgs e) => OpenTarget(TargetFromMenu(sender));

        // Takes effect at the next start: the current session either already has the helper or
        // will raise its offer on the first action that needs it.
        void ElevateAtStart_Click(object sender, RoutedEventArgs e) =>
            ElevationSettingsStore.Save(elevateAtStart.IsChecked == true);

        async void Language_Click(object sender, RoutedEventArgs e)
        {
            var picker = new LanguageSelectionWindow(CultureInfo.CurrentUICulture.Name);
            await picker.ShowDialog(this);
            if (!picker.Accepted || picker.SelectedCulture == CultureInfo.CurrentUICulture.Name) return;
            LanguageSettingsStore.Save(picker.SelectedCulture);
            App.Restart();
        }

        void OpenTarget(BasketTarget target)
        {
            if (target == null) return;
            try { target.Path.Open(); }
            catch (Exception ex) { Model.Status = L.Format("OpenTargetFailed", ex.Message); }
        }

        // The two big drop zones are only shown while a drag is in progress: pinned for the
        // whole drag when it originates in the files view, otherwise kept alive by drag-over
        // activity on the bar and hidden by the timer shortly after the drag moves away.
        DispatcherTimer basketOverlayHide;
        bool basketOverlayPinned;

        void ShowBasketDropOverlay(bool pinned = false)
        {
            basketOverlayPinned |= pinned;
            BasketDropOverlay.IsVisible = true;
            if (basketOverlayPinned) return;
            if (basketOverlayHide == null)
            {
                basketOverlayHide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                basketOverlayHide.Tick += (o, e) => HideBasketDropOverlay();
            }
            basketOverlayHide.Stop();
            basketOverlayHide.Start();
        }

        void HideBasketDropOverlay()
        {
            basketOverlayPinned = false;
            basketOverlayHide?.Stop();
            BasketDropOverlay.IsVisible = false;
        }

        void ConfigureDragAndDrop()
        {
            //The bar shows the zones; it is not a drop spot itself
            TargetBar.AddHandler(DragDrop.DragOverEvent, (_, e) =>
            {
                if (GetDropSources(e).Length > 0) ShowBasketDropOverlay();
            }, RoutingStrategies.Tunnel);
            TargetBar.AddHandler(DragDrop.DragOverEvent, (_, e) =>
            {
                e.DragEffects = DragDropEffects.None;
                e.Handled = true;
            });

            BasketAddZone.AddHandler(DragDrop.DragOverEvent, BasketAddZone_DragOver);
            BasketAddZone.AddHandler(DragDrop.DragLeaveEvent, BasketZone_DragLeave);
            BasketAddZone.AddHandler(DragDrop.DropEvent, BasketAddZone_Drop);
            BasketUseZone.AddHandler(DragDrop.DragOverEvent, BasketUseZone_DragOver);
            BasketUseZone.AddHandler(DragDrop.DragLeaveEvent, BasketZone_DragLeave);
            BasketUseZone.AddHandler(DragDrop.DropEvent, BasketUseZone_Drop);

            //Target buttons are template items: one handler set on their zone finds the
            //button under the pointer; elsewhere in the zone a drop adds a new target
            BasketItemsZone.AddHandler(DragDrop.DragOverEvent, (s, e) =>
            {
                if (BasketTargetButton(e.Source) is { } button) BasketTarget_DragOver(button, e);
                else BasketAddZone_DragOver(BasketItemsZone, e);
            });
            BasketItemsZone.AddHandler(DragDrop.DragLeaveEvent, (s, e) =>
            {
                foreach (var b in basketItems.GetVisualDescendants().OfType<Button>()) b.Opacity = 1;
                BasketItemsZone.Opacity = 1;
            });
            BasketItemsZone.AddHandler(DragDrop.DropEvent, async (s, e) =>
            {
                if (BasketTargetButton(e.Source) is { } button) await BasketTarget_Drop(button, e);
                else BasketAddZone_Drop(BasketItemsZone, e);
            });
            basketItems.AddHandler(DoubleTappedEvent, (_, e) =>
            {
                if (BasketTargetButton(e.Source) is { Tag: BasketTarget target })
                {
                    OpenTarget(target);
                    e.Handled = true;
                }
            });

            //Result cells: Name accepts folders (and runs programs), Folder accepts its folder
            filesView.AddHandler(DragDrop.DragOverEvent, FileCell_DragOver);
            filesView.AddHandler(DragDrop.DragLeaveEvent, FileCell_DragLeave);
            filesView.AddHandler(DragDrop.DropEvent, FileCell_Drop);
        }

        static Button BasketTargetButton(object source)
            => (source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is { Tag: BasketTarget } button ? button : null;

        void BasketAddZone_DragOver(object sender, DragEventArgs e) => SetBasketDragEffect(sender, e,
            GetDropSources(e).Length > 0, DragDropEffects.Copy);

        void BasketUseZone_DragOver(object sender, DragEventArgs e)
        {
            var sources = GetDropSources(e);
            var targets = BasketTargets.Where(TargetIsAvailable).ToArray();
            SetBasketDragEffect(sender, e, targets.Length > 0 && sources.Length > 0,
                SuggestedBasketDropEffect(e, sources, targets));
        }

        void BasketTarget_DragOver(Button button, DragEventArgs e)
        {
            var sources = GetDropSources(e);
            var target = button.Tag as BasketTarget;
            SetBasketDragEffect(button, e, target != null && TargetIsAvailable(target) && sources.Length > 0,
                target == null ? DragDropEffects.None : SuggestedBasketDropEffect(e, sources, new[] { target }));
        }

        static void SetBasketDragEffect(object sender, DragEventArgs e, bool valid, DragDropEffects effect)
        {
            e.DragEffects = valid ? CoerceDropEffect(e.DragEffects, effect) : DragDropEffects.None;
            if (sender is Visual visual) visual.Opacity = valid ? 0.65 : 1;
            e.Handled = true;
        }

        void BasketZone_DragLeave(object sender, DragEventArgs e)
        {
            if (sender is Visual visual) visual.Opacity = 1;
        }

        void BasketAddZone_Drop(object sender, DragEventArgs e)
        {
            if (sender is Visual visual) visual.Opacity = 1;
            HideBasketDropOverlay();
            AddBasketTargets(GetDropSources(e));
            e.Handled = true;
        }

        async void BasketUseZone_Drop(object sender, DragEventArgs e)
        {
            BasketUseZone.Opacity = 1;
            HideBasketDropOverlay();
            e.Handled = true;
            var sources = GetDropSources(e);
            var targets = BasketTargets.ToArray();
            var defaultAction = SuggestedBasketTransferAction(e, sources, targets);
            e.DragEffects = DropEffectFor(defaultAction);
            await UseBasketTargets(sources, targets, defaultAction);
        }

        async Task BasketTarget_Drop(Button button, DragEventArgs e)
        {
            // Mark the drop handled before the first await - otherwise it bubbles on and the
            // zone's own drop action adds the source directory as a new target.
            e.Handled = true;
            if (button.Tag is not BasketTarget target)
            {
                e.DragEffects = DragDropEffects.None;
                return;
            }
            button.Opacity = 1;
            HideBasketDropOverlay();
            var sources = GetDropSources(e);
            var defaultAction = SuggestedBasketTransferAction(e, sources, new[] { target });
            e.DragEffects = DropEffectFor(defaultAction);
            await UseBasketTargets(sources, new[] { target }, defaultAction);
        }

        async void PasteToBasket_Click(object sender, RoutedEventArgs e) => await PasteToBasket();

        async Task PasteToBasket(FileTransferAction? action = null, FileCollisionAction? collision = null,
            bool chooseAction = true)
        {
            string[] paths;
            bool move;
            try
            {
                paths = await FileClipboard.GetFiles(this);
                move = await FileClipboard.RequestsMove(this);
            }
            catch (Exception ex) { Model.Status = L.Format("ClipboardReadFailed", ex.Message); return; }
            await UseBasketTargets(paths, BasketTargets.ToArray(),
                action ?? (move ? FileTransferAction.Move : FileTransferAction.Copy),
                collision, chooseAction);
        }

        static bool TargetIsAvailable(BasketTarget target) =>
            target.Kind == BasketTargetKind.Folder ? Directory.Exists(target.Path) : File.Exists(target.Path);

        static FileTransferAction SuggestedBasketTransferAction(DragEventArgs e, string[] sources,
            BasketTarget[] targets)
        {
            var folders = targets.Where(x => x.Kind == BasketTargetKind.Folder && TargetIsAvailable(x))
                .Select(x => x.Path).ToArray();
            if (folders.Length == 0) return FileTransferAction.Copy;
            var modifiers = e.KeyModifiers;
            // A heterogeneous or multi-target operation has no Shell equivalent. Keep
            // copy as the safe default unless the user explicitly requests an action.
            if (targets.Length > 1 && modifiers == KeyModifiers.None)
                return FileTransferAction.Copy;
            return SuggestedTransferAction(modifiers, sources, folders);
        }

        static DragDropEffects SuggestedBasketDropEffect(DragEventArgs e, string[] sources,
            BasketTarget[] targets) => DropEffectFor(SuggestedBasketTransferAction(e, sources, targets));

        internal static FileTransferAction SuggestedTransferAction(KeyModifiers modifiers, string[] sources,
            string[] destinations)
        {
            if (sources.Any(IsTemporaryDragPath)) return FileTransferAction.Copy;
            if (modifiers.HasFlag(KeyModifiers.Alt)) return FileTransferAction.SymbolicLink;
            if (modifiers.HasFlag(KeyModifiers.Control)) return FileTransferAction.Copy;
            if (modifiers.HasFlag(KeyModifiers.Shift)) return FileTransferAction.Move;
            if (destinations.Length != 1 || sources.Length == 0) return FileTransferAction.Copy;
            try
            {
                //Same drive (Windows) or the same mounted file system (Linux) => move
                var targetRoot = FsRoots.MountPointOf(IOPath.GetFullPath(destinations[0]));
                return sources.All(source => string.Equals(
                        FsRoots.MountPointOf(IOPath.GetFullPath(source)), targetRoot,
                        PathText.Comparison))
                    ? FileTransferAction.Move
                    : FileTransferAction.Copy;
            }
            catch { return FileTransferAction.Copy; }
        }

        static bool IsTemporaryDragPath(string path) =>
            IsBelow(path, App.TempFolder) || IsBelow(path, App.ClipboardTempFolder);

        static bool IsBelow(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
            var prefix = root.TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar) +
                IOPath.DirectorySeparatorChar;
            return path.StartsWith(prefix, PathText.Comparison);
        }

        static DragDropEffects DropEffectFor(FileTransferAction action) => action switch
        {
            FileTransferAction.Move => DragDropEffects.Move,
            FileTransferAction.SymbolicLink or FileTransferAction.HardLink => DragDropEffects.Link,
            _ => DragDropEffects.Copy
        };

        static DragDropEffects CoerceDropEffect(DragDropEffects allowed, DragDropEffects requested)
        {
            //Some platforms report no allowed effects at all - take the request as it is
            if (allowed == DragDropEffects.None || allowed.HasFlag(requested)) return requested;
            if (allowed.HasFlag(DragDropEffects.Copy)) return DragDropEffects.Copy;
            if (allowed.HasFlag(DragDropEffects.Move)) return DragDropEffects.Move;
            if (allowed.HasFlag(DragDropEffects.Link)) return DragDropEffects.Link;
            return DragDropEffects.None;
        }

        async Task UseBasketTargets(string[] sources, BasketTarget[] requestedTargets,
            FileTransferAction defaultFolderAction = FileTransferAction.Copy,
            FileCollisionAction? collisionForAll = null,
            bool chooseFolderAction = true)
        {
            sources = sources.Where(x => File.Exists(x) || Directory.Exists(x))
                .Distinct(PathText.Comparer).ToArray();
            var targets = requestedTargets.Where(TargetIsAvailable).ToArray();
            if (sources.Length == 0 || targets.Length == 0)
            {
                Model.Status = L.Text("NoSourcesOrTargets");
                return;
            }

            var folders = targets.Where(x => x.Kind == BasketTargetKind.Folder).ToArray();
            var archives = targets.Where(x => x.Kind == BasketTargetKind.Archive).ToArray();
            var executables = targets.Where(x => x.Kind == BasketTargetKind.Executable).ToArray();
            var unsupported = targets.Where(x => x.Kind == BasketTargetKind.File).ToArray();
            var sourceDescription = FileOperationText.DescribeItems(sources);
            var summary = $"{L.Format("BasketUseConfirm", targets.Length)}\n\n{sourceDescription}\n\n" +
                (folders.Length > 0 ? L.Format("BasketFolders", folders.Length) + "\n" : "") +
                (archives.Length > 0 ? L.Format("BasketArchives", archives.Length) + "\n" : "") +
                (executables.Length > 0 ? L.Format("BasketPrograms", executables.Length) + "\n" : "") +
                (unsupported.Length > 0 ? L.Format("BasketUnsupported", unsupported.Length) + "\n" : "");
            if ((targets.Length > 1 || executables.Length > 0 || archives.Length > 0) &&
                await MessageDialog.Show(this, summary, Title, MessageDialogButtons.OkCancel,
                    MessageDialogIcon.Question) != MessageDialogResult.Ok)
                return;

            FileTransferAction? folderAction = null;
            if (folders.Length > 0)
            {
                //Keyboard sequences name the action explicitly and transfer directly;
                //the chooser dialog belongs to the mouse paths (toolbar button, drop)
                if (!chooseFolderAction)
                    folderAction = defaultFolderAction;
                else if (await ChooseTransferAction(sources, folders.Length, defaultFolderAction) is { } selectedAction)
                    folderAction = selectedAction;
                else
                    return;
            }

            var errors = new List<string>();
            foreach (var archive in archives)
            {
                try { await Task.Run(() => ZipExtensions.AddToArchive(archive.Path, sources)); }
                catch (Exception ex) { errors.Add($"{archive.Path}: {ex.Message}"); }
            }
            foreach (var executable in executables)
            {
                try { StartProgramWith(executable.Path, sources); }
                catch (Exception ex) { errors.Add($"{executable.Path}: {ex.Message}"); }
            }
            if (folders.Length > 0)
            {
                if (!await TransferPaths(sources, folders.Select(x => x.Path).ToArray(), folderAction.Value, collisionForAll))
                    errors.Add(L.Text("FolderTransfersIncomplete"));
            }
            Model.Status = errors.Count == 0
                ? L.Format("BasketUsed", sourceDescription, targets.Length)
                : CompletedWithErrors(errors);
        }

        /// <summary>Start a program with the paths as its arguments, in its own folder</summary>
        static void StartProgramWith(string executable, IReadOnlyList<string> paths)
        {
            if (!OperatingSystem.IsWindows())
            {
                Shell.Start(executable, paths, IOPath.GetDirectoryName(executable));
                return;
            }
            var start = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true,
                WorkingDirectory = IOPath.GetDirectoryName(executable)
            };
            foreach (var path in paths) start.ArgumentList.Add(path);
            Process.Start(start);
        }
    }
}
