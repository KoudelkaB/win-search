using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace search
{
    /// <summary>
    /// Top-level window hosting one of the single-file HTML apps copied to WebApps next to the
    /// exe (from the external/singleHtmlApps submodule, https://github.com/KoudelkaB/singleHtmlApps) in WebView2.
    /// Pages are served from a virtual https host mapped to the local folder, so they need no
    /// network and keep their localStorage (Log explorer profiles) between runs. The selected
    /// files reach the page as FileSystemFileHandles and are handed to the page's own file input,
    /// drop handler or embedding API - the HTML files stay unmodified copies of the upstream apps.
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

        static readonly Lazy<string> runtimeVersion = new(() =>
        {
            try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch { return null; } // Runtime not installed or WebView2Loader.dll missing
        });
        static readonly Lazy<bool> runtimeSupported = new(() => SupportsFileHandoff(runtimeVersion.Value));

        /// <summary>
        /// Set when the handoff failed on a runtime that passed the version check - keeps the
        /// text viewer on its fallback for the rest of the session
        /// </summary>
        static volatile bool handoffUnsupported;

        /// <summary>
        /// The pages are styled for a browser (16 px text); this brings them close to the 12 px
        /// UI of the main window, leaving more of the window to the file content.
        /// Ctrl+wheel changes it and the choice is kept per app with the window size (WebAppLayoutStore).
        /// </summary>
        internal const double DefaultZoom = 0.8;

        /// <summary>
        /// One browser environment (and user data folder) shared by all windows. The default
        /// data folder sits next to the exe, which is not writable under Program Files.
        /// </summary>
        static Task<CoreWebView2Environment> environment;

        /// <summary>
        /// A hidden WebView kept for the whole session. Starting the WebView2 browser process
        /// took seconds, and it ended again with the last window - so the first window of every
        /// batch opened blank. While this one lives the process stays up and windows open at once.
        /// </summary>
        static CoreWebView2Controller warmController;
        static bool shuttingDown, prewarming;

        /// <summary>
        /// Start the browser process in the background (see warmController). Does nothing without
        /// a usable runtime; a failure only leaves the first window slower.
        /// </summary>
        internal static async void Prewarm(Window host)
        {
            if (shuttingDown || prewarming || warmController != null || !IsBrowserAvailable) return;
            prewarming = true;
            CoreWebView2Controller controller = null;
            try
            {
                environment ??= CoreWebView2Environment.CreateAsync(null, UserDataPaths.For("WebView2"));
                var browser = await environment;
                if (shuttingDown || !host.IsLoaded) return;
                controller = await browser.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(host).EnsureHandle());
                if (shuttingDown || !host.IsLoaded) { controller.Close(); return; }
                controller.IsVisible = false;
                warmController = controller;
            }
            catch (Exception e)
            {
                controller?.Close();
                if (environment?.IsFaulted == true) environment = null;
                $"WebView2 prewarm failed: {e.Message}".Debug();
            }
            finally { prewarming = false; }
        }

        /// <summary>Release the hidden controller as well as the visible windows closed by WPF.</summary>
        internal static void Shutdown()
        {
            shuttingDown = true;
            warmController?.Close();
            warmController = null;
            environment = null;
        }

        /// <summary>
        /// Runs in every page before its own scripts. Waits for the host message and feeds the
        /// readable files to the page the way a user would; unreadable ones are reported back
        /// as {failed:[{index,error}]} and do not block the rest.
        /// "inputs" - one file per &lt;input type=file&gt;; "after" delays a step until that
        ///            element is shown (Hex editor resets the comparison pane while loading
        ///            the first file, so the second one must wait for it);
        /// "drop"   - all files in one drop event on the document (Log explorer multi-file input);
        /// "logExplorer" - options for window.logExplorer.open(files, options) of the page (full
        ///            paths, combine mode and a starting filter), the drop is the fallback without it.
        /// </summary>
        const string FileHandoffScript = """
            (() => {
                if (!window.chrome || !chrome.webview) return;
                const shown = id => { const e = document.getElementById(id); return e && getComputedStyle(e).display !== 'none'; };
                const until = async test => { for (let i = 0; i < 1200 && !test(); i++) await new Promise(r => setTimeout(r, 50)); };
                const transfer = files => { const dt = new DataTransfer(); files.forEach(f => dt.items.add(f)); return dt; };
                // A File is a snapshot: reading it fails once the file changes on disk (a log being
                // written). Take the content at once and retry with a fresh snapshot a few times.
                const read = async h => {
                    for (let attempt = 1; ; attempt++) {
                        const file = await h.getFile();
                        try { return new File([await file.arrayBuffer()], file.name, { type: file.type, lastModified: file.lastModified }); }
                        catch (error) { if (attempt >= 5 || error.name !== 'NotReadableError') throw error; }
                        await new Promise(r => setTimeout(r, 50 * attempt));
                    }
                };
                chrome.webview.addEventListener('message', async e => {
                    const m = e.data || {};
                    const handles = Array.from(e.additionalObjects || []);
                    const results = await Promise.allSettled(handles.map(h => Promise.resolve().then(() => read(h))));
                    const files = [], paths = [], failed = [];
                    results.forEach((r, index) => r.status === 'fulfilled'
                        ? (files.push(r.value), paths.push(((m.logExplorer || {}).paths || [])[index]))
                        : failed.push({ index, error: String((r.reason && (r.reason.message || r.reason.name)) || r.reason) }));
                    if (failed.length) chrome.webview.postMessage({ failed });
                    if (!files.length) return;
                    if (m.logExplorer && window.logExplorer) {
                        await window.logExplorer.open(files, { ...m.logExplorer, paths });
                        return;
                    }
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

        // Measure the app, not body/scrollHeight: body's 100svh minimum would prevent
        // shrinking after returning from Scientific or Programmer to Standard.
        internal const string CalculatorSizeScript = """
            (() => {
                window.addEventListener('DOMContentLoaded', () => {
                    const app = document.querySelector('.app');
                    if (!app || !window.chrome?.webview) return;
                    // Keep the preferred width across zoom/resize. Measuring the temporary
                    // mobile breakpoint here would otherwise trap the window in a narrow layout.
                    const preferredWidth = parseFloat(getComputedStyle(app).maxWidth) || app.getBoundingClientRect().width;
                    let timer, previous = '';
                    const report = () => {
                        const body = getComputedStyle(document.body);
                        const bounds = app.getBoundingClientRect();
                        const width = Math.ceil(Math.max(bounds.width, app.scrollWidth, preferredWidth) +
                            parseFloat(body.paddingLeft) + parseFloat(body.paddingRight) + 2);
                        const height = Math.ceil(bounds.height + parseFloat(body.paddingTop) + parseFloat(body.paddingBottom) + 2);
                        const key = width + ':' + height;
                        if (key === previous) return;
                        previous = key;
                        chrome.webview.postMessage({ calculatorSize: { width, height } });
                    };
                    const queue = () => { clearTimeout(timer); timer = setTimeout(report, 50); };
                    new ResizeObserver(queue).observe(app);
                    window.addEventListener('resize', queue);
                    document.fonts.ready.then(queue);
                    queue();
                }, { once: true });
            })();
            """;

        readonly WebView2 view = new();
        readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4, 8, 4) };
        readonly Border noticeBar;
        readonly string page;
        readonly string[] files;
        readonly LogExplorerLoad load;
        readonly DispatcherTimer saveLayout;
        Size? calculatorContentSize;
        bool closed;

        /// <summary>
        /// The installed runtime can host the apps with the selected files loaded
        /// </summary>
        public static bool IsAvailable => !handoffUnsupported && runtimeSupported.Value;

        /// <summary>Pages without file input only need an installed browser runtime.</summary>
        public static bool IsBrowserAvailable => !string.IsNullOrWhiteSpace(runtimeVersion.Value);

        internal static bool SupportsFileHandoff(string runtimeVersion)
            => !string.IsNullOrWhiteSpace(runtimeVersion) &&
               CoreWebView2Environment.CompareBrowserVersions(runtimeVersion, MinimumRuntime) >= 0;

        public static bool IsWebApp(string app) => app is Apps.LogExplorer or Apps.HexEditor or Apps.Calculator;

        /// <summary>
        /// Show the app in a new window with the files loaded. Log explorer takes them as one
        /// appended source with the filter when load is given, otherwise it asks how to combine them.
        /// </summary>
        public static void Open(string page, string[] files = null, LogExplorerLoad load = null)
            => new WebAppWindow(page, files ?? Array.Empty<string>(), load).Show();

        /// <summary>
        /// Problem shown above the page, null while there is none
        /// </summary>
        internal string NoticeText => noticeBar.Visibility == Visibility.Visible ? notice.Text : null;

        WebAppWindow(string page, string[] files, LogExplorerLoad load)
        {
            this.page = page;
            this.files = files;
            this.load = load;
            Title = TitleFor(null);
            Icon = Application.Current?.MainWindow?.Icon;
            var layout = WebAppLayoutStore.Load(page);
            var workArea = SystemParameters.WorkArea;
            // Calculator fits its content after navigation; ignore sizes saved by the old
            // generic 1280px window. Scale the initial width so a saved zoom starts in the
            // desktop layout as well, before the page can report its actual dimensions.
            Width = Math.Min(page == Apps.Calculator ? 760 * layout.Zoom / DefaultZoom : layout.Width is > 200 ? layout.Width : 1280, workArea.Width);
            Height = Math.Min(page == Apps.Calculator ? 860 : layout.Height is > 150 ? layout.Height : 860, workArea.Height);
            if (layout.Maximized) WindowState = WindowState.Maximized;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            // Kept on every resize, not only on close - the main window can end the process first
            saveLayout = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
                (_, __) => SaveLayout(), Dispatcher) { IsEnabled = false };
            SizeChanged += (_, __) => LayoutChanged();
            StateChanged += (_, __) =>
            {
                LayoutChanged();
                Dispatcher.BeginInvoke(() => FitCalculator(), DispatcherPriority.Loaded);
            };
            DpiChanged += (_, __) => Dispatcher.BeginInvoke(() => FitCalculator(), DispatcherPriority.Loaded);
            Closing += (_, __) => { if (saveLayout.IsEnabled) SaveLayout(); };
            notice.SetResourceReference(TextBlock.ForegroundProperty, SystemColors.InfoTextBrushKey);
            noticeBar = new Border { Child = notice, Visibility = Visibility.Collapsed };
            noticeBar.SetResourceReference(Border.BackgroundProperty, SystemColors.InfoBrushKey);
            var root = new DockPanel();
            DockPanel.SetDock(noticeBar, Dock.Top);
            root.Children.Add(noticeBar);
            root.Children.Add(view);
            Content = root;
            Loaded += async (_, __) => await Start();
            Closed += (_, __) =>
            {
                closed = true;
                saveLayout.Stop();
                view.Dispose();
            };
        }

        void LayoutChanged()
        {
            saveLayout.Stop();
            if (IsLoaded) saveLayout.Start(); // Not for the size set while opening
        }

        void SaveLayout()
        {
            saveLayout.Stop();
            if (WindowState == WindowState.Minimized) return; // Would forget that it was maximized
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            if (bounds.IsEmpty) return;
            WebAppLayoutStore.Update(page, layout =>
            {
                layout.Width = Math.Round(bounds.Width);
                layout.Height = Math.Round(bounds.Height);
                layout.Maximized = WindowState == WindowState.Maximized;
            });
        }

        void FitCalculator()
        {
            if (calculatorContentSize is not Size content || !IsLoaded || WindowState != WindowState.Normal ||
                view.ActualWidth <= 0 || view.ActualHeight <= 0) return;
            var frame = new Size(Math.Max(0, ActualWidth - view.ActualWidth), Math.Max(0, ActualHeight - view.ActualHeight));
            var bounds = CalculatorBoundsFor(content, view.ZoomFactor, frame,
                new Rect(Left, Top, ActualWidth, ActualHeight), CurrentWorkArea());
            if (Math.Abs(Width - bounds.Width) > 1) Width = bounds.Width;
            if (Math.Abs(Height - bounds.Height) > 1) Height = bounds.Height;
            Left = bounds.Left;
            Top = bounds.Top;
        }

        internal static Rect CalculatorBoundsFor(Size content, double zoom, Size frame, Rect current, Rect workArea)
        {
            var width = Math.Min(Math.Ceiling(content.Width * zoom + frame.Width), workArea.Width);
            var height = Math.Min(Math.Ceiling(content.Height * zoom + frame.Height), workArea.Height);
            return new Rect(Math.Clamp(current.Left, workArea.Left, workArea.Right - width),
                Math.Clamp(current.Top, workArea.Top, workArea.Bottom - height), width, height);
        }

        Rect CurrentWorkArea()
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(new WindowInteropHelper(this).Handle, 2), ref info))
                return SystemParameters.WorkArea;
            var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            return new Rect(scale.Transform(new Point(info.Work.Left, info.Work.Top)),
                scale.Transform(new Point(info.Work.Right, info.Work.Bottom)));
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MonitorRect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct MonitorInfo { public int Size; public MonitorRect Monitor, Work; public uint Flags; }

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        void ReceiveCalculatorSize(string json)
        {
            try
            {
                using var message = JsonDocument.Parse(json);
                if (!message.RootElement.TryGetProperty("calculatorSize", out var size) ||
                    !size.TryGetProperty("width", out var w) || !w.TryGetDouble(out var width) ||
                    !size.TryGetProperty("height", out var h) || !h.TryGetDouble(out var height) ||
                    !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return;
                calculatorContentSize = new Size(width, height);
                FitCalculator();
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { }
        }

        /// <summary>
        /// The file names (the first few when there are many) and the page title
        /// </summary>
        string TitleFor(string documentTitle)
        {
            var names = files.Select(Path.GetFileName);
            if (files.Length > 4) names = names.Take(3).Append($"+{files.Length - 3}");
            return string.Join(" - ", names.Append(string.IsNullOrWhiteSpace(documentTitle)
                ? Path.GetFileNameWithoutExtension(page)
                : documentTitle));
        }

        string Message => MessageFor(page, files, load);

        /// <summary>
        /// The host message telling FileHandoffScript how to hand the files to the page. Log
        /// explorer gets their full paths too - a browser File carries only the name.
        /// </summary>
        internal static string MessageFor(string page, string[] files, LogExplorerLoad load) => page == Apps.HexEditor
            ? """{"inputs":[{"id":"file-input-1"},{"id":"file-input-2","after":"main-content"}]}"""
            : JsonSerializer.Serialize(new
            {
                drop = true,
                logExplorer = new
                {
                    paths = files,
                    combine = load == null ? null : "append",
                    filter = string.IsNullOrEmpty(load?.Filter) ? null : new { text = load.Filter, caseSensitive = load.CaseSensitive }
                }
            });

        /// <summary>
        /// The page in the UI language of the application (Log explorer is localized)
        /// </summary>
        internal static string UrlFor(string page)
            => Origin + page + "?lang=" + Uri.EscapeDataString(CultureInfo.CurrentUICulture.Name);

        void ShowNotice(string text)
        {
            notice.Text = text;
            noticeBar.Visibility = Visibility.Visible;
        }

        async Task Start()
        {
            if (closed || shuttingDown) return;
            CoreWebView2 core;
            try
            {
                environment ??= CoreWebView2Environment.CreateAsync(null, UserDataPaths.For("WebView2"));
                await view.EnsureCoreWebView2Async(await environment);
                if (closed || shuttingDown) return;
                core = view.CoreWebView2;
                view.ZoomFactor = WebAppLayoutStore.Load(page).Zoom;
                view.ZoomFactorChanged += (_, __) =>
                {
                    WebAppLayoutStore.Update(page, layout => layout.Zoom = Math.Round(view.ZoomFactor, 2));
                    FitCalculator();
                };
                core.SetVirtualHostNameToFolderMapping(HostName,
                    Path.Combine(AppContext.BaseDirectory, "WebApps"), CoreWebView2HostResourceAccessKind.Deny);
                if (files.Length > 0) await core.AddScriptToExecuteOnDocumentCreatedAsync(FileHandoffScript);
                if (page == Apps.Calculator) await core.AddScriptToExecuteOnDocumentCreatedAsync(CalculatorSizeScript);
                if (closed || shuttingDown) return;
            }
            catch (Exception e)
            {
                if (closed || shuttingDown) return;
                if (environment?.IsFaulted == true) environment = null; // Let a later open retry
                MessageBox.Show(this, e.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }

            core.DocumentTitleChanged += (_, __) => Title = TitleFor(core.DocumentTitle);
            core.WebMessageReceived += (_, e) =>
            {
                if (e.Source.StartsWith(Origin, StringComparison.OrdinalIgnoreCase))
                {
                    if (page == Apps.Calculator) ReceiveCalculatorSize(e.WebMessageAsJson);
                    else ReportUnreadable(e.WebMessageAsJson);
                }
            };
            var delivered = false;
            core.NavigationCompleted += (_, e) =>
            {
                // Only the first page load gets the files - a reload starts empty as in a browser
                if (delivered || !e.IsSuccess) return;
                delivered = true;
                // The handles grant file access - post them only to our own page
                if (!core.Source.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) return;
                if (files.Length == 0)
                {
                    view.Focus();
                    return;
                }
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
                    ShowNotice(L.Format("WebFilesNotPassed", ex.Message));
                    return;
                }
                view.Focus();
            };
            core.Navigate(UrlFor(page));
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
            ShowNotice(L.Format("WebRuntimeTooOld", MinimumRuntime));
        }

        void ReportUnreadable(string json)
        {
            try
            {
                using var message = JsonDocument.Parse(json);
                if (!message.RootElement.TryGetProperty("failed", out var failed)) return;
                var text = new StringBuilder(L.Text("WebCouldNotRead"));
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

    /// <summary>
    /// What a Log explorer window starts with: the files appended as one source, filtered to
    /// the lines containing Filter (none when empty)
    /// </summary>
    internal sealed record LogExplorerLoad(string Filter, bool CaseSensitive);

    /// <summary>
    /// Zoom and window size of one web app, kept when the user changes them
    /// </summary>
    internal sealed class WebAppLayout
    {
        public double Zoom { get; set; } = WebAppWindow.DefaultZoom;
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Maximized { get; set; }
    }

    /// <summary>
    /// WebAppLayout per app page, e.g. {"LogExplorer.html":{"Zoom":0.8,"Width":1280,...}}
    /// </summary>
    internal static class WebAppLayoutStore
    {
        static readonly string Path = UserDataPaths.For("webapp-windows.json");
        static readonly object gate = new();

        public static WebAppLayout Load(string page)
        {
            lock (gate)
            {
                var layout = Read().GetValueOrDefault(page) ?? new WebAppLayout();
                if (layout.Zoom is < 0.25 or > 5) layout.Zoom = WebAppWindow.DefaultZoom;
                return layout;
            }
        }

        public static void Update(string page, Action<WebAppLayout> change)
        {
            lock (gate)
            {
                try
                {
                    var all = Read();
                    var layout = all.GetValueOrDefault(page) ?? new WebAppLayout();
                    change(layout);
                    all[page] = layout;
                    File.WriteAllText(Path, JsonSerializer.Serialize(all));
                }
                catch (Exception e) { $"saving web app layout failed: {e.Message}".Debug(); }
            }
        }

        static Dictionary<string, WebAppLayout> Read()
        {
            try { return JsonSerializer.Deserialize<Dictionary<string, WebAppLayout>>(File.ReadAllText(Path)) ?? new(); }
            catch { return new(); }
        }
    }
}
