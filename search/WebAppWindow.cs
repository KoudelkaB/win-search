using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;

namespace search
{
    /// <summary>
    /// Top-level window hosting one of the single-file HTML apps copied to WebApps next to the
    /// exe (from the external/singleHtmlApps submodule, https://github.com/KoudelkaB/singleHtmlApps).
    /// The HTML files stay unmodified copies of the upstream apps; the selected files are
    /// handed to the page's own file input, drop handler or embedding API.
    ///
    /// Windows: Avalonia's NativeWebView runs WebView2, driven through its CoreWebView2 - the
    /// pages come from a virtual https host mapped to the local folder and the files arrive
    /// as FileSystemFileHandles. The origin stays the same; a separate browser profile allows
    /// the WPF and Avalonia versions to run concurrently (see WebAppProfile).
    ///
    /// Linux: <see cref="WebAppServer"/> serves pages and files on loopback, shown by WebKitGTK
    /// inside this window, in a native web dialog, or - without WebKitGTK - in the default browser.
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
            if (!OperatingSystem.IsWindows()) return null;
            try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch { return null; } // Runtime not installed or WebView2Loader.dll missing
        });
        static readonly Lazy<bool> runtimeSupported = new(() => SupportsFileHandoff(runtimeVersion.Value));
        static readonly Lazy<string> windowsProfile = new(() =>
            WebAppProfile.Prepare(Path.GetDirectoryName(UserDataPaths.For(WebAppProfile.FolderName))));

        /// <summary>How Linux shows the pages: inside this window, a native dialog, or a browser</summary>
        enum LinuxHost { Embedded, Dialog, Browser }

        static readonly Lazy<LinuxHost> linuxHost = new(() =>
        {
            try
            {
                var info = Avalonia.Platform.WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WebKitGtk);
                if (info.IsSupported && info.IsInstalled)
                {
                    if (info.SupportedScenarios.HasFlag(WebViewEmbeddingScenario.NativeControlHost)) return LinuxHost.Embedded;
                    if (info.SupportedScenarios.HasFlag(WebViewEmbeddingScenario.NativeDialog)) return LinuxHost.Dialog;
                }
                else $"WebKitGTK unavailable: {info.UnavailableReason}".Debug();
            }
            catch (Exception e) { $"WebKitGTK probe failed: {e.Message}".Debug(); }
            return LinuxHost.Browser;
        });

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

        static bool shuttingDown;
        static readonly List<WebAppWindow> openWindows = new();

        // Runs in every page before its own scripts (Windows, see the class comment). Waits for
        // the host message and feeds the readable files to the page the way a user would;
        // unreadable ones are reported back as {failed:[{index,error}]} and do not block the rest.
        // "inputs" - one file per <input type=file>; "after" delays a step until that element
        //            is shown (Hex editor resets the comparison pane while loading the first file,
        //            so the second one must wait for it);
        // "drop"   - all files in one drop event on the document (Log explorer multi-file input);
        // "logExplorer" - options for window.logExplorer.open(files, options) of the page (full
        //            paths, combine mode and a starting filter), the drop is the fallback without it.
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

        readonly NativeWebView view = new();
        readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4, 8, 4) };
        readonly Border noticeBar;
        readonly string page;
        readonly string[] files;
        readonly LogExplorerLoad load;
        readonly DispatcherTimer saveLayout;
        Size? calculatorContentSize;
        bool closed, started, opened;
        string serverToken;
        double zoom;
        IntPtr controller; //ICoreWebView2Controller (Windows), not owned

        /// <summary>
        /// The installed runtime can host the apps with the selected files loaded
        /// </summary>
        public static bool IsAvailable => OperatingSystem.IsWindows()
            ? !handoffUnsupported && runtimeSupported.Value
            : true; //Linux: WebKitGTK or the default browser, both through the loopback server

        /// <summary>Pages without file input only need an installed browser runtime.</summary>
        public static bool IsBrowserAvailable => !OperatingSystem.IsWindows() || !string.IsNullOrWhiteSpace(runtimeVersion.Value);

        internal static bool SupportsFileHandoff(string runtimeVersion)
            => !string.IsNullOrWhiteSpace(runtimeVersion) &&
               CoreWebView2Environment.CompareBrowserVersions(runtimeVersion, MinimumRuntime) >= 0;

        public static bool IsWebApp(string app) => app is Apps.LogExplorer or Apps.HexEditor or Apps.Calculator;

        /// <summary>
        /// Show the app in a new window with the files loaded. Log explorer takes them as one
        /// appended source with the filter when load is given, otherwise it asks how to combine them.
        /// </summary>
        public static void Open(string page, string[] files = null, LogExplorerLoad load = null)
        {
            files ??= Array.Empty<string>();
            if (!OperatingSystem.IsWindows() && linuxHost.Value != LinuxHost.Embedded)
            {
                OpenOutside(page, files, load);
                return;
            }
            new WebAppWindow(page, files, load).Show();
        }

        /// <summary>
        /// The web apps no longer start a browser process ahead of time: Avalonia's WebView
        /// creates its own environment per window. Kept as the hook the main window calls.
        /// </summary>
        internal static void Prewarm(Window host) { }

        /// <summary>Close every web app window and stop the loopback server</summary>
        internal static void Shutdown()
        {
            shuttingDown = true;
            foreach (var window in openWindows.ToArray())
                try { window.Close(); } catch { }
            WebAppServer.Shutdown();
        }

        /// <summary>
        /// Linux without an embeddable WebKitGTK: a native web dialog, or the default browser
        /// </summary>
        static void OpenOutside(string page, string[] files, LogExplorerLoad load)
        {
            var layout = WebAppLayoutStore.Load(page);
            string token = null;
            NativeWebDialog dialog = null;
            var url = WebAppServer.Register(page, files, MessageFor(page, files, load), layout.Zoom, message =>
            {
                try
                {
                    using var json = JsonDocument.Parse(message);
                    if (json.RootElement.TryGetProperty("zoom", out var z) && z.TryGetDouble(out var value))
                        WebAppLayoutStore.Update(page, l => l.Zoom = Math.Round(value, 2));
                    else if (json.RootElement.TryGetProperty("calculatorSize", out var size) && dialog != null
                        && size.TryGetProperty("width", out var w) && size.TryGetProperty("height", out var h))
                        Dispatcher.UIThread.Post(() => dialog.Resize(
                            (int)Math.Ceiling(w.GetDouble() * layout.Zoom) + 16, (int)Math.Ceiling(h.GetDouble() * layout.Zoom) + 16));
                }
                catch { }
            }, out token);
            if (linuxHost.Value == LinuxHost.Dialog)
            {
                try
                {
                    dialog = new NativeWebDialog { Title = TitleFor(page, files, null) };
                    dialog.EnvironmentRequested += ConfigureEnvironment;
                    dialog.Closing += (_, __) => WebAppServer.Unregister(token);
                    dialog.NavigationCompleted += async (_, __) =>
                    {
                        try
                        {
                            var title = await dialog.InvokeScript("document.title");
                            dialog.Title = TitleFor(page, files, title?.Trim('"'));
                        }
                        catch { }
                    };
                    var w = page == Apps.Calculator ? 760 : layout.Width is > 200 ? (int)layout.Width : 1280;
                    var h = page == Apps.Calculator ? 860 : layout.Height is > 150 ? (int)layout.Height : 860;
                    dialog.Resize(w, h);
                    dialog.Show(App.MainWindowOrNull);
                    dialog.Navigate(new Uri(url));
                    return;
                }
                catch (Exception e) { $"native web dialog failed, using the browser: {e.Message}".Debug(); }
            }
            try { Shell.OpenDefault(url); }
            catch (Exception e) { $"opening {url} in the browser failed: {e.Message}".Debug(); }
        }

        static void ConfigureEnvironment(object sender, WebViewEnvironmentRequestedEventArgs e)
        {
            switch (e)
            {
                case WindowsWebView2EnvironmentRequestedEventArgs webView2:
                    webView2.UserDataFolder = windowsProfile.Value;
                    break;
                case GtkWebViewEnvironmentRequestedEventArgs gtk:
                    gtk.BaseDataDirectory = UserDataPaths.For("WebKit");
                    gtk.BaseCacheDirectory = Path.Combine(UserDataPaths.For("WebKit"), "cache");
                    break;
            }
        }

        /// <summary>
        /// Problem shown above the page, null while there is none
        /// </summary>
        internal string NoticeText => noticeBar.IsVisible ? notice.Text : null;

        WebAppWindow(string page, string[] files, LogExplorerLoad load)
        {
            this.page = page;
            this.files = files;
            this.load = load;
            Title = TitleFor(page, files, null);
            Icon = App.MainWindowOrNull?.Icon;
            var layout = WebAppLayoutStore.Load(page);
            zoom = layout.Zoom;
            // Calculator fits its content after navigation; ignore sizes saved by the old
            // generic 1280px window. Scale the initial width so a saved zoom starts in the
            // desktop layout as well, before the page can report its actual dimensions.
            Width = page == Apps.Calculator ? 760 * layout.Zoom / DefaultZoom : layout.Width is > 200 ? layout.Width : 1280;
            Height = page == Apps.Calculator ? 860 : layout.Height is > 150 ? layout.Height : 860;
            if (layout.Maximized) WindowState = WindowState.Maximized;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            // Kept on every resize, not only on close - the main window can end the process first
            saveLayout = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
                (_, __) => SaveLayout()) { IsEnabled = false };
            Resized += (_, __) => LayoutChanged();
            PropertyChanged += (_, e) =>
            {
                if (e.Property != WindowStateProperty) return;
                LayoutChanged();
                Dispatcher.UIThread.Post(FitCalculator, DispatcherPriority.Loaded);
            };
            ScalingChanged += (_, __) => Dispatcher.UIThread.Post(FitCalculator, DispatcherPriority.Loaded);
            Closing += (_, __) => { if (saveLayout.IsEnabled || opened) SaveLayout(); };
            notice.Foreground = Brushes.Black;
            noticeBar = new Border { Child = notice, IsVisible = false, Background = new SolidColorBrush(Color.FromRgb(255, 255, 225)) };
            var root = new DockPanel();
            DockPanel.SetDock(noticeBar, Dock.Top);
            root.Children.Add(noticeBar);
            root.Children.Add(view);
            view.EnvironmentRequested += ConfigureEnvironment;
            view.AdapterCreated += async (_, e) => await Start(e);
            view.NavigationCompleted += async (_, e) =>
            {
                //The Windows path names the window from CoreWebView2.DocumentTitleChanged
                if (OperatingSystem.IsWindows() || closed) return;
                try { Title = TitleFor(page, files, (await view.InvokeScript("document.title"))?.Trim('"')); }
                catch { }
            };
            Opened += (_, __) =>
            {
                opened = true;
                openWindows.Add(this);
            };
            Closed += (_, __) =>
            {
                closed = true;
                openWindows.Remove(this);
                saveLayout.Stop();
                WebAppServer.Unregister(serverToken);
            };
            //Attaching the view can request its environment immediately. Register every
            //handler first so the initial browser uses our profile and navigation setup.
            Content = root;
        }

        void LayoutChanged()
        {
            saveLayout.Stop();
            if (opened) saveLayout.Start(); // Not for the size set while opening
        }

        void SaveLayout()
        {
            saveLayout.Stop();
            if (WindowState == WindowState.Minimized) return; // Would forget that it was maximized
            var maximized = WindowState == WindowState.Maximized;
            var size = ClientSize;
            var currentZoom = CurrentZoom();
            WebAppLayoutStore.Update(page, layout =>
            {
                if (!maximized && size.Width > 0 && size.Height > 0)
                {
                    layout.Width = Math.Round(size.Width);
                    layout.Height = Math.Round(size.Height);
                }
                layout.Maximized = maximized;
                layout.Zoom = Math.Round(currentZoom, 2);
            });
        }

        void FitCalculator()
        {
            if (calculatorContentSize is not Size content || !opened || WindowState != WindowState.Normal ||
                view.Bounds.Width <= 0 || view.Bounds.Height <= 0) return;
            var frameSize = FrameSize ?? ClientSize;
            var frame = new Size(Math.Max(0, frameSize.Width - view.Bounds.Width), Math.Max(0, frameSize.Height - view.Bounds.Height));
            var scaling = RenderScaling;
            var current = new Rect(Position.X / scaling, Position.Y / scaling, frameSize.Width, frameSize.Height);
            var bounds = CalculatorBoundsFor(content, CurrentZoom(), frame, current, CurrentWorkArea());
            var clientWidth = bounds.Width - (frameSize.Width - ClientSize.Width);
            var clientHeight = bounds.Height - (frameSize.Height - ClientSize.Height);
            if (Math.Abs(Width - clientWidth) > 1) Width = clientWidth;
            if (Math.Abs(Height - clientHeight) > 1) Height = clientHeight;
            Position = new PixelPoint((int)Math.Round(bounds.X * scaling), (int)Math.Round(bounds.Y * scaling));
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
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen == null) return new Rect(0, 0, 1920, 1080);
            var scaling = screen.Scaling;
            var area = screen.WorkingArea;
            return new Rect(area.X / scaling, area.Y / scaling, area.Width / scaling, area.Height / scaling);
        }

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
        static string TitleFor(string page, string[] files, string documentTitle)
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
            noticeBar.IsVisible = true;
        }

        async Task Start(WebViewAdapterEventArgs adapter)
        {
            if (closed || shuttingDown || started) return;
            started = true;
            if (!OperatingSystem.IsWindows())
            {
                var url = WebAppServer.Register(page, files, Message, zoom, ReceiveServerMessage, out serverToken);
                view.Navigate(new Uri(url));
                return;
            }

            CoreWebView2 core;
            try
            {
                if ((adapter.TryGetPlatformHandle() ?? view.TryGetPlatformHandle()) is not IWindowsWebView2PlatformHandle handle
                    || handle.CoreWebView2 == IntPtr.Zero)
                    throw new InvalidOperationException("The WebView2 control is not available.");
                core = CoreWebView2.CreateFromComICoreWebView2(handle.CoreWebView2);
                controller = handle.CoreWebView2Controller;
                SetZoom(zoom);
                core.SetVirtualHostNameToFolderMapping(HostName,
                    Path.Combine(AppContext.BaseDirectory, "WebApps"), CoreWebView2HostResourceAccessKind.Deny);
                if (files.Length > 0) await core.AddScriptToExecuteOnDocumentCreatedAsync(FileHandoffScript);
                if (page == Apps.Calculator) await core.AddScriptToExecuteOnDocumentCreatedAsync(CalculatorSizeScript);
                if (closed || shuttingDown) return;
            }
            catch (Exception e)
            {
                $"Web app {page} initialization failed: {e}".Debug();
                if (closed || shuttingDown) return;
                await MessageDialog.Show(this, e.Message, Title, MessageDialogButtons.Ok, MessageDialogIcon.Error);
                Close();
                return;
            }

            core.DocumentTitleChanged += (_, __) => Title = TitleFor(page, files, core.DocumentTitle);
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

        /// <summary>A message the page posted to the loopback server (Linux)</summary>
        void ReceiveServerMessage(string json) => Dispatcher.UIThread.Post(() =>
        {
            if (closed) return;
            try
            {
                using var message = JsonDocument.Parse(json);
                if (message.RootElement.TryGetProperty("zoom", out var z) && z.TryGetDouble(out var value))
                {
                    zoom = value;
                    LayoutChanged();
                    FitCalculator();
                }
                else if (message.RootElement.TryGetProperty("calculatorSize", out _)) ReceiveCalculatorSize(json);
                else ReportUnreadable(json);
            }
            catch (JsonException) { }
        });

        /// <summary>
        /// ICoreWebView2Controller get_ZoomFactor/put_ZoomFactor (vtable slots 7 and 8 after
        /// IUnknown, IsVisible and Bounds) - the WebView2 SDK cannot wrap an existing controller
        /// </summary>
        unsafe double CurrentZoom()
        {
            if (!OperatingSystem.IsWindows() || controller == IntPtr.Zero) return zoom;
            try
            {
                var vtable = *(IntPtr**)controller;
                double value;
                var hr = ((delegate* unmanaged[Stdcall]<IntPtr, double*, int>)vtable[7])(controller, &value);
                if (hr >= 0 && double.IsFinite(value) && value > 0) zoom = value;
            }
            catch { }
            return zoom;
        }

        unsafe void SetZoom(double value)
        {
            zoom = value;
            if (controller == IntPtr.Zero) return;
            try
            {
                var vtable = *(IntPtr**)controller;
                ((delegate* unmanaged[Stdcall]<IntPtr, double, int>)vtable[8])(controller, value);
            }
            catch { }
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
                foreach (var file in files) Apps.Notepad.Open($"\"{file}\"");
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
