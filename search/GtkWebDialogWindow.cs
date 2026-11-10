using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace search
{
    /// <summary>
    /// Makes the GTK window of a NativeWebDialog an ordinary top-level window (Linux).
    ///
    /// Avalonia.Controls.WebView 12.1.1 shows that window with type hint 3, which is
    /// GDK_WINDOW_TYPE_HINT_TOOLBAR, not the DIALOG its source comment intends. GNOME gives a
    /// toolbar window no frame and does not place it: the web apps opened in the top left
    /// corner and could not be moved, resized or closed. The dialog sets the hint just before
    /// it realizes and shows the window, all in one step on the GTK thread, so the hint is
    /// replaced from the window's "realize" signal - after it is set, before the window is mapped.
    /// </summary>
    internal static unsafe class GtkWebDialogWindow
    {
        const int GdkWindowTypeHintNormal = 0;
        const int GtkWinPosCenter = 1;
        const int GConnectAfter = 1;

        /// <summary>
        /// Call from the dialog's AdapterCreated event, which comes before the dialog queues its
        /// Show on the GTK thread. Both go through g_timeout_add(0), which runs them in order.
        /// </summary>
        public static void MakeNormal(NativeWebDialog dialog)
        {
            if (!OperatingSystem.IsLinux() || dialog.TryGetPlatformHandle() is not { HandleDescriptor: "GtkWindow", Handle: var window }
                || window == IntPtr.Zero) return;
            try { g_timeout_add(0, (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int>)&OnGtkThread, window); }
            catch (Exception e) { $"GTK web dialog fix not applied: {e.Message}".Debug(); }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static int OnGtkThread(IntPtr window)
        {
            try
            {
                if (gtk_widget_get_realized(window) == 0)
                    g_signal_connect_data(window, "realize", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnRealized,
                        IntPtr.Zero, IntPtr.Zero, GConnectAfter);
                else if (gtk_widget_get_visible(window) != 0)
                {
                    // Already shown as a toolbar: map it again, so that the window manager
                    // frames and places it as a normal window
                    gtk_widget_hide(window);
                    Apply(window);
                    gtk_window_present(window);
                }
                else Apply(window);
            }
            catch { }
            return 0; // G_SOURCE_REMOVE
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnRealized(IntPtr window, IntPtr data)
        {
            try { Apply(window); }
            catch { }
        }

        /// <summary>Normal window centered on the screen - as the Windows version opens the apps</summary>
        static void Apply(IntPtr window)
        {
            gtk_window_set_type_hint(window, GdkWindowTypeHintNormal);
            gtk_window_set_position(window, GtkWinPosCenter);
        }

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
