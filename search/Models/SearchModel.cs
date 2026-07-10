using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace search.Models
{
    class SearchModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public event Action UIRefreshRequested;

        public string Status { get; set; }

        /// <summary>
        /// Background drive-loading progress. Kept separate from Status so it does not
        /// replace feedback for actions initiated by the user.
        /// </summary>
        public string LoadStatus { get; private set; }

        /// <summary>
        /// Supplementary drive-loading details shown when hovering LoadStatus.
        /// </summary>
        public string LoadStatusTooltip { get; private set; }

        /// <summary>
        /// True while the filter update is running
        /// </summary>
        public bool Filtering { get; private set; }

        /// <summary>
        /// True while search in file contents is running
        /// </summary>
        public bool Searching { get; private set; }

        /// <summary>
        /// True while the file system is being (re)loaded from the NTFS MFT
        /// </summary>
        public bool Loading { get; private set; }

        public string CountsInfo => Items.Count == files.Count ? $"{files.Count:# ### ###}" : $"{Items.Count:### ###}/{files.Count:# ### ###}";

        public ObservableCollection<INode> Items { get; private set; } = new ObservableCollection<INode>();

        // Keyed by the nodes themselves (NodePath hashes/compares their component chains),
        // yet still queried by plain path strings - so the bulk MFT load holds no full-path
        // strings at all. Watcher additions may key by the path string; it is the same
        // instance their FileNode stores anyway.
        static NonBlocking.ConcurrentDictionary<object, INode> files = new(NodePath.KeyComparer);
        static ConcurrentBag<ZipNode> zipNodes = new();
        static INode[] exes;
        Dispatcher Dispatcher = Dispatcher.CurrentDispatcher; //Constructor is called from UI thread => get its dispatcher
        SemaphoreSlim Updating = new SemaphoreSlim(1, 1);
        string filter = null, sort = "+" + nameof(INode.LastChangeTime);
        NodeFilter nodeFilter = new NodeFilter("");
        volatile object lastUpdate = DateTime.MinValue;
        volatile bool itemsComplete = true; //false => Items hold only a part of the filtered result (publishing was canceled)
        int refreshQueued = 0; //1 => a data refresh is already queued and covers all changes arriving before it runs
        const int MaxItems = 100000; //Publish just the first 100 000 filtered items

        public Action BeforeItemsExchange = () => { };
        public Action AfterItemsExchange = () => { };

        public async Task Update(string newFilter = null, string newSort = null)
        {
            // A data refresh is already queued and covers this change too => skip the Task.Run
            // (change storms during a load would otherwise spawn thousands of tasks per second)
            if ((newFilter ?? newSort) == null && Volatile.Read(ref refreshQueued) == 1) return;

            // Return immediately => do not slow down UI!!!
            await Task.Run(async () =>
            {
                // null in both filter and sort means unchanged => pure data refresh
                var dataRefresh = (newFilter ?? newSort) == null;
                var created = DateTime.Now;
                object update = created;
                if (dataRefresh)
                {
                    //Coalesce data refreshes - the single queued one covers all changes arriving before it runs
                    //and it never cancels a running update (a canceled publish would leave Items partial forever
                    //under a steady stream of file system events)
                    if (Interlocked.Exchange(ref refreshQueued, 1) == 1) return;
                    await Task.Delay(1000); //Batch bursts of changes
                }
                else
                {
                    //User change - take over the pipeline immediately, the running update is canceled
                    lastUpdate = update;
                    Filtering = true;

                    //Debounce typing - the new update starts after a short pause
                    if (newFilter != null && filter != newFilter)
                    {
                        await Task.Delay(150);
                        if (!ReferenceEquals(update, lastUpdate)) return;
                    }
                }

                // Do not run for out dated filter/sort/files
                bool IsCanceled() => !ReferenceEquals(update, lastUpdate);

                await Updating.WaitAsync();
                try
                {
                    if (dataRefresh)
                    {
                        refreshQueued = 0; //Changes from now on queue a new refresh
                        if ((DateTime)lastUpdate > created) return; //A newer update runs against current data anyway
                        lastUpdate = update;
                        Filtering = true;
                    }

                    // null in filters and sorters means unchanged
                    newFilter ??= filter;
                    newSort ??= sort;
                    var refresh = dataRefresh
                        | filter != newFilter //refilter also on new filter
                        | sort != newSort //a new ordering needs the full filtered source, not the current result window
                        | !itemsComplete; //previous publishing was canceled => Items are partial and can not be resorted in place

                    // Set current filter/sorter before possible canceling
                    filter = newFilter;
                    sort = newSort;
                    nodeFilter = new NodeFilter(filter);

                    if (IsCanceled()) return;
                    var items = GetItems(refresh, IsCanceled);
                    if (items == null || IsCanceled()) return;
                    //Items is mutated on the UI thread (incremental updates) => the comparison may
                    //see a torn list; publish on any doubt instead of dropping the refresh
                    try { if (items.IsIdentical(Items)) return; } catch { }

                    //Publish progressively in batches - the first results show immediately
                    //and the UI thread is never blocked for long (input has higher priority)
                    const int batchSize = 25000;
                    var target = new RangeObservableCollection<INode>();
                    var applied = false;
                    itemsComplete = false;
                    for (int i = 0; (i == 0 || i < items.Count) && !IsCanceled(); i += batchSize)
                    {
                        var batch = items.GetRange(i, Math.Min(batchSize, items.Count - i));
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (IsCanceled()) return; //Do not flash outdated results
                            if (!applied)
                            {
                                BeforeItemsExchange();
                                Items = target;
                                applied = true;
                            }
                            target.AddRange(batch);
                            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountsInfo)));
                        }, DispatcherPriority.Background);
                    }
                    itemsComplete = applied && !IsCanceled();
                    if (applied) await Dispatcher.InvokeAsync(AfterItemsExchange);
                }
                catch (Exception e)
                {
                    await Log($"Exception: {e.Message}");
                }
                finally
                {
                    if (!IsCanceled()) Filtering = false; //When canceled the newer update owns the indicator
                    Updating.Release(); //Allow next change
                }
            });
        }

        /// <summary>
        /// Apply changed nodes to the UI - in place when possible, otherwise queue a full refresh
        /// </summary>
        /// <param name="nodes"></param>
        /// <returns></returns>
        public async Task UpdateFor(params INode[] nodes)
        {
            var changed = nodes.Where(x => x != null).ToArray();
            if (changed.Length == 0) return;

            //Maintain the published window in place - avoids refiltering and resorting millions of files per change
            if (await TryIncrementalUpdate(changed)) return;

            var nf = nodeFilter;
            if (changed.Any(nf.Matches)) await Update();
        }

        /// <summary>
        /// Update Items in place for a few changed nodes (remove + sorted insert)
        /// instead of refiltering and resorting all files
        /// </summary>
        /// <param name="changed"></param>
        /// <returns>false => the caller must run a full update</returns>
        async Task<bool> TryIncrementalUpdate(INode[] changed)
        {
            var currentFilter = filter;
            var currentSort = sort;
            var nf = nodeFilter;
            var compare = SortComparison(currentSort);
            //While a scan runs, changes arrive in storms and each in-place insert costs an
            //O(Items.Count) IndexOf on the UI thread - that saturates the dispatcher and starves
            //the batch publish holding the Updating semaphore (frozen filter, endless Loading).
            //The coalesced full refresh covers these changes instead.
            if (Loading || compare == null || !itemsComplete) return false;

            return await Dispatcher.InvokeAsync(() =>
            {
                //Revalidate on the UI thread - Items are exchanged only here
                if (!itemsComplete || currentFilter != filter || currentSort != sort) return false;
                var items = Items;
                var updated = false;
                //Remove+Insert silently drops the row from the view's selection => snapshot it
                //and restore after (same INode instances get re-inserted), so a selected file
                //stays selected while it keeps changing on disk
                BeforeItemsExchange();
                foreach (var node in changed)
                {
                    var index = items.IndexOf(node);
                    //Present in files under its path and matching the filter => it belongs to the result
                    //(the node itself is a valid key - no full path is materialized here)
                    var include = files.TryGetValue(node, out var current) && ReferenceEquals(current, node) && nf.Matches(node);
                    if (index >= 0)
                    {
                        items.RemoveAt(index);
                        updated = true;
                    }
                    if (include)
                    {
                        var at = BinaryIndex(items, node, compare);
                        if (at < items.Count || items.Count < MaxItems) //Beyond the published window => skip
                        {
                            items.Insert(at, node);
                            updated = true;
                        }
                    }
                }
                while (items.Count > MaxItems) items.RemoveAt(items.Count - 1);
                AfterItemsExchange();
                if (updated) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountsInfo)));
                return true;
            });
        }

        /// <summary>
        /// First index at which node can be inserted keeping items sorted
        /// </summary>
        static int BinaryIndex(IList<INode> items, INode node, Comparison<INode> compare)
        {
            int lo = 0, hi = items.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (compare(items[mid], node) <= 0) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        /// <summary>
        /// Comparison producing the same ordering as GetItems for the given sort
        /// </summary>
        Comparison<INode> SortComparison(string sort)
        {
            if (string.IsNullOrWhiteSpace(sort)) return null;
            var up = sort[0] == '+';
            Comparison<INode> key;
            bool ascending;
            switch (sort.Substring(1))
            {
                case "C": key = (a, b) => FoundRank(a).CompareTo(FoundRank(b)); ascending = up; break;
                case nameof(INode.Name): key = (a, b) => string.Compare(a.Name, b.Name); ascending = up; break;
                case nameof(INode.Size): key = (a, b) => a.Size.CompareTo(b.Size); ascending = !up; break;
                case nameof(INode.LastChangeTime): key = (a, b) => a.LastChangeTime.CompareTo(b.LastChangeTime); ascending = !up; break;
                case nameof(INode.LastAccessTime): key = (a, b) => a.LastAccessTime.CompareTo(b.LastAccessTime); ascending = !up; break;
                case nameof(INode.FullName): key = NodePath.ByPath.Compare; ascending = up; break;
                case nameof(INode.Folder): key = NodePath.ByFolderThenName.Compare; ascending = up; break;
                default: return null;
            }
            return ascending ? key : (a, b) => key(b, a);
        }

        int FoundRank(INode x) => FoundIn(x) switch { true => 1, false => 2, null => x.IsDirectory ? 4 : 3 };

        /// <summary>
        /// Get items according to current filter and sort
        /// </summary>
        /// <param name="refresh">refilter from files (false - use Items)</param>
        /// <param name="IsCanceled">Check if to cancel</param>
        /// <returns></returns>
        List<INode> GetItems(bool refresh, Func<bool> IsCanceled)
        {
            //Cancel
            var cts = new CancellationTokenSource();
            T CancelOr<T>(T v)
            {
                if (IsCanceled()) cts.Cancel();
                return v;
            }
            while (!IsCanceled())
            {
                try
                {
                    var src = (refresh ? files.Values.AsParallel() : Items.AsParallel()).WithCancellation(cts.Token);
                    if (refresh && filter != null)
                    {
                        src = src.Where(x => CancelOr(nodeFilter.Matches(x)));
                    }
                    if (!string.IsNullOrWhiteSpace(sort))
                    {
                        bool up = sort[0] == '+';
                        void addSort<T>(Func<INode, T> s, bool _up) => src = _up ?
                            src.OrderBy(x => CancelOr(s(x))) :
                            src.OrderByDescending(x => CancelOr(s(x)));
                        switch (sort.Substring(1))
                        {
                            case "C": addSort(x => FoundRank(x), up); break;
                            case nameof(INode.Name): addSort(x => x.Name, up); break;
                            case nameof(INode.Size): addSort(x => x.Size, !up); break;
                            case nameof(INode.LastChangeTime): addSort(x => x.LastChangeTime, !up); break;
                            case nameof(INode.LastAccessTime): addSort(x => x.LastAccessTime, !up); break;
                            //Sorted by the parent chains - no full path strings are built
                            case nameof(INode.FullName):
                                src = up ? src.OrderBy(x => CancelOr(x), NodePath.ByPath)
                                         : src.OrderByDescending(x => CancelOr(x), NodePath.ByPath);
                                break;
                            case nameof(INode.Folder):
                                src = up ? src.OrderBy(x => CancelOr(x), NodePath.ByFolderThenName)
                                         : src.OrderByDescending(x => CancelOr(x), NodePath.ByFolderThenName);
                                break;
                        }
                    }
                    return src.Take(MaxItems).ToList(); // Take just the first 100 000
                }
                catch (OperationCanceledException)
                {
                    return null;   //Discard and run new filter
                }
                catch (InvalidOperationException)
                {
                    continue; //Enumeration changed => try again
                }
            }
            return null;
        }

        /// <summary>
        /// Remove node and subtract a removed file from its ancestor directory sizes
        /// </summary>
        /// <param name="path"></param>
        INode Remove(string path)
        {
            if (!files.TryRemove(path, out var n)) return null;
            //Directories are skipped: a recursive delete raises an event per contained file and
            //subtracting the aggregate too would double-count. A tree that leaves without
            //per-file events (move to recycle bin = rename of the top folder) stays counted
            //until the next MFT reload.
            if (!n.IsDirectory) PropagateSizeDelta(path, -(long)n.Size);
            return n;
        }

        /// <summary>
        /// Refresh node and apply its size change to ancestor directory sizes
        /// </summary>
        /// <param name="path"></param>
        INode Refresh(string path)
        {
            if (files.TryGetValue(path, out var n))
            {
                //Directory sizes are aggregates - only file sizes contribute, including the
                //rare flip between file and directory
                var oldSize = n.IsDirectory ? 0L : (long)n.Size;
                n.Refresh();
                PropagateSizeDelta(path, (n.IsDirectory ? 0L : (long)n.Size) - oldSize);
                return n;
            }
            return null;
        }

        /// <summary>
        /// Get the node or index a newly seen file, adding a new file to its ancestor directory sizes
        /// </summary>
        INode GetOrAddNew(string path)
        {
            var added = new FileNode(path);
            var node = files.GetOrAdd(path, added);
            //Only a first sighting contributes - an already indexed node was counted by the scan
            if (ReferenceEquals(node, added) && !node.IsDirectory) PropagateSizeDelta(path, (long)node.Size);
            return node;
        }

        /// <summary>
        /// Best-effort update of aggregated ancestor directory sizes for a file size delta.
        /// Runs only on the serialized FS change thread; exact sizes are restored by the
        /// next MFT reload (F12), which also heals drift from missed events.
        /// </summary>
        void PropagateSizeDelta(string path, long delta)
        {
            if (delta == 0) return;
            for (var dir = Path.GetDirectoryName(path); dir != null; dir = Path.GetDirectoryName(dir))
                if (files.TryGetValue(dir, out var d) && d.IsDirectory) d.AddSizeDelta(delta);
            RefreshSizesSoon();
        }

        int sizeRefreshQueued = 0; //1 => a UI repaint covering all size changes so far is already queued
        /// <summary>
        /// Repaint visible rows so changed directory sizes show, coalesced to one refresh
        /// per 2s - never per event, which would saturate the dispatcher during change storms
        /// </summary>
        void RefreshSizesSoon()
        {
            if (Interlocked.CompareExchange(ref sizeRefreshQueued, 1, 0) != 0) return;
            Task.Run(async () =>
            {
                await Task.Delay(2000);
                Interlocked.Exchange(ref sizeRefreshQueued, 0);
                UIRefreshRequested?.Invoke();
            });
        }

        /// <summary>
        /// Checks if desired text was found in file
        /// </summary>
        /// <param name="n"></param>
        /// <returns></returns>
        public bool? FoundIn(INode n) => searched.TryGetValue(n, out var v) ? v : null;

        // Keyed by node identity - result rows are the same instances, and no path strings are held
        NonBlocking.ConcurrentDictionary<INode, bool?> searched = new();

        // Update continualy the UI until canceled
        // refreshUI: false => only run the action (Status binds via INPC); a full list refresh
        // every second is only needed when row content changes outside the binding (Find results)
        async Task ContinualUpdate(CancellationToken ct, Action a, bool refreshUI = true)
        {
            while (!ct.IsCancellationRequested)
            {
                a();
                if (refreshUI) UIRefreshRequested?.Invoke();
                try
                {
                    await Task.Delay(1000, ct); // Wait for 1 second or until canceled
                }
                catch (TaskCanceledException)
                {
                    break; // Exit loop if canceled
                }
                catch (OperationCanceledException)
                {
                    break; // Exit loop if canceled
                }
            }
        }

        CancellationTokenSource lastFind = null;
        public async Task Find(string text = null, bool caseInsensitive = false, string encoding = "UTF-8")
        {
            if (String.IsNullOrWhiteSpace(text))
            {
                // Typing in the Search box cancels a previous content search. Once its
                // markings are already gone, doing it again must not refresh the whole
                // result list for every keystroke.
                Interlocked.Exchange(ref lastFind, null)?.Cancel();
                if (searched.IsEmpty && !Searching) return;
                searched.Clear();
                Searching = false;
                UIRefreshRequested?.Invoke();
                return;
            }

            // Stop previous and start new find
            var thisFind = new CancellationTokenSource();
            Interlocked.Exchange(ref lastFind, thisFind)?.Cancel();
            searched.Clear();
            Searching = true;
            var watch = Stopwatch.StartNew();

            // Search
            var update = ContinualUpdate(thisFind.Token, () =>
                Status = $"Searching for '{text}' - {searched.Count} files checked in {watch.Elapsed.TotalSeconds:0.0}s");
            var searchText = caseInsensitive ? text.ToLowerInvariant() : text;

            // Convert search text to bytes based on encoding
            ReadOnlyMemory<byte> toFind;
            try
            {
                toFind = ConvertSearchTextToBytes(searchText, encoding);
            }
            catch (Exception ex)
            {
                Status = $"Search failed: {ex.Message}";
                Searching = false;
                return;
            }

            // Snapshot on the calling (UI) thread - Items can be exchanged/appended during the search
            var nodes = Items.Where(x => !x.IsDirectory).ToArray();
            try
            {
                await Task.Run(() => nodes.AsParallel().WithCancellation(thisFind.Token)
                    .ForAll(
                    n => searched[n] = Find(n.FullName, toFind, caseInsensitive)
                    ));
            }
            catch (OperationCanceledException) { }

            // Show results
            var result = thisFind.IsCancellationRequested ? "canceled" : "done";
            var counts = searched.Values.GroupBy(x => x).ToDictionary(x => $"{x.Key}", x => x.Count()); // null can not be key in dictionary => string
            thisFind.Cancel();
            await update;
            if (ReferenceEquals(thisFind, lastFind)) //Do not overwrite state of a newer search
            {
                Searching = false;
                Status = $"Search of '{text}' {result} => file counts (found/not/failed): {counts.Get("True")}/{counts.Get("False")}/{counts.Get("")} in {watch.Elapsed.TotalSeconds:0.0}s";
                UIRefreshRequested?.Invoke();

                // The parallel search and its result aggregation can leave substantial
                // short-lived data in Gen2/LOH. Compact it once the current search has
                // published its final state, never for a search superseded by a newer one.
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
            }
        }

        bool? Find(string path, ReadOnlyMemory<byte> search, bool caseInsensitive = false)
        {
            try
            {
                if (search.Length == 0) return true;
                Span<byte> buf = stackalloc byte[1 << 16]; //64KB fast buffer
                Span<byte> lowerBuf = caseInsensitive ? stackalloc byte[1 << 16] : Span<byte>.Empty;
                using var s = File.OpenRead(path);
                int start = 0; //Overlap kept from the previous block
                while (true)
                {
                    var read = s.Read(buf.Slice(start));
                    var len = start + read;
                    var bufSlice = buf.Slice(0, len);

                    var haystack = bufSlice;
                    if (caseInsensitive)
                    {
                        // Convert buffer to lowercase for case-insensitive search
                        var lowerSlice = lowerBuf.Slice(0, len);
                        for (int i = 0; i < len; i++)
                        {
                            var b = bufSlice[i];
                            lowerSlice[i] = (b >= 65 && b <= 90) ? (byte)(b + 32) : b; // Convert A-Z to a-z
                        }
                        haystack = lowerSlice;
                    }

                    if (haystack.IndexOf(search.Span) != -1) return true;
                    if (read == 0) return false; //End of file
                    // Keep the tail that could contain the start of a match crossing the block boundary
                    start = Math.Min(search.Length - 1, len);
                    bufSlice.Slice(len - start).CopyTo(buf);
                }
            }
            catch
            {
                return null;
            }
        }

        // Registrations are added on the UI thread and completed on the FS change processing thread => lock
        Dictionary<string, List<TaskCompletionSource<INode>>> OnFileCreated =
            new Dictionary<string, List<TaskCompletionSource<INode>>>(StringComparer.InvariantCultureIgnoreCase);
        Task<INode> WaitForFileCreationIf(string path, Func<bool> p)
        {
            var t = new TaskCompletionSource<INode>();
            lock (OnFileCreated)
            {
                if (!OnFileCreated.TryGetValue(path, out var l)) l = OnFileCreated[path] = new List<TaskCompletionSource<INode>>();
                l.Add(t);
            }
            if (!p())
            {
                lock (OnFileCreated)
                {
                    if (OnFileCreated.TryGetValue(path, out var l))
                    {
                        l.Remove(t);
                        if (l.Count == 0) OnFileCreated.Remove(path);
                    }
                }
                t.SetCanceled();
            }
            return t.Task;
        }

        public SearchModel() => FSChangeProcessor.Run(async d => await InitFromNTFS()
            , async e =>
            {
                try
                {
                    //Status = $"WATCHED {++watched}. changes => last {DateTime.Now.TimeOfDay} {e.FullPath}";
                    switch (e.ChangeType)
                    {
                        case WatcherChangeTypes.Created:
                            //Trace.WriteLine($"+{e.FullPath}");
                            var node = GetOrAddNew(e.FullPath);
                            await UpdateFor(node);
                            List<TaskCompletionSource<INode>> tcs = null;
                            lock (OnFileCreated)
                            {
                                if (OnFileCreated.TryGetValue(e.FullPath, out tcs)) OnFileCreated.Remove(e.FullPath);
                            }
                            if (tcs != null) foreach (var x in tcs) x.SetResult(node);
                            break;
                        case WatcherChangeTypes.Changed:
                            //Change attributes and size
                            await UpdateFor(Refresh(e.FullPath) ?? GetOrAddNew(e.FullPath));
                            break;
                        case WatcherChangeTypes.Renamed:
                            //Trace.WriteLine($"{e.OldFullPath}->{e.FullPath}");
                            //Remove first so a same-directory rename nets to zero on the shared ancestors
                            await UpdateFor(
                                Remove((e as RenamedEventArgs).OldFullPath),
                                GetOrAddNew(e.FullPath));
                            break;
                        case WatcherChangeTypes.Deleted:
                            //Trace.WriteLine($"-{e.FullPath}");
                            await UpdateFor(Remove(e.FullPath));
                            break;
                    }
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show(ex.ToString(), $"FS change {e.FullPath} failed");
                    await Log($"FS change {e} failed: {ex}");
                }
            });

        /// <summary>
        /// Find file by name
        /// WARN: Slow implementation
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public static string FindFile(string name) => files.Values.AsParallel().FirstOrDefault(
                  x => !x.IsDirectory &&
                  x.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase) && x.Name.Length == name.Length)?.FullName;

        public static string FindFile(params string[] names)
        {
            foreach (var name in names)
            {
                var path = FindFile(name);
                if (path != null) return path;
            }
            return null;
        }

        public static string FindExe(params string[] names)
        {
            foreach (var name in names)
            {
                var path = exes?.AsParallel().FirstOrDefault(n =>
                NodePath.LeafEquals(n, name) &&
                // do not get development exes
                !NodePath.HasPathComponent(n, "debug") &&
                !NodePath.HasPathComponent(n, "obj"))?.FullName;

                if (path != null) return path;
            }
            return null;
        }

        int initRequests = 0;
        bool firstLoadPublished; //false until the first scan has shown results => publish each finished drive as it lands

        /// <summary>
        /// Refresh files
        /// Concurrent requests (e.g. one per drive on startup) are coalesced into a single scan
        /// </summary>
        Task InitFromNTFS()
        {
            //The already queued/running scan covers this request too
            if (Interlocked.Increment(ref initRequests) > 1) return Task.CompletedTask;
            return Task.Run(async () =>
            {
                Loading = true;
                LoadStatusTooltip = null;
                var watch = Stopwatch.StartNew();
                var origins = new NonBlocking.ConcurrentDictionary<string, MftOrigin>();
                var progress = new CancellationTokenSource();
                var progressTask = ContinualUpdate(progress.Token, () =>
                    LoadStatus = $"Loading drives - {files.Count:# ### ###} files - {(int)watch.Elapsed.TotalSeconds}s",
                    refreshUI: false);
                try
                {
                    while (true)
                    {
                        await Task.Delay(500); //Give the remaining drives time to report in
                        var requested = Volatile.Read(ref initRequests);
                        $"drive scan started (covering {requested} requests)".Debug();
                        origins.Clear();
                        ntfsWalked = false;
                        try
                        {
                            //Add files from all NTFS drives - each drive loads in parallel into a shared dictionary
                            var loaded = new NonBlocking.ConcurrentDictionary<object, INode>(NodePath.KeyComparer);
                            // First load => publish every finished drive immediately, so a fast MFT drive
                            // shows at once instead of waiting on the slowest drive's walk (which may hang).
                            // Must NOT key off files.Count: the FileSystemWatcher pre-populates files on any
                            // startup churn, which would silently disable the progressive publish.
                            var publishPartial = !firstLoadPublished;
                            await Task.WhenAll(DriveInfo.GetDrives().Select(d => Task.Run(() =>
                            {
                                try
                                {
                                    //IsReady of an unreachable network drive blocks in SMB timeouts
                                    //for tens of seconds - probe once with a deadline so a dead
                                    //mapping can never stall the whole scan
                                    if (!IsReadyFast(d))
                                    {
                                        $"drive {d.Name} not ready => skipped".Debug();
                                        return;
                                    }
                                    var entries = DriveEntries(d, out var origin);
                                    origins[d.Name.TrimEnd(Path.DirectorySeparatorChar)] = origin;
                                    //The node itself is the key - no full path string is ever built or stored
                                    foreach (var n in entries) loaded[n] = n;
                                    if (publishPartial)
                                    {
                                        files = loaded;
                                        _ = Update(); //Do not await - the publish must not delay the remaining drives
                                    }
                                }
                                catch { }
                            })));
                            files = loaded;

                            // Add previously added zipNodes that still exist
                            var zn = zipNodes.ToArray();
                            zipNodes.Clear();
                            zn.Select(x => x.ZIP.FullName).Distinct().OrderBy(x => x).ForEach(x =>
                              {
                                  if (files.TryGetValue(x, out var n)) AddArchive(n);
                              });
                            exes = files.Values.AsParallel()
                                .Where(n => !n.IsDirectory && NodePath.LeafEndsWith(n, ".exe")).ToArray();
                        }
                        catch { }
                        
                        //Update filtered files
                        await Update();

                        // Clean freed memory
                        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);

                        //Results are on screen now => later scans rebuild silently and swap at the end
                        firstLoadPublished = true;
                        //Quit when no new request arrived during the scan
                        if (Interlocked.CompareExchange(ref initRequests, 0, requested) == requested) return;
                        $"drive scan rerun: {Volatile.Read(ref initRequests) - requested} new requests arrived during the scan".Debug();
                    }
                }
                finally
                {
                    progress.Cancel();
                    await progressTask;
                    Loading = false;
                    LoadStatus = $"Loaded {files.Count:# ### ###} files in {watch.Elapsed.TotalSeconds:0.0}s";
                    LoadStatusTooltip = OriginsInfo(origins).Trim();
                    LoadStatus.Debug();
                    UIRefreshRequested?.Invoke();
                }
            });
        }

        static SemaphoreSlim logging = new SemaphoreSlim(1, 1);
        Task Log(string text)
        {
            var tid = Thread.CurrentThread.ManagedThreadId; //Save logging thread id
            return Task.Run(async () =>
            {
                await logging.WaitAsync();
                await File.AppendAllTextAsync("log.txt", $"{DateTime.Now} {tid} {text}\n");
                logging.Release();
            });
        }

        /// <summary>
        /// DriveInfo.IsReady with a deadline: an unreachable network drive blocks the query
        /// in SMB timeouts for tens of seconds - abandon the probe and report not ready
        /// </summary>
        static bool IsReadyFast(DriveInfo d)
        {
            var probe = Task.Run(() => { try { return d.IsReady; } catch { return false; } });
            return probe.Wait(TimeSpan.FromSeconds(5)) && probe.Result;
        }

        volatile bool ntfsWalked; //An NTFS drive had to fall back to the walk => hint how to get MFT indexing

        /// <summary>
        /// Get all files of the drive: NTFS via the raw $MFT (direct when elevated -> broker -> service),
        /// anything else - or NTFS with no MFT source available - via the directory walk
        /// </summary>
        /// <param name="drive"></param>
        /// <returns></returns>
        IEnumerable<INode> DriveEntries(DriveInfo drive, out MftOrigin origin)
        {
            origin = MftOrigin.Walk;
            if (drive?.IsReady != true) return Enumerable.Empty<INode>();

            if (string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                var nodes = MftSource.TryGetNodes(drive, out origin);
                if (nodes != null) return nodes;
                ntfsWalked = true;
            }

            origin = MftOrigin.Walk;
            return DirectoryWalker.Walk(drive);
        }

        /// <summary>
        /// "(C: service, D: folder walk)" tooltip text for the load status, with a hint
        /// when an NTFS drive had to be walked
        /// </summary>
        string OriginsInfo(NonBlocking.ConcurrentDictionary<string, MftOrigin> origins)
        {
            if (origins.IsEmpty) return "";
            var parts = string.Join(", ", origins.OrderBy(x => x.Key).Select(x => $"{x.Key} " + x.Value switch
            {
                MftOrigin.Service => "service",
                MftOrigin.Direct => "direct",
                MftOrigin.Broker => "admin helper",
                _ => "folder walk"
            }));
            var hint = ntfsWalked
                ? " - install the File Search Manager service or accept the admin prompt for instant NTFS indexing"
                : "";
            return $" ({parts}){hint}";
        }

        /// <summary>
        /// Add the content of the archive into the search
        /// </summary>
        /// <param name="path"></param>
        public static bool AddArchive(INode n)
        {
            try
            {
                //Check if the zip is allready added
                if (zipNodes.FirstOrDefault(z => z.ZIP == n) != null)
                    return true; //Allready added

                //Get archive that the node represents or throw
                using var a = n.ToArchive();
                if (a == null) return false;

                //Add all entries from the archive to node tree
                var src = a.Entries.AsParallel();
                var keys = src.Select(e => e.Key.AsMemory()).Where(x => !x.IsEmpty);
                src.Select(e => new ZipNode(n, e))
                    //Add remaining dirs not included in archive
                    .Concat(keys.Select(k => k.ParentFolder()).Where(x => !x.IsEmpty).Distinct()
                    .Except(keys, MemoryCharComparer.IgnoreCase)
                    .Select(d => new ZipNode(n, d.ToString()))
                    ).ForEach(n =>
                    {
                        files.AddOrUpdate(n.FullName, x => n, (x, y) => n);
                        zipNodes.Add(files[n.FullName] as ZipNode);
                    });
                return true;
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>
        /// Get the filter for given folders adding archives content as a folder
        /// </summary>
        /// <param name="nodes"></param>
        /// <returns></returns>
        public NodeFilter FilterFolders(params INode[] nodes) => new NodeFilter(string.Join(" ",
            nodes.Where(n => n.IsDirectory || AddArchive(n))
            .Select(n => $"\"{n.FullName}\"")));

        /// <summary>
        /// Get parent node TODO: Create if not exist?
        /// </summary>
        /// <param name="n"></param>
        /// <returns></returns>
        public INode GetParent(INode n) => n.PathParent ?? FindByPath(Path.GetDirectoryName(n.FullName));

        /// <summary>
        /// Node indexed under the path, tolerating the trailing-backslash-less root form ("C:")
        /// </summary>
        internal static INode FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (files.TryGetValue(path, out var n)) return n;
            return path[^1] != '\\' && files.TryGetValue(path + '\\', out n) ? n : null;
        }

        /// <summary>
        /// Unzip all zip nodes - each in separate directory
        /// </summary>
        /// <param name="nodes"></param>
        /// <returns></returns>
        public async IAsyncEnumerable<INode> UnZip(bool call7zip, params INode[] nodes)
        {
            // Start all unzips on the thread pool (Unzip runs synchronously inside WaitForFileCreationIf)
            var tasks = nodes.Select(z => Task.Run(() =>
             {
                 var dir = (z.FullName + "_").NewOutDir();
                 return WaitForFileCreationIf(dir, () => z.Unzip(dir));
             })).ToArray();
            foreach (var t in tasks)
            {
                INode item = null;
                try { item = await t; } catch (OperationCanceledException) { } //Failed unzip => no node
                if (item != null) yield return item;
            }
        }

        /// <summary>
        /// Zip all nodes into ziped file named after first node
        /// </summary>
        /// <param name="seven"></param>
        /// <param name="nodes"></param>
        /// <returns></returns>
        public async Task<INode> Zip(bool call7zip, bool seven = false, params INode[] nodes)
        {
            if (nodes.FirstOrDefault() == null) return null;
            var zip = (nodes[0].FullName + (seven || call7zip ? ".7z" : ".zip")).NewOutFile();
            var paths = nodes.Select(n => n.FullName).ToArray();
            Exception failure = null;
            try
            {
                // Register the watcher before writing, but perform archive creation
                // on a worker thread so large directory trees do not freeze the UI.
                return await Task.Run(() => WaitForFileCreationIf(zip, () =>
                {
                    try
                    {
                        ZipExtensions.Zip(zip, call7zip, paths);
                        return File.Exists(zip);
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                        $"Zip failed: {ex}".Debug();
                        return false;
                    }
                }));
            }
            catch (OperationCanceledException) when (failure != null)
            {
                throw new InvalidOperationException(failure.Message, failure);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        public IEnumerable<INode> ToTextNodes(params INode[] nodes)
        {
            foreach (var n in nodes)
            {
                switch (Path.GetExtension(n.Name).ToLower())
                {
                    case ".xlsx":
                        {
                            string dir = App.CreateTempFolder();
                            foreach (var txt in n.GetExcelSheets())
                            {
                                var path = Path.Combine(dir, Path.GetRandomFileName());
                                File.WriteAllText(path, txt, Encoding.UTF8);
                                yield return new FileNode(path);
                            }
                        }
                        continue;
                }
                yield return n;
            }
        }

        private ReadOnlyMemory<byte> ConvertSearchTextToBytes(string searchText, string encoding)
        {
            return encoding switch
            {
                "UTF-8" => Encoding.UTF8.GetBytes(searchText).AsMemory(),
                "UTF-16" => Encoding.Unicode.GetBytes(searchText).AsMemory(),
                "HEX" => ConvertHexStringToBytes(searchText),
                _ => Encoding.UTF8.GetBytes(searchText).AsMemory() // Default to UTF-8
            };
        }

        private ReadOnlyMemory<byte> ConvertHexStringToBytes(string hexText)
        {
            try
            {
                // Parse hex string (space-separated bytes)
                string[] hexBytes = hexText.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (hexBytes.Length == 0)
                    throw new ArgumentException("No hex bytes found");

                byte[] bytes = hexBytes.Select(hex => Convert.ToByte(hex, 16)).ToArray();
                return bytes.AsMemory();
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Invalid hexadecimal format: {ex.Message}. Use space-separated hex bytes (e.g., '48 65 6C 6C 6F')");
            }
        }
    }

    /// <summary>
    /// ObservableCollection with cheap bulk Add raising a single Reset notification
    /// </summary>
    class RangeObservableCollection<T> : ObservableCollection<T>
    {
        public void AddRange(IEnumerable<T> items)
        {
            foreach (var x in items) Items.Add(x);
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

}
