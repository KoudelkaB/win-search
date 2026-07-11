using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace search
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        //To be deleted on App closeup
        static readonly int pid = Process.GetCurrentProcess().Id;
        static readonly string dirName = "search.";
        static readonly string clipboardDirName = "search.clipboard.";
        static readonly long processStartTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        public static readonly string TempFolder = Path.Combine(Path.GetTempPath(), $"{dirName}{pid}.{processStartTicks}");
        public static readonly string ClipboardTempFolder = Path.Combine(Path.GetTempPath(), $"{clipboardDirName}{pid}.{processStartTicks}");
        /// <summary>
        /// Create unique temporal folder in App temp directory (will be deleted autmaticly)
        /// </summary>
        /// <returns></returns>
        public static string CreateTempFolder()
            => CreateTempFolder(TempFolder);

        public static string CreateClipboardTempFolder()
            => CreateTempFolder(ClipboardTempFolder);

        static string CreateTempFolder(string root)
        {
            var path = Path.Combine(root, $"{DateTime.Now:MMdd-HHmmss}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return path;
        }
        static App()
        {
            StorageMaintenance.RunStartupCleanup(Path.GetTempPath(), TempFolder, DateTime.UtcNow);
        }

        public App()
        {
            int minWorker, minIOC;
            // Get the current settings.
            ThreadPool.GetMinThreads(out minWorker, out minIOC);
            // Change the minimum number of worker threads to four, but
            // keep the old setting for minimum asynchronous I/O 
            // completion threads.
            //if (ThreadPool.SetMinThreads(4, minIOC))
            //{
            //    // The minimum number of threads was set successfully.
            //}
            //else
            //{
            //    // The minimum number of threads was not changed.
            //}

            //Commands run as async void handlers - an exception there bypasses every local
            //try/catch and would kill the whole app; report it and keep running instead
            DispatcherUnhandledException += (o, e) =>
            {
                $"UI exception: {e.Exception}".Debug();
                MessageBox.Show(e.Exception.Message, "Command failed", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };

            Exit += (o, e) =>
            {
                $"App exiting with {e.ApplicationExitCode}".Debug();
                //Best-effort removal of this run's temp folder; anything locked here
                //is swept by the stale-folder cleanup on the next start
                StorageMaintenance.TryDeleteDirectory(TempFolder);
                try
                {
                    var clipboardPaths = Clipboard.ContainsFileDropList()
                        ? Clipboard.GetFileDropList().Cast<string>()
                        : Array.Empty<string>();
                    StorageMaintenance.CleanupClipboardMaterializations(ClipboardTempFolder, clipboardPaths);
                }
                catch { } // Keep clipboard files when clipboard access is temporarily unavailable.
            };
        }
    }
}
