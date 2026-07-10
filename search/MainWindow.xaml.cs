using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
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
        bool updatingFolderWidth;
        bool columnWidthHandlersAttached;
        WindowLayout pendingLayout;
        Point? dragStart;
        INode dragSourceNode;
        string dragSourceColumn;
        bool preserveSelectionOnMouseUp;
        bool dragStarted;
        Border dropHoverBorder;
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
            ApplyWindowLayout();
            AttachColumnWidthHandlers();
            SizeChanged += (_, __) => UpdateFolderColumnWidth();
            Closing += (_, __) => WindowLayoutStore.Save(this, CaptureColumnWidths());

            DataContext = new Models.SearchModel();
            filterTextBox.SuggestionList = () => Keyboard.Modifiers == ModifierKeys.Control ? filters.LastUsed : filters.MostUsed;
            filterTextBox.TextSelected += t => filters.Add2History(filterTextBox.Text);
            filterTextBox.DeleteItem = item => filters.Delete(item);

            // Configure findTextBox with search terms autocomplete
            findTextBox.SuggestionList = () => Keyboard.Modifiers == ModifierKeys.Control ? searchTerms.LastUsed : searchTerms.MostUsed;
            findTextBox.TextSelected += t => searchTerms.Add2History(findTextBox.Text);
            findTextBox.DeleteItem = item => searchTerms.Delete(item);

            filterTextBoxCmd.Text = "<CTRL><Left> previous filter\r\n<CTRL><Right> next filter\r\n<Down> show suggestions\r\n<Del> delete selected suggestion";
            findCmd.Text = "Search in files\r\n\r\nEncoding options:\r\n• UTF-8 (text)\r\n• UTF-16 (text)\r\n• HEX (space-separated bytes)\r\n\r\nControls:\r\n• Case sensitivity checkbox\r\n• <Down> show suggestions\r\n• <Del> delete selected item";

            // Add commander
            CommandTree Enter(string hint) => (Key.None, $"enter {hint}\nwhile leaving any key pressed");
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
            filesViewCmd.Commands.Add(new CommandTree[] {
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
                (Key.Z, "Zip selected", async (n,a)=>await Zip(n,a), new CommandTree[] {(Key.NumPad7, "Call 7z.exe")}),
                (Key.F12, "Refresh from NTFS", (n,a)=> FSChangeProcessor.RefreshFromNFT())
            });

            // Save filter to history on any command 
            filesViewCmd.OnCommand += () => filters.Used(filterTextBox.Text);
            filesViewCmd.nodes = () => filesView.SelectedItems?.Cast<INode>() ?? Enumerable.Empty<INode>();
            filesView.SelectionChanged += (o, e) => filesViewCmd.OnChange();

            // Preserver selection on Items update
            INode[] selected = null;
            Model.BeforeItemsExchange = () => selected = filesView.SelectedItems.Cast<INode>().ToArray();
            Model.AfterItemsExchange = () =>
            {
                if (selected != null && selected.Length > 0) filesView.Select(selected);
                selected = null;
                UpdateFolderColumnWidth();
            };
            Model.UIRefreshRequested += () => Dispatcher.Invoke(() =>
            {
                filesView.Items.Refresh();
                UpdateFolderColumnWidth();
            });
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
                DragDrop.DoDragDrop(filesView, data, DragDropEffects.Copy);
            }
            catch (Exception ex)
            {
                Model.Status = $"Drag failed: {ex.Message}";
            }
            finally
            {
                dragStarted = false;
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
            var validTarget = sources.Length > 0 &&
                (TryGetExecutableDrop(sender, out _) || TryGetDropTargets(sender, out _));
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
            e.Effects = DragDropEffects.Copy;
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

            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            if (TryGetExecutableDrop(sender, out var executable))
            {
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

            if (!TryGetDropTargets(sender, out var targets) ||
                !TryChooseTransferAction(sources.Length, targets.Length, FileTransferAction.Copy, out var action))
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

        static bool CanUnzip(INode node)
            => !node.IsDirectory && File.Exists(node.FullName) &&
               ArchiveExtensions.Contains(IOPath.GetExtension(node.Name));

        async void ContextZip_Click(object sender, RoutedEventArgs e)
            => await Zip(ContextNodes(), Array.Empty<Key>());

        async void ContextZip7z_Click(object sender, RoutedEventArgs e)
            => await Zip(ContextNodes(), new[] { Key.NumPad7 });

        async void ContextUnzip_Click(object sender, RoutedEventArgs e)
            => await Unzip(ContextNodes(), Array.Empty<Key>());

        private void ListView_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Up:
                case Key.Down:
                case Key.PageDown:
                case Key.PageUp:
                    // Skip list view control keys
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
            filesViewCmd.KeyPressed(e.Key);
            $"Key {e.Key} pressed => Still down: {string.Join(", ", filesViewCmd.KeysDown().Select(x => x.ToString()))}".Debug();
            e.Handled = true;
        }

        bool ClipBoardCut = false;
        async void ListView_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            try
            {
                e.Handled = filesViewCmd.KeyReleased(e.Key);
                $"Key {e.Key} released => Down: {string.Join(", ", filesViewCmd.KeysDown().Select(x => x.ToString()))}".Debug();
            }
            catch (Exception ex)
            {
                $"KeyDown exception {ex}".Debug();
                MessageBox.Show($"{ex}", "Error processing KeyDown");
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
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
                        // Ask user in dialog
                        var ed = new EditValueWindow($"Rename: {x.Name}");
                        ed.Value.Text = System.IO.Path.GetFileName(x.Name);
                        if (ed.ShowDialog() == true) dest = ed.Value.Text;
                        else return;
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

        void NewFolders(IEnumerable<INode> nodes, IEnumerable<Key> arg)
        {
            var e = arg.GetEnumerator();
            bool overwrite = e.MoveNext() && e.Current == Key.O;
            if (overwrite) e.MoveNext();
            var name = arg.ReadTill();
            if (string.IsNullOrWhiteSpace(name))
            {
                var ed = new EditValueWindow($"Folder name to create in selected directories");
                ed.Value.Text = "";
                if (ed.ShowDialog() != true) return;
                name = ed.Value.Text;
            }
            // Create the folder in all selected directories or file parents
            foreach (var x in nodes)
                try
                {
                    var dir = System.IO.Path.Combine(x.IsDirectory ? x.FullName : x.FullName.Directory(), name);
                    if (overwrite) dir.DeletePathIfExists();
                    System.IO.Directory.CreateDirectory(dir);
                }
                catch { }
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
        GridViewColumnHeader _lastHeaderClicked = null;
        ListSortDirection _lastDirection = ListSortDirection.Ascending;
        async void ListViewColumnHeader_Click(object sender, RoutedEventArgs e)
        {
            var c = Cursor;
            Cursor = Cursors.Wait;
            try
            {
                var headerClicked = e.OriginalSource as GridViewColumnHeader;
                ListSortDirection direction;

                if (headerClicked != null)
                {
                    if (headerClicked.Role != GridViewColumnHeaderRole.Padding)
                    {
                        //Determine new sorting
                        if (headerClicked != _lastHeaderClicked)
                        {
                            direction = ListSortDirection.Ascending;
                        }
                        else
                        {
                            if (_lastDirection == ListSortDirection.Ascending)
                            {
                                direction = ListSortDirection.Descending;
                            }
                            else
                            {
                                direction = ListSortDirection.Ascending;
                            }
                        }

                        var columnBinding = headerClicked.Column.DisplayMemberBinding as Binding;
                        var sortBy = columnBinding?.Path.Path ?? headerClicked.Column.Header as string;

                        await Model.Update(newSort: (direction == ListSortDirection.Ascending ? '+' : '-') + sortBy);

                        //Save current sorting
                        if (direction == ListSortDirection.Ascending)
                        {
                            headerClicked.Column.HeaderTemplate =
                              Resources["HeaderTemplateArrowUp"] as DataTemplate;
                        }
                        else
                        {
                            headerClicked.Column.HeaderTemplate =
                              Resources["HeaderTemplateArrowDown"] as DataTemplate;
                        }

                        // Remove arrow from previously sorted header  
                        if (_lastHeaderClicked != null && _lastHeaderClicked != headerClicked)
                        {
                            _lastHeaderClicked.Column.HeaderTemplate = null;
                        }

                        _lastHeaderClicked = headerClicked;
                        _lastDirection = direction;

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
    #endregion
}
