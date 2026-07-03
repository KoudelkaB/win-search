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
#warning TODO: do not use static => inversion of control
        static readonly int pid = Process.GetCurrentProcess().Id;
        static readonly string dirName = "search.";
        public static readonly string TempFolder = Path.Combine(Path.GetTempPath(), dirName + pid);
        /// <summary>
        /// Create unique temporal folder in App temp directory (will be deleted autmaticly)
        /// </summary>
        /// <returns></returns>
        public static string CreateTempFolder()
        {
            var path = Path.Combine(App.TempFolder, DateTime.Now.ToString("MMdd-HHmmss"));
            Directory.CreateDirectory(path);
            return path;
        }
        static App()
        {
            //Clean all search temp folders from previous runs
            foreach (var d in Directory.EnumerateDirectories(Path.GetDirectoryName(TempFolder), dirName + "*"))
            {
                if (int.TryParse(Path.GetExtension(d).Substring(1), out var pid))
                {
                    try
                    {
                        Process.GetProcessById(pid);
                    }
                    catch
                    {
                        //Clean temp folder if the process not running anymore
                        try { Directory.Delete(d, true); } catch { }
                    }
                }
            }
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

            Exit += (o, e) =>
            {
                $"App exiting with {e.ApplicationExitCode}".Debug();
                //Best-effort removal of this run's temp folder; anything locked here
                //is swept by the stale-folder cleanup on the next start
                try { Directory.Delete(TempFolder, true); } catch { }
            };
        }
    }
}
