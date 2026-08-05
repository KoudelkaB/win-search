using System;
using System.Runtime.InteropServices;
using search.Core;

namespace search
{
    /// <summary>
    /// Cheapest possible "is WinSearchService serving right now" test: a single WaitNamedPipe
    /// call with a one-millisecond timeout, which returns on the spot instead of connecting. This runs on
    /// the startup path before the window exists, so it must not cost measurable time - opening
    /// the SCM (ServiceController) or actually connecting the pipe both do.
    ///
    /// WaitNamedPipe has no NMPWAIT_NOWAIT sentinel: positive values are milliseconds, while
    /// zero means "use the server's default". Keep the explicit one-millisecond bound.
    ///
    /// A false negative is harmless by design. Every way this can be wrong (service still
    /// starting during boot, all pipe instances momentarily busy) makes the caller spawn the
    /// elevated broker eagerly, which is just the old behaviour - never something worse.
    /// </summary>
    internal static class ServiceProbe
    {
        internal const int ProbeTimeoutMs = 1;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool WaitNamedPipeW(string name, int timeoutMilliseconds);

        public static bool IsServing()
        {
            try
            {
                var serving = WaitNamedPipeW(
                    $@"\\.\pipe\{ServicePipe.PipeName}", ProbeTimeoutMs);
                var error = serving ? 0 : Marshal.GetLastWin32Error();
                StorageMaintenance.AppendDiagnostic(
                    $"Service probe: serving={serving}; timeout={ProbeTimeoutMs}ms; win32={error}");
                return serving;
            }
            catch (Exception e)
            {
                StorageMaintenance.AppendDiagnostic($"Service probe failed: {e}");
                $"service probe failed: {e.Message}".Debug();
                return false;
            }
        }
    }
}
