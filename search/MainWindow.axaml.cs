using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using search.Models;
using IOPath = System.IO.Path;

namespace search
{
    /// <summary>
    /// The main window: filter, content search, the result grid with its keyboard command
    /// sequences, the target basket and the status bar.
    /// </summary>
    public partial class MainWindow : Window
    {
        public static readonly DirectProperty<MainWindow, string> FilterTextProperty =
            AvaloniaProperty.RegisterDirect<MainWindow, string>(nameof(FilterText), o => o.FilterText);

        string filterText = "";
        /// <summary>The filter box text as a bindable property (pinned filter highlight)</summary>
        public string FilterText
        {
            get => filterText;
            private set => SetAndRaise(FilterTextProperty, ref filterText, value ?? "");
        }

        int selectedCount;
        public int SelectedCount
        {
            get => selectedCount;
            private set
            {
                selectedCount = value;
                selectedCountRun.Text = value.ToString("N0", CultureInfo.CurrentCulture);
            }
        }

        string selectedCycleInfo = "";
        public string SelectedCycleInfo
        {
            get => selectedCycleInfo;
            private set
            {
                selectedCycleInfo = value ?? "";
                selectedCycleRun.Text = selectedCycleInfo;
            }
        }

        /// <summary>
        /// Modifier keys as of the last key event in this window - code that is not handling an
        /// input event of its own (the history list, a header click) asks this instead
        /// </summary>
        static KeyModifiers currentModifiers;
        internal static KeyModifiers CurrentModifiers => currentModifiers;

        readonly Filters filters = new Filters();
        readonly SearchTerms searchTerms = new SearchTerms();
        public ObservableCollection<PinnedFilter> PinnedFilters { get; } = new();
        public ObservableCollection<BasketTarget> BasketTargets { get; } = new();
        readonly SearchModel model;
        WindowLayout pendingLayout;
        Point? dragStart;
        PointerPressedEventArgs dragPress;
        INode dragSourceNode;
        string dragSourceColumn;
        bool preserveSelectionOnMouseUp;
        bool dragStarted;
        Border dropHoverBorder;
        IBrush dropHoverOriginalBackground;
        PinnedFilter editingPinnedFilter;
        string editingPinnedOriginalName;
        bool finishingPinnedEdit;
        INode inlineRenameNode;
        TextBox inlineRenameEditor;
        TextBlock inlineRenameDisplay;
        bool finishingInlineRename;
        bool elevationForegroundRestored;
        bool restoringSelection;
        INode activeSelectedNode;
        INode lastSelectionJumpTarget;
        INode[] selectedCycleOrder;
        int selectedCycleIndex = -1;
        INode[] newFolderTargets;
        bool newFolderOverwrite;
        string contextTargetColumn;
        bool fileOperationInProgress;
        bool closingSaved;
        readonly HashSet<DataGridRow> realizedRows = new();
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
        static readonly TimeSpan ProgressDialogDelay = TimeSpan.FromSeconds(1);
        static readonly IBrush DirectoryBrush = Brushes.Blue;
        static readonly IBrush FoundBrush = Brushes.Green;
        static readonly IBrush MissingBrush = Brushes.Red;
        static readonly IBrush PlainBrush = Brushes.Black;

        /// <summary>For the XAML loader and the designer only</summary>
        public MainWindow() : this(startFileSystem: true) { }

