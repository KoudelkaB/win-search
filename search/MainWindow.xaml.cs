using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using search.Models;
using SharpCompress.Common;
using IOPath = System.IO.Path;

namespace search
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        Filters filters = new Filters();
        SearchTerms searchTerms = new SearchTerms();
        public ObservableCollection<PinnedFilter> PinnedFilters { get; } = new();
        public ObservableCollection<BasketTarget> BasketTargets { get; } = new();
        bool updatingFolderWidth;
        bool columnWidthHandlersAttached;
        WindowLayout pendingLayout;
        Point? dragStart;
        INode dragSourceNode;
        string dragSourceColumn;
        bool preserveSelectionOnMouseUp;
        bool dragStarted;
        Border dropHoverBorder;
        PinnedFilter editingPinnedFilter;
        string editingPinnedOriginalName;
        bool finishingPinnedEdit;
        INode inlineRenameNode;
        TextBox inlineRenameEditor;
        TextBlock inlineRenameDisplay;
        bool finishingInlineRename;
        bool elevationForegroundRestored;
        INode[] newFolderTargets;
        bool newFolderOverwrite;
        string contextTargetColumn;
        static readonly HashSet<string> ExecutableExtensions = new(
            new[] { ".exe", ".com", ".bat", ".cmd", ".msi", ".ps1", ".vbs", ".vbe",
                    ".js", ".jse", ".wsf", ".wsh", ".msc", ".lnk" }
                .Concat((Environment.GetEnvironmentVariable("PATHEXT") ?? "")
                    .Split(';', StringSplitOptions.RemoveEmptyEntries)),
            StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> ArchiveExtensions = new(
            new[] { ".zip", ".7z", ".rar", ".tar", ".gz", ".gzip", ".tgz",
                    ".bz2", ".bzip2", ".tbz", ".tbz2", ".xz", ".txz", ".lz", ".lzip" },
            StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> TextExtensions = new(
            new[] { ".txt", ".log", ".csv", ".ini", ".cfg", ".conf", ".json", ".xml",
                    ".yaml", ".yml", ".md", ".cs", ".xaml", ".csproj", ".sln", ".ps1",
                    ".bat", ".cmd", ".html", ".htm", ".sql", ".prn" },
            StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> WebExtensions = new(
            new[] { ".htm", ".html", ".xml", ".json", ".pdf", ".svg", ".png", ".jpg",
                    ".jpeg", ".gif", ".webp", ".txt" },
            StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> CodeExtensions = new(
            TextExtensions.Concat(new[] { ".vb", ".cpp", ".c", ".h", ".hpp", ".js", ".ts",
                                          ".css", ".scss", ".py", ".java", ".vcxproj", ".vbproj" }),
            StringComparer.OrdinalIgnoreCase);

        public MainWindow()
        {
            InitializeComponent();
            Broker.StartupElevationAccepted += Broker_StartupElevationAccepted;
            ApplyWorkspaceSettings(WorkspaceSettingsStore.Load());
            ApplyWindowLayout();
            AttachColumnWidthHandlers();
            SizeChanged += (_, __) => UpdateFolderColumnWidth();
            Closing += (_, __) =>
            {
                Broker.StartupElevationAccepted -= Broker_StartupElevationAccepted;
                WindowLayoutStore.Save(this, CaptureColumnWidths());
                SaveWorkspaceSettings();
            };

            DataContext = new Models.SearchModel();
            ShowSortIndicator(Models.SearchModel.DefaultSort);
            filterTextBox.SuggestionList = () => Keyboard.Modifiers == ModifierKeys.Control ? filters.LastUsed : filters.MostUsed;
            filterTextBox.TextSelected += t => filters.Add2History(filterTextBox.Text);
            filterTextBox.DeleteItem = item => filters.Delete(item);

            // Configure findTextBox with search terms autocomplete
            findTextBox.SuggestionList = () => Keyboard.Modifiers == ModifierKeys.Control ? searchTerms.LastUsed : searchTerms.MostUsed;
            findTextBox.TextSelected += t => searchTerms.Add2History(findTextBox.Text);
            findTextBox.DeleteItem = item => searchTerms.Delete(item);

            filterTextBoxCmd.Text = L.Text("FilterFieldHints");
            findCmd.Text = L.Text("SearchFieldHints");

            // Add commander
            CommandTree Enter(string hint) => (Key.None, L.Format("EnterValueKeepingKey", L.Text(hint)));
            var openIn = new CommandTree[] {
                (Key.T, "Text viever (LogReader id installed)", n => n.AtLeast(1) && Apps.TextViever !=null),
                (Key.B, "File browser", n => n.AtLeast(1) && Apps.Explorer !=null),
                (Key.W, "Web browser", n => n.AtLeast(1) && Apps.WebBrowser !=null),
                (Key.C, "Chrome", n => n.AtLeast(1) && Apps.Chrome !=null),
                (Key.F, "Firefox", n => n.AtLeast(1) && Apps.Firefox !=null),
                (Key.O, "Opera", n => n.AtLeast(1) && Apps.Opera !=null),
                (Key.E, "Edge", n => n.AtLeast(1) && Apps.Edge !=null),
                (Key.I, "Internet explorer", n => n.AtLeast(1) && Apps.IExplore !=null),
                (Key.A, "Adobe Reader", n => n.AtLeast(1) && Apps.AdobeReader !=null),
                (Key.S, "Shell", (Key.P, "Power Shell")),
                (Key.V, "Visual Studio", n => n.AtLeast(1) && Apps.VisualStudio !=null),
                (Key.D, "Visual Studio Code", n => n.AtLeast(1) && Apps.VSCode !=null),
                (Key.Y, "Antigravity", n => n.AtLeast(1) && Apps.Antigravity !=null),
                (Key.G, "Ghostscript", n => n.AtLeast(1) && Apps.GhostScript !=null, Enter("DPI")),
                (Key.P, "GhostPCL",  n => n.AtLeast(1) && Apps.GhostPcl !=null, Enter("DPI")),
                (Key.X, "GhostXPS",  n => n.AtLeast(1) && Apps.GhostXps !=null, Enter("DPI")),
                (Key.R, "PRN Viewer", n => n.AtLeast(1) && Apps.TextViever !=null, Enter("DPI"))
            };

            var timeSource = new CommandTree[] { (Key.V, "from clipboard"), (Key.C, "current") };
            var f2 = new CommandTree[] {
                    (Key.V, "Path/name from clipboard"),
                    (Key.N, "Change name",Enter("string to delete")),
                    (Key.E, "Change extension", new CommandTree[] { Enter("Extension"), (Key.Delete,"Delete extension") }),
                    (Key.C, "Change creation time", timeSource),
                    (Key.W, "Change last write time", timeSource),
                    (Key.OemPeriod, "Add extension",Enter("Extension")),
                    (Key.Delete, "delete from name",Enter("string to delete")),
                    (Key.F, "Add as prefix",Enter("prefix")),
                    (Key.L, "Add as posfix",Enter("postfix")),
                    (Key.Insert, "insert string",Enter("insertion index")),
                    (Key.R, "Replace substring",Enter("string to replace"))
            };

            var overwrite = (Key.O, "Overwrite");
            //Keyboard names the action explicitly (L/H or the copy/move default) => transfer
            //directly; only the mouse paths (toolbar button, drop) ask with the action dialog
            CommandTree.Command pasteToAllTargets = async (n, a) => await PasteToBasket(
                a.Contains(Key.L) ? FileTransferAction.SymbolicLink :
                a.Contains(Key.H) ? FileTransferAction.HardLink : null,
                a.Contains(Key.O) ? FileCollisionAction.Overwrite : null,
                chooseAction: false);
            var pasteToAllKeys = new CommandTree[] { overwrite, (Key.L, "as link", overwrite), (Key.H, "as hard link", overwrite) };
            //One ALT command set - the result-list commander and the window-wide commander
            //(hints panel) share it, so the same sequences behave the same everywhere
            var altCommands = new CommandTree[] {
                (Key.N, "add selected as targets", n => n.AtLeast(1), (n,a) => AddBasketTargets(n.Select(NameTargetPath))),
                (Key.F, "add parent Folders as targets", n => n.AtLeast(1), (n,a) => AddBasketTargets(n.Select(x => IOPath.GetDirectoryName(x.FullName)))),
                (Key.V, "send clipboard to all targets", pasteToAllTargets, pasteToAllKeys),
                (Key.C, "Clear targets", (n,a) => ClearTargets_Click(this, new RoutedEventArgs())),
                (Key.P, "Pin current filter", (n,a) => PinFilter_Click(this, new RoutedEventArgs())),
                (Key.I, "Import pinned filters and targets", (n,a) => ImportWorkspace_Click(this, new RoutedEventArgs())),
                (Key.E, "Export pinned filters and targets", (n,a) => ExportWorkspace_Click(this, new RoutedEventArgs()))
            };
            filesViewCmd.Commands.Add(new CommandTree[] {
                //F1/F12 execute in Window_KeyDown before the grid sees them. Keeping them
                //in this tree makes the working window-wide shortcuts visible in grid hints.
                (Key.F1, "HintF1"),
                (Key.F12, "HintF12"),
                (Key.D, "Compare in diff tool", n => n.IsCount(2),  async (n,a) => await OpenDiff(n)),
                (Key.Enter, "Filter folders", async (n,a)=> await FilterFolders(n.ToArray())),
                (Key.Delete, "Delete", async (n,a)=>await Delete(n)),
                (Key.C, "Copy", (n,a) => Copy(n,a), new CommandTree[] {
                    (Key.Add, "Append to clipboard", (n,a) => CopyAppend(n,a), new CommandTree[] { (Key.V, "file version"), (Key.T, "creation time"), (Key.W, "last write time"), (Key.A, "last access time") }),
                    (Key.V, "file version"),
                    (Key.T, "creation time"),
                    (Key.W, "last write time"),
                    (Key.A, "last access time")
                }),
                (Key.O, "Open in", async (n,a)=> await OpenIn(n,a), openIn),
                (Key.A, "Open in (as admin)", async (n,a)=> await OpenIn(n,a,true), openIn),
                (Key.F2, "Rename / Change", Rename, new CommandTree[] {(Key.O, "Overwrite", f2)}.Concat(f2).ToArray()),
                (Key.F3, "View node", async (n,a)=>{ if (n.Any()) await Open(Apps.ViewerFor(n.First().GetFileOrTempPath()), Model.ToTextNodes(n.ToArray()).ToArray()); }),
                (Key.F4, "Edit",async (n,a)=> await Open(Apps.TextEditor, n.ToArray())),
                (Key.N, "Copy name", (n,a)=>SetCpliboard(String.Join("\r\n", n.Select(n => n.Name)))),
                (Key.P, "Copy path", (n,a)=>SetCpliboard(String.Join("\r\n", n.Select(n => n.FullName)))),
                (Key.F, "Copy folder name", (n,a)=>SetCpliboard(String.Join("\r\n", n.Select(n => Model.GetParent(n).FullName)))),
                (Key.M, "Make directory in selected", (n,a)=>NewFolders(n,a),Enter("folder name")),
                (Key.V, "Paste",async (n,a)=>await Paste(n,a), new CommandTree[] { overwrite, (Key.L, "as link", overwrite), (Key.H, "as hard link", overwrite) }),
                (Key.X, "Cut", (n,a)=> Copy(n,a,true)),
                (Key.S, "Select", new CommandTree[] {
                    (Key.A, "All", (n,a) => { if (filesView.SelectedItems.Count == filesView.Items.Count) filesView.UnselectAll(); else filesView.SelectAll();}),
                    (Key.D, "Directories", (n,a) => filesView.Select(Items.Where(n => n.IsDirectory))),
                    (Key.F, "Files",  (n,a) => filesView.Select(Items.Where(n => !n.IsDirectory))),
                    (Key.I, "Invert selection", (n,a) => InvertSelection()),
                    (Key.G, "Green rows", (n,a) => filesView.Select(Items.Where(n => Model.FoundIn(n) == true))),
                    (Key.R, "Red rows", (n,a) => filesView.Select(Items.Where(n => Model.FoundIn(n) == false))),
                    (Key.B, "Black rows", (n,a) => filesView.Select(Items.Where(n => Model.FoundIn(n) == null))),
                }),
                (Key.T, "add selected as targets", (n,a) => AddBasketTargets(n.Select(NameTargetPath)), new CommandTree[] {
                    (Key.F, "add parent Folders as targets", n => n.AtLeast(1), (n,a) => AddBasketTargets(n.Select(x => IOPath.GetDirectoryName(x.FullName)))),
                    (Key.V, "send clipboard to all targets", pasteToAllTargets, pasteToAllKeys),
                    (Key.C, "Clear targets", (n,a) => ClearTargets_Click(this, new RoutedEventArgs()))
                }),
                (Key.LeftAlt, "ALT", altCommands),
                (Key.LeftCtrl,"CTRL", new CommandTree[] {
                    (Key.A, "Select/Unselect all", (n,a) => filesView.ToogleSelectAll()),
                    (Key.C, "Copy", (n,a) => Copy(n,a)),
                    (Key.D, "Filter Directores of selected", async (n,a) => await FilterFolders(n.Select(x => Model.GetParent(x)).ToArray())),
                    (Key.F, "Filter selected Folders", async (n,a) => await FilterFolders(n.Where(n => n.IsDirectory).ToArray())),
                    (Key.N, "Create new folders in selected", (n,a) => NewFolders(n,a),Enter("folder name")),
                    (Key.V, "Paste (choose action)", async (n,a) => await PasteWithDialog(n)),
                    (Key.X, "Cut", (n,a) => Copy(n,a,true))
                }),
                (Key.LeftShift,"Shift", new CommandTree[] {
                    (Key.Delete, "Permanently delete - do not confirm", async (n,a)=>await Delete(n,false,true)),
                }),
                (Key.RightShift, "Focus filter", (n,a)=>filterTextBox.Focus()),
                (Key.U, "Unzip archive", async (n,a)=>await Unzip(n,a), new CommandTree[] {(Key.NumPad7, "Call 7z.exe")}),
                (Key.Z, "Zip selected", async (n,a)=>await Zip(n,a), new CommandTree[] {(Key.NumPad7, "Call 7z.exe")})
            });

            // Save filter to history on any command
            filesViewCmd.OnCommand += () => filters.Used(filterTextBox.Text);
            filesViewCmd.nodes = () => filesView.SelectedItems?.Cast<INode>() ?? Enumerable.Empty<INode>();
            filesView.SelectionChanged += (o, e) => filesViewCmd.OnChange();

            //The window-wide commander: the same ALT tree (and behavior) as the result list,
            //fed from Window_KeyDown/KeyUp whenever the list itself does not have the focus.
            //F12/Escape execute in Window_KeyDown - listed here so the hints show them
            globalCmd.Commands.Add(new CommandTree[] {
                (Key.F1, "HintF1"),
                (Key.F12, "HintF12"),
                (Key.Escape, "HintEscape"),
                (Key.LeftAlt, "ALT", altCommands)
            });
            globalCmd.OnCommand += () => filters.Used(filterTextBox.Text);
            globalCmd.nodes = () => filesView.SelectedItems?.Cast<INode>() ?? Enumerable.Empty<INode>();
            filesView.SelectionChanged += (o, e) => globalCmd.OnChange();
            globalCmd.OnChange();

            // Preserver selection on Items update
            INode[] selected = null;
            INode focused = null;
            var selectedStart = -1;
            var restoreKeyboardFocus = false;
            Model.BeforeItemsExchange = () =>
            {
                selected = filesView.SelectedItems.Cast<INode>().ToArray();
                selectedStart = selected.Select(filesView.Items.IndexOf)
                    .Where(i => i >= 0).DefaultIfEmpty(-1).Min();
                restoreKeyboardFocus = filesView.IsKeyboardFocusWithin;
                focused = restoreKeyboardFocus
                    ? (ItemsControl.ContainerFromElement(filesView, Keyboard.FocusedElement as DependencyObject)
                        as ListViewItem)?.DataContext as INode
                    : null;
            };
            Model.AfterItemsExchange = () =>
            {
                var survivors = selected?.Where(filesView.Items.Contains).ToArray() ?? Array.Empty<INode>();
                if (survivors.Length > 0) filesView.Select(survivors);

                if (restoreKeyboardFocus)
                {
                    var focusTarget = focused != null && filesView.Items.Contains(focused)
                        ? focused
                        : survivors.OrderBy(filesView.Items.IndexOf).FirstOrDefault();

                    //All selected rows disappeared (typically a completed delete). Keep the
                    //keyboard caret at the block's old first index, which now contains the
                    //next row; at the end of the list fall back to the preceding row.
                    if (focusTarget == null && selected?.Length > 0)
                    {
                        var continuation = SelectionContinuationIndex(selectedStart, filesView.Items.Count);
                        if (continuation >= 0)
                        {
                            focusTarget = filesView.Items[continuation] as INode;
                            filesView.SelectedItem = focusTarget;
                        }
                    }
                    if (focusTarget != null) FocusFileRow(focusTarget);
                }

                selected = null;
                focused = null;
                selectedStart = -1;
                restoreKeyboardFocus = false;
                UpdateFolderColumnWidth();
            };
            Model.UIRefreshRequested += () => Dispatcher.Invoke(() =>
            {
                filesView.Items.Refresh();
                UpdateFolderColumnWidth();
            });
            //Repaint just the changed rows, and only when they are actually realized on screen.
            //A full Items.Refresh() regenerates every visible row (blink) and resets the keyboard
            //navigation state, breaking SHIFT+Up/Down selection on unrelated file system changes.
            Model.RowsRefreshRequested += nodes => Dispatcher.Invoke(() =>
            {
                foreach (var node in nodes)
                {
                    if (filesView.ItemContainerGenerator.ContainerFromItem(node) is ListViewItem row)
                    {
                        //INode raises no change notifications => rebind the row to re-read its values.
                        //Off-screen rows need nothing: virtualization rebinds them when realized.
                        var context = row.DataContext;
                        row.DataContext = null;
                        row.DataContext = context;
                    }
                }
            });
        }

        internal static int SelectionContinuationIndex(int firstSelectedIndex, int remainingCount)
            => firstSelectedIndex < 0 || remainingCount <= 0
                ? -1
                : Math.Min(firstSelectedIndex, remainingCount - 1);

        void FocusFileRow(INode node)
        {
            bool TryFocus()
            {
                if (!filesView.Items.Contains(node)) return true;
                filesView.ScrollIntoView(node);
                if (filesView.ItemContainerGenerator.ContainerFromItem(node) is not ListViewItem row) return false;
                row.Focus(); //Updates WPF's keyboard action item used as the next SHIFT anchor
                return true;
            }

            //A replaced/virtualized collection may realize the row only after layout.
            if (!TryFocus()) Dispatcher.BeginInvoke(() => TryFocus(), DispatcherPriority.Input);
        }

        WorkspaceSettings CaptureWorkspaceSettings() => new()
        {
            PinnedFilters = PinnedFilters.Select(x => new PinnedFilter
            {
                Name = x.Name,
                Filter = x.Filter
            }).ToList(),
            BasketTargets = BasketTargets.Select(x => new BasketTarget
            {
                Path = x.Path,
                Kind = x.Kind
            }).ToList()
        };

        void ApplyWorkspaceSettings(WorkspaceSettings settings)
        {
            PinnedFilters.Clear();
            foreach (var filter in settings.PinnedFilters)
                PinnedFilters.Add(filter);
            BasketTargets.Clear();
            foreach (var target in settings.BasketTargets)
                BasketTargets.Add(target);
        }

        void SaveWorkspaceSettings()
        {
            try { WorkspaceSettingsStore.Save(CaptureWorkspaceSettings()); }
            catch (Exception ex) { Model.Status = $"Could not save workspace settings: {ex.Message}"; }
        }

        void Drives_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new DrivesWindow { Owner = this };
            //A changed selection reflects on the next scan => refresh right away
            if (dialog.ShowDialog() == true) FSChangeProcessor.RefreshFromNFT();
        }

        void PinFilter_Click(object sender, RoutedEventArgs e)
        {
            CancelPinnedFilterEdit();
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
            if (string.IsNullOrWhiteSpace(filter)) return "All files";
            var value = filter.Trim().Trim('"').TrimEnd('\\');
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

        PinnedFilter PinnedFilterFromMenu(object sender) =>
            sender is MenuItem item && item.Parent is ContextMenu menu &&
            menu.PlacementTarget is FrameworkElement { Tag: PinnedFilter pinned } ? pinned : null;

        void PinnedFilterUpdate_Click(object sender, RoutedEventArgs e)
        {
            var pinned = PinnedFilterFromMenu(sender);
            if (pinned == null) return;
            var index = PinnedFilters.IndexOf(pinned);
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
        }

        bool PinnedFilterNameIsValid(PinnedFilter pinned, out string error)
        {
            if (string.IsNullOrWhiteSpace(pinned?.Name))
            {
                error = "Enter a name.";
                return false;
            }
            if (PinnedFilters.Any(x => !ReferenceEquals(x, pinned) &&
                string.Equals(x.Name?.Trim(), pinned.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                error = "A pinned filter with this name already exists.";
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

        void PinnedFilterName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox { DataContext: PinnedFilter pinned } editor) return;
            var valid = PinnedFilterNameIsValid(pinned, out var error);
            editor.BorderBrush = valid ? SystemColors.ControlDarkBrush : Brushes.IndianRed;
            editor.ToolTip = error ?? pinned.Filter;
        }

        void PinnedFilterName_KeyDown(object sender, KeyEventArgs e)
        {
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

        void PinnedFilterName_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (finishingPinnedEdit || editingPinnedFilter == null) return;
            if (!FinishPinnedFilterEdit(commit: true) && sender is TextBox editor)
                Dispatcher.BeginInvoke(() => editor.Focus(), DispatcherPriority.Input);
        }

        void PinnedFilterName_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is TextBox editor && editor.IsVisible)
                Dispatcher.BeginInvoke(() =>
                {
                    editor.Focus();
                    editor.SelectAll();
                }, DispatcherPriority.Input);
        }

        void ExportWorkspace_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Export pinned filters and targets",
                Filter = "File Search Manager settings (*.winsearch-settings.json)|*.winsearch-settings.json|JSON files (*.json)|*.json",
                FileName = "file-search-manager.winsearch-settings.json",
                AddExtension = true,
                DefaultExt = ".json"
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                WorkspaceSettingsStore.Export(CaptureWorkspaceSettings(), dialog.FileName);
                Model.Status = $"Exported workspace settings to \"{dialog.FileName}\"";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Settings export failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        void ImportWorkspace_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Import pinned filters and targets",
                Filter = "File Search Manager settings (*.winsearch-settings.json;*.json)|*.winsearch-settings.json;*.json|All files (*.*)|*.*",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var imported = WorkspaceSettingsStore.Import(dialog.FileName);
                if ((PinnedFilters.Count > 0 || BasketTargets.Count > 0) &&
                    MessageBox.Show(this,
                        $"Replace {PinnedFilters.Count} pinned filter(s) and {BasketTargets.Count} target(s) with the imported settings?",
                        "Import settings", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;
                ApplyWorkspaceSettings(imported);
                SaveWorkspaceSettings();
                Model.Status = $"Imported {PinnedFilters.Count} pinned filter(s) and {BasketTargets.Count} target(s)";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Settings import failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        void AddNameTargets_Click(object sender, RoutedEventArgs e) =>
            AddBasketTargets(filesView.SelectedItems.Cast<INode>().Select(NameTargetPath));

        void AddFolderTargets_Click(object sender, RoutedEventArgs e) =>
            AddBasketTargets(filesView.SelectedItems.Cast<INode>().Select(x => IOPath.GetDirectoryName(x.FullName)));

        void ContextAddTarget_Click(object sender, RoutedEventArgs e)
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
                    BasketTargets.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var kind = GetBasketTargetKind(path);
                if (kind == BasketTargetKind.File || IsTemporaryDragPath(path))
                    continue;
                BasketTargets.Add(new BasketTarget { Path = path, Kind = kind });
                added++;
            }
            if (added > 0) SaveWorkspaceSettings();
            Model.Status = added > 0 ? $"Added {added} target(s)" : "No new valid targets to add";
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

        BasketTarget TargetFromMenu(object sender) =>
            sender is MenuItem item && item.Parent is ContextMenu menu &&
            menu.PlacementTarget is FrameworkElement { Tag: BasketTarget target } ? target : null;

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

        void TargetOpen_Click(object sender, RoutedEventArgs e)
        {
            var target = TargetFromMenu(sender);
            OpenTarget(target);
        }

        void Language_Click(object sender, RoutedEventArgs e)
        {
            var picker = new LanguageSelectionWindow(CultureInfo.CurrentUICulture.Name) { Owner = this };
            if (picker.ShowDialog() != true || picker.SelectedCulture == CultureInfo.CurrentUICulture.Name) return;
            LanguageSettingsStore.Save(picker.SelectedCulture);
            var start = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = true };
            // Carry the original arguments over, but drop --help so a language change made from
            // a help-launched instance does not reopen the help window after the restart.
            foreach (var argument in Environment.GetCommandLineArgs().Skip(1)
                         .Where(a => !a.Equals("--help", StringComparison.OrdinalIgnoreCase)))
                start.ArgumentList.Add(argument);
            Process.Start(start);
            Application.Current.Shutdown();
        }

        void Target_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            OpenTarget((sender as FrameworkElement)?.Tag as BasketTarget);
            e.Handled = true;
        }

        void OpenTarget(BasketTarget target)
        {
            if (target == null) return;
            try { Process.Start(new ProcessStartInfo(target.Path) { UseShellExecute = true }); }
            catch (Exception ex) { Model.Status = $"Could not open target: {ex.Message}"; }
        }

        // The two big drop zones are only shown while a drag is in progress: pinned for the
        // whole drag when it originates in the files view, otherwise kept alive by drag-over
        // activity on the bar and hidden by the timer shortly after the drag moves away.
        DispatcherTimer basketOverlayHide;
        bool basketOverlayPinned;

        void ShowBasketDropOverlay(bool pinned = false)
        {
            basketOverlayPinned |= pinned;
            BasketDropOverlay.Visibility = Visibility.Visible;
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
            BasketDropOverlay.Visibility = Visibility.Collapsed;
        }

        void TargetBar_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (GetDropSources(e).Length > 0) ShowBasketDropOverlay();
        }

        void TargetBar_DragOver(object sender, DragEventArgs e)
        {
            // Reached only when no inner drop zone claimed the drag - the bar itself is not a drop spot
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        void BasketAddZone_DragOver(object sender, DragEventArgs e) => SetBasketDragEffect(sender, e,
            GetDropSources(e).Length > 0, DragDropEffects.Copy);

        void BasketUseZone_DragOver(object sender, DragEventArgs e)
        {
            var sources = GetDropSources(e);
            var targets = BasketTargets.Where(TargetIsAvailable).ToArray();
            SetBasketDragEffect(sender, e, targets.Length > 0 && sources.Length > 0,
                SuggestedBasketDropEffect(e, sources, targets));
        }

        void BasketTarget_DragOver(object sender, DragEventArgs e)
        {
            var sources = GetDropSources(e);
            var target = (sender as FrameworkElement)?.Tag as BasketTarget;
            SetBasketDragEffect(sender, e, target != null && TargetIsAvailable(target) && sources.Length > 0,
                target == null ? DragDropEffects.None : SuggestedBasketDropEffect(e, sources, new[] { target }));
        }

        static void SetBasketDragEffect(object sender, DragEventArgs e, bool valid, DragDropEffects effect)
        {
            e.Effects = valid ? CoerceDropEffect(e.AllowedEffects, effect) : DragDropEffects.None;
            if (sender is Border border) border.Opacity = valid ? 0.65 : 1;
            if (sender is Button button) button.Opacity = valid ? 0.65 : 1;
            e.Handled = true;
        }

        void BasketZone_DragLeave(object sender, DragEventArgs e)
        {
            if (sender is UIElement element) element.Opacity = 1;
        }

        void BasketAddZone_Drop(object sender, DragEventArgs e)
        {
            if (sender is UIElement element) element.Opacity = 1;
            HideBasketDropOverlay();
            AddBasketTargets(GetDropSources(e));
            e.Handled = true;
        }

        async void BasketUseZone_Drop(object sender, DragEventArgs e)
        {
            BasketUseZone.Opacity = 1;
            HideBasketDropOverlay();
            var sources = GetDropSources(e);
            var targets = BasketTargets.ToArray();
            var defaultAction = SuggestedBasketTransferAction(e, sources, targets);
            e.Effects = DropEffectFor(defaultAction);
            await UseBasketTargets(sources, targets, defaultAction);
            e.Handled = true;
        }

        async void BasketTarget_Drop(object sender, DragEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: BasketTarget target }) return;
            ((UIElement)sender).Opacity = 1;
            HideBasketDropOverlay();
            var sources = GetDropSources(e);
            var defaultAction = SuggestedBasketTransferAction(e, sources, new[] { target });
            e.Effects = DropEffectFor(defaultAction);
            await UseBasketTargets(sources, new[] { target }, defaultAction);
            e.Handled = true;
        }

        async void PasteToBasket_Click(object sender, RoutedEventArgs e) => await PasteToBasket();

        async Task PasteToBasket(FileTransferAction? action = null, FileCollisionAction? collision = null,
            bool chooseAction = true)
        {
            string[] paths;
            try { paths = Clipboard.GetFileDropList().Cast<string>().ToArray(); }
            catch (Exception ex) { Model.Status = $"Cannot read clipboard files: {ex.Message}"; return; }
            await UseBasketTargets(paths, BasketTargets.ToArray(),
                action ?? (ClipboardRequestsMove() ? FileTransferAction.Move : FileTransferAction.Copy),
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
            var modifiers = DragModifiers(e);
            // A heterogeneous or multi-target operation has no Shell equivalent. Keep
            // copy as the safe default unless the user explicitly requests an action.
            if (targets.Length > 1 && modifiers == ModifierKeys.None)
                return FileTransferAction.Copy;
            return SuggestedTransferAction(modifiers, sources, folders);
        }

        static DragDropEffects SuggestedBasketDropEffect(DragEventArgs e, string[] sources,
            BasketTarget[] targets) => DropEffectFor(SuggestedBasketTransferAction(e, sources, targets));

        static ModifierKeys DragModifiers(DragEventArgs e)
        {
            var modifiers = ModifierKeys.None;
            if (e.KeyStates.HasFlag(DragDropKeyStates.ControlKey)) modifiers |= ModifierKeys.Control;
            if (e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey)) modifiers |= ModifierKeys.Shift;
            if (e.KeyStates.HasFlag(DragDropKeyStates.AltKey)) modifiers |= ModifierKeys.Alt;
            return modifiers;
        }

        internal static FileTransferAction SuggestedTransferAction(ModifierKeys modifiers, string[] sources,
            string[] destinations)
        {
            if (sources.Any(IsTemporaryDragPath)) return FileTransferAction.Copy;
            if (modifiers.HasFlag(ModifierKeys.Alt)) return FileTransferAction.SymbolicLink;
            if (modifiers.HasFlag(ModifierKeys.Control)) return FileTransferAction.Copy;
            if (modifiers.HasFlag(ModifierKeys.Shift)) return FileTransferAction.Move;
            if (destinations.Length != 1 || sources.Length == 0) return FileTransferAction.Copy;
            try
            {
                var targetRoot = IOPath.GetPathRoot(IOPath.GetFullPath(destinations[0]));
                return sources.All(source => string.Equals(
                        IOPath.GetPathRoot(IOPath.GetFullPath(source)), targetRoot,
                        StringComparison.OrdinalIgnoreCase))
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
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        static DragDropEffects DropEffectFor(FileTransferAction action) => action switch
        {
            FileTransferAction.Move => DragDropEffects.Move,
            FileTransferAction.SymbolicLink or FileTransferAction.HardLink => DragDropEffects.Link,
            _ => DragDropEffects.Copy
        };

        static DragDropEffects CoerceDropEffect(DragDropEffects allowed, DragDropEffects requested)
        {
            if (allowed.HasFlag(requested)) return requested;
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
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var targets = requestedTargets.Where(TargetIsAvailable).ToArray();
            if (sources.Length == 0 || targets.Length == 0)
            {
                Model.Status = "No available sources or targets";
                return;
            }

            var folders = targets.Where(x => x.Kind == BasketTargetKind.Folder).ToArray();
            var archives = targets.Where(x => x.Kind == BasketTargetKind.Archive).ToArray();
            var executables = targets.Where(x => x.Kind == BasketTargetKind.Executable).ToArray();
            var unsupported = targets.Where(x => x.Kind == BasketTargetKind.File).ToArray();
            var summary = $"Use {sources.Length} item(s) with {targets.Length} target(s)?\n\n" +
                (folders.Length > 0 ? $"Folders: {folders.Length}\n" : "") +
                (archives.Length > 0 ? $"Archives to update: {archives.Length}\n" : "") +
                (executables.Length > 0 ? $"Programs to start: {executables.Length}\n" : "") +
                (unsupported.Length > 0 ? $"Unsupported targets to skip: {unsupported.Length}\n" : "");
            if ((targets.Length > 1 || executables.Length > 0 || archives.Length > 0) &&
                MessageBox.Show(this, summary, "Use target basket", MessageBoxButton.OKCancel,
                    MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            FileTransferAction? folderAction = null;
            if (folders.Length > 0)
            {
                //Keyboard sequences name the action explicitly and transfer directly;
                //the chooser dialog belongs to the mouse paths (toolbar button, drop)
                if (!chooseFolderAction)
                    folderAction = defaultFolderAction;
                else if (TryChooseTransferAction(sources.Length, folders.Length, defaultFolderAction, out var selectedAction))
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
                try
                {
                    var start = new ProcessStartInfo
                    {
                        FileName = executable.Path,
                        UseShellExecute = true,
                        WorkingDirectory = IOPath.GetDirectoryName(executable.Path)
                    };
                    foreach (var source in sources) start.ArgumentList.Add(source);
                    Process.Start(start);
                }
                catch (Exception ex) { errors.Add($"{executable.Path}: {ex.Message}"); }
            }
            if (folders.Length > 0)
            {
                if (!await TransferPaths(sources, folders.Select(x => x.Path).ToArray(), folderAction.Value, collisionForAll))
                    errors.Add("One or more folder transfers did not complete.");
            }
            Model.Status = errors.Count == 0
                ? $"Used {sources.Length} item(s) with {targets.Length} target(s)"
                : $"Target operations completed with {errors.Count} error(s): {string.Join(", ", errors)}";
        }

        private void ApplyWindowLayout()
        {
            pendingLayout = WindowLayoutStore.Load();
            if (pendingLayout == null)
                return;

            if (!IsVisibleOnAnyScreen(pendingLayout))
            {
                pendingLayout = null;
                return;
            }

            if (pendingLayout.Width > 0 && pendingLayout.Height > 0)
            {
                Width = pendingLayout.Width;
                Height = pendingLayout.Height;
            }

            if (!double.IsNaN(pendingLayout.Left) && !double.IsNaN(pendingLayout.Top))
            {
                Left = pendingLayout.Left;
                Top = pendingLayout.Top;
            }

            WindowState = pendingLayout.WindowState;
        }

        private static bool IsVisibleOnAnyScreen(WindowLayout layout)
        {
            if (layout.Width <= 0 || layout.Height <= 0)
                return false;

            var windowBounds = new Rect(layout.Left, layout.Top, layout.Width, layout.Height);
            var virtualScreen = new Rect(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight);

            return virtualScreen.IntersectsWith(windowBounds);
        }

        private void AttachColumnWidthHandlers()
        {
            if (columnWidthHandlersAttached || filesView.View is not GridView gridView)
                return;

            var descriptor = DependencyPropertyDescriptor.FromProperty(GridViewColumn.WidthProperty, typeof(GridViewColumn));
            foreach (var column in gridView.Columns)
            {
                descriptor.AddValueChanged(column, (_, __) => UpdateFolderColumnWidth());
            }

            columnWidthHandlersAttached = true;
        }

        private void ApplyColumnWidths(IReadOnlyList<double> widths)
        {
            if (filesView.View is not GridView gridView)
                return;

            for (var i = 0; i < gridView.Columns.Count && i < widths.Count; i++)
            {
                var width = widths[i];
                if (width > 0)
                {
                    gridView.Columns[i].Width = width;
                }
            }

            UpdateFolderColumnWidth();
        }

        private IReadOnlyList<double> CaptureColumnWidths()
        {
            if (filesView.View is not GridView gridView)
                return Array.Empty<double>();

            return gridView.Columns.Cast<GridViewColumn>().Select(c => c.ActualWidth).ToArray();
        }

        private void UpdateFolderColumnWidth()
        {
            if (updatingFolderWidth || filesView.View is not GridView gridView || gridView.Columns.Count < 5)
                return;

            var folderColumn = gridView.Columns[4];
            var available = filesView.ActualWidth - SystemParameters.VerticalScrollBarWidth - 8;
            if (available <= 0)
                return;

            var fixedWidth = gridView.Columns
                .Cast<GridViewColumn>()
                .Where((_, index) => index != 4)
                .Sum(column => column.ActualWidth);

            var width = Math.Max(80, available - fixedWidth);
            if (Math.Abs(folderColumn.Width - width) < 0.5)
                return;

            try
            {
                updatingFolderWidth = true;
                folderColumn.Width = width;
            }
            finally
            {
                updatingFolderWidth = false;
            }
        }

        /// <summary>
        /// Currently filtered items
        /// </summary>
        IEnumerable<INode> Items => filesView.Items.OfType<INode>();

        Models.SearchModel Model => DataContext as Models.SearchModel;

        async private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            filterTextBox.Focus();
            if (Program.OpenHelpRequested) OpenHelp();
            // UAC may have been accepted before this window subscribed or became visible.
            if (Broker.ElevationAccepted) RestoreForegroundAfterElevation();
            await Model.Update("");
            ApplySavedColumnWidths();
            UpdateFolderColumnWidth();
        }

        private void ApplySavedColumnWidths()
        {
            if (pendingLayout?.ColumnWidths == null || pendingLayout.ColumnWidths.Count == 0)
                return;

            ApplyColumnWidths(pendingLayout.ColumnWidths);
            pendingLayout = null;
        }

        async private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var filter = (sender as TextBox).Text;
            await Model.Update(filter);
        }

        async Task StartSearching() 
        {
            // Save search term to history when search is performed
            if (!string.IsNullOrWhiteSpace(findTextBox.Text))
            {
                searchTerms.Used(findTextBox.Text);
            }
            
            string encoding = GetCurrentEncoding();
            await Model.Find(findTextBox.Text, caseInsensitive.IsChecked == true, encoding).ConfigureAwait(false);
        }

        private void EncodingComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (findTextBox == null || encodingComboBox == null) return;

            string currentText = findTextBox.Text;
            if (string.IsNullOrEmpty(currentText)) return;

            string fromEncoding = GetPreviousEncoding();
            string toEncoding = GetCurrentEncoding();

            try
            {
                string convertedText = ConvertTextEncoding(currentText, fromEncoding, toEncoding);
                findTextBox.Text = convertedText;
                // Only update previousEncoding after successful conversion
                previousEncoding = toEncoding;
            }
            catch (Exception ex)
            {
                // If conversion fails, keep the original text and reset the ComboBox
                MessageBox.Show($"Encoding conversion failed: {ex.Message}", "Encoding Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                // Reset ComboBox to previous encoding
                encodingComboBox.SelectedIndex = GetEncodingIndex(fromEncoding);
            }
        }

        private int GetEncodingIndex(string encoding)
        {
            return encoding switch
            {
                "UTF-8" => 0,
                "UTF-16" => 1,
                "HEX" => 2,
                _ => 0
            };
        }

        private string GetCurrentEncoding()
        {
            return encodingComboBox.SelectedIndex switch
            {
                0 => "UTF-8",
                1 => "UTF-16",
                2 => "HEX",
                _ => "UTF-8"
            };
        }

        private string previousEncoding = "UTF-8";
        
        private string GetPreviousEncoding()
        {
            return previousEncoding;
        }

        private string ConvertTextEncoding(string text, string fromEncoding, string toEncoding)
        {
            if (fromEncoding == toEncoding) return text;

            switch (fromEncoding)
            {
                case "UTF-8":
                    return ConvertFromUtf8(text, toEncoding);
                case "UTF-16":
                    return ConvertFromUtf16(text, toEncoding);
                case "HEX":
                    return ConvertFromHex(text, toEncoding);
                default:
                    return text;
            }
        }

        private string ConvertFromUtf8(string utf8Text, string toEncoding)
        {
            byte[] utf8Bytes = Encoding.UTF8.GetBytes(utf8Text);
            
            return toEncoding switch
            {
                "UTF-16" => Encoding.Unicode.GetString(Encoding.Convert(Encoding.UTF8, Encoding.Unicode, utf8Bytes)),
                "HEX" => string.Join(" ", utf8Bytes.Select(b => b.ToString("X2"))),
                _ => utf8Text
            };
        }

        private string ConvertFromUtf16(string utf16Text, string toEncoding)
        {
            byte[] utf16Bytes = Encoding.Unicode.GetBytes(utf16Text);
            
            return toEncoding switch
            {
                "UTF-8" => Encoding.UTF8.GetString(Encoding.Convert(Encoding.Unicode, Encoding.UTF8, utf16Bytes)),
                "HEX" => string.Join(" ", utf16Bytes.Select(b => b.ToString("X2"))),
                _ => utf16Text
            };
        }

        private string ConvertFromHex(string hexText, string toEncoding)
        {
            try
            {
                // Parse hex string (space-separated bytes)
                string[] hexBytes = hexText.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                byte[] bytes = hexBytes.Select(hex => Convert.ToByte(hex, 16)).ToArray();
                
                return toEncoding switch
                {
                    "UTF-8" => Encoding.UTF8.GetString(bytes),
                    "UTF-16" => Encoding.Unicode.GetString(bytes),
                    _ => hexText
                };
            }
            catch (Exception)
            {
                throw new ArgumentException("Invalid hexadecimal format. Use space-separated hex bytes (e.g., '48 65 6C 6C 6F')");
            }
        }

        async private void findTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    // Start searching on Enter
                    await StartSearching();
                    break;
                default:
                    // Stop searching on pressing enyching else
                    await Model.Find(null, false, "UTF-8").ConfigureAwait(false);
                    break;
            }
        }

        async void filterTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Start searching after filter selection from list box or just enter.
            switch (e.Key)
            {
                case Key.RightShift:
                    filesView.Focus(); // Usually done by mouse
                    break;
                case Key.Enter:
                    await StartSearching();
                    break;
            }
        }

        private void ButtonExit_Click(object sender, RoutedEventArgs e)
        {
            "ButtonExit_Click".Debug();
            Application.Current.Shutdown();
        }

        #region To be moved to base class
        /// <summary>
        /// Show wait cursor and show exception during given action
        /// </summary>
        /// <param name="a"></param>
        /// <returns></returns>
        async Task WaitFor(Action a)
        {
            var c = Cursor;
            try
            {
                Cursor = Cursors.Wait;
                //await Task.Run(() => a()); Can not run on another thread
                // Show the wait cursor first
                await Task.Yield();
                a();
            }
            catch (Exception ex)
            {
                $"WaitFor exception {ex}".Debug();
                MessageBox.Show(ex.Message, "Error");
            }
            Cursor = c;
        }

        #endregion

        /// <summary>
        /// Change the filter to all folders given
        /// </summary>
        /// <param name="nodes"></param>
        async Task FilterFolders(params INode[] nodes) => await WaitFor(() =>
        {
            var folders = Model.FilterFolders(nodes).ToString();
            if (string.IsNullOrEmpty(folders)) nodes.ForEach(n => n.GetFileOrTempPath().Open());
            else filterTextBox.Text = folders;
        });

        /// <summary>
        /// Open node by Shell
        /// </summary>
        /// <param name="n"></param>
        /// <returns></returns>
        async Task Open(INode n, bool asAdmin = false) => await WaitFor(() => n.GetFileOrTempPath().Open(elevated: asAdmin));

        /// <summary>
        /// Open node in program
        /// </summary>
        /// <param name="n"></param>
        /// <returns></returns>
        async Task Open(string path, INode[] nodes, bool asAdmin = false)
        {
            if (nodes.Length == 0)
            {
                Model.Status = "Nothing selected";
                return;
            }
            if (path == null) nodes.ForEach(n => n.GetFileOrTempPath().Open("", elevated: asAdmin)); //Open the files directly
            else
            {
                var args = path.Split('\0');
                var workingDir = nodes.FirstOrDefault(n => n.IsDirectory)?.FullName
                    ?? System.IO.Path.GetDirectoryName(nodes.First().FullName);
                var q = args[0] == Apps.Powershell ? "'" : "\"";
                await WaitFor(() => args[0].Open(
                string.Join(" ", args.Skip(1).Concat(nodes.Select(n => $"{q}{n.GetFileOrTempPath()}{q}"))), workingDir, asAdmin));
            }
        }

        async void ListViewItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // To be used for single listView without inside controls
            //if (e.LeftButton == MouseButtonState.Pressed && sender is ListViewItem i && i.Content is INode n)
            //{
            //    var pos = e.GetPosition(i);
            //    var lv = i.ParentControl().ParentControl();

            //    filters.Used(filterTextBox.Text);
            //    await FilterFolders(n);
            //    //e.Handled = true;
            //}
        }

        async void Name_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (inlineRenameEditor != null || e.OriginalSource is TextBox)
                return;
            if (sender is FrameworkElement l && l.ParentControl() is ListViewItem i && i.Content is INode n)
            {
                BeginMouseDrag(e, i, n, "Name");
                if (e.ClickCount == 2)
                {
                    CancelMouseDrag();
                    filters.Used(filterTextBox.Text);
                    await FilterFolders(n);
                    e.Handled = true;
                }
            }
        }

        void Folder_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement l && l.ParentControl() is ListViewItem i && i.Content is INode n)
            {
                BeginMouseDrag(e, i, n, "Folder");
                if (e.ClickCount == 2)
                {
                    CancelMouseDrag();
                    filters.Used(filterTextBox.Text);
                    Apps.Explorer.Open($"/select,\"{n.GetFileOrTempPath()}\"");
                    e.Handled = true;
                }
            }
        }

        void BeginMouseDrag(MouseButtonEventArgs e, ListViewItem item, INode node, string column)
        {
            if (e.ChangedButton != MouseButton.Left || e.ClickCount != 1)
                return;

            dragStart = e.GetPosition(filesView);
            dragSourceNode = node;
            dragSourceColumn = column;
            dragStarted = false;

            // WPF normally collapses an extended selection as soon as a selected
            // row is pressed. Preserve it long enough to permit a multi-item drag;
            // a plain click still collapses it in the mouse-up handler below.
            preserveSelectionOnMouseUp =
                Keyboard.Modifiers == ModifierKeys.None &&
                item.IsSelected &&
                filesView.SelectedItems.Count > 1;
            if (preserveSelectionOnMouseUp)
            {
                item.Focus();
                e.Handled = true;
            }
        }

        void FilesView_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || dragStart == null || dragSourceNode == null)
                return;

            var position = e.GetPosition(filesView);
            if (Math.Abs(position.X - dragStart.Value.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(position.Y - dragStart.Value.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            var nodes = filesView.SelectedItems.Contains(dragSourceNode)
                ? filesView.SelectedItems.Cast<INode>().ToArray()
                : new[] { dragSourceNode };
            var paths = dragSourceColumn == "Folder"
                ? nodes.Select(n => IOPath.GetDirectoryName(n.FullName))
                : nodes.Select(n => n.GetFileOrTempPath());
            var existingPaths = paths
                .Where(p => !string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            dragStarted = true;
            preserveSelectionOnMouseUp = false;
            CancelMouseDrag(keepDragStarted: true);
            if (existingPaths.Length == 0)
            {
                Model.Status = "Nothing available to drag";
                return;
            }

            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, existingPaths);
            try
            {
                ShowBasketDropOverlay(pinned: true);
                DragDrop.DoDragDrop(filesView, data,
                    DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            }
            catch (Exception ex)
            {
                Model.Status = $"Drag failed: {ex.Message}";
            }
            finally
            {
                dragStarted = false;
                HideBasketDropOverlay();
            }
        }

        void FilesView_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var clickedNode = dragSourceNode;
            if (preserveSelectionOnMouseUp && !dragStarted && clickedNode != null)
            {
                filesView.UnselectAll();
                filesView.SelectedItem = clickedNode;
                e.Handled = true;
            }
            CancelMouseDrag();
        }

        void CancelMouseDrag(bool keepDragStarted = false)
        {
            dragStart = null;
            dragSourceNode = null;
            dragSourceColumn = null;
            preserveSelectionOnMouseUp = false;
            if (!keepDragStarted)
                dragStarted = false;
        }

        void FileCell_DragOver(object sender, DragEventArgs e)
        {
            var sources = GetDropSources(e);
            var executableTarget = TryGetExecutableDrop(sender, out _);
            var folderTarget = TryGetDropTargets(sender, out var targets);
            var validTarget = sources.Length > 0 && (executableTarget || folderTarget);
            if (sender is not Border border || !validTarget)
            {
                ClearDropHighlight();
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            if (dropHoverBorder != border)
            {
                ClearDropHighlight();
                dropHoverBorder = border;
                dropHoverBorder.Background = SystemColors.HighlightBrush;
                dropHoverBorder.Opacity = 0.35;
            }
            var requested = executableTarget
                ? DragDropEffects.Copy
                : DropEffectFor(SuggestedTransferAction(DragModifiers(e), sources, targets));
            e.Effects = CoerceDropEffect(e.AllowedEffects, requested);
            e.Handled = true;
        }

        void FileCell_DragLeave(object sender, DragEventArgs e)
        {
            if (ReferenceEquals(sender, dropHoverBorder))
                ClearDropHighlight();
        }

        async void FileCell_Drop(object sender, DragEventArgs e)
        {
            ClearDropHighlight();
            var sources = GetDropSources(e);
            if (sources.Length == 0)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            e.Handled = true;
            if (TryGetExecutableDrop(sender, out var executable))
            {
                e.Effects = DragDropEffects.Copy;
                try
                {
                    var start = new ProcessStartInfo
                    {
                        FileName = executable,
                        UseShellExecute = true,
                        WorkingDirectory = IOPath.GetDirectoryName(executable)
                    };
                    foreach (var source in sources)
                        start.ArgumentList.Add(source);
                    Process.Start(start);
                    Model.Status = $"Started \"{executable}\" with {sources.Length} dropped argument(s)";
                }
                catch (Exception ex)
                {
                    Model.Status = $"Could not start \"{executable}\": {ex.Message}";
                }
                return;
            }

            if (!TryGetDropTargets(sender, out var targets))
            {
                e.Effects = DragDropEffects.None;
                return;
            }
            var defaultAction = SuggestedTransferAction(DragModifiers(e), sources, targets);
            e.Effects = CoerceDropEffect(e.AllowedEffects, DropEffectFor(defaultAction));
            if (!TryChooseTransferAction(sources.Length, targets.Length, defaultAction, out var action))
            {
                e.Effects = DragDropEffects.None;
                return;
            }
            await TransferPaths(sources, targets, action);
        }

        static string[] GetDropSources(DragEventArgs e)
        {
            return e.Data.GetDataPresent(DataFormats.FileDrop) &&
                   e.Data.GetData(DataFormats.FileDrop) is string[] dropped
                ? dropped.Where(p => File.Exists(p) || Directory.Exists(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : Array.Empty<string>();
        }

        bool TryGetDropTargets(object sender, out string[] targets)
        {
            targets = Array.Empty<string>();
            if (sender is not FrameworkElement cell ||
                cell.ParentControl() is not ListViewItem hovered ||
                hovered.Content is not INode hoveredNode)
                return false;

            var nodes = hovered.IsSelected
                ? filesView.SelectedItems.Cast<INode>()
                : new[] { hoveredNode };
            targets = ((cell.Tag as string) == "Folder"
                    ? nodes.Select(n => IOPath.GetDirectoryName(n.FullName))
                    : nodes.Where(n => n.IsDirectory).Select(n => n.FullName))
                .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return targets.Length > 0;
        }

        static bool TryGetExecutableDrop(object sender, out string executable)
        {
            executable = null;
            if (sender is not FrameworkElement { Tag: "Name" } cell ||
                cell.ParentControl() is not ListViewItem { Content: INode node } ||
                node.IsDirectory || !File.Exists(node.FullName) || !IsExecutable(node.FullName))
                return false;
            executable = node.FullName;
            return true;
        }

        static bool IsExecutable(string path)
            => ExecutableExtensions.Contains(IOPath.GetExtension(path));

        static bool IsDescendant(string candidate, string directory)
        {
            var parent = IOPath.GetFullPath(directory).TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar);
            var child = IOPath.GetFullPath(candidate).TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar);
            return child.StartsWith(parent + IOPath.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        void ClearDropHighlight()
        {
            if (dropHoverBorder == null)
                return;
            dropHoverBorder.Background = Brushes.Transparent;
            dropHoverBorder.Opacity = 1;
            dropHoverBorder = null;
        }

        bool TryChooseTransferAction(
            int sourceCount,
            int destinationCount,
            FileTransferAction defaultAction,
            out FileTransferAction action)
        {
            var dialog = new FileTransferDialog(sourceCount, destinationCount, defaultAction) { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                action = default;
                return false;
            }
            action = dialog.Action;
            return true;
        }

        async void ListViewItem_RightButtonDown(object sender, MouseButtonEventArgs e)
        {
            contextTargetColumn = ContextTargetColumn(e.OriginalSource as DependencyObject);
            if (sender is ListViewItem item && item.Content is INode n)
            {
                //CTRL + click => open the file by windows default action
                if (Keyboard.IsKeyDown(Key.RightCtrl) || Keyboard.IsKeyDown(Key.LeftCtrl))
                {
                    filters.Used(filterTextBox.Text);
                    await Open(n);
                    e.Handled = true;
                }
                else if (!item.IsSelected)
                {
                    filesView.UnselectAll();
                    item.IsSelected = true;
                    item.Focus();
                }
            }
        }

        INode[] ContextNodes() => filesView.SelectedItems.Cast<INode>().ToArray();

        async void ContextOpen_Click(object sender, RoutedEventArgs e)
            => await Open(null, ContextNodes());

        void ContextOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            foreach (var node in ContextNodes())
                Apps.Explorer.Open($"/select,\"{node.GetFileOrTempPath()}\"");
        }

        void ContextCopy_Click(object sender, RoutedEventArgs e)
            => Copy(ContextNodes(), Array.Empty<Key>());

        void ContextCut_Click(object sender, RoutedEventArgs e)
            => Copy(ContextNodes(), Array.Empty<Key>(), true);

        async void ContextPaste_Click(object sender, RoutedEventArgs e)
            => await PasteWithDialog(ContextNodes());

        void ContextCopyPath_Click(object sender, RoutedEventArgs e)
            => SetCpliboard(string.Join("\r\n", ContextNodes().Select(n => n.FullName)));

        void ContextRename_Click(object sender, RoutedEventArgs e)
            => Rename(ContextNodes(), new[] { Key.None });

        async void ContextDelete_Click(object sender, RoutedEventArgs e)
            => await Delete(ContextNodes());

        void FilesContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is not ContextMenu menu)
                return;
            var nodes = ContextNodes();
            var addTarget = menu.Items.OfType<MenuItem>().FirstOrDefault(i => Equals(i.Tag, "AddTarget"));
            if (addTarget != null)
            {
                var paths = contextTargetColumn == "Name"
                    ? nodes.Select(NameTargetPath).ToArray()
                    : contextTargetColumn == "Folder"
                        ? nodes.Select(x => IOPath.GetDirectoryName(x.FullName)).ToArray()
                        : Array.Empty<string>();
                var eligible = paths.Length > 0 && paths.All(IsEligibleTargetPath) &&
                    paths.Any(path => !BasketTargets.Any(target =>
                        string.Equals(target.Path, path, StringComparison.OrdinalIgnoreCase)));
                addTarget.Visibility = eligible ? Visibility.Visible : Visibility.Collapsed;
                addTarget.Header = contextTargetColumn == "Folder"
                    ? "Add parent folder(s) to target basket"
                    : "Add Name item(s) to target basket";
                addTarget.InputGestureText = contextTargetColumn == "Folder" ? "Alt+F" : "Alt+N";
            }
            PopulateOpenWith(menu, nodes);
            var canZip = nodes.Length > 0 &&
                nodes.All(n => File.Exists(n.FullName) || Directory.Exists(n.FullName));
            foreach (var item in menu.Items.OfType<MenuItem>().Where(i => Equals(i.Tag, "Zip")))
                item.IsEnabled = canZip;
            foreach (var item in menu.Items.OfType<MenuItem>().Where(i => Equals(i.Tag, "Zip7z")))
                item.Visibility = canZip && !string.IsNullOrWhiteSpace(Apps.SevenZip)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            foreach (var item in menu.Items.OfType<MenuItem>().Where(i => Equals(i.Tag, "Unzip")))
                item.Visibility = nodes.Length > 0 && nodes.All(CanUnzip)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        void FilesContextMenu_Closed(object sender, RoutedEventArgs e) => contextTargetColumn = null;

        void PopulateOpenWith(ContextMenu menu, INode[] nodes)
        {
            var openWith = menu.Items.OfType<MenuItem>().FirstOrDefault(i => Equals(i.Tag, "OpenWith"));
            if (openWith == null)
                return;
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
                var item = new MenuItem { Header = label, ToolTip = executable };
                item.Click += async (_, __) => await OpenIn(nodes, new[] { key });
                openWith.Items.Add(item);
            }

            var diffTool = Apps.DiffTool;
            if (nodes.Length == 2 && !string.IsNullOrWhiteSpace(diffTool))
            {
                var diffItem = new MenuItem { Header = "Diff tool", ToolTip = diffTool };
                diffItem.Click += async (_, __) => await OpenDiff(nodes);
                openWith.Items.Add(diffItem);
            }

            var sevenZipFileManager = Apps.SevenZipFileManager;
            if (!string.IsNullOrWhiteSpace(sevenZipFileManager) &&
                nodes.All(n => File.Exists(n.FullName) || Directory.Exists(n.FullName)))
            {
                var sevenZipItem = new MenuItem { Header = "7-Zip", ToolTip = sevenZipFileManager };
                sevenZipItem.Click += async (_, __) =>
                {
                    foreach (var node in nodes)
                        await Open(sevenZipFileManager, new[] { node });
                };
                openWith.Items.Add(sevenZipItem);
            }

            Add("Text viewer", Key.T, Apps.TextViever, TextExtensions.Contains);
            Add("Chrome", Key.C, Apps.Chrome, WebExtensions.Contains);
            Add("Edge", Key.E, Apps.Edge, WebExtensions.Contains);
            Add("Firefox", Key.F, Apps.Firefox, WebExtensions.Contains);
            Add("Opera", Key.O, Apps.Opera, WebExtensions.Contains);
            Add("Adobe Reader", Key.A, Apps.AdobeReader,
                extension => extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase));
            Add("Visual Studio", Key.V, Apps.VisualStudio, CodeExtensions.Contains);
            Add("Visual Studio Code", Key.D, Apps.VSCode, CodeExtensions.Contains);
            Add("Antigravity", Key.Y, Apps.Antigravity, CodeExtensions.Contains);
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
                    Add("Detected PRN viewer", Key.R, viewer, _ => true);
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

        async void ContextZip_Click(object sender, RoutedEventArgs e)
            => await Zip(ContextNodes(), Array.Empty<Key>());

        async void ContextZip7z_Click(object sender, RoutedEventArgs e)
            => await Zip(ContextNodes(), new[] { Key.NumPad7 });

        async void ContextUnzip_Click(object sender, RoutedEventArgs e)
            => await Unzip(ContextNodes(), Array.Empty<Key>());

        // Keys pressed or released while Alt is involved arrive as Key.System with the real key
        // in SystemKey. Feeding raw Key.System into the commander shows a bogus <System> hint and,
        // worse, unpairs press/release (AltGr = LeftCtrl+RightAlt releases LeftCtrl as Key.System),
        // so the pressed-keys set never empties and the hint sticks. RightAlt maps to LeftAlt so
        // AltGr layouts get the same ALT commands.
        static Key CommandKey(KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            return key == Key.RightAlt ? Key.LeftAlt : key;
        }

        void Broker_StartupElevationAccepted()
        {
            try
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (IsLoaded) RestoreForegroundAfterElevation();
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

            // Activate normally first. If Windows rejects that foreground request, briefly
            // enter the topmost z-order so the window is at least restored above other apps.
            if (!Activate())
            {
                var wasTopmost = Topmost;
                Topmost = true;
                Activate();
                Topmost = wasTopmost;
            }
            Focus();
        }

        private void ListView_KeyDown(object sender, KeyEventArgs e)
        {
            if (ReferenceEquals(e.OriginalSource, inlineRenameEditor))
                return;
            var key = CommandKey(e);
            switch (key)
            {
                case Key.Up:
                case Key.Down:
                case Key.PageDown:
                case Key.PageUp:
                    // Skip list view control keys
                    return;
                case Key.F4 when Keyboard.Modifiers.HasFlag(ModifierKeys.Alt):
                    // Leave Alt+F4 to the system so the window can close
                    return;
            }
            // Auto-repeats of a held key carry no new command information - processing them
            // would just spam the log (and watching one's own log file then loops forever)
            if (e.IsRepeat)
            {
                e.Handled = true;
                return;
            }
            // Receive another command
            filesViewCmd.KeyPressed(key);
            $"Key {key} pressed => Still down: {string.Join(", ", filesViewCmd.KeysDown().Select(x => x.ToString()))}".Debug();
            e.Handled = true;
        }

        bool ClipBoardCut = false;
        async void ListView_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (ReferenceEquals(e.OriginalSource, inlineRenameEditor))
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
                MessageBox.Show($"{ex}", "Error processing KeyDown");
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (ReferenceEquals(e.OriginalSource, inlineRenameEditor) ||
                ReferenceEquals(e.OriginalSource, inlineActionTextBox) ||
                e.OriginalSource is TextBox { DataContext: PinnedFilter { IsEditing: true } })
                return;
            //ALT command chords work window-wide through the global commander - the same tree,
            //hints and behavior as in the result list (which feeds its own commander instead)
            if (!filesView.IsKeyboardFocusWithin)
            {
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                //Only pure LeftAlt starts a chord: AltGr (RightAlt/Ctrl+Alt) must keep typing
                //special characters into the text boxes, and F4/Space stay with the system
                //(close window, system menu)
                var starts = key == Key.LeftAlt && Keyboard.Modifiers == ModifierKeys.Alt;
                if ((starts || globalCmd.IsReceivingCommandKeys) && key != Key.F4 && key != Key.Space)
                {
                    if (!e.IsRepeat) globalCmd.KeyPressed(key);
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
                    //Escape from current directory level up
                    filterTextBox.Text = new NodeFilter(filterTextBox.Text).Up().ToString();
                    break;
                case Key.Home:
                    //Do not break caret navigation/selection in text boxes
                    if (!filesView.IsKeyboardFocusWithin) return;
                    filterTextBox.Home();
                    filterTextBox.Focus();
                    break;
                case Key.End:
                    if (!filesView.IsKeyboardFocusWithin) return;
                    filterTextBox.End();
                    filterTextBox.Focus();
                    break;
                case Key.Left:
                    if (Keyboard.Modifiers != ModifierKeys.Control) return;
                    filters.Add2History(filterTextBox.Text);
                    filterTextBox.Text = filters.Backward;
                    break;
                case Key.Right:
                    if (Keyboard.Modifiers != ModifierKeys.Control) return;
                    filterTextBox.Text = filters.Forward;
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }

        internal static string HelpFilePath => FindHelpFile(CultureInfo.CurrentUICulture);

        internal static string FindHelpFile(CultureInfo culture)
        {
            var docs = IOPath.Combine(AppContext.BaseDirectory, "Docs");
            var candidates = new[]
            {
                IOPath.Combine(docs, $"HELP.{culture.Name}.md"),
                IOPath.Combine(docs, $"HELP.{culture.TwoLetterISOLanguageName}.md"),
                IOPath.Combine(docs, "HELP.md")
            };
            return candidates.FirstOrDefault(File.Exists) ?? candidates[^1];
        }

        void OpenHelp()
        {
            var helpFile = HelpFilePath;
            if (!File.Exists(helpFile))
            {
                MessageBox.Show(this, L.Text("HelpNotFound"), L.Text("Help"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                new HelpWindow(helpFile) { Owner = this }.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"{L.Text("HelpFailed")}: {ex.Message}", L.Text("Help"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        void Window_KeyUp(object sender, KeyEventArgs e)
        {
            if (ReferenceEquals(e.OriginalSource, inlineRenameEditor) ||
                ReferenceEquals(e.OriginalSource, inlineActionTextBox) ||
                e.OriginalSource is TextBox { DataContext: PinnedFilter { IsEditing: true } })
                return;
            //Complete the global commander's chord - the command runs when all keys are released
            if (!filesView.IsKeyboardFocusWithin && globalCmd.IsReceivingCommandKeys)
                e.Handled = globalCmd.KeyReleased(e.Key == Key.System ? e.SystemKey : e.Key);
        }

        /// <summary>
        /// Set clipboard text
        /// </summary>
        /// <param name="text"></param>
        void SetCpliboard(string text)
        {
            try
            {
                ClipBoardCut = false;
                Model.Status = $"Clipboarded \"{text}\"";
                Clipboard.SetText(text);
                return;
            }
            catch
            {
                //Works even when exception is thrown!!!
            }
        }

        void InvertSelection()
        {
            var original = filesView.SelectedItems.Cast<INode>().ToArray();
            filesView.Select(filesView.Items.OfType<INode>().Except(original, ReferenceEqualityComparer.Instance));
        }

        async Task Delete(IEnumerable<INode> nodes, bool confirm = true, bool permanently = false)
        {
            var na = nodes.ToArray();
            if (na.Length == 0)
                return;
            if (!confirm ||
                MessageBox.Show(
                    permanently
                        ? $"Permanently delete {na.Length} selected item(s)?"
                        : $"Move {na.Length} selected item(s) to the Recycle Bin?",
                    permanently ? "Permanently delete" : "Recycle",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            {
                ClipBoardCut = false;
                Model.Status = permanently
                    ? $"Permanently deleting {na.Length} item(s)"
                    : $"Moving {na.Length} item(s) to the Recycle Bin";
                await Task.Yield();
                var errors = new List<string>();
                foreach (INode n in na)
                    try
                    {
                        if (permanently || n is ZipNode)
                            n.Delete();
                        else if (n.IsDirectory)
                            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                                n.FullName,
                                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                                Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
                        else
                            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                                n.FullName,
                                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                                Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
                        Models.Extensions.EchoDeleted(n);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{n.FullName}: {ex.Message}");
                    }
                Model.Status = errors.Count == 0
                    ? permanently ? $"Permanently deleted {na.Length} item(s)" : $"Recycled {na.Length} item(s)"
                    : $"Delete completed with {errors.Count} error(s): {string.Join(", ", errors)}";
            }
        }

        async Task OpenIn(IEnumerable<INode> nodes, IEnumerable<Key> arg, bool asAdmin = false)
        {
            if (!nodes.Any())
            {
                Model.Status = "Nothing selected";
                return;
            }
            var a = arg.FirstOrDefault();
            if (a == Key.T) nodes = Model.ToTextNodes(nodes.ToArray());
            await Open(a switch
            {
                Key.T => Apps.TextViever,
                Key.B => Apps.Explorer + "\0/select,", // File browser
                Key.W => Apps.WebBrowser,
                Key.E => Apps.Edge,
                Key.C => Apps.Chrome,
                Key.F => Apps.Firefox,
                Key.I => Apps.IExplore,
                Key.O => Apps.Opera,
                Key.A => Apps.AdobeReader,
                Key.S => arg.Last() == Key.P ? Apps.Powershell + "\0-NoExit -Command &" : Apps.Cmd + "\0/k",
                Key.V => Apps.VisualStudio,
                Key.D => Apps.VSCode,
                Key.G => Apps.GhostScript + Resolution(),
                Key.P => Apps.GhostPcl + Resolution(),
                Key.X => Apps.GhostXps + Resolution(),
                Key.R => Apps.ViewerFor(nodes.First().FullName).Split("-r")[0] + Resolution(),
                Key.Y => Apps.Antigravity,
                _ => null // Open in default system app
            }, nodes.ToArray(), asAdmin);
            string Resolution()
            {
                var a = arg.Skip(1).ReadTill(); // keys argument after first one
                if (a.Length == 0) return "\0-r72"; // Screen resolution by default
                return "\0-r" + a;
            };
        }


        async Task OpenDiff(IEnumerable<INode> nodes)
        {
            var diff = Apps.DiffTool;
            if (diff != null) await Open(diff, Model.ToTextNodes(nodes.ToArray()).ToArray());
        }

        void Copy(IEnumerable<INode> nodes, IEnumerable<Key> arg, bool cut = false)
        {
            var nodeArray = nodes.ToArray();
            var e = arg.GetEnumerator();
            if (e.MoveNext())
            {
                // What to copy
                string clip = "";
                switch (e.Current)
                {
                    case Key.V: // Copy files version
                        clip = string.Join("\r\n", nodeArray.Select(n => FileVersionInfo.GetVersionInfo(n.FullName)));
                        break;
                    case Key.T: // Copy creation times
                        clip = string.Join("\r\n", nodeArray.Select(n => File.GetCreationTime(n.FullName)));
                        break;
                    case Key.W: // Copy last write times
                        clip = string.Join("\r\n", nodeArray.Select(n => File.GetLastWriteTime(n.FullName)));
                        break;
                    case Key.A: // Copy last access times
                        clip = string.Join("\r\n", nodeArray.Select(n => File.GetLastAccessTime(n.FullName)));
                        break;
                    default:
                        return; // Node defined
                }
                SetCpliboard(clip);
                return;
            }

            if (nodeArray.Length == 0)
            {
                Model.Status = "Nothing selected";
                return;
            }
            if (cut && nodeArray.Any(n => n is ZipNode))
            {
                cut = false;
                Model.Status = "Archive entries cannot be cut; they were copied instead";
            }
            string[] files;
            try
            {
                files = nodeArray.Select(n => n.GetFileOrTempPath(preserveAfterExit: true))
                    .Where(p => File.Exists(p) || Directory.Exists(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (files.Length == 0)
                {
                    Model.Status = "No existing files to copy";
                    return;
                }
                SetFileClipboard(files, cut);
            }
            catch (Exception ex)
            {
                Model.Status = $"Clipboard copy failed: {ex.Message}";
                return;
            }
            ClipBoardCut = cut;
            Model.Status = $"Clipboarded {(files.Length == 1 ? $"\"{files[0]}\"" : $"{files.Length} items")} to {(ClipBoardCut ? "MOVE" : "COPY")}";
        }

        static void SetFileClipboard(string[] files, bool cut)
        {
            var paths = new StringCollection();
            paths.AddRange(files);
            var data = new DataObject();
            data.SetFileDropList(paths);
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 1)));
            Clipboard.SetDataObject(data, true);
            StorageMaintenance.CleanupClipboardMaterializations(App.ClipboardTempFolder, files);
        }

        static bool ClipboardRequestsMove()
        {
            try
            {
                var value = Clipboard.GetData("Preferred DropEffect");
                if (value is MemoryStream stream)
                {
                    stream.Position = 0;
                    using var reader = new BinaryReader(stream, Encoding.Default, leaveOpen: true);
                    return reader.ReadInt32() == 2;
                }
                return value is byte[] bytes && bytes.Length >= sizeof(int) && BitConverter.ToInt32(bytes, 0) == 2;
            }
            catch
            {
                return false;
            }
        }

        void CopyAppend(IEnumerable<INode> nodes, IEnumerable<Key> arg)
        {
            var e = arg.GetEnumerator();
            if (e.MoveNext())
            {
                // What to copy - append mode for text attributes
                string existingText = "";
                try { existingText = Clipboard.GetText(); } catch { }
                
                string clip = "";
                switch (e.Current)
                {
                    case Key.V: // Copy files version
                        clip = string.Join("\r\n", nodes.Select(n => FileVersionInfo.GetVersionInfo(n.FullName)));
                        break;
                    case Key.T: // Copy creation times
                        clip = string.Join("\r\n", nodes.Select(n => File.GetCreationTime(n.FullName)));
                        break;
                    case Key.W: // Copy last write times
                        clip = string.Join("\r\n", nodes.Select(n => File.GetLastWriteTime(n.FullName)));
                        break;
                    case Key.A: // Copy last access times
                        clip = string.Join("\r\n", nodes.Select(n => File.GetLastAccessTime(n.FullName)));
                        break;
                    default:
                        return; // Node defined
                }
                
                if (!string.IsNullOrEmpty(existingText))
                    clip = existingText + "\r\n" + clip;
                SetCpliboard(clip);
                return;
            }

            // Append mode for file drop list
            var files = nodes.Select(n => n.GetFileOrTempPath(preserveAfterExit: true))
                .Where(p => File.Exists(p) || Directory.Exists(p))
                .ToArray();
            var existingFiles = new StringCollection();
            try
            {
                var current = Clipboard.GetFileDropList();
                foreach (var f in current) existingFiles.Add(f);
            }
            catch { }
            
            foreach (var file in files) existingFiles.Add(file);
            SetFileClipboard(existingFiles.Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), cut: false);
            Model.Status = $"Appended {(files.Length == 1 ? $"\"{files[0]}\"" : $"{files.Length} items")} to clipboard";
        }

        void Rename(IEnumerable<INode> nodes, IEnumerable<Key> arg)
        {
            var nodeArray = nodes.ToArray();
            var arguments = arg.ToArray();
            var directRename = arguments.Length == 0 || arguments.All(x => x == Key.None);
            if (directRename)
            {
                if (nodeArray.Length == 1)
                    BeginInlineRename(nodeArray[0]);
                else if (nodeArray.Length > 1)
                    Model.Status = "Inline rename is available for one item. Use an F2 transformation command for multiple items.";
                return;
            }
            nodes = nodeArray;
            arg = arguments;
            string name = "", ext = "", subStr = "";
            int index = 0;
            var e = arg.GetEnumerator();
            bool overwrite = e.MoveNext() && e.Current == Key.O, replace = false;
            if (overwrite) e.MoveNext();
            switch (e.Current)
            {
                case Key.V: // File name from clipboard
                    name = Clipboard.GetText();
                    ext = System.IO.Path.GetExtension(name);
                    name = System.IO.Path.GetFileNameWithoutExtension(name);
                    break;
                case Key.N: // Add extension
                    name = e.ReadTill();
                    break;
                case Key.E: // Change extension
                    ext = e.ReadTill();
                    break;
                case Key.OemPeriod: // Add extension
                    ext = e.ReadTill();
                    index = -1;
                    break;
                case Key.F: // Add first
                    subStr = e.ReadTill(Key.Delete);
                    break;
                case Key.L: // Add last
                    subStr = e.ReadTill(Key.Delete);
                    index = -1;
                    break;
                case Key.Insert: // Insert substring
                    if (int.TryParse(e.ReadTill(Key.OemComma), out index)) subStr = e.ReadTill(Key.Delete);
                    break;
                case Key.R: // Replace substring by another
                    subStr = e.ReadTill(Key.OemComma);
                    name = e.ReadTill();
                    replace = true;
                    break;
                case Key.Delete: // Delete substring from name
                    subStr = e.ReadTill();
                    replace = true;
                    break;
                case Key.C:
                case Key.W:
                case Key.A:
                    {
                        // Change creation or modification time
                        Action<string, DateTime> set = e.Current switch { Key.C => File.SetCreationTime, Key.W => File.SetLastWriteTime, _ => File.SetLastAccessTime };
                        DateTime time = DateTime.Now;
                        if (e.MoveNext())
                        {
                            switch (e.Current)
                            {
                                case Key.V: // Time from clipboard
                                    var text = Clipboard.GetText();
                                    if (!DateTime.TryParse(text, out time))
                                    {
                                        MessageBox.Show($"Invalid date/time format in clipboard: {text}", "Error");
                                        return;
                                    }
                                    break;
                                case Key.C: // Current time
                                    break;
                            }
                        }
                        // Set the time on all nodes
                        foreach (var n in nodes)
                            try
                            {
                                set(n.FullName, time);
                            }
                            catch { }
                        return;
                    }
            }
            // F/L/Insert entry finished by <Del> => delete the substring instead of inserting it
            replace |= e.Current == Key.Delete && !string.IsNullOrEmpty(subStr);
            if (replace && string.IsNullOrEmpty(subStr)) return; // Nothing to replace/delete
            List<string> errors = new();
            foreach (var x in nodes)
            {
                // Construct new name
                var dest = replace ? "" : name;
                if (replace)
                {
                    // Replace (or delete when the replacement is empty) all substring occurrences in the name
                    dest = System.IO.Path.GetFileNameWithoutExtension(x.Name).Replace(subStr, name, StringComparison.OrdinalIgnoreCase);
                    dest = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(x.FullName), dest + System.IO.Path.GetExtension(x.Name));
                }
                else if (string.IsNullOrEmpty(dest))
                {
                    if (!string.IsNullOrEmpty(ext))
                    {
                        if (index == 0) dest = System.IO.Path.ChangeExtension(x.FullName, "." + ext);
                        else dest = x.FullName + "." + ext;
                    }
                    else if (!string.IsNullOrEmpty(subStr))
                    {
                        dest = System.IO.Path.GetFileNameWithoutExtension(x.Name);
                        dest = dest.Insert(Math.Min(dest.Length, Math.Max(0, index >= 0 ? index : dest.Length + 1 + index)), subStr);
                        dest = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(x.FullName), dest + System.IO.Path.GetExtension(x.Name));
                    }
                    else
                    {
                        Model.Status = "No rename value was entered.";
                        return;
                    }
                }
                else if (string.IsNullOrEmpty(ext)) dest += System.IO.Path.GetExtension(x.Name); // Replace only the name
                else dest += "." + ext; // Add requested extension

                // Rename the file
                if (System.IO.Path.GetFileName(dest) == dest) dest = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(x.FullName), dest);
                if (x.FullName != dest) errors.AddRange(x.FullName.UniversalCopyOrMove(dest, overwrite, true));
            }
            Model.Status = errors.Count > 0 ? $"Rename failed with {errors.Count} errors: {string.Join(", ", errors)}" : "Rename done.";
        }

        void BeginInlineRename(INode node)
        {
            if (node == null || node is ZipNode || (!File.Exists(node.FullName) && !Directory.Exists(node.FullName)))
            {
                Model.Status = "Only a physical file or folder can be renamed inline.";
                return;
            }
            FinishInlineRename(commit: false);
            filesView.ScrollIntoView(node);
            filesView.UpdateLayout();
            if (filesView.ItemContainerGenerator.ContainerFromItem(node) is not ListViewItem item)
            {
                Model.Status = "The selected item is not currently visible.";
                return;
            }
            var editor = FindVisualChild<TextBox>(item, "InlineNameEditor");
            var display = FindVisualChild<TextBlock>(item, "NameDisplay");
            if (editor == null || display == null) return;

            inlineRenameNode = node;
            inlineRenameEditor = editor;
            inlineRenameDisplay = display;
            editor.Text = node.Name;
            editor.BorderBrush = SystemColors.ControlDarkBrush;
            editor.ToolTip = null;
            display.Visibility = Visibility.Collapsed;
            editor.Visibility = Visibility.Visible;
            editor.Focus();
            editor.Select(0, IOPath.GetFileNameWithoutExtension(node.Name).Length);
        }

        static T FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            if (parent == null) return null;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is T match && match.Name == name) return match;
                var nested = FindVisualChild<T>(child, name);
                if (nested != null) return nested;
            }
            return null;
        }

        void InlineNameEditor_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                FinishInlineRename(commit: true);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                FinishInlineRename(commit: false);
                e.Handled = true;
            }
        }

        static string ContextTargetColumn(DependencyObject source)
        {
            for (var current = source; current != null;)
            {
                if (current is FrameworkElement { Tag: string tag } && tag is "Name" or "Folder")
                    return tag;
                try { current = VisualTreeHelper.GetParent(current); }
                catch { current = LogicalTreeHelper.GetParent(current); }
            }
            return null;
        }

        void InlineNameEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!finishingInlineRename && ReferenceEquals(sender, inlineRenameEditor))
                FinishInlineRename(commit: true);
        }

        bool FinishInlineRename(bool commit)
        {
            if (finishingInlineRename || inlineRenameEditor == null || inlineRenameNode == null) return true;
            var editor = inlineRenameEditor;
            var node = inlineRenameNode;
            if (commit)
            {
                var newName = editor.Text;
                var invalid = string.IsNullOrWhiteSpace(newName) || newName is "." or ".." ||
                    newName.IndexOfAny(IOPath.GetInvalidFileNameChars()) >= 0 ||
                    !string.Equals(IOPath.GetFileName(newName), newName, StringComparison.Ordinal);
                if (invalid)
                    return RejectInlineRename("Enter a valid file or folder name.");
                var destination = IOPath.Combine(IOPath.GetDirectoryName(node.FullName), newName);
                if (!string.Equals(destination, node.FullName, StringComparison.OrdinalIgnoreCase) &&
                    (File.Exists(destination) || Directory.Exists(destination)))
                    return RejectInlineRename($"'{newName}' already exists.");
                if (!string.Equals(destination, node.FullName, StringComparison.Ordinal))
                {
                    var errors = node.FullName.UniversalCopyOrMove(destination, overwrite: false, move: true);
                    if (errors.Count > 0)
                        return RejectInlineRename(string.Join(", ", errors));
                    Model.Status = $"Renamed '{node.Name}' to '{newName}'";
                }
            }

            finishingInlineRename = true;
            try
            {
                editor.Visibility = Visibility.Collapsed;
                if (inlineRenameDisplay != null) inlineRenameDisplay.Visibility = Visibility.Visible;
                inlineRenameNode = null;
                inlineRenameEditor = null;
                inlineRenameDisplay = null;
                filesView.Focus();
                return true;
            }
            finally { finishingInlineRename = false; }
        }

        bool RejectInlineRename(string message)
        {
            Model.Status = message;
            inlineRenameEditor.BorderBrush = Brushes.IndianRed;
            inlineRenameEditor.ToolTip = message;
            Dispatcher.BeginInvoke(() => inlineRenameEditor?.Focus(), DispatcherPriority.Input);
            return false;
        }

        void NewFolders(IEnumerable<INode> nodes, IEnumerable<Key> arg)
        {
            var targetNodes = nodes.ToArray();
            var e = arg.GetEnumerator();
            bool overwrite = e.MoveNext() && e.Current == Key.O;
            if (overwrite) e.MoveNext();
            var name = arg.ReadTill();
            if (string.IsNullOrWhiteSpace(name))
            {
                ShowNewFolderBar(targetNodes, overwrite);
                return;
            }
            CreateFolders(targetNodes, name, overwrite);
        }

        void ShowNewFolderBar(INode[] nodes, bool overwrite)
        {
            if (nodes.Length == 0)
            {
                Model.Status = "Select at least one destination for the new folder.";
                return;
            }
            FinishInlineRename(commit: true);
            CancelPinnedFilterEdit();
            newFolderTargets = nodes;
            newFolderOverwrite = overwrite;
            inlineActionLabel.Text = nodes.Length == 1
                ? $"Create folder in {nodes[0].FullName.Directory()}:"
                : $"Create folder in {nodes.Length} selected destinations:";
            inlineActionTextBox.Text = "";
            inlineActionTextBox.BorderBrush = SystemColors.ControlDarkBrush;
            inlineActionTextBox.ToolTip = null;
            inlineActionBar.Visibility = Visibility.Visible;
            inlineActionTextBox.Focus();
        }

        void InlineActionAccept_Click(object sender, RoutedEventArgs e) => AcceptInlineAction();

        void InlineActionCancel_Click(object sender, RoutedEventArgs e) => CloseInlineActionBar();

        void InlineActionTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                AcceptInlineAction();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CloseInlineActionBar();
                e.Handled = true;
            }
        }

        void AcceptInlineAction()
        {
            if (newFolderTargets == null) return;
            var name = inlineActionTextBox.Text;
            if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
                name.IndexOfAny(IOPath.GetInvalidFileNameChars()) >= 0 ||
                !string.Equals(IOPath.GetFileName(name), name, StringComparison.Ordinal))
            {
                const string error = "Enter a valid folder name.";
                inlineActionTextBox.BorderBrush = Brushes.IndianRed;
                inlineActionTextBox.ToolTip = error;
                Model.Status = error;
                inlineActionTextBox.Focus();
                return;
            }
            var targets = newFolderTargets;
            var overwrite = newFolderOverwrite;
            CloseInlineActionBar();
            CreateFolders(targets, name, overwrite);
        }

        void CloseInlineActionBar()
        {
            inlineActionBar.Visibility = Visibility.Collapsed;
            newFolderTargets = null;
            newFolderOverwrite = false;
            filesView.Focus();
        }

        void CreateFolders(IEnumerable<INode> nodes, string name, bool overwrite)
        {
            var errors = new List<string>();
            // Create the folder in all selected directories or file parents
            foreach (var x in nodes)
                try
                {
                    var dir = System.IO.Path.Combine(x.IsDirectory ? x.FullName : x.FullName.Directory(), name);
                    if (overwrite) dir.DeletePathIfExists();
                    System.IO.Directory.CreateDirectory(dir);
                }
                catch (Exception ex) { errors.Add($"{x.FullName}: {ex.Message}"); }
            Model.Status = errors.Count == 0
                ? $"Created folder '{name}'"
                : $"Folder creation completed with {errors.Count} error(s): {string.Join(", ", errors)}";
        }

        async Task Paste(IEnumerable<INode> nodes, IEnumerable<Key> arg)
        {
            var paths = Clipboard.GetFileDropList().Cast<string>().ToArray();
            if (paths.Length == 0)
            {
                Model.Status = "No files in clipboard";
                return;
            }
            var clipboardMove = ClipboardRequestsMove();
            var action = arg.Contains(Key.L) ? FileTransferAction.SymbolicLink :
                arg.Contains(Key.H) ? FileTransferAction.HardLink :
                clipboardMove ? FileTransferAction.Move :
                FileTransferAction.Copy;
            var overwrite = arg.Contains(Key.O);
            var completed = await TransferPaths(
                paths,
                PasteDestinations(nodes),
                action,
                overwrite ? FileCollisionAction.Overwrite : null);
            if (completed && action == FileTransferAction.Move)
                ConsumeCutClipboard();
            else if (completed && clipboardMove)
                PreserveClipboardAsCopy(paths);
            ClipBoardCut = false;
        }

        async Task PasteWithDialog(IEnumerable<INode> nodes)
        {
            string[] paths;
            try
            {
                paths = Clipboard.GetFileDropList().Cast<string>().ToArray();
            }
            catch (Exception ex)
            {
                Model.Status = $"Cannot read clipboard files: {ex.Message}";
                return;
            }
            var destinations = PasteDestinations(nodes);
            if (paths.Length == 0)
            {
                Model.Status = "No files in clipboard";
                return;
            }
            if (destinations.Length == 0)
            {
                Model.Status = "No paste destination selected";
                return;
            }
            var clipboardMove = ClipboardRequestsMove();
            var defaultAction = clipboardMove
                ? FileTransferAction.Move
                : FileTransferAction.Copy;
            if (!TryChooseTransferAction(paths.Length, destinations.Length, defaultAction, out var action))
                return;
            var completed = await TransferPaths(paths, destinations, action);
            if (completed && action == FileTransferAction.Move)
                ConsumeCutClipboard();
            else if (completed && clipboardMove)
                PreserveClipboardAsCopy(paths);
            ClipBoardCut = false;
        }

        static string[] PasteDestinations(IEnumerable<INode> nodes) =>
            nodes.Select(n => n.FullName.Directory())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        void ConsumeCutClipboard()
        {
            ClipBoardCut = false;
            try { Clipboard.Clear(); } catch { }
        }

        void PreserveClipboardAsCopy(string[] paths)
        {
            ClipBoardCut = false;
            try { SetFileClipboard(paths, cut: false); } catch { }
        }

        async Task<bool> TransferPaths(
            string[] paths,
            string[] destinations,
            FileTransferAction action,
            FileCollisionAction? collisionForAll = null)
        {
            if (paths.Length == 0 || destinations.Length == 0)
            {
                Model.Status = "Nothing to transfer";
                return false;
            }

            Model.Status = $"{action}: {paths.Length} item(s) to {destinations.Length} folder(s)";
            var total = paths.Length * destinations.Length;
            var progress = new TransferProgressWindow($"{action} file operation", total) { Owner = this };
            var errors = new List<string>();
            var completedCount = 0;
            var skipped = 0;
            var cancelled = false;
            filesView.IsEnabled = false;
            progress.Show();
            try
            {
                for (var targetIndex = 0; targetIndex < destinations.Length; targetIndex++)
                foreach (var file in paths)
                {
                    if (cancelled || progress.Token.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }
                    var destination = IOPath.Combine(destinations[targetIndex],
                        IOPath.GetFileName(file.TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar)));
                    if (Directory.Exists(file) && IsDescendant(destination, file))
                    {
                        errors.Add($"Cannot transfer '{file}' into itself.");
                        completedCount++;
                        progress.Report(completedCount, file);
                        continue;
                    }

                    var overwrite = false;
                    if (File.Exists(destination) || Directory.Exists(destination))
                    {
                        var collision = collisionForAll;
                        if (collision == null)
                        {
                            var dialog = new FileCollisionDialog(destination) { Owner = progress };
                            if (dialog.ShowDialog() != true || dialog.Action == FileCollisionAction.Cancel)
                            {
                                cancelled = true;
                                break;
                            }
                            collision = dialog.Action;
                            if (dialog.ShouldApplyToAll)
                                collisionForAll = collision;
                        }
                        if (collision == FileCollisionAction.Skip)
                        {
                            skipped++;
                            completedCount++;
                            progress.Report(completedCount, $"Skipped {destination}");
                            continue;
                        }
                        if (collision == FileCollisionAction.Rename)
                            destination = UniqueDestination(destination, Directory.Exists(file));
                        else
                            overwrite = collision == FileCollisionAction.Overwrite;
                    }

                    // One source cannot be moved to several destinations. Match the
                    // previous cut/paste behavior: copy to earlier targets, move last.
                    var targetAction = action == FileTransferAction.Move && targetIndex < destinations.Length - 1
                        ? FileTransferAction.Copy
                        : action;
                    progress.Report(completedCount, $"{targetAction}: {file} -> {destination}");
                    var operationErrors = await Task.Run(() =>
                    {
                        if (targetAction == FileTransferAction.SymbolicLink)
                            return file.Softlink(destination, overwrite);
                        if (targetAction == FileTransferAction.HardLink)
                            return file.Hardlink(destination, overwrite);
                        return file.UniversalCopyOrMove(destination, overwrite,
                            move: targetAction == FileTransferAction.Move);
                    });
                    errors.AddRange(operationErrors);
                    completedCount++;
                    progress.Report(completedCount, file);
                }
            }
            finally
            {
                progress.Complete();
                filesView.IsEnabled = true;
                filesView.Focus();
            }

            Model.Status = cancelled
                ? $"{action} cancelled after {completedCount} of {total} operation(s)"
                : errors.Count > 0
                    ? $"{action} completed with {errors.Count} error(s): {string.Join(", ", errors)}"
                    : skipped > 0
                        ? $"{action} completed; skipped {skipped} conflict(s)"
                        : $"{action} completed for {paths.Length} item(s) and {destinations.Length} folder(s)";
            return !cancelled && errors.Count == 0 && skipped == 0;
        }

        static string UniqueDestination(string destination, bool directory)
        {
            var parent = IOPath.GetDirectoryName(destination);
            var name = directory ? IOPath.GetFileName(destination) : IOPath.GetFileNameWithoutExtension(destination);
            var extension = directory ? "" : IOPath.GetExtension(destination);
            for (var number = 2; ; number++)
            {
                var candidate = IOPath.Combine(parent, $"{name} ({number}){extension}");
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                    return candidate;
            }
        }

        async Task Unzip(IEnumerable<INode> nodes, IEnumerable<Key> arg)
        {
            var e = arg.GetEnumerator();
            bool call7zip = e.MoveNext() && e.Current == Key.NumPad7;

            //Unzip
            filesView.SelectedItems.Clear();
            await foreach (var item in Model.UnZip(call7zip, nodes.ToArray()))
                filesView.SelectedItems.Add(item);
        }

        async Task Zip(IEnumerable<INode> nodes, IEnumerable<Key> arg, bool seven = false)
        {
            var selected = nodes.ToArray();
            if (selected.Length == 0)
            {
                Model.Status = "Nothing selected to zip";
                return;
            }
            var e = arg.GetEnumerator();
            bool call7zip = e.MoveNext() && e.Current == Key.NumPad7;

            Model.Status = $"Zipping {selected.Length} item(s)";
            INode zip;
            try
            {
                zip = await Model.Zip(call7zip, seven, selected);
            }
            catch (Exception ex)
            {
                Model.Status = $"Zip failed: {ex.Message}";
                return;
            }
            if (zip == null)
            {
                Model.Status = "Zip failed";
                return;
            }
            filesView.SelectedItem = zip;
            Model.Status = $"Created \"{zip.FullName}\"";
        }

        #region Sorting by column headers
        GridViewColumn sortedColumn;
        string displayedSort;

        internal static ListSortDirection HeaderSortDirection(string sort)
        {
            var ascending = sort[0] == '+';
            var sortBy = sort.Substring(1);

            // Size and date columns intentionally show their most useful order first:
            // largest/newest to smallest/oldest. Their '+' token therefore maps to a
            // descending value order, unlike the text and content-result columns.
            if (sortBy == nameof(INode.Size) ||
                sortBy == nameof(INode.LastChangeTime) ||
                sortBy == nameof(INode.LastAccessTime))
            {
                ascending = !ascending;
            }

            return ascending ? ListSortDirection.Ascending : ListSortDirection.Descending;
        }

        void ShowSortIndicator(string sort)
        {
            if (string.IsNullOrWhiteSpace(sort) || sort.Length < 2 ||
                filesView.View is not GridView gridView)
            {
                return;
            }

            var sortBy = sort.Substring(1);
            var column = gridView.Columns.Cast<GridViewColumn>()
                .FirstOrDefault(c => string.Equals(GridViewSort.GetSortKey(c), sortBy, StringComparison.Ordinal));
            if (column == null)
                return;

            if (sortedColumn != null && sortedColumn != column)
                sortedColumn.HeaderTemplate = null;

            var templateKey = HeaderSortDirection(sort) == ListSortDirection.Ascending
                ? "HeaderTemplateArrowUp"
                : "HeaderTemplateArrowDown";
            column.HeaderTemplate = Resources[templateKey] as DataTemplate;
            sortedColumn = column;
            displayedSort = sort;
        }

        async void ListViewColumnHeader_Click(object sender, RoutedEventArgs e)
        {
            var c = Cursor;
            Cursor = Cursors.Wait;
            try
            {
                var headerClicked = e.OriginalSource as GridViewColumnHeader;

                if (headerClicked != null)
                {
                    if (headerClicked.Role != GridViewColumnHeaderRole.Padding)
                    {
                        // The sort field must be language-independent: the column header text is
                        // localized, so fall back to it only when no stable key/binding is set.
                        var columnBinding = headerClicked.Column.DisplayMemberBinding as Binding;
                        var sortBy = GridViewSort.GetSortKey(headerClicked.Column)
                            ?? columnBinding?.Path.Path
                            ?? headerClicked.Column.Header as string;

                        if (string.IsNullOrEmpty(sortBy))
                            return;

                        var sameColumn = displayedSort?.Length > 1 &&
                            string.Equals(displayedSort.Substring(1), sortBy, StringComparison.Ordinal);
                        var sign = sameColumn && displayedSort[0] == '+' ? '-' : '+';
                        var newSort = sign + sortBy;

                        await Model.Update(newSort: newSort);
                        ShowSortIndicator(newSort);

                        // ListView updates are rendered after this handler yields to the
                        // dispatcher. Keep the wait cursor until that render has completed.
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    }
                }
            }
            finally
            {
                Cursor = c;
            }
        }
        #endregion
    }

    #region converters

    public class Bool2VisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
             => (bool)value ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotImplementedException();
    }
    public class IsDirectoryBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
             => value is System.IO.FileAttributes a && a.HasFlag(System.IO.FileAttributes.Directory);

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotImplementedException();
    }
    public class IsFoundBooleanConverter : IMultiValueConverter
    {
        public object Convert(object[] value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
             => value[0] is INode n && value[1] is SearchModel m ? m.FoundIn(n) : null;

        object[] IMultiValueConverter.ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    public class ActiveFilterBrushConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            => values.Length >= 2 && string.Equals(values[0] as string, values[1] as string,
                StringComparison.Ordinal)
                ? new SolidColorBrush(Color.FromRgb(190, 220, 250))
                : SystemColors.ControlBrush;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
    #endregion
}
