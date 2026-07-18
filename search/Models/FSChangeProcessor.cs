using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace search.Models
{
    /// <summary>
    /// Watches every drive for file-system changes and feeds them, serialized per drive,
    /// into the model's change handler. NTFS drives are watched through the USN change
    /// journal (kernel-persisted, never drops a change); everything else falls back to
    /// FileSystemWatcher. The app's own operations report themselves through
    /// <see cref="Echo"/> - a priority lane, so the user sees their own action instantly
    /// even while a change storm is queued.
    /// </summary>
    static class FSChangeProcessor
    {
        internal const int NormalCoalesceWindowMs = 200;
        internal const int StormCoalesceWindowMs = 500;
        internal const NotifyFilters WatcherNotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
            | NotifyFilters.Attributes | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime;

        /// <summary>
        /// Per-drive event queue with its own processing thread: a slow stat on one drive
        /// (dead network mapping) can never delay another drive's events. Within a drive
        /// events stay ordered (a rename must not race its own delete). Everything already
        /// waiting is handed to the handler as ONE batch - the handler can then apply a
        /// burst (delete of nine folders) with one index pass and one grid update instead
        /// of paying both per event; the bounded batch also gives backpressure in storms.
        /// </summary>
        sealed class DriveQueue
        {
            readonly ConcurrentQueue<(FsEvent Event, TaskCompletionSource Done)> normal = new(), urgent = new();
            readonly AutoResetEvent signal = new(false);
            readonly List<(FsEvent Event, TaskCompletionSource Done)> batch = new();
            int normalCount, urgentCount;

            //A short quiet window folds save/build bursts into one metadata/grid pass. Once
            //the queue is clearly busy, extend the same window (from its original start) to
            //500 ms. Both values stay comfortably below the one-second live-update budget.
            const int StormThreshold = 64;
            const int MaxBatch = 4096;

            public DriveQueue()
            {
                new Task(() =>
                {
                    while (true)
                    {
                        signal.WaitOne();
                        while (true)
                        {
                            batch.Clear();

                            //The echo lane drains immediately - the user's own delete/rename must
                            //not wait for either a queued storm or the normal coalescing window.
                            while (batch.Count < MaxBatch && urgent.TryDequeue(out var urgentItem))
                            {
                                Interlocked.Decrement(ref urgentCount);
                                batch.Add(urgentItem);
                            }
                            if (batch.Count == 0)
                            {
                                if (Volatile.Read(ref normalCount) == 0) break;
                                WaitForNormalWindow();
                                //An echo arriving during the wait owns the next pass. Leave the
                                //normal queue intact and return to the urgent drain above.
                                if (Volatile.Read(ref urgentCount) != 0) continue;
                                while (batch.Count < MaxBatch && normal.TryDequeue(out var normalItem))
                                {
                                    Interlocked.Decrement(ref normalCount);
                                    batch.Add(normalItem);
                                }
                            }
                            if (batch.Count == 0) continue;
                            try
                            {
                                var events = CoalesceChangedEvents(batch.Select(x => x.Event));
                                if (events.Length > 0) Changed(events).Wait();
                            }
                            catch { }
                            foreach (var item in batch) item.Done?.TrySetResult();
                        }
                    }
                }, TaskCreationOptions.LongRunning).Start();
            }

            void WaitForNormalWindow()
            {
                var started = Environment.TickCount64;
                var target = NormalCoalesceWindowMs;
                while (true)
                {
                    if (Volatile.Read(ref urgentCount) != 0) return;
                    if (Volatile.Read(ref normalCount) >= StormThreshold) target = StormCoalesceWindowMs;
                    var remaining = target - (int)(Environment.TickCount64 - started);
                    if (remaining <= 0) return;
                    //Signals make the event-rate check react immediately; the absolute deadline
                    //prevents a continuous stream from extending the window beyond 500 ms.
                    signal.WaitOne(remaining);
                }
            }

            public Task Enqueue(FsEvent e, bool priority = false)
            {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (priority)
                {
                    urgent.Enqueue((e, done));
                    Interlocked.Increment(ref urgentCount);
                    signal.Set();
                }
                else
                {
                    normal.Enqueue((e, done));
                    //The transition from empty wakes the consumer. Further normal events
                    //are intentionally silent while its time window is open; otherwise a
                    //write storm would wake the thread once per notification just to keep
                    //checking the same deadline. Urgent events always signal above.
                    if (Interlocked.Increment(ref normalCount) == 1) signal.Set();
                }
                return done.Task;
            }
        }

        /// <summary>
        /// Collapse repeated data-change notifications inside one time window. Structural
        /// events are ordering barriers: changes on either side of create/delete/rename are
        /// kept separate, so index surgery observes exactly the same event order as before.
        /// </summary>
        internal static FsEvent[] CoalesceChangedEvents(IEnumerable<FsEvent> events)
        {
            var result = new List<FsEvent>();
            var pending = new Dictionary<string, FsEvent>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();

            void Flush()
            {
                foreach (var path in order) result.Add(pending[path]);
                pending.Clear();
                order.Clear();
            }

            foreach (var e in events)
            {
                if (e?.ChangeType == WatcherChangeTypes.Changed && e.FullPath != null)
                {
                    if (!pending.ContainsKey(e.FullPath)) order.Add(e.FullPath);
                    pending[e.FullPath] = e; //last notification carries the final on-disk state
                    continue;
                }
                Flush();
                if (e != null) result.Add(e);
            }
            Flush();
            return result.ToArray();
        }

        static readonly NonBlocking.ConcurrentDictionary<string, DriveQueue> queues = new(StringComparer.OrdinalIgnoreCase);
        static readonly NonBlocking.ConcurrentDictionary<string, IDisposable> sources = new(StringComparer.OrdinalIgnoreCase); //Watcher or USN reader per drive root
        static Action<string> Started;
        static Func<FsEvent[], Task> Changed;
        static int started = 0;

        /// <summary>
        /// Which drive roots should be indexed (set by the model to the user's drive
        /// selection). A drive that is not indexed is not watched either - its scan still
        /// runs once so a newly deselected drive gets its stale entries pruned.
        /// May block on flaky network drives - only called from a drive's own task.
        /// </summary>
        public static Func<string, bool> ShouldIndex = _ => true;

        /// <summary>Node indexed under a path (set by the model; USN map maintenance)</summary>
        public static Func<string, INode> Lookup = _ => null;

        /// <summary>
        /// Reconcile these directories against the disk (set by the model) - the USN fallback
        /// for records whose file reference is not in the map (deletes on a walked drive,
        /// entries staled by a parent rename).
        /// </summary>
        public static Func<string[], Task> ReconcileDirs = _ => Task.CompletedTask;

        public static bool Run(Action<string> Started, Func<FsEvent[], Task> Changed)
        {
            // Ensure the following code is run only once
            if (Interlocked.CompareExchange(ref started, 1, 0) != 0) return false;

            FSChangeProcessor.Started = Started;
            FSChangeProcessor.Changed = Changed;

            //Watch all drives - each on its own task: creating a watcher on a dead network
            //mapping blocks in SMB timeouts and must not delay the local drives behind it
            foreach (var d in DriveInfo.GetDrives().Select(x => x.RootDirectory.FullName).Distinct())
                Task.Run(() => AddFolder(d));
            return true;
        }

        static DriveQueue QueueFor(string path)
            => queues.GetOrAdd(RootOf(path), _ => new DriveQueue());

        static string RootOf(string path)
        {
            try { return Path.GetPathRoot(path) is { Length: > 0 } r ? r : path; }
            catch { return path; }
        }

        /// <summary>
        /// Apply a change the app itself just performed - jumps the queue so the user sees
        /// their own action instantly, independent of watcher health. Idempotent with the
        /// watcher/journal event that reports the same operation later.
        /// </summary>
        public static Task Echo(FsEvent e)
            => Changed == null ? Task.CompletedTask : QueueFor(e.FullPath).Enqueue(e, priority: true);

        /// <summary>
        /// Hand one drive's freshly published scan to its USN watcher - fills the file
        /// reference map that resolves the paths of deleted/renamed-away files (the
        /// unprivileged journal read carries no names).
        /// </summary>
        public static void PopulateFrnMap(string root, IEnumerable<INode> nodes)
        {
            if (sources.TryGetValue(root, out var s) && s is UsnDriveWatcher usn) usn.Populate(nodes);
        }

        /// <summary>
        /// Refresh all drives from NTFS - every current drive plus any previously watched
        /// root (a vanished drive gets one last scan that prunes its stale entries)
        /// </summary>
        public static void RefreshFromNFT() => DriveInfo.GetDrives().Select(x => x.RootDirectory.FullName)
            .Union(sources.Keys, StringComparer.OrdinalIgnoreCase).ToList()
            .ForEach(d => Task.Run(() => AddFolder(d)));

        static void DisposeSource(this string path)
        {
            if (sources.TryRemove(path, out var s))
            {
                try { s.Dispose(); } catch { }
            }
        }

        static void AddFolder(string path)
        {
            try
            {
                path.DisposeSource(); // Dispose the old source

                if (!ShouldIndex(path)) return; //Not indexed => not watched; Started below still prunes it

                var queue = QueueFor(path);
                //NTFS => USN change journal (opened BEFORE Started fires the scan, so every
                //change since this moment is replayed and nothing falls between snapshot and
                //watch); anything else (FAT, exFAT, ReFS, network mappings) => FileSystemWatcher.
                //A journal that later proves unreadable on this system swaps itself for a
                //watcher too - USN is an upgrade where available, never a requirement.
                var source = UsnDriveWatcher.TryStart(path,
                    e => queue.Enqueue(e),
                    () => { try { Started(path); } catch { } },
                    w => FallBackToWatcher(path, w, queue))
                    ?? CreateWatcher(path, queue);
                if (source == null) return;

                while (!sources.TryAdd(path, source)) path.DisposeSource(); // Remove the old in case of race condition

                //The journal may have proved unreadable before the registration above - the
                //dead callback then found no registered instance to replace, so replace now
                if (source is UsnDriveWatcher { IsDead: true } deadUsn) FallBackToWatcher(path, deadUsn, queue);
            }
            catch (Exception) { }
            //Request the (re)scan even when watching failed (missing or dead drive) - the
            //scan prunes its stale entries and a later refresh can revive the watcher
            finally { try { Started(path); } catch { } }
        }

        /// <summary>
        /// The drive's USN journal turned out unreadable on this system - replace that
        /// exact source with a FileSystemWatcher and rescan to cover anything missed. A
        /// concurrent AddFolder/refresh owning the slot by then wins; this fallback backs off.
        /// </summary>
        static void FallBackToWatcher(string path, UsnDriveWatcher usn, DriveQueue queue)
        {
            try
            {
                if (usn == null || !sources.TryGetValue(path, out var current) || !ReferenceEquals(current, usn))
                    return; //Replaced or removed meanwhile - whoever did owns the drive now
                var watcher = CreateWatcher(path, queue);
                if (sources.TryUpdate(path, watcher, usn)) usn.Dispose();
                else watcher.Dispose();
            }
            catch (Exception e) { $"watcher fallback on {path} failed: {e.Message}".Debug(); }
            finally { try { Started(path); } catch { } } //Rescan bridges the watch gap
        }

        static IDisposable CreateWatcher(string path, DriveQueue queue)
        {
            var w = new FileSystemWatcher(path)
            {
                NotifyFilter = WatcherNotifyFilter,
                IncludeSubdirectories = true
            };
            FileSystemEventHandler handler = (o, e) => queue.Enqueue(FsEvent.From(e));
            w.Created += handler;
            w.Deleted += handler;
            w.Changed += handler;
            w.Renamed += (o, e) => queue.Enqueue(FsEvent.From(e));
            w.Error += (o, e) =>
            {
                var ex = e.GetException();
                if (ex is InternalBufferOverflowException)
                {
                    //Too many changes at once => need to restart watching on the drive given by exception message
                    $"watcher buffer overflow on {path} => restart watcher + rescan".Debug();
                    AddFolder(path);
                }
                else $"watcher error on {path}: {ex.Message}".Debug();
            };
            w.InternalBufferSize = 1 << 16; //Max value

            w.EnableRaisingEvents = true;
            return new WatcherHolder(w);
        }

        sealed class WatcherHolder : IDisposable
        {
            readonly FileSystemWatcher watcher;
            public WatcherHolder(FileSystemWatcher w) => watcher = w;
            public void Dispose()
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
        }
    }
}
