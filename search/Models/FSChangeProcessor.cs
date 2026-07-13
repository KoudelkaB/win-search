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
        static Func<FileSystemEventArgs, Task> Changed;
        static int started = 0;

        /// <summary>
        /// Which drive roots should be indexed (set by the model to the user's drive
        /// selection). A drive that is not indexed is not watched either - its scan still
        /// runs once so a newly deselected drive gets its stale entries pruned.
        /// May block on flaky network drives - only called from a drive's own task.
        /// </summary>
        public static Func<string, bool> ShouldIndex = _ => true;

        public static bool Run(Action<string> Started, Func<FileSystemEventArgs, Task> Changed)
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
                    //Process events one at a time - awaiting each keeps ordering (a rename must not
                    //race its own delete) and gives backpressure: an unawaited async handler would
                    //spawn thousands of concurrent updates during a change storm, starving the
                    //thread pool and the UI dispatcher. Bursts are absorbed by fsChanges instead.
                    while (fsChanges.TryDequeue(out var e))
                    {
                        try { Changed(e).Wait(); } catch { }
                    }
                }
            }, TaskCreationOptions.LongRunning).Start();

            //Watch all drives - each on its own task: creating a watcher on a dead network
            //mapping blocks in SMB timeouts and must not delay the local drives behind it
            foreach (var d in DriveInfo.GetDrives().Select(x => x.RootDirectory.FullName).Distinct())
                Task.Run(() => AddFolder(d));
            return true;
        }

        static void newChange(object o, FileSystemEventArgs e)
        {
            fsChanges.Enqueue(e);
            newFSChanges.Set();
        }

        /// <summary>
        /// Refresh all drives from NTFS - every current drive plus any previously watched
        /// root (a vanished drive gets one last scan that prunes its stale entries)
        /// </summary>
        public static void RefreshFromNFT() => DriveInfo.GetDrives().Select(x => x.RootDirectory.FullName)
            .Union(watchers.Keys, StringComparer.OrdinalIgnoreCase).ToList()
            .ForEach(d => Task.Run(() => AddFolder(d)));
        
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

                if (!ShouldIndex(path)) return; //Not indexed => not watched; Started below still prunes it

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
                        $"watcher buffer overflow on {path} => restart watcher + rescan".Debug();
                        AddFolder(path);
                    }
                    else $"watcher error on {path}: {ex.Message}".Debug();
                };
                w.InternalBufferSize = 1 << 16; //Max value

                //Start watching
                w.EnableRaisingEvents = true;
                while (!watchers.TryAdd(path, w)) path.DisposeWatcher(); // Remove the old in case of raise condition
            }
            catch (Exception) { }
            //Request the (re)scan even when watching failed (missing or dead drive) - the
            //scan prunes its stale entries and a later refresh can revive the watcher
            finally { try { Started(path); } catch { } }
        }
    }
}
