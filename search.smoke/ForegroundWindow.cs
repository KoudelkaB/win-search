#if WINDOWS_HOST
using System;
using System.Runtime.InteropServices;

namespace search.Smoke
{
    /// <summary>
    /// The window that gets real keys and clicks. Windows lets only the foreground program
    /// hand the foreground on, so the test joins the input of the current foreground thread
    /// for the moment of the switch.
    /// </summary>
    internal static class ForegroundWindow
    {
        public static IntPtr Handle() => GetForegroundWindow();

        public static int ProcessId()
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(window, out var process);
            return (int)process;
        }

        public static void Bring(IntPtr window)
        {
            var foreground = GetForegroundWindow();
            var foregroundThread = GetWindowThreadProcessId(foreground, out _);
            var ownThread = GetCurrentThreadId();
            var attached = foregroundThread != 0 && foregroundThread != ownThread
                && AttachThreadInput(ownThread, foregroundThread, true);
            try
            {
                if (IsIconic(window)) ShowWindow(window, 9); //SW_RESTORE
                BringWindowToTop(window);
                //Refused while the user has just typed elsewhere (the foreground lock); the
                //Alt+Tab switch still works then and sends no key to anybody
                if (!SetForegroundWindow(window) || GetForegroundWindow() != window) SwitchToThisWindow(window, true);
            }
            finally
            {
                if (attached) AttachThreadInput(ownThread, foregroundThread, false);
            }
        }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr window);
        [DllImport("user32.dll")] static extern void SwitchToThisWindow(IntPtr window, bool altTab);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachInput);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    }
}
#endif
