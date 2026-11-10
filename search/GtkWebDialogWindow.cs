using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace search
{
    /// <summary>
    /// The GTK side of a NativeWebDialog (Linux): an ordinary top-level window and WebKit's own zoom.
    ///
    /// Avalonia.Controls.WebView 12.1.1 shows the window with type hint 3, which is
    /// GDK_WINDOW_TYPE_HINT_TOOLBAR, not the DIALOG its source comment intends. GNOME gives a
    /// toolbar window no frame and does not place it: the web apps opened in the top left
    /// corner and could not be moved, resized or closed. The dialog sets the hint just before
    /// it realizes and shows the window, all in one step on the GTK thread, so the hint is
    /// replaced from the window's "realize" signal - after it is set, before the window is mapped.
    ///
    /// The zoom is WebKit's zoom level, which works like the browser zoom of the Windows
    /// version: the page lays out for the viewport in CSS px, so media queries and vh units follow it.
    /// CSS zoom on the page does not do that - zoomed out, the calculator switched to its narrow
    /// layout, and zoomed in, its 100svh body grew past the window.
    /// </summary>
    internal sealed unsafe class GtkWebDialogWindow
    {
        const int GdkWindowTypeHintNormal = 0;
        const int GtkWinPosCenter = 1;
        const int GConnectAfter = 1;

        readonly NativeWebDialog dialog;
        // Set before GTK destroys the window - it does so on its own thread, after Closing
        volatile bool closed;

        /// <summary>Attach before the dialog is shown</summary>
        public GtkWebDialogWindow(NativeWebDialog dialog, double zoom)
        {
            this.dialog = dialog;
            // AdapterCreated comes before the dialog queues its Show on the GTK thread; both
            // go through g_timeout_add(0), which runs them in order
            dialog.AdapterCreated += (_, __) =>
            {
                MakeNormal();
                SetZoom(zoom);
            };
            dialog.Closing += (_, __) => closed = true;
        }

        /// <summary>WebKit's zoom level of the page, from any thread</summary>
        public void SetZoom(double zoom) => Post(() =>
        {
            if (closed || setZoomLevel.Value == IntPtr.Zero) return;
            if (dialog.TryGetPlatformHandle() is IGtkWebViewPlatformHandle { WebKitWebView: var view } && view != IntPtr.Zero)
                ((delegate* unmanaged[Cdecl]<IntPtr, double, void>)setZoomLevel.Value)(view, zoom);
        });

        void MakeNormal()
        {
            if (dialog.TryGetPlatformHandle() is not { HandleDescriptor: "GtkWindow", Handle: var window } || window == IntPtr.Zero)
                return;
            Post(() =>
            {
                if (gtk_widget_get_realized(window) == 0)
                    g_signal_connect_data(window, "realize", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnRealized,
                        IntPtr.Zero, IntPtr.Zero, GConnectAfter);
                else if (gtk_widget_get_visible(window) != 0)
                {
                    // Already shown as a toolbar: map it again, so that the window manager
                    // frames and places it as a normal window
                    gtk_widget_hide(window);
                    Normal(window);
                    gtk_window_present(window);
                }
                else Normal(window);
            });
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnRealized(IntPtr window, IntPtr data)
        {
            try { Normal(window); }
            catch { }
        }

        /// <summary>Normal window centered on the screen - as the Windows version opens the apps</summary>
        static void Normal(IntPtr window)
        {
            gtk_window_set_type_hint(window, GdkWindowTypeHintNormal);
            gtk_window_set_position(window, GtkWinPosCenter);
        }

        /// <summary>Run on the GTK thread, after the work queued there before</summary>
        static void Post(Action action)
        {
            if (!OperatingSystem.IsLinux()) return;
            var handle = GCHandle.Alloc(action);
            try { g_timeout_add(0, (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int>)&RunPosted, GCHandle.ToIntPtr(handle)); }
            catch (Exception e)
            {
                handle.Free();
                $"GTK web dialog call not queued: {e.Message}".Debug();
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static int RunPosted(IntPtr data)
        {
            var handle = GCHandle.FromIntPtr(data);
            try { ((Action)handle.Target)(); }
            catch (Exception e) { $"GTK web dialog call failed: {e.Message}".Debug(); }
            finally { handle.Free(); }
            return 0; // G_SOURCE_REMOVE
        }

        // The WebKitGTK the dialog loaded - Avalonia prefers the 4.1 API
        static readonly Lazy<IntPtr> setZoomLevel = new(() =>
        {
            foreach (var name in new[] { "libwebkit2gtk-4.1.so.0", "libwebkit2gtk-4.0.so.37" })
                if (NativeLibrary.TryLoad(name, out var library) && NativeLibrary.TryGetExport(library, "webkit_web_view_set_zoom_level", out var function))
                    return function;
            return IntPtr.Zero;
        });

        const string Gtk = "libgtk-3.so.0";
        [DllImport(Gtk)] static extern void gtk_window_set_type_hint(IntPtr window, int hint);
        [DllImport(Gtk)] static extern void gtk_window_set_position(IntPtr window, int position);
        [DllImport(Gtk)] static extern void gtk_window_present(IntPtr window);
        [DllImport(Gtk)] static extern int gtk_widget_get_realized(IntPtr widget);
        [DllImport(Gtk)] static extern int gtk_widget_get_visible(IntPtr widget);
        [DllImport(Gtk)] static extern void gtk_widget_hide(IntPtr widget);
        [DllImport("libgobject-2.0.so.0")]
        static extern ulong g_signal_connect_data(IntPtr instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string signal,
            IntPtr handler, IntPtr data, IntPtr destroyData, int flags);
        [DllImport("libglib-2.0.so.0")] static extern uint g_timeout_add(uint interval, IntPtr function, IntPtr data);
    }
}
