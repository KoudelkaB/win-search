using System;
using System.Collections.Generic;
using System.Text;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace search.Models
{
    public static class FSChangeProcessor
    {
        static ConcurrentQueue<FileSystemEventArgs> fsChanges = new();
        static NonBlocking.ConcurrentDictionary<string, FileSystemWatcher> watchers = new(); // To keep watchers alive
        static AutoResetEvent newFSChanges = new AutoResetEvent(false);
        static Action<string> Started;
        static Action<FileSystemEventArgs> Changed;
        static int started = 0;

        public static bool Run(Action<string> Started, Action<FileSystemEventArgs> Changed)
        {
            // Ensure the following code is run only once
            if (Interlocked.CompareExchange(ref started, 1, 0) != 0) return false;

            // Set events
            FSChangeProcessor.Started = Started;
            FSChangeProcessor.Changed = Changed;

            //Start processing loop
            new Task(() =>
            {
                while (true)
                {
                    newFSChanges.WaitOne();
                    //Process all events in the queue
                    while (fsChanges.TryDequeue(out var e)) Changed(e);
                }
            }, TaskCreationOptions.LongRunning).Start();

            //Watch all drives
            Task.Run(() =>
            {
                foreach (var d in DriveInfo.GetDrives().Select(x => x.RootDirectory.FullName).Distinct()) AddFolder(d);
            });
            return true;
        }

        static void newChange(object o, FileSystemEventArgs e)
        {
            fsChanges.Enqueue(e);
            newFSChanges.Set();
        }

        /// <summary>
        /// Refresh all active drives from NTF
        /// </summary>
        public static void RefreshFromNFT() => watchers.ToList().ForEach(x => AddFolder(x.Key));
        
        static void DisposeWatcher(this string path)
        {
            if (watchers.TryRemove(path, out var w))
            {
                w.EnableRaisingEvents = false;
                w.Dispose();
            }

        }

        static void AddFolder(string path)
        {
            try
            {
                path.DisposeWatcher(); // Dispose the old watcher

                //var d = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var w = new FileSystemWatcher(path)
                //Nefunguje v Linuxu!!! inotifywait funguje, ale jen na jednu složku nebo soubor
                //Ale je tam příkaz locate a sudo updatedb (trvá minutu) který by se snad dal použít?
                //Nebo universální System.IO.FileSystem.Watcher.Polling z corefxlab nugetu?
                //Na win zase https://sourceforge.net/projects/ntfs-search/ nebo http://sourceforge.net/projects/swiftsearch/
                //přímé čtení $MFT souboru - http://www.ntfs.com/ntfs-mft.htm, http://www.ntfs.com/ntfs-system-files.htm
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes | NotifyFilters.Size |
                        NotifyFilters.LastWrite | NotifyFilters.LastAccess | NotifyFilters.CreationTime,
                    IncludeSubdirectories = true
                };
                w.Created += newChange;
                w.Renamed += newChange;
                w.Deleted += newChange;
                w.Changed += newChange;
                w.Error += (o, e) =>
                {
                    var ex = e.GetException();
                    if (ex is InternalBufferOverflowException ie)
                    {
                        //Too many changes at once => need to restart watching on the drive given by exception message
                        AddFolder(path);
                    }
                    Console.WriteLine(e.GetException().Message);
                };
                w.InternalBufferSize = 1 << 16; //Max value

                //Start watching
                w.EnableRaisingEvents = true;
                while (!watchers.TryAdd(path, w)) path.DisposeWatcher(); // Remove the old in case of raise condition
                Started(path);
            }
            catch (Exception e) { }
        }
    }
}
