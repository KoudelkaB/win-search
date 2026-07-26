using System;
using System.Runtime.InteropServices;
using search.Core;

namespace search
{
    /// <summary>
    /// Cheapest possible "is WinSearchService serving right now" test: a single WaitNamedPipe
    /// call with NMPWAIT_NOWAIT, which returns on the spot instead of connecting. This runs on
    /// the startup path before the window exists, so it must not cost measurable time - opening
    /// the SCM (ServiceController) or actually connecting the pipe both do.
    ///
    /// NMPWAIT_NOWAIT is 1, not 0: a zero timeout means "use the server's own default", which
    /// is exactly the blocking wait this probe exists to avoid.
    ///
    /// A false negative is harmless by design. Every way this can be wrong (service still
    /// starting during boot, all pipe instances momentarily busy) makes the caller spawn the
    /// elevated broker eagerly, which is just the old behaviour - never something worse.
    /// </summary>
    internal static class ServiceProbe
    {
        const int NMPWAIT_NOWAIT = 1;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool WaitNamedPipeW(string name, int timeoutMilliseconds);

        public static bool IsServing()
        {
            try
            {
                return WaitNamedPipeW($@"\\.\pipe\{ServicePipe.PipeName}", NMPWAIT_NOWAIT);
            }
            catch (Exception e)
            {
                $"service probe failed: {e.Message}".Debug();
                return false;
            }
        }
    }
}
