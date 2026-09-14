using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace search
{
    /// <summary>
    /// Top-level window hosting one of the single-file HTML apps copied to WebApps next to the
    /// exe (from the external/singleHtmlApps submodule, https://github.com/KoudelkaB/singleHtmlApps) in WebView2.
    /// Pages are served from a virtual https host mapped to the local folder, so they need no
    /// network and keep their localStorage (Log explorer profiles) between runs. The selected
    /// files reach the page as FileSystemFileHandles and are handed to the page's own file input
    /// or drop handler - the HTML files stay unmodified copies of the upstream apps.
    /// </summary>
    internal sealed class WebAppWindow : Window
    {
        /// <summary>
        /// Must use a reserved TLD that is never resolved. A ".local" name made every navigation
        /// wait ~2 s for a multicast DNS lookup before the virtual host mapping served the page.
        /// </summary>
        const string HostName = "webapps.file-search-manager.example";
        const string Origin = "https://" + HostName + "/";

        /// <summary>
        /// First runtime with CreateWebFileSystemFileHandle (ICoreWebView2Environment14) and
        /// PostWebMessageAsJsonWithAdditionalObjects (ICoreWebView2_23), both SDK 1.0.2651.64, which
        /// requires Runtime 127.0.2651.64 (learn.microsoft.com/microsoft-edge/webview2/release-notes/sdk/1-0-2651-64).
        /// Older runtimes load the page but cannot deliver the files.
        /// </summary>
        internal const string MinimumRuntime = "127.0.2651.64";

        static readonly Lazy<bool> runtimeSupported = new(() =>
        {
            try { return SupportsFileHandoff(CoreWebView2Environment.GetAvailableBrowserVersionString()); }
            catch { return false; } // Runtime not installed or WebView2Loader.dll missing
        });

        /// <summary>
        /// Set when the handoff failed on a runtime that passed the version check - keeps the
        /// text viewer on its fallback for the rest of the session
        /// </summary>
        static volatile bool handoffUnsupported;

        /// <summary>
        /// One browser environment (and user data folder) shared by all windows. The default
        /// data folder sits next to the exe, which is not writable under Program Files.
        /// </summary>
        static Task<CoreWebView2Environment> environment;

        /// <summary>
        /// Runs in every page before its own scripts. Waits for the host message and feeds the
        /// readable files to the page the way a user would; unreadable ones are reported back
        /// as {failed:[{index,error}]} and do not block the rest.
        /// "inputs" - one file per &lt;input type=file&gt;; "after" delays a step until that
        ///            element is shown (Hex editor resets the comparison pane while loading
        ///            the first file, so the second one must wait for it);
        /// "drop"   - all files in one drop event on the document (Log explorer multi-file input).
        /// </summary>
        const string FileHandoffScript = """
            (() => {
                if (!window.chrome || !chrome.webview) return;
                const shown = id => { const e = document.getElementById(id); return e && getComputedStyle(e).display !== 'none'; };
                const until = async test => { for (let i = 0; i < 1200 && !test(); i++) await new Promise(r => setTimeout(r, 50)); };
                const transfer = files => { const dt = new DataTransfer(); files.forEach(f => dt.items.add(f)); return dt; };
                chrome.webview.addEventListener('message', async e => {
                    const m = e.data || {};
                    const handles = Array.from(e.additionalObjects || []);
                    const results = await Promise.allSettled(handles.map(h => Promise.resolve().then(() => h.getFile())));
                    const files = [], failed = [];
                    results.forEach((r, index) => r.status === 'fulfilled'
                        ? files.push(r.value)
                        : failed.push({ index, error: String((r.reason && (r.reason.message || r.reason.name)) || r.reason) }));
                    if (failed.length) chrome.webview.postMessage({ failed });
                    if (!files.length) return;
                    if (m.drop) {
                        document.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: transfer(files) }));
                        return;
                    }
                    for (let i = 0; i < files.length && i < (m.inputs || []).length; i++) {
                        const step = m.inputs[i];
                        if (step.after) await until(() => shown(step.after));
                        const input = document.getElementById(step.id);
                        input.files = transfer([files[i]]).files;
                        input.dispatchEvent(new Event('change', { bubbles: true }));
                    }
                });
            })();
            """;

        readonly WebView2 view = new();
        readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4, 8, 4) };
        readonly Border noticeBar;
        readonly string page;
        readonly string[] files;

        /// <summary>
        /// The installed runtime can host the apps with the selected files loaded
        /// </summary>
        public static bool IsAvailable => !handoffUnsupported && runtimeSupported.Value;

        internal static bool SupportsFileHandoff(string runtimeVersion)
            => !string.IsNullOrWhiteSpace(runtimeVersion) &&
               CoreWebView2Environment.CompareBrowserVersions(runtimeVersion, MinimumRuntime) >= 0;

        public static bool IsWebApp(string app) => app is Apps.LogExplorer or Apps.HexEditor;

        /// <summary>
        /// Show the app in a new window with the files loaded
        /// </summary>
        public static void Open(string page, string[] files) => new WebAppWindow(page, files).Show();

        /// <summary>
        /// Problem shown above the page, null while there is none
        /// </summary>
        internal string NoticeText => noticeBar.Visibility == Visibility.Visible ? notice.Text : null;

        WebAppWindow(string page, string[] files)
        {
            this.page = page;
            this.files = files;
            Title = TitleFor(null);
            Icon = Application.Current?.MainWindow?.Icon;
            Width = 1280;
            Height = 860;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            notice.SetResourceReference(TextBlock.ForegroundProperty, SystemColors.InfoTextBrushKey);
            noticeBar = new Border { Child = notice, Visibility = Visibility.Collapsed };
            noticeBar.SetResourceReference(Border.BackgroundProperty, SystemColors.InfoBrushKey);
            var root = new DockPanel();
            DockPanel.SetDock(noticeBar, Dock.Top);
            root.Children.Add(noticeBar);
            root.Children.Add(view);
            Content = root;
            Loaded += async (_, __) => await Start();
            Closed += (_, __) => view.Dispose();
        }

        string TitleFor(string documentTitle) => string.Join(" - ",
            files.Select(Path.GetFileName).Append(string.IsNullOrWhiteSpace(documentTitle)
                ? Path.GetFileNameWithoutExtension(page)
                : documentTitle));

        string Message => page == Apps.HexEditor
            ? """{"inputs":[{"id":"file-input-1"},{"id":"file-input-2","after":"main-content"}]}"""
            : """{"drop":true}""";

        void ShowNotice(string text)
        {
            notice.Text = text;
            noticeBar.Visibility = Visibility.Visible;
        }

        async Task Start()
        {
            CoreWebView2 core;
            try
            {
                environment ??= CoreWebView2Environment.CreateAsync(null, UserDataPaths.For("WebView2"));
                await view.EnsureCoreWebView2Async(await environment);
                core = view.CoreWebView2;
                core.SetVirtualHostNameToFolderMapping(HostName,
                    Path.Combine(AppContext.BaseDirectory, "WebApps"), CoreWebView2HostResourceAccessKind.Deny);
                await core.AddScriptToExecuteOnDocumentCreatedAsync(FileHandoffScript);
            }
            catch (Exception e)
            {
                if (environment?.IsFaulted == true) environment = null; // Let a later open retry
                MessageBox.Show(this, e.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }

            core.DocumentTitleChanged += (_, __) => Title = TitleFor(core.DocumentTitle);
            core.WebMessageReceived += (_, e) =>
            {
                if (e.Source.StartsWith(Origin, StringComparison.OrdinalIgnoreCase))
                    ReportUnreadable(e.WebMessageAsJson);
            };
            var delivered = false;
            core.NavigationCompleted += (_, e) =>
            {
                // Only the first page load gets the files - a reload starts empty as in a browser
                if (delivered || !e.IsSuccess) return;
                delivered = true;
                // The handles grant file access - post them only to our own page
                if (!core.Source.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) return;
                try
                {
                    var handles = files.Select(f => (object)core.Environment.CreateWebFileSystemFileHandle(
                        f, CoreWebView2FileSystemHandlePermission.ReadOnly)).ToList();
                    core.PostWebMessageAsJson(Message, handles);
                }
                catch (Exception ex) when (ex is NotImplementedException or InvalidCastException or MissingMethodException)
                {
                    HandoffUnsupported();
                    return;
                }
                catch (Exception ex)
                {
                    ShowNotice($"The files could not be passed to the page: {ex.Message}");
                    return;
                }
                view.Focus();
            };
            core.Navigate(Origin + page);
        }

        /// <summary>
        /// The runtime passed the version check but lacks the file handle APIs
        /// </summary>
        void HandoffUnsupported()
        {
            handoffUnsupported = true;
            if (page == Apps.LogExplorer)
            {
                //Keep the text viewer working - the same fallback as without the runtime
                foreach (var file in files) "notepad".Open($"\"{file}\"");
                Close();
                return;
            }
            ShowNotice($"The installed Microsoft Edge WebView2 Runtime cannot open files in this window " +
                $"(version {MinimumRuntime} or newer is needed). Drop the files into the page instead.");
        }

        void ReportUnreadable(string json)
        {
            try
            {
                using var message = JsonDocument.Parse(json);
                if (!message.RootElement.TryGetProperty("failed", out var failed)) return;
                var text = new StringBuilder("Could not read:");
                foreach (var item in failed.EnumerateArray())
                {
                    var index = item.GetProperty("index").GetInt32();
                    var path = index >= 0 && index < files.Length ? files[index] : $"#{index}";
                    text.Append($"\n{path} - {item.GetProperty("error").GetString()}");
                }
                ShowNotice(text.ToString());
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                // Not a handoff report - the page itself never posts to the host
            }
        }
    }
}