        internal MainWindow(bool startFileSystem)
        {
            InitializeComponent();
            basketItems.ItemsSource = BasketTargets;
            pinnedItems.ItemsSource = PinnedFilters;
            BasketTargets.CollectionChanged += (_, __) => UpdateBasketCounts();
            UpdateBasketCounts();
            if (OperatingSystem.IsWindows())
            {
                Broker.StartupElevationAccepted += Broker_StartupElevationAccepted;
                elevateAtStart.IsChecked = ElevationSettingsStore.StartHelperAtLaunch();
            }
            else elevateAtStart.IsVisible = false; //No UAC helper outside Windows
            ApplyWorkspaceSettings(WorkspaceSettingsStore.Load());
            ApplyWindowLayout();
            Closing += (_, __) => SaveOnClose();

            //Let the window finish its first layout/render before watchers start replaying
            //events and the C: MFT begins allocating. The health log showed the initial render
            //handler, not grid application, owning the startup red interval.
            model = new SearchModel(startFileSystem: false);
            DataContext = model;
            if (startFileSystem) Opened += StartFileSystemAfterFirstRender;
            //Loading changes on the thread that scans the drive. Only the captured model may be
            //read there - UI objects belong to the UI thread.
            model.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SearchModel.Loading) && !model.Loading)
                    Dispatcher.UIThread.Post(() =>
                    {
                        filesViewCmd.OnChange();
                        globalCmd.OnChange();
                    });
            };
            // The runtime check reads the installed WebView2 version - kept off the first render
            Opened += (_, __) => Dispatcher.UIThread.Post(() =>
            {
                showInFilesButton.IsVisible = WebAppWindow.IsAvailable;
                calculatorButton.IsVisible = WebAppWindow.IsBrowserAvailable;
                PrewarmWebAppsAfterLoad();
            }, DispatcherPriority.ApplicationIdle);
            Opened += Window_Opened;
            ShowSortIndicator(SearchModel.DefaultSort);
            ConfigureTextBoxes();
            ConfigureGrid();
            ConfigureDragAndDrop();
            ConfigureCommands();
            ConfigureSelectionPreservation();
            ConfigureFocusHints();

            AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
            AddHandler(KeyUpEvent, Window_KeyUp, RoutingStrategies.Tunnel);
        }

        void SaveOnClose()
        {
            if (closingSaved) return;
            closingSaved = true;
            if (OperatingSystem.IsWindows()) Broker.StartupElevationAccepted -= Broker_StartupElevationAccepted;
            WindowLayoutStore.Save(this, CaptureColumnWidths());
            SaveWorkspaceSettings();
            filters.Dispose();
            searchTerms.Dispose();
            model?.Dispose();
        }

        void UpdateBasketCounts()
        {
            var count = BasketTargets.Count;
            pasteToBasketText.Text = $"{L.Caption("send clipboard to all targets")} ({count})…";
            pasteToBasketButton.IsEnabled = count > 0;
            BasketUseText.Text = $"{L.Text("SendAll")} ({count})";
        }

        void ConfigureTextBoxes()
        {
            filterTextBox.SelectFirstSuggestion = true; // The filter applies as typed, Enter picks the top one
            filterTextBox.SuggestionList = () => CurrentModifiers == KeyModifiers.Control ? filters.LastUsed : filters.MostUsed;
            filterTextBox.TextSelected += t => filters.Add2History(filterTextBox.Text);
            filterTextBox.DeleteItem = item => filters.Delete(item);
            filterTextBox.TextChanged += TextBox_TextChanged;
            filterTextBox.AddHandler(KeyDownEvent, filterTextBox_PreviewKeyDown, RoutingStrategies.Tunnel);

            // Configure findTextBox with search terms autocomplete
            findTextBox.SuggestionList = () => CurrentModifiers == KeyModifiers.Control ? searchTerms.LastUsed : searchTerms.MostUsed;
            findTextBox.TextSelected += t => searchTerms.Add2History(findTextBox.Text);
            findTextBox.DeleteItem = item => searchTerms.Delete(item);
            findTextBox.AddHandler(KeyDownEvent, findTextBox_PreviewKeyDown, RoutingStrategies.Tunnel);

            encodingComboBox.SelectionChanged += EncodingComboBox_SelectionChanged;
            inlineActionTextBox.AddHandler(KeyDownEvent, InlineActionTextBox_KeyDown, RoutingStrategies.Tunnel);

            filterTextBoxCmd.Text = L.Text("FilterFieldHints");
            findCmd.Text = L.Text("SearchFieldHints");

            //Pinned filter name editors are created by the item template
            pinnedItems.AddHandler(KeyDownEvent, PinnedFilterName_KeyDown, RoutingStrategies.Tunnel);
            pinnedItems.AddHandler(LostFocusEvent, PinnedFilterName_LostFocus, RoutingStrategies.Bubble);
            pinnedItems.AddHandler(TextBox.TextChangedEvent, PinnedFilterName_TextChanged, RoutingStrategies.Bubble);
        }

        /// <summary>
        /// Hints for the focused field and the result list - computed on every focus move,
        /// Avalonia has no style triggers to bind several conditions
        /// </summary>
        void ConfigureFocusHints()
        {
            AddHandler(GotFocusEvent, (_, __) => UpdateHints(), RoutingStrategies.Bubble, handledEventsToo: true);
            AddHandler(LostFocusEvent, (_, __) => Dispatcher.UIThread.Post(UpdateHints), RoutingStrategies.Bubble, handledEventsToo: true);
            globalCmd.ReceivingChanged += UpdateHints;
            UpdateHints();
        }

        void UpdateHints()
        {
            var gridFocused = filesView.IsKeyboardFocusWithin;
            var chord = globalCmd.IsReceivingCommandKeys;
            filterTextBoxCmd.IsVisible = filterTextBox.IsKeyboardFocusWithin && !chord;
            findCmd.IsVisible = findTextBox.IsKeyboardFocusWithin && !chord;
            filesViewCmd.IsVisible = gridFocused;
            // The focused result list shows its own commander incl. the ALT tree
            globalCmd.IsVisible = !gridFocused;
        }

        void ConfigureCommands()
        {
            // Add commander
            CommandTree Enter(string hint) => (Key.None, L.Format("EnterValueKeepingKey", L.Text(hint)));
            var openIn = new CommandTree[] {
                (Key.T, "Log explorer", n => n.AtLeast(1) && Apps.TextViever != null),
                (Key.B, "File browser", n => n.AtLeast(1) && Apps.Explorer != null),
                (Key.W, "Web browser", n => n.AtLeast(1) && Apps.WebBrowser != null),
                OperatingSystem.IsWindows()
                    ? (Key.S, "Shell", (Key.P, "Power Shell"))
                    : (Key.S, "Shell"),
                (Key.G, "Ghostscript", n => n.AtLeast(1) && Apps.GhostScript != null, Enter("DPI")),
                (Key.P, "GhostPCL", n => n.AtLeast(1) && Apps.GhostPcl != null, Enter("DPI")),
                (Key.X, "GhostXPS", n => n.AtLeast(1) && Apps.GhostXps != null, Enter("DPI")),
                (Key.R, "PRN Viewer", n => n.AtLeast(1) && Apps.TextViever != null, Enter("DPI"))
            }.Concat(FileApplications.Commands()).ToArray();

            var timeSource = new CommandTree[] { (Key.V, "from clipboard"), (Key.C, "current") };
            var f2 = new CommandTree[] {
                    (Key.V, "Path/name from clipboard"),
                    (Key.N, "Change name",Enter("new name")),
                    (Key.E, "Change extension", new CommandTree[] { Enter("Extension"), (Key.Delete,"Delete extension") }),
                    (Key.C, "Change creation time", timeSource),
                    (Key.W, "Change last write time", timeSource),
                    (Key.A, "Change last access time", timeSource),
                    (Key.OemPeriod, "Add extension",Enter("Extension")),
                    (Key.Delete, L.Caption("delete from name"),Enter("string to delete")),
                    (Key.F, "Add as prefix",Enter("prefix")),
                    (Key.L, "Add as posfix",Enter("postfix")),
                    (Key.Insert, L.Caption("insert string"),Enter("insertion index")),
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
                (Key.K, "Calculator", n => WebAppWindow.IsBrowserAvailable, (n,a) => OpenCalculator()),
                (Key.N, L.Caption("add selected as targets"), n => n.AtLeast(1), (n,a) => AddBasketTargets(n.Select(NameTargetPath))),
                (Key.F, L.Caption("add parent Folders as targets"), n => n.AtLeast(1), (n,a) => AddBasketTargets(n.Select(x => IOPath.GetDirectoryName(x.FullName)))),
                (Key.V, L.Caption("send clipboard to all targets"), pasteToAllTargets, pasteToAllKeys),
                (Key.C, "Clear targets", (n,a) => ClearTargets_Click(this, new RoutedEventArgs())),
                (Key.P, "Pin current filter", (n,a) => PinFilter_Click(this, new RoutedEventArgs())),
                (Key.I, "Import pinned filters and targets", (n,a) => ImportWorkspace_Click(this, new RoutedEventArgs())),
                (Key.E, "Export pinned filters and targets", (n,a) => ExportWorkspace_Click(this, new RoutedEventArgs()))
            };
            //V pastes at once. The shared caption "Paste…" belongs to the context menu entry,
            //which opens the action dialog like Ctrl+V - without the ellipsis here.
            CommandTree pasteDirectly = (Key.V, "Paste", async (n,a)=>await Paste(n,a), new CommandTree[] { overwrite, (Key.L, "as link", overwrite), (Key.H, "as hard link", overwrite) });
            pasteDirectly.Help = pasteDirectly.Help.TrimEnd('…', '.');
            filesViewCmd.Commands.Add(new CommandTree[] {
                //F1/F12 execute in Window_KeyDown before the grid sees them. Keeping them
                //in this tree makes the working window-wide shortcuts visible in grid hints.
                (Key.F1, "HintF1"),
                (Key.F12, "HintF12"),
                FileApplications.DiffCommand(async (n,a) => await OpenDiff(n)),
                (Key.H, "Hex editor / compare", n => (n.IsCount(1) || n.IsCount(2)) && n.All(x => !x.IsDirectory) && WebAppWindow.IsAvailable,
                    async (n,a) => await Open(Apps.HexEditor, n.ToArray())),
                (Key.L, "Log explorer", n => n.AtLeast(1) && WebAppWindow.IsAvailable,
                    async (n,a) => await Open(Apps.LogExplorer, Model.ToTextNodes(n.ToArray()).ToArray())),
                (Key.G, "Show in files", n => Model.HasFound && WebAppWindow.IsAvailable, async (n,a) => await ShowInFiles()),
                //Enter filters into folders and archives; a selection without any is opened instead
                (Key.Enter, $"{L.Text("Filter folders")} / {L.Text("Open")}", async (n,a)=> await FilterFolders(n.ToArray())),
                (Key.Delete, "Recycle", async (n,a)=>await Delete(n)),
                (Key.C, "Copy", async (n,a) => await Copy(n,a), new CommandTree[] {
                    (Key.Add, "Append to clipboard", async (n,a) => await CopyAppend(n,a), new CommandTree[] { (Key.V, "file version"), (Key.T, "creation time"), (Key.W, "last write time"), (Key.A, "last access time") }),
                    (Key.V, "file version"),
                    (Key.T, "creation time"),
                    (Key.W, "last write time"),
                    (Key.A, "last access time")
                }),
                (Key.O, "Open in", async (n,a)=> await OpenIn(n,a), openIn),
                (Key.A, "Open in (as admin)", async (n,a)=> await OpenIn(n,a,true), openIn),
                (Key.F2, "Rename / Change", async (n,a) => await Rename(n,a), new CommandTree[] {(Key.O, "Overwrite", f2)}.Concat(f2).ToArray()),
                (Key.F3, "View node", n => n.AtLeast(1), async (n,a)=>{ if (n.Any()) await Open(Apps.ViewerFor(n.First().GetFileOrTempPath()), Model.ToTextNodes(n.ToArray()).ToArray()); }),
                (Key.F4, "Edit", n => n.AtLeast(1), async (n,a)=> await Open(Apps.TextEditor, n.ToArray())),
                (Key.N, "Copy name", async (n,a)=> await SetClipboardText(string.Join(Environment.NewLine, n.Select(n => n.Name)))),
                (Key.P, "Copy path", async (n,a)=> await SetClipboardText(string.Join(Environment.NewLine, n.Select(n => n.FullName)))),
                (Key.F, "Copy folder name", async (n,a)=> await SetClipboardText(string.Join(Environment.NewLine, n.Select(n => Model.GetParent(n)?.FullName ?? IOPath.GetDirectoryName(n.FullName))))),
                (Key.M, "Make directory in selected", (n,a)=>NewFolders(n,a),Enter("folder name")),
                pasteDirectly,
                (Key.X, "Cut", async (n,a)=> await Copy(n,a,true)),
                (Key.S, "Select", new CommandTree[] {
                    (Key.A, "All", (n,a) => GridSelection.ToggleSelectAll(filesView, Model.Items.Count)),
                    (Key.D, "Directories", (n,a) => SelectNodes(Items.Where(n => n.IsDirectory))),
                    (Key.F, "Files",  (n,a) => SelectNodes(Items.Where(n => !n.IsDirectory))),
                    (Key.I, "Invert selection", (n,a) => InvertSelection()),
                    (Key.G, "Found files (green rows)", (n,a) => SelectFound()),
                    (Key.R, "Red rows", (n,a) => SelectNodes(Items.Where(n => Model.FoundIn(n) == false))),
                    (Key.B, "Black rows", (n,a) => SelectNodes(Items.Where(n => Model.FoundIn(n) == null))),
                }),
                (Key.T, L.Caption("add selected as targets"), (n,a) => AddBasketTargets(n.Select(NameTargetPath)), new CommandTree[] {
                    (Key.F, L.Caption("add parent Folders as targets"), n => n.AtLeast(1), (n,a) => AddBasketTargets(n.Select(x => IOPath.GetDirectoryName(x.FullName)))),
                    (Key.V, L.Caption("send clipboard to all targets"), pasteToAllTargets, pasteToAllKeys),
                    (Key.C, "Clear targets", (n,a) => ClearTargets_Click(this, new RoutedEventArgs()))
                }),
                (Key.LeftAlt, "ALT", altCommands),
                (Key.LeftCtrl,"CTRL", new CommandTree[] {
                    (Key.A, "Select/Unselect all", (n,a) => GridSelection.ToggleSelectAll(filesView, Model.Items.Count)),
                    (Key.C, "Copy", async (n,a) => await Copy(n,a)),
                    (Key.D, "Filter Directores of selected", async (n,a) => await FilterFolders(n.Select(x => Model.GetParent(x)).Where(x => x != null).ToArray())),
                    (Key.F, "Filter selected Folders", async (n,a) => await FilterFolders(n.Where(n => n.IsDirectory).ToArray())),
                    (Key.J, "Show next selected item", n => n.AtLeast(1), (n,a) => JumpToSelected(reverse: false)),
                    (Key.LeftShift, "SHIFT", new CommandTree[] {
                        (Key.J, "Show previous selected item", n => n.AtLeast(1), (n,a) => JumpToSelected(reverse: true))
                    }),
                    (Key.N, "Create new folders in selected", (n,a) => NewFolders(n,a),Enter("folder name")),
                    (Key.V, "Paste (choose action)", async (n,a) => await PasteWithDialog(n)),
                    (Key.X, "Cut", async (n,a) => await Copy(n,a,true))
                }),
                (Key.LeftShift,"SHIFT", new CommandTree[] {
                    (Key.Delete, "Permanently delete - do not confirm", async (n,a)=>await Delete(n,false,true)),
                }),
                (Key.RightShift, "Focus filter", (n,a)=>filterTextBox.Focus()),
                (Key.U, "Unzip archive", async (n,a)=>await Unzip(n,a), new CommandTree[] {(Key.NumPad7, "Call 7z.exe", n => Apps.SevenZip != null)}),
                (Key.Z, "Zip selected", async (n,a)=>await Zip(n,a), new CommandTree[] {(Key.NumPad7, "Call 7z.exe", n => Apps.SevenZip != null)})
            });

            // Save filter to history on any command
            filesViewCmd.OnCommand += () => filters.Used(filterTextBox.Text);
            filesViewCmd.nodes = SelectedNodes;
            filesView.SelectionChanged += (o, e) =>
            {
                SelectedCount = filesView.SelectedItems.Count;
                if (!restoringSelection) SelectionChanged(e);
                filesViewCmd.OnChange();
                globalCmd.OnChange();
            };

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
            globalCmd.nodes = SelectedNodes;
            globalCmd.OnChange();

            // A key released elsewhere never reaches the commander. Left in keysDown it would
            // later complete a stale sequence - a stuck Shift plus Delete becomes Shift+Delete
            // (permanent delete) on the next Shift tap. Forget held keys once focus is gone.
            filesView.LostFocus += (o, e) => Dispatcher.UIThread.Post(() =>
            {
                // Also raised when focus just moves between cells
                if (!filesView.IsKeyboardFocusWithin) filesViewCmd.Cancel();
            });
            Deactivated += (o, e) =>
            {
                filesViewCmd.Cancel();
                globalCmd.Cancel();
            };
        }

        IEnumerable<INode> SelectedNodes()
            => filesView.SelectedItems?.OfType<INode>().ToArray() ?? Enumerable.Empty<INode>();

        void SelectNodes(IEnumerable<INode> nodes)
            => GridSelection.Select(filesView, Model.Items, nodes);

        void ConfigureSelectionPreservation()
        {
            // Preserve selection on Items update
            INode[] selected = null;
            INode focused = null;
            var selectedStart = -1;
            var restoreKeyboardFocus = false;
            Model.BeforeItemsExchange = () =>
            {
                restoringSelection = true;
                selected = filesView.SelectedItems.OfType<INode>().ToArray();
                selectedStart = FirstSelectedItemIndex(selected);
                focused = filesView.IsKeyboardFocusWithin
                    ? activeSelectedNode ?? filesView.SelectedItem as INode
                    : null;
                restoreKeyboardFocus = focused != null || filesView.IsKeyboardFocusWithin;
            };
            Model.AfterItemsExchange = () =>
            {
                var survivors = Array.Empty<INode>();
                try
                {
                    survivors = SelectionSurvivors(selected);
                    if (survivors.Length > 0) SelectNodes(survivors);

                    if (restoreKeyboardFocus)
                    {
                        var focusedSurvivor = focused != null && Model.Items.Contains(focused)
                            ? focused
                            : null;

                        if (focusedSurvivor != null)
                        {
                            activeSelectedNode = focusedSurvivor;
                            //Collection publication must never move the viewport. Restore the
                            //keyboard row only.
                            FocusFileRow(focusedSurvivor, scroll: false);
                        }
                        //All selected rows disappeared (typically a completed delete). Keep the
                        //keyboard caret at the block's old first index, which now contains the
                        //next row; at the end of the list fall back to the preceding row.
                        else if (survivors.Length == 0 && selected?.Length > 0)
                        {
                            var continuation = SelectionContinuationIndex(selectedStart, Model.Items.Count);
                            if (continuation >= 0)
                            {
                                var focusTarget = Model.Items[continuation];
                                filesView.SelectedItem = focusTarget;
                                activeSelectedNode = focusTarget;
                                if (focusTarget != null) FocusFileRow(focusTarget);
                            }
                        }
                    }
                }
                finally
                {
                    selected = null;
                    focused = null;
                    selectedStart = -1;
                    restoreKeyboardFocus = false;
                    restoringSelection = false;
                    SelectedCount = filesView.SelectedItems.Count;
                    ReconcileSelectionCycleAfterPublication(survivors);
                }
            };
            Model.UIRefreshRequested += () => Dispatcher.UIThread.Invoke(() =>
            {
                foreach (var row in realizedRows.ToArray()) RefreshRow(row, inlineRenameNode);
            });
            //Repaint just the changed rows, and only when they are actually realized on screen.
            //A full refresh regenerates every visible row (blink) and resets the keyboard
            //navigation state, breaking SHIFT+Up/Down selection on unrelated file system changes.
            Model.RowsRefreshRequested += nodes => Dispatcher.UIThread.Invoke(() =>
            {
                if (realizedRows.Count == 0) return;
                var changed = new HashSet<object>(nodes, ReferenceEqualityComparer.Instance);
                //INode raises no change notifications => make each row re-read its values.
                //Off-screen rows need nothing: virtualization binds them when realized.
                foreach (var row in realizedRows.ToArray())
                    if (row.DataContext != null && changed.Contains(row.DataContext))
                        RefreshRow(row, inlineRenameNode);
            });
        }

        /// <summary>
        /// Start the web view runtime once the first index load is done - it never competes
        /// with the MFT read, and the first Log explorer window skips the cold start
        /// </summary>
        async void PrewarmWebAppsAfterLoad()
        {
            var waited = Stopwatch.StartNew();
            // Loading turns on only after the first render - give it a moment before trusting "false"
            while (waited.Elapsed < TimeSpan.FromSeconds(60) && (waited.Elapsed < TimeSpan.FromSeconds(3) || Model?.Loading == true))
                await Task.Delay(500);
            if (IsVisible) WebAppWindow.Prewarm(this);
        }

        void StartFileSystemAfterFirstRender(object sender, EventArgs e)
        {
            Opened -= StartFileSystemAfterFirstRender;
            Dispatcher.UIThread.Post(() => Model?.StartFileSystemProcessing(),
                DispatcherPriority.ApplicationIdle);
        }

        internal static int SelectionContinuationIndex(int firstSelectedIndex, int remainingCount)
            => firstSelectedIndex < 0 || remainingCount <= 0
                ? -1
                : Math.Min(firstSelectedIndex, remainingCount - 1);

        /// <summary>The data item of the row containing the element (null outside a row)</summary>
        internal static object FocusedItemFromElement(Visual itemsControl, object focusedElement)
        {
            if (focusedElement is not Visual element || itemsControl == null)
                return null;
            return element.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext;
        }

        int FirstSelectedItemIndex(INode[] selected)
        {
            if (selected == null || selected.Length == 0) return -1;
            var items = Model.Items;
            if (selected.Length <= 4)
                return selected.Select(items.IndexOf)
                    .Where(index => index >= 0)
                    .DefaultIfEmpty(-1)
                    .Min();

            var selectedSet = new HashSet<object>(selected, ReferenceEqualityComparer.Instance);
            for (var index = 0; index < items.Count; index++)
                if (selectedSet.Contains(items[index])) return index;
            return -1;
        }

        INode[] SelectionSurvivors(INode[] selected)
        {
            if (selected == null || selected.Length == 0) return Array.Empty<INode>();
            var items = Model.Items;
            if (selected.Length <= 4)
                return selected.Where(items.Contains).ToArray();

            //For a large multi-selection avoid Selected × Items IndexOf/Contains work.
            //The published grid is capped at 100k, so this stays linear and the temporary
            //set is proportional to the selection rather than to the multi-million index.
            var selectedSet = new HashSet<object>(selected, ReferenceEqualityComparer.Instance);
            return items.Where(selectedSet.Contains).ToArray();
        }

        internal static int SelectionCycleTargetIndex(
            int selectedCount, int activeIndex, int lastJumpIndex, bool reverse)
        {
            if (selectedCount <= 0) return -1;
            if (lastJumpIndex >= 0 && lastJumpIndex < selectedCount)
                return (lastJumpIndex + (reverse ? -1 : 1) + selectedCount) % selectedCount;
            if (activeIndex >= 0 && activeIndex < selectedCount) return activeIndex;
            return reverse ? selectedCount - 1 : 0;
        }

        internal static int SelectionCycleItemIndex<T>(
            IReadOnlyList<T> items, T target, int cachedIndex) where T : class
        {
            if (target == null || items == null) return -1;
            if (cachedIndex >= 0 && cachedIndex < items.Count &&
                ReferenceEquals(items[cachedIndex], target))
            {
                return cachedIndex;
            }
            for (var index = 0; index < items.Count; index++)
                if (ReferenceEquals(items[index], target)) return index;
            return -1;
        }

        void SelectionChanged(SelectionChangedEventArgs e)
        {
            if (filesView.SelectedItems.Count == 0)
            {
                activeSelectedNode = null;
            }
            else
            {
                var added = e.AddedItems.OfType<INode>().LastOrDefault();
                if (added != null)
                    activeSelectedNode = added;
                else if (activeSelectedNode != null && !filesView.SelectedItems.Contains(activeSelectedNode))
                    activeSelectedNode = null;
            }
            ResetSelectionCycle();
        }

        void ResetSelectionCycle()
        {
            lastSelectionJumpTarget = null;
            InvalidateSelectionCycleOrder();
        }

        void InvalidateSelectionCycleOrder()
        {
            selectedCycleOrder = null;
            selectedCycleIndex = -1;
            SelectedCycleInfo = "";
        }

        void ReconcileSelectionCycleAfterPublication(INode[] survivors)
        {
            if (lastSelectionJumpTarget == null)
            {
                InvalidateSelectionCycleOrder();
                return;
            }

            //SelectionSurvivors already returns visual order for larger selections. For the
            //common small selection, sorting at most four references avoids allocating/scanning
            //a 100k-row cycle array merely to retain the cursor across a live update.
            selectedCycleOrder = survivors.Length <= 4
                ? survivors.OrderBy(Model.Items.IndexOf).ToArray()
                : survivors;
            selectedCycleIndex = SelectionCycleItemIndex(
                selectedCycleOrder, lastSelectionJumpTarget, cachedIndex: -1);
            if (selectedCycleIndex < 0)
            {
                ResetSelectionCycle();
                return;
            }
            SelectedCycleInfo =
                $" · {selectedCycleIndex + 1:N0}/{selectedCycleOrder.Length:N0}";
        }

        void SelectedItems_Click(object sender, RoutedEventArgs e)
            => JumpToSelected(CurrentModifiers.HasFlag(KeyModifiers.Shift));

        void JumpToSelected(bool reverse)
        {
            if (filesView.SelectedItems.Count == 0)
            {
                ResetSelectionCycle();
                return;
            }

            if (selectedCycleOrder == null)
            {
                var selected = new HashSet<object>(
                    filesView.SelectedItems.Cast<object>(), ReferenceEqualityComparer.Instance);
                selectedCycleOrder = Model.Items
                    .Where(selected.Contains)
                    .ToArray();
            }
            if (selectedCycleOrder.Length == 0)
            {
                ResetSelectionCycle();
                return;
            }

            var lastIndex = SelectionCycleItemIndex(
                selectedCycleOrder, lastSelectionJumpTarget, selectedCycleIndex);
            var activeIndex = lastIndex >= 0 || activeSelectedNode == null
                ? -1
                : Array.FindIndex(selectedCycleOrder, node => ReferenceEquals(node, activeSelectedNode));
            var targetIndex = SelectionCycleTargetIndex(
                selectedCycleOrder.Length, activeIndex, lastIndex, reverse);
            if (targetIndex < 0) return;

            var target = selectedCycleOrder[targetIndex];
            selectedCycleIndex = targetIndex;
            lastSelectionJumpTarget = target;
            activeSelectedNode = target;
            SelectedCycleInfo = $" · {targetIndex + 1:N0}/{selectedCycleOrder.Length:N0}";
            FocusFileRow(target);
        }

        /// <summary>
        /// Move the keyboard row to the node (the anchor of the next Shift+arrow) without
        /// changing the selection; scroll it into view unless told not to
        /// </summary>
        void FocusFileRow(INode node, bool scroll = true)
        {
            var index = Model.Items.IndexOf(node);
            if (index < 0) return;
            if (scroll) filesView.ScrollIntoView(node, null);
            if (!GridSelection.SetCurrentRow(filesView, index) && scroll)
                filesView.ScrollIntoView(node, null);
            if (!filesView.IsKeyboardFocusWithin && IsActive) filesView.Focus();
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
            catch (Exception ex) { Model.Status = L.Format("WorkspaceSaveFailed", ex.Message); }
        }

        async void Drives_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new DrivesWindow();
            //Refresh only roots whose effective selection changed. Refreshing every drive
            //made a quick S: toggle start two unrelated 25-second C: MFT scans.
            if (await dialog.ShowDialog<bool>(this))
                foreach (var root in dialog.ChangedRoots)
                    FSChangeProcessor.RefreshDrive(root, DriveScanReason.DriveSelection);
        }
    }
}
