using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using search.Models;
using IOPath = System.IO.Path;

namespace search
{
    /// <summary>Filter/search boxes and every file operation of the result list</summary>
    public partial class MainWindow
    {
        #region Filter and search boxes

        async void TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var filter = filterTextBox.Text;
            FilterText = filter;
            await Model.Update(filter);
        }

        async Task StartSearching()
        {
            // Save search term to history when search is performed
            if (!string.IsNullOrWhiteSpace(findTextBox.Text))
                searchTerms.Used(findTextBox.Text);

            string encoding = GetCurrentEncoding();
            await Model.Find(findTextBox.Text, caseInsensitive.IsChecked == true, encoding);
        }

        async void EncodingComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (findTextBox == null || encodingComboBox == null) return;

            string currentText = findTextBox.Text;
            string fromEncoding = previousEncoding;
            string toEncoding = GetCurrentEncoding();
            if (string.IsNullOrEmpty(currentText))
            {
                // Nothing to convert, but the text typed next is in the new encoding. Leaving
                // the old one remembered made the following switch convert from the wrong
                // encoding - HEX bytes typed into an empty box were re-encoded as UTF-8 text.
                previousEncoding = toEncoding;
                return;
            }

            try
            {
                findTextBox.Text = ConvertTextEncoding(currentText, fromEncoding, toEncoding);
                // Only update previousEncoding after successful conversion
                previousEncoding = toEncoding;
            }
            catch (Exception ex)
            {
                // If conversion fails, keep the original text and reset the ComboBox
                encodingComboBox.SelectedIndex = GetEncodingIndex(fromEncoding);
                await MessageDialog.Show(this, L.Format("EncodingConversionFailed", ex.Message), L.Text("Error"),
                    MessageDialogButtons.Ok, MessageDialogIcon.Warning);
            }
        }

        static int GetEncodingIndex(string encoding) => encoding switch
        {
            "UTF-8" => 0,
            "UTF-16" => 1,
            "HEX" => 2,
            _ => 0
        };

        string GetCurrentEncoding() => encodingComboBox.SelectedIndex switch
        {
            0 => "UTF-8",
            1 => "UTF-16",
            2 => "HEX",
            _ => "UTF-8"
        };

        string previousEncoding = "UTF-8";

        static string ConvertTextEncoding(string text, string fromEncoding, string toEncoding)
        {
            if (fromEncoding == toEncoding) return text;
            return fromEncoding switch
            {
                "UTF-8" => ConvertFromUtf8(text, toEncoding),
                "UTF-16" => ConvertFromUtf16(text, toEncoding),
                "HEX" => ConvertFromHex(text, toEncoding),
                _ => text
            };
        }

        static string ConvertFromUtf8(string utf8Text, string toEncoding)
        {
            byte[] utf8Bytes = Encoding.UTF8.GetBytes(utf8Text);
            return toEncoding switch
            {
                "UTF-16" => Encoding.Unicode.GetString(Encoding.Convert(Encoding.UTF8, Encoding.Unicode, utf8Bytes)),
                "HEX" => string.Join(" ", utf8Bytes.Select(b => b.ToString("X2"))),
                _ => utf8Text
            };
        }

        static string ConvertFromUtf16(string utf16Text, string toEncoding)
        {
            byte[] utf16Bytes = Encoding.Unicode.GetBytes(utf16Text);
            return toEncoding switch
            {
                "UTF-8" => Encoding.UTF8.GetString(Encoding.Convert(Encoding.Unicode, Encoding.UTF8, utf16Bytes)),
                "HEX" => string.Join(" ", utf16Bytes.Select(b => b.ToString("X2"))),
                _ => utf16Text
            };
        }

        static string ConvertFromHex(string hexText, string toEncoding)
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
                throw new ArgumentException(L.Text("InvalidHex"));
            }
        }

        async void findTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    // Start searching on Enter and hand the keyboard to the results, so the
                    // following keys (G = show in files, ...) are commands, not search text.
                    // Handling the key keeps it from the inner text box, so a suggestion
                    // chosen from the history is taken here first.
                    findTextBox.AcceptSuggestion();
                    e.Handled = true;
                    filesView.Focus();
                    await StartSearching();
                    break;
                case Key.Up or Key.Down or Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
                    or Key.LeftAlt or Key.RightAlt or Key.Escape:
                    break;
                default:
                    // Stop searching on pressing anything else
                    await Model.Find(null, false, "UTF-8");
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
                    // A filter chosen from the history applies before the search starts
                    filterTextBox.AcceptSuggestion();
                    await StartSearching();
                    break;
            }
        }

        #endregion

        /// <summary>
        /// Show the wait cursor and report an exception of the action
        /// </summary>
        async Task WaitFor(Action a)
        {
            var c = Cursor;
            try
            {
                Cursor = new Cursor(StandardCursorType.Wait);
                // Show the wait cursor first
                await Task.Yield();
                a();
            }
            catch (Exception ex)
            {
                $"WaitFor exception {ex}".Debug();
                await MessageDialog.Show(this, ex.Message, L.Text("Error"), MessageDialogButtons.Ok, MessageDialogIcon.Error);
            }
            Cursor = c;
        }

        /// <summary>
        /// Change the filter to all folders given
        /// </summary>
        async Task FilterFolders(params INode[] nodes) => await WaitFor(() =>
        {
            var folders = Model.FilterFolders(nodes).ToString();
            if (string.IsNullOrEmpty(folders)) nodes.ForEach(n => n.GetFileOrTempPath().Open());
            else filterTextBox.Text = folders;
        });

        /// <summary>
        /// Open node by Shell
        /// </summary>
        async Task Open(INode n, bool asAdmin = false) => await WaitFor(() => n.GetFileOrTempPath().Open(elevated: asAdmin));

        void OpenCalculator()
        {
            if (WebAppWindow.IsBrowserAvailable) WebAppWindow.Open(Apps.Calculator);
        }

        void Calculator_Click(object sender, RoutedEventArgs e) => OpenCalculator();

        /// <summary>Open nodes in a program.</summary>
        async Task Open(string path, INode[] nodes, bool asAdmin = false, LogExplorerLoad load = null)
        {
            if (nodes.Length == 0)
            {
                Model.Status = L.Text("NothingSelected");
                return;
            }
            if (path == null) nodes.ForEach(n => n.GetFileOrTempPath().Open("", elevated: asAdmin)); //Open the files directly
            else if (WebAppWindow.IsWebApp(path.Split('\0')[0]))
            {
                //Built-in HTML app - its own window in this process, never elevated
                if (path == Apps.LogExplorer && !await ConfirmLogExplorerLoad(nodes)) return;
                string[] files = null;
                await WaitFor(() => files = nodes.Where(n => !n.IsDirectory).Select(n => n.GetFileOrTempPath()).ToArray());
                if (files == null || files.Length == 0) Model.Status = L.Text("NothingSelected");
                else WebAppWindow.Open(path.Split('\0')[0], files, load);
            }
            else
            {
                var args = path.Split('\0');
                var workingDir = nodes.FirstOrDefault(n => n.IsDirectory)?.FullName
                    ?? IOPath.GetDirectoryName(nodes.First().FullName);
                var powershell = args[0] == Apps.Powershell;
                // PowerShell doubles a quote inside '...'. Elsewhere a trailing backslash (a
                // drive root "C:\") must be doubled, or \" escapes the closing quote.
                string Quote(string p) => powershell && OperatingSystem.IsWindows()
                    ? $"'{p.Replace("'", "''")}'"
                    : OperatingSystem.IsWindows()
                        ? $"\"{(p.EndsWith('\\') ? p + "\\" : p)}\""
                        : Shell.Quote(p);
                await WaitFor(() => args[0].Open(
                    string.Join(" ", args.Skip(1).Concat(nodes.Select(n => Quote(n.GetFileOrTempPath())))), workingDir, asAdmin));
            }
        }

        /// <summary>
        /// Log explorer holds every file in memory as text, several times its size on disk.
        /// Above these limits it asks before loading - however the files were chosen.
        /// </summary>
        internal const ulong LogExplorerConfirmBytes = 100UL * 1024 * 1024;
        internal const int LogExplorerConfirmFiles = 500;

        internal static bool NeedsLogExplorerConfirmation(INode[] nodes, out int count, out ulong bytes)
        {
            var files = nodes.Where(n => !n.IsDirectory).ToArray();
            count = files.Length;
            bytes = files.Aggregate(0UL, (sum, n) => sum + n.Size);
            return bytes > LogExplorerConfirmBytes || count > LogExplorerConfirmFiles;
        }

        async Task<bool> ConfirmLogExplorerLoad(INode[] nodes)
            => !NeedsLogExplorerConfirmation(nodes, out var count, out var bytes) ||
               await MessageDialog.Show(this, L.Format("LogExplorerLarge", count, TransferProgressWindow.FormatBytes((long)Math.Min(bytes, long.MaxValue))),
                   Title, MessageDialogButtons.YesNo, MessageDialogIcon.Warning) == MessageDialogResult.Yes;

        /// <summary>
        /// Select the shown files containing the searched text (green rows) - rows beyond the
        /// result window cannot be selected
        /// </summary>
        void SelectFound() => SelectNodes(Items.Where(n => !n.IsDirectory && Model.FoundIn(n) == true));

        /// <summary>
        /// Open the files containing the searched text in one Log explorer window, appended and
        /// filtered to the lines with the text. A HEX search has no text to filter by.
        /// </summary>
        async Task ShowInFiles()
        {
            var found = Model.FoundFiles();
            var query = Model.LastFind;
            if (found.Length == 0 || query == null)
            {
                Model.Status = L.Text("NoFoundFiles");
                return;
            }
            var load = new LogExplorerLoad(query.Encoding == "HEX" ? null : query.Text, !query.CaseInsensitive);
            await Open(Apps.LogExplorer, Model.ToTextNodes(found).ToArray(), load: load);
        }

        async void ShowInFiles_Click(object sender, RoutedEventArgs e) => await ShowInFiles();

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

        async void OpenHelp()
        {
            var helpFile = HelpFilePath;
            if (!File.Exists(helpFile))
            {
                await MessageDialog.Show(this, L.Text("HelpNotFound"), L.Text("Help"),
                    MessageDialogButtons.Ok, MessageDialogIcon.Information);
                return;
            }

            try
            {
                new HelpWindow(helpFile).Show(this);
            }
            catch (Exception ex)
            {
                await MessageDialog.Show(this, $"{L.Text("HelpFailed")}: {ex.Message}", L.Text("Help"),
                    MessageDialogButtons.Ok, MessageDialogIcon.Error);
            }
        }

        /// <summary>
        /// Set clipboard text
        /// </summary>
        async Task SetClipboardText(string text)
        {
            try
            {
                await FileClipboard.SetText(this, text);
                Model.Status = L.Format("CopiedToClipboard", text);
            }
            catch (Exception ex)
            {
                Model.Status = L.Format("ClipboardCopyFailed", ex.Message);
            }
        }

        void InvertSelection()
        {
            var original = new HashSet<object>(filesView.SelectedItems.Cast<object>(), ReferenceEqualityComparer.Instance);
            SelectNodes(Items.Where(n => !original.Contains(n)));
        }

        async Task Delete(IEnumerable<INode> nodes, bool confirm = true, bool permanently = false)
        {
            var na = nodes.ToArray();
            if (na.Length == 0)
                return;
            //Items inside an archive can be neither recycled nor deleted. Asking "Move to the
            //Recycle Bin?" first and failing afterwards read as if the delete had been tried.
            var archiveItems = na.Count(n => n is ZipNode);
            if (archiveItems > 0)
            {
                na = na.Where(n => n is not ZipNode).ToArray();
                Model.Status = L.Format("ArchiveItemsNotDeleted", archiveItems);
                if (na.Length == 0)
                    return;
            }
            if (fileOperationInProgress)
            {
                Model.Status = L.Text("OperationRunning");
                return;
            }
            var itemDescription = FileOperationText.DescribeItems(
                na.Select(n => n.FullName).ToArray());
            if (confirm &&
                await MessageDialog.Show(this,
                    $"{L.Text(permanently ? "ConfirmPermanentDelete" : "ConfirmRecycle")}\n\n{itemDescription}",
                    L.Text(permanently ? "PermanentDelete" : "Recycle"),
                    MessageDialogButtons.YesNo, MessageDialogIcon.Question, MessageDialogResult.No) != MessageDialogResult.Yes)
                return;

            //Resource key of "Moving to the Recycle Bin: {0}" - the status, then each item
            var verb = permanently ? "DeletingPermanently" : "Recycling";
            Model.Status = L.Format(verb, itemDescription);
            var itemWork = na.Select(n => SizeAsWork(n.Size)).ToArray();
            var totalWork = itemWork.Aggregate(0L, SaturatingAdd);
            var observation = new DeleteProgressObservation(L.Format(verb, itemDescription));
            var progress = new TransferProgressWindow(
                L.Text(permanently ? "PermanentDelete" : "Recycle"))
            {
                OwnerWindow = this
            };
            progress.SetCancellationAvailable(
                na.Length > 1,
                L.Text("DeleteNotCancellable"));
            progress.Begin(totalWork);
            progress.Observe(
                observation.Sample,
                completedWork: 0,
                maximumWork: totalWork,
                currentItem: observation.CurrentItem,
                sampleItem: () => observation.CurrentItem);
            progress.ShowAfter(ProgressDialogDelay);
            fileOperationInProgress = true;

            //Recursive and shell deletes can take seconds and can themselves produce a large
            //watcher storm. Keep the filesystem work (and on Windows its COM dialogs) on a
            //dedicated thread instead of blocking the UI thread for the entire operation.
            (List<string> Errors, int Completed, bool Cancelled) outcome;
            try
            {
                outcome = await RunDeleteThread(() =>
                {
                    var result = new List<string>();
                    var completed = 0;
                    long completedWork = 0;
                    var cancelled = false;
                    for (var index = 0; index < na.Length; index++)
                    {
                        if (progress.Token.IsCancellationRequested)
                        {
                            cancelled = true;
                            break;
                        }

                        var n = na[index];
                        observation.Begin(n, completedWork, itemWork[index], verb);
                        try
                        {
                            if (permanently)
                                n.Delete();
                            else
                                n.Recycle();
                            Models.Extensions.EchoDeleted(n);
                        }
                        catch (Exception ex)
                        {
                            result.Add($"{n.FullName}: {ex.Message}");
                            //A recursive delete is not transactional: it may remove many
                            //children before one protected/locked path fails. Persist the exact
                            //error and post-failure state so the next report distinguishes ACL,
                            //read-only/locked files and a partial delete.
                            var state = $"fileExists={File.Exists(n.FullName)} "
                                + $"directoryExists={Directory.Exists(n.FullName)}";
                            StorageMaintenance.AppendLog("log.txt",
                                $"{DateTime.Now} {Thread.CurrentThread.ManagedThreadId} "
                                + $"Delete failed permanently={permanently} path={n.FullName} "
                                + $"type={ex.GetType().FullName} hresult=0x{ex.HResult:X8} "
                                + $"{state}: {ex}{Environment.NewLine}");
                        }
                        completed++;
                        completedWork = SaturatingAdd(completedWork, itemWork[index]);
                        observation.Complete(completedWork);
                    }
                    return (result, completed, cancelled);
                });
            }
            finally
            {
                progress.Complete();
                fileOperationInProgress = false;
            }

            Model.Status = (outcome.Cancelled
                ? L.Format("CancelledAfter", outcome.Completed, na.Length)
                : outcome.Errors.Count == 0
                    ? L.Format(permanently ? "DeletedPermanently" : "Recycled", itemDescription)
                    : CompletedWithErrors(outcome.Errors))
                + (archiveItems > 0 ? " · " + L.Format("ArchiveItemsNotDeleted", archiveItems) : "");
        }

        sealed class DeleteProgressObservation
        {
            readonly object gate = new();
            long completedWork;
            long operationWork;
            string path;
            string parentPath;
            ulong pathStartSize;
            ulong parentStartSize;
            string currentItem;

            public DeleteProgressObservation(string initialItem) => currentItem = initialItem;

            public string CurrentItem
            {
                get
                {
                    lock (gate)
                        return currentItem;
                }
            }

            public void Begin(INode node, long completed, long work, string verb)
            {
                var itemPath = node.FullName;
                var itemParent = IOPath.GetDirectoryName(itemPath);
                var itemParentSize = SearchModel.FindByPath(itemParent)?.Size ?? 0;
                lock (gate)
                {
                    completedWork = completed;
                    operationWork = work;
                    path = itemPath;
                    parentPath = itemParent;
                    pathStartSize = node.Size;
                    parentStartSize = itemParentSize;
                    currentItem = L.Format(verb, itemPath);
                }
            }

            public void Complete(long completed)
            {
                lock (gate)
                {
                    completedWork = completed;
                    operationWork = 0;
                    path = null;
                    parentPath = null;
                }
            }

            public long Sample()
            {
                long completed;
                long maximum;
                string itemPath;
                string itemParent;
                ulong itemStartSize;
                ulong itemParentStartSize;
                lock (gate)
                {
                    completed = completedWork;
                    maximum = operationWork;
                    itemPath = path;
                    itemParent = parentPath;
                    itemStartSize = pathStartSize;
                    itemParentStartSize = parentStartSize;
                }

                if (itemPath == null || maximum == 0)
                    return completed;
                var itemSize = SearchModel.FindByPath(itemPath)?.Size ?? 0;
                var parentSize = SearchModel.FindByPath(itemParent)?.Size ?? itemParentStartSize;
                var observed = Math.Max(
                    PositiveSizeDecrease(itemStartSize, itemSize),
                    PositiveSizeDecrease(itemParentStartSize, parentSize));
                return SaturatingAdd(completed, Math.Min(maximum, observed));
            }
        }

        /// <summary>Run the delete on its own thread (STA on Windows - the shell's recycle dialogs need it)</summary>
        static Task<T> RunDeleteThread<T>(Func<T> action)
        {
            var completion = new TaskCompletionSource<T>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { completion.TrySetResult(action()); }
                catch (Exception ex) { completion.TrySetException(ex); }
            })
            {
                IsBackground = true,
                Name = "File delete"
            };
            if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return completion.Task;
        }

        async Task OpenIn(IEnumerable<INode> nodes, IEnumerable<Key> arg, bool asAdmin = false)
        {
            if (!nodes.Any())
            {
                Model.Status = L.Text("NothingSelected");
                return;
            }
            var application = FileApplications.Find(arg, nodes.ToArray());
            if (application != null)
            {
                // A comparison needs its pair; any other explicitly chosen application opens
                // the selection even when its hint is hidden for these file types.
                if (application.Compare && !application.Supports(nodes.ToArray())) Model.Status = L.Text("NoDiffTool");
                else await OpenApplication(application, nodes.ToArray(), asAdmin);
                return;
            }
            // Releasing a category key alone does not open the system default application.
            if (FileApplications.Groups.Any(g => g.Key == arg.FirstOrDefault())) return;
            var a = arg.FirstOrDefault();
            if (a == Key.B)
            {
                //File browser: one window per node. Reveal falls back to the nearest
                //existing folder for a node that is no longer on disk.
                var reveal = nodes.ToArray();
                await WaitFor(() => reveal.ForEach(n => n.GetFileOrTempPath().Reveal(asAdmin)));
                return;
            }
            if (a == Key.S && !OperatingSystem.IsWindows())
            {
                //A terminal in the folder of each selected item
                var folders = nodes.Select(n => n.IsDirectory ? n.FullName : IOPath.GetDirectoryName(n.FullName))
                    .Where(f => !string.IsNullOrEmpty(f)).Distinct(PathText.Comparer).ToArray();
                await WaitFor(() => folders.ForEach(f => Shell.OpenTerminal(f)));
                return;
            }
            if (a == Key.T) nodes = Model.ToTextNodes(nodes.ToArray());
            await Open(a switch
            {
                Key.T => Apps.TextViever,
                Key.W => Apps.WebBrowser ?? Apps.TextViever,
                Key.S => arg.Last() == Key.P ? Apps.Powershell + "\0-NoExit -Command &" : Apps.Cmd + "\0/k",
                Key.G => Apps.GhostScript + Resolution(),
                Key.P => Apps.GhostPcl + Resolution(),
                Key.X => Apps.GhostXps + Resolution(),
                Key.R => Apps.ViewerFor(nodes.First().FullName).Split("-r")[0] + Resolution(),
                _ => null // Open in default system app
            }, nodes.ToArray(), asAdmin);
            string Resolution()
            {
                var a = arg.Skip(1).ReadTill(); // keys argument after first one
                if (a.Length == 0) return "\0-r72"; // Screen resolution by default
                return "\0-r" + a;
            };
        }

        async Task OpenApplication(FileApplications.Application application, INode[] nodes, bool asAdmin = false)
        {
            var executable = application.Executable;
            if (string.IsNullOrWhiteSpace(executable))
            {
                Model.Status = L.Format("ApplicationNotFound", application.Name);
                return;
            }
            // Diff tools compare text: a workbook is compared as the text of its sheets,
            // one file per workbook - the tool takes exactly one file per side.
            if (application.Compare) nodes = Model.ToComparisonNodes(nodes).ToArray();
            var command = application.CommandFor(executable);
            if (application.Separate)
            {
                foreach (var node in nodes) await Open(command, new[] { node }, asAdmin);
            }
            else await Open(command, nodes, asAdmin);
        }

        async Task OpenDiff(IEnumerable<INode> nodes)
        {
            var selection = nodes.ToArray();
            var application = FileApplications.FindDiff(selection);
            if (application != null) await OpenApplication(application, selection);
            else Model.Status = L.Text("NoDiffTool");
        }

        static string AttributeText(IEnumerable<INode> nodes, Key what) => string.Join(Environment.NewLine, what switch
        {
            Key.V => nodes.Select(n => FileVersionInfo.GetVersionInfo(n.FullName).ToString()),
            Key.T => nodes.Select(n => File.GetCreationTime(n.FullName).ToString()),
            Key.W => nodes.Select(n => File.GetLastWriteTime(n.FullName).ToString()),
            Key.A => nodes.Select(n => File.GetLastAccessTime(n.FullName).ToString()),
            _ => Enumerable.Empty<string>()
        });

        async Task Copy(IEnumerable<INode> nodes, IEnumerable<Key> arg, bool cut = false)
        {
            var nodeArray = nodes.ToArray();
            var e = arg.GetEnumerator();
            if (e.MoveNext())
            {
                // What to copy
                if (e.Current is not (Key.V or Key.T or Key.W or Key.A)) return; // Node defined
                await SetClipboardText(AttributeText(nodeArray, e.Current));
                return;
            }

            if (nodeArray.Length == 0)
            {
                Model.Status = L.Text("NothingSelected");
                return;
            }
            var cutDowngraded = cut && nodeArray.Any(n => n is ZipNode);
            if (cutDowngraded) cut = false;
            string[] files;
            try
            {
                files = nodeArray.Select(n => n.GetFileOrTempPath(preserveAfterExit: true))
                    .Where(p => File.Exists(p) || Directory.Exists(p))
                    .Distinct(PathText.Comparer)
                    .ToArray();
                if (files.Length == 0)
                {
                    Model.Status = L.Text("NothingToCopy");
                    return;
                }
                await FileClipboard.SetFiles(this, files, cut);
            }
            catch (Exception ex)
            {
                Model.Status = L.Format("ClipboardCopyFailed", ex.Message);
                return;
            }
            //The explanation is the final status - set earlier, the "copied" text replaced it at once
            Model.Status = cutDowngraded
                ? L.Text("ArchiveCutCopied")
                : L.Format(cut ? "CutToClipboard" : "CopiedToClipboard", FileOperationText.DescribeItems(files));
        }

        async Task CopyAppend(IEnumerable<INode> nodes, IEnumerable<Key> arg)
        {
            var e = arg.GetEnumerator();
            if (e.MoveNext())
            {
                // What to copy - append mode for text attributes
                if (e.Current is not (Key.V or Key.T or Key.W or Key.A)) return; // Node defined
                string existingText = "";
                try { existingText = await FileClipboard.GetText(this) ?? ""; } catch { }
                var clip = AttributeText(nodes, e.Current);
                if (!string.IsNullOrEmpty(existingText))
                    clip = existingText + Environment.NewLine + clip;
                await SetClipboardText(clip);
                return;
            }

            // Append mode for file drop list
            var files = nodes.Select(n => n.GetFileOrTempPath(preserveAfterExit: true))
                .Where(p => File.Exists(p) || Directory.Exists(p))
                .ToArray();
            var existingFiles = new List<string>();
            try { existingFiles.AddRange(await FileClipboard.GetFiles(this)); }
            catch { }
            existingFiles.AddRange(files);
            try
            {
                await FileClipboard.SetFiles(this, existingFiles.Distinct(PathText.Comparer).ToArray(), cut: false);
                Model.Status = L.Format("AppendedToClipboard", FileOperationText.DescribeItems(files));
            }
            catch (Exception ex) { Model.Status = L.Format("ClipboardCopyFailed", ex.Message); }
        }

        async Task Rename(IEnumerable<INode> nodes, IEnumerable<Key> arg)
        {
            var nodeArray = nodes.ToArray();
            var arguments = arg.ToArray();
            var directRename = arguments.Length == 0 || arguments.All(x => x == Key.None);
            if (directRename)
            {
                if (nodeArray.Length == 1)
                    BeginInlineRename(nodeArray[0]);
                else if (nodeArray.Length > 1)
                    Model.Status = L.Text("RenameOneOnly");
                return;
            }
            nodes = nodeArray;
            arg = arguments;
            string name = "", ext = "", subStr = "";
            int index = 0;
            var e = arg.GetEnumerator();
            bool overwrite = e.MoveNext() && e.Current == Key.O, replace = false, removeExt = false;
            if (overwrite) e.MoveNext();
            switch (e.Current)
            {
                case Key.V: // File name from clipboard
                    name = await FileClipboard.GetText(this) ?? "";
                    ext = IOPath.GetExtension(name);
                    name = IOPath.GetFileNameWithoutExtension(name);
                    break;
                case Key.N: // Add extension
                    name = e.ReadTill();
                    break;
                case Key.E: // Change extension, E <Del> removes it
                    ext = e.ReadTill(Key.Delete);
                    removeExt = ext.Length == 0 && e.Current == Key.Delete;
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
                        // Change creation, last write, or last access time
                        var which = e.Current;
                        DateTime time = DateTime.Now;
                        if (e.MoveNext())
                        {
                            switch (e.Current)
                            {
                                case Key.V: // Time from clipboard
                                    var text = await FileClipboard.GetText(this);
                                    if (!L.TryParseDate(text?.Trim(), out time))
                                    {
                                        await MessageDialog.Show(this, L.Format("InvalidClipboardDate", text), L.Text("Error"),
                                            MessageDialogButtons.Ok, MessageDialogIcon.Error);
                                        return;
                                    }
                                    break;
                                case Key.C: // Current time
                                    break;
                            }
                        }
                        // Set the time on all nodes. Failures used to be swallowed silently,
                        // so a write-protected item looked like it had been changed.
                        var timeErrors = new List<string>();
                        foreach (var n in nodes)
                            try
                            {
                                SetNodeTime(n.FullName, which, time);
                            }
                            catch (Exception ex)
                            {
                                $"set time on '{n.FullName}' failed {ex}".Debug();
                                timeErrors.Add(ex.Message);
                            }
                        Model.Status = timeErrors.Count > 0
                            ? CompletedWithErrors(timeErrors)
                            : L.Text("TimeChanged");
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
                    dest = IOPath.GetFileNameWithoutExtension(x.Name).Replace(subStr, name, StringComparison.OrdinalIgnoreCase);
                    dest = IOPath.Combine(IOPath.GetDirectoryName(x.FullName), dest + IOPath.GetExtension(x.Name));
                }
                else if (string.IsNullOrEmpty(dest))
                {
                    if (removeExt) dest = IOPath.ChangeExtension(x.FullName, null);
                    else if (!string.IsNullOrEmpty(ext))
                    {
                        if (index == 0) dest = IOPath.ChangeExtension(x.FullName, "." + ext);
                        else dest = x.FullName + "." + ext;
                    }
                    else if (!string.IsNullOrEmpty(subStr))
                    {
                        dest = IOPath.GetFileNameWithoutExtension(x.Name);
                        dest = dest.Insert(Math.Min(dest.Length, Math.Max(0, index >= 0 ? index : dest.Length + 1 + index)), subStr);
                        dest = IOPath.Combine(IOPath.GetDirectoryName(x.FullName), dest + IOPath.GetExtension(x.Name));
                    }
                    else
                    {
                        Model.Status = L.Text("NoRenameValue");
                        return;
                    }
                }
                else if (string.IsNullOrEmpty(ext)) dest += IOPath.GetExtension(x.Name); // Replace only the name
                else dest += "." + ext; // Add requested extension

                // Rename the file
                if (IOPath.GetFileName(dest) == dest) dest = IOPath.Combine(IOPath.GetDirectoryName(x.FullName), dest);
                if (x.FullName != dest) errors.AddRange(x.FullName.UniversalCopyOrMove(dest, overwrite, true));
            }
            Model.Status = errors.Count > 0 ? CompletedWithErrors(errors) : L.Text("RenameDone");
        }

        /// <summary>
        /// Set one timestamp, falling back to the elevated broker when Windows denies the
        /// write. Directories need the Directory.* overloads - the File.* ones throw
        /// FileNotFoundException when handed a directory path.
        /// </summary>
        static void SetNodeTime(string path, Key which, DateTime time)
        {
            var directory = Directory.Exists(path);
            Action<string, DateTime> set = (which, directory) switch
            {
                (Key.C, false) => File.SetCreationTime,
                (Key.C, true) => Directory.SetCreationTime,
                (Key.W, false) => File.SetLastWriteTime,
                (Key.W, true) => Directory.SetLastWriteTime,
                (_, false) => File.SetLastAccessTime,
                (_, true) => Directory.SetLastAccessTime
            };
            try
            {
                set(path, time);
            }
            catch (UnauthorizedAccessException)
            {
                if (!OperatingSystem.IsWindows() || !Broker.EnsureStarted(Broker.ElevationWait)) throw;
                Broker.SetTimesElevated(path,
                    which == Key.C ? time : null,
                    which == Key.W ? time : null,
                    which == Key.A ? time : null);
            }
        }

        #region Inline rename

        void BeginInlineRename(INode node)
        {
            if (node == null || node is ZipNode || (!File.Exists(node.FullName) && !Directory.Exists(node.FullName)))
            {
                Model.Status = L.Text("RenameOnlyPhysical");
                return;
            }
            FinishInlineRename(commit: false);
            filesView.ScrollIntoView(node, null);
            //The row may be realized only by the layout pass after the scroll
            Dispatcher.UIThread.Post(() => StartInlineRename(node), DispatcherPriority.Loaded);
        }

        void StartInlineRename(INode node)
        {
            if (RowFor(node) is not DataGridRow row)
            {
                Model.Status = L.Text("ItemNotVisible");
                return;
            }
            var editor = row.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "InlineNameEditor");
            var display = row.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "NameDisplay");
            if (editor == null || display == null) return;

            inlineRenameNode = node;
            inlineRenameEditor = editor;
            inlineRenameDisplay = display;
            editor.Text = node.Name;
            editor.ClearValue(TextBox.BorderBrushProperty);
            ToolTip.SetTip(editor, null);
            display.IsVisible = false;
            editor.IsVisible = true;
            editor.Focus();
            editor.SelectionStart = 0;
            editor.SelectionEnd = IOPath.GetFileNameWithoutExtension(node.Name).Length;
        }

        void InlineNameEditor_KeyDown(object sender, KeyEventArgs e)
        {
            if (inlineRenameEditor == null || !IsInlineEditorEvent(e) || e.Source is not Visual v
                || !(v == inlineRenameEditor || inlineRenameEditor.IsVisualAncestorOf(v))) return;
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

        void InlineNameEditor_LostFocus(object sender, RoutedEventArgs e)
        {
            if (finishingInlineRename || inlineRenameEditor == null) return;
            if (e.Source is not Visual v || !(v == inlineRenameEditor || inlineRenameEditor.IsVisualAncestorOf(v))) return;
            //Focus moving inside the editor (its context menu) is not the end of the edit
            Dispatcher.UIThread.Post(() =>
            {
                if (inlineRenameEditor != null && !inlineRenameEditor.IsKeyboardFocusWithin)
                    FinishInlineRename(commit: true);
            });
        }

        bool FinishInlineRename(bool commit)
        {
            if (finishingInlineRename || inlineRenameEditor == null || inlineRenameNode == null) return true;
            var editor = inlineRenameEditor;
            var node = inlineRenameNode;
            if (commit)
            {
                var newName = editor.Text ?? "";
                var invalid = string.IsNullOrWhiteSpace(newName) || newName is "." or ".." ||
                    newName.IndexOfAny(IOPath.GetInvalidFileNameChars()) >= 0 ||
                    !string.Equals(IOPath.GetFileName(newName), newName, StringComparison.Ordinal);
                if (invalid)
                    return RejectInlineRename(L.Text("InvalidName"));
                var destination = IOPath.Combine(IOPath.GetDirectoryName(node.FullName), newName);
                if (!string.Equals(destination, node.FullName, PathText.Comparison) &&
                    (File.Exists(destination) || Directory.Exists(destination)))
                    return RejectInlineRename(L.Format("AlreadyExists", newName));
                if (!string.Equals(destination, node.FullName, StringComparison.Ordinal))
                {
                    var errors = node.FullName.UniversalCopyOrMove(destination, overwrite: false, move: true);
                    if (errors.Count > 0)
                        return RejectInlineRename(string.Join(", ", errors));
                    Model.Status = L.Format("RenamedTo", node.Name, newName);
                }
            }

            finishingInlineRename = true;
            try
            {
                editor.IsVisible = false;
                if (inlineRenameDisplay != null) inlineRenameDisplay.IsVisible = true;
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
            ToolTip.SetTip(inlineRenameEditor, message);
            Dispatcher.UIThread.Post(() => inlineRenameEditor?.Focus(), DispatcherPriority.Input);
            return false;
        }

        #endregion

        #region New folders

        /// <summary>
        /// Every key of the new-folder argument belongs to the typed name. Unlike F2, this
        /// command tree declares no Overwrite step, so reading a leading O as that flag made
        /// any name starting with "o" (e.g. "output") RECURSIVELY DELETE the existing folder
        /// of that name before recreating it.
        /// </summary>
        internal static string NewFolderName(IEnumerable<Key> arg) => arg.ReadTill();

        void NewFolders(IEnumerable<INode> nodes, IEnumerable<Key> arg)
        {
            var targetNodes = nodes.ToArray();
            var name = NewFolderName(arg);
            if (string.IsNullOrWhiteSpace(name))
            {
                ShowNewFolderBar(targetNodes, overwrite: false);
                return;
            }
            CreateFolders(targetNodes, name, overwrite: false);
        }

        void ShowNewFolderBar(INode[] nodes, bool overwrite)
        {
            if (nodes.Length == 0)
            {
                Model.Status = L.Text("SelectFolderDestination");
                return;
            }
            FinishInlineRename(commit: true);
            CancelPinnedFilterEdit();
            newFolderTargets = nodes;
            newFolderOverwrite = overwrite;
            inlineActionLabel.Text = nodes.Length == 1
                ? L.Format("CreateFolderIn", nodes[0].FullName.Directory())
                : L.Format("CreateFolderInMany", nodes.Length);
            inlineActionTextBox.Text = "";
            inlineActionTextBox.ClearValue(TextBox.BorderBrushProperty);
            ToolTip.SetTip(inlineActionTextBox, null);
            inlineActionBar.IsVisible = true;
            Dispatcher.UIThread.Post(() => inlineActionTextBox.Focus(), DispatcherPriority.Loaded);
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
            var name = inlineActionTextBox.Text ?? "";
            if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
                name.IndexOfAny(IOPath.GetInvalidFileNameChars()) >= 0 ||
                !string.Equals(IOPath.GetFileName(name), name, StringComparison.Ordinal))
            {
                var error = L.Text("InvalidName");
                inlineActionTextBox.BorderBrush = Brushes.IndianRed;
                ToolTip.SetTip(inlineActionTextBox, error);
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
            inlineActionBar.IsVisible = false;
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
                    var dir = IOPath.Combine(x.IsDirectory ? x.FullName : x.FullName.Directory(), name);
                    if (overwrite) dir.DeletePathIfExists();
                    Directory.CreateDirectory(dir);
                }
                catch (Exception ex) { errors.Add($"{x.FullName}: {ex.Message}"); }
            Model.Status = errors.Count == 0
                ? L.Format("CreatedItem", name)
                : CompletedWithErrors(errors);
        }

        #endregion

        #region Paste and transfers

        async Task Paste(IEnumerable<INode> nodes, IEnumerable<Key> arg)
        {
            string[] paths;
            bool clipboardMove;
            try
            {
                paths = await FileClipboard.GetFiles(this);
                clipboardMove = await FileClipboard.RequestsMove(this);
            }
            catch (Exception ex)
            {
                Model.Status = L.Format("ClipboardReadFailed", ex.Message);
                return;
            }
            if (paths.Length == 0)
            {
                Model.Status = L.Text("NoClipboardFiles");
                return;
            }
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
                await ConsumeCutClipboard();
            else if (completed && clipboardMove)
                await PreserveClipboardAsCopy(paths);
        }

        async Task PasteWithDialog(IEnumerable<INode> nodes)
        {
            string[] paths;
            bool clipboardMove;
            try
            {
                paths = await FileClipboard.GetFiles(this);
                clipboardMove = await FileClipboard.RequestsMove(this);
            }
            catch (Exception ex)
            {
                Model.Status = L.Format("ClipboardReadFailed", ex.Message);
                return;
            }
            var destinations = PasteDestinations(nodes);
            if (paths.Length == 0)
            {
                Model.Status = L.Text("NoClipboardFiles");
                return;
            }
            if (destinations.Length == 0)
            {
                Model.Status = L.Text("NoPasteDestination");
                return;
            }
            var defaultAction = clipboardMove
                ? FileTransferAction.Move
                : FileTransferAction.Copy;
            if (await ChooseTransferAction(paths, destinations.Length, defaultAction) is not { } action)
                return;
            var completed = await TransferPaths(paths, destinations, action);
            if (completed && action == FileTransferAction.Move)
                await ConsumeCutClipboard();
            else if (completed && clipboardMove)
                await PreserveClipboardAsCopy(paths);
        }

        static string[] PasteDestinations(IEnumerable<INode> nodes) =>
            nodes.Select(n => n.FullName.Directory())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(PathText.Comparer)
                .ToArray();

        async Task ConsumeCutClipboard()
        {
            try { await FileClipboard.Clear(this); } catch { }
        }

        async Task PreserveClipboardAsCopy(string[] paths)
        {
            try { await FileClipboard.SetFiles(this, paths, cut: false); } catch { }
        }

        async Task<bool> TransferPaths(
            string[] paths,
            string[] destinations,
            FileTransferAction action,
            FileCollisionAction? collisionForAll = null)
        {
            if (paths.Length == 0 || destinations.Length == 0)
            {
                Model.Status = L.Text("NothingToTransfer");
                return false;
            }
            if (fileOperationInProgress)
            {
                Model.Status = L.Text("OperationRunning");
                return false;
            }

            var itemDescription = FileOperationText.DescribeItems(paths);
            var actionName = ActionName(action);
            //"3 items to 2 folder(s)" - the prompt of the action dialog without its trailing colon
            var transferDescription = L.Format("ItemToFolders", itemDescription, destinations.Length)
                .TrimEnd(TrailingColon);
            Model.Status = $"{actionName}: {transferDescription}";
            var total = paths.Length * destinations.Length;
            var sourceNodes = paths.Select(SearchModel.FindByPath).ToArray();
            var sourceWork = paths.Select((path, index) =>
                CachedTransferWork(path, sourceNodes[index])).ToArray();
            var workPerDestination = sourceWork.Aggregate(0L, SaturatingAdd);
            var progress = new TransferProgressWindow(actionName) { OwnerWindow = this };
            var errors = new List<string>();
            var completedCount = 0;
            long completedWork = 0;
            var skipped = 0;
            var cancelled = false;
            progress.Begin(SaturatingMultiply(workPerDestination, destinations.Length));
            progress.ShowAfter(ProgressDialogDelay);
            fileOperationInProgress = true;
            try
            {
                for (var targetIndex = 0; targetIndex < destinations.Length; targetIndex++)
                for (var sourceIndex = 0; sourceIndex < paths.Length; sourceIndex++)
                {
                    var file = paths[sourceIndex];
                    var operationWork = sourceWork[sourceIndex];
                    var sourceIsDirectory = sourceNodes[sourceIndex]?.IsDirectory
                        ?? Directory.Exists(file);
                    if (cancelled || progress.Token.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }
                    var destination = IOPath.Combine(destinations[targetIndex],
                        IOPath.GetFileName(file.TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar)));
                    if (sourceIsDirectory && IsDescendant(destination, file))
                    {
                        errors.Add(L.Format("TransferIntoItself", file));
                        completedCount++;
                        completedWork = SaturatingAdd(completedWork, operationWork);
                        progress.Report(completedWork, file);
                        continue;
                    }

                    var overwrite = false;
                    var destinationExisted = File.Exists(destination) || Directory.Exists(destination);
                    if (destinationExisted)
                    {
                        var collision = collisionForAll;
                        if (collision == null)
                        {
                            // Fast transfers keep the delayed progress window hidden; the main
                            // window owns the question until progress has actually appeared.
                            var dialog = new FileCollisionDialog(destination);
                            bool dialogResult;
                            progress.PauseTiming();
                            try
                            {
                                dialogResult = await dialog.ShowDialog<bool>(CollisionDialogOwner(progress, this));
                            }
                            finally
                            {
                                progress.ResumeTiming();
                            }
                            if (!dialogResult || dialog.Action == FileCollisionAction.Cancel)
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
                            completedWork = SaturatingAdd(completedWork, operationWork);
                            progress.Report(completedWork, L.Format("SkippedItem", destination));
                            continue;
                        }
                        if (collision == FileCollisionAction.Rename)
                        {
                            destination = UniqueDestination(destination, sourceIsDirectory);
                            destinationExisted = false;
                        }
                        else
                            overwrite = collision == FileCollisionAction.Overwrite;
                    }

                    // One source cannot be moved to several destinations. Match the
                    // previous cut/paste behavior: copy to earlier targets, move last.
                    var targetAction = action == FileTransferAction.Move && targetIndex < destinations.Length - 1
                        ? FileTransferAction.Copy
                        : action;
                    var progressItem = $"{ActionName(targetAction)}: {file} → {destination}";
                    var parentPath = destinations[targetIndex];
                    var parentStartSize = SearchModel.FindByPath(parentPath)?.Size ?? 0;
                    var destinationStartSize = SearchModel.FindByPath(destination)?.Size ?? 0;
                    var destinationForSample = destination;
                    var existedForSample = destinationExisted;
                    progress.Observe(
                        () => SampleIndexedTransferWork(
                            parentPath,
                            parentStartSize,
                            destinationForSample,
                            destinationStartSize,
                            existedForSample),
                        completedWork,
                        operationWork,
                        progressItem);
                    var operationErrors = await Task.Run(() =>
                    {
                        progress.Token.ThrowIfCancellationRequested();
                        if (targetAction == FileTransferAction.SymbolicLink)
                            return file.Softlink(destinationForSample, overwrite);
                        if (targetAction == FileTransferAction.HardLink)
                            return file.Hardlink(destinationForSample, overwrite);
                        return file.UniversalCopyOrMove(destinationForSample, overwrite,
                            move: targetAction == FileTransferAction.Move,
                            cancellationToken: progress.Token);
                    });
                    errors.AddRange(operationErrors);
                    completedCount++;
                    completedWork = SaturatingAdd(completedWork, operationWork);
                    progress.Report(completedWork, file);
                }
            }
            catch (OperationCanceledException) when (progress.Token.IsCancellationRequested)
            {
                cancelled = true;
            }
            finally
            {
                progress.Complete();
                fileOperationInProgress = false;
            }

            Model.Status = $"{actionName}: " + (cancelled
                ? L.Format("CancelledAfter", completedCount, total)
                : errors.Count > 0
                    ? CompletedWithErrors(errors)
                    : skipped > 0
                        ? L.Format("TransferSkipped", skipped)
                        : L.Format("TransferDone", transferDescription));
            return !cancelled && errors.Count == 0 && skipped == 0;
        }

        /// <summary>
        /// Action name for status and progress texts, worded as in the action dialog - the enum
        /// identifiers read "SymbolicLink" and "HardLink", and are the keys of those captions
        /// </summary>
        internal static string ActionName(FileTransferAction action, CultureInfo culture = null)
            => L.Text(action.ToString(), culture ?? CultureInfo.CurrentUICulture);

        /// <summary>The status of an operation that left some items undone</summary>
        static string CompletedWithErrors(IReadOnlyCollection<string> errors)
            => L.Format("CompletedWithErrors", errors.Count, string.Join(", ", errors));

        //Ends the localized "… to 2 folder(s):" prompt (French sets a space before the colon)
        static readonly char[] TrailingColon = { ':', '：', ' ', ' ' };

        static long CachedTransferWork(string path, INode indexed)
        {
            if (indexed != null)
                return SizeAsWork(indexed.Size);
            try
            {
                var file = new FileInfo(path);
                if (file.Exists)
                    return SizeAsWork((ulong)Math.Max(0, file.Length));
            }
            catch
            {
                // A missing/stale index entry must not trigger a recursive fallback scan.
            }
            return 1;
        }

        static long SampleIndexedTransferWork(
            string parentPath,
            ulong parentStartSize,
            string destination,
            ulong destinationStartSize,
            bool destinationExisted)
        {
            var parentSize = SearchModel.FindByPath(parentPath)?.Size ?? parentStartSize;
            var destinationSize = SearchModel.FindByPath(destination)?.Size ?? 0;
            var parentGrowth = PositiveSizeGrowth(parentSize, parentStartSize);
            var destinationGrowth = destinationExisted
                ? PositiveSizeGrowth(destinationSize, destinationStartSize)
                : SizeAsWork(destinationSize, emptyIsOne: false);
            return Math.Max(parentGrowth, destinationGrowth);
        }

        internal static Window CollisionDialogOwner(Window progress, Window fallback) =>
            progress?.IsVisible == true ? progress : fallback;

        internal static long PositiveSizeGrowth(ulong current, ulong initial) =>
            current <= initial ? 0 : SizeAsWork(current - initial, emptyIsOne: false);

        internal static long PositiveSizeDecrease(ulong initial, ulong current) =>
            current >= initial ? 0 : SizeAsWork(initial - current, emptyIsOne: false);

        internal static long SizeAsWork(ulong size, bool emptyIsOne = true) =>
            size == 0
                ? emptyIsOne ? 1 : 0
                : size > long.MaxValue ? long.MaxValue : (long)size;

        static long SaturatingAdd(long left, long right) =>
            left > long.MaxValue - right ? long.MaxValue : left + right;

        static long SaturatingMultiply(long value, int multiplier) =>
            multiplier > 0 && value > long.MaxValue / multiplier
                ? long.MaxValue
                : value * multiplier;

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

        #endregion

        async Task Unzip(IEnumerable<INode> nodes, IEnumerable<Key> arg)
        {
            var e = arg.GetEnumerator();
            bool call7zip = e.MoveNext() && e.Current == Key.NumPad7;

            var extracted = new List<INode>();
            await foreach (var item in Model.UnZip(call7zip, nodes.ToArray()))
                extracted.Add(item);
            SelectNodes(extracted);
        }

        async Task Zip(IEnumerable<INode> nodes, IEnumerable<Key> arg, bool seven = false)
        {
            var selected = nodes.ToArray();
            if (selected.Length == 0)
            {
                Model.Status = L.Text("NothingSelected");
                return;
            }
            var e = arg.GetEnumerator();
            bool call7zip = e.MoveNext() && e.Current == Key.NumPad7;

            Model.Status = L.Format("Zipping", FileOperationText.DescribeItems(
                selected.Select(n => n.FullName).ToArray()));
            INode zip;
            try
            {
                zip = await Model.Zip(call7zip, seven, selected);
            }
            catch (Exception ex)
            {
                Model.Status = L.Format("ZipFailed", ex.Message);
                return;
            }
            if (zip == null)
            {
                Model.Status = L.Format("ZipFailed", L.Text("Unknown"));
                return;
            }
            filesView.SelectedItem = zip;
            Model.Status = L.Format("CreatedItem", zip.FullName);
        }
    }
}
