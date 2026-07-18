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
using System.Buffers;

namespace search.Models
{
    class SearchModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public event Action UIRefreshRequested;

        /// <summary>
        /// Repaint just the rows of these nodes (their displayed values changed in place).
        /// Unlike UIRefreshRequested this never regenerates the whole view - unrelated file
        /// system activity must not blink the grid or disturb keyboard selection.
        /// </summary>
        public event Action<INode[]> RowsRefreshRequested;

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

        public ObservableCollection<INode> Items { get; private set; } = new RangeObservableCollection<INode>();

        // Keyed by the nodes themselves (NodePath hashes/compares their component chains),
        // yet still queried by plain path strings - so the bulk MFT load holds no full-path
        // strings at all. Watcher additions may key by the path string; it is the same
        // instance their FileNode stores anyway.
        static NonBlocking.ConcurrentDictionary<object, INode> files = new(NodePath.KeyComparer);
        static ConcurrentBag<ZipNode> zipNodes = new();
        static INode[] exes;
        Dispatcher Dispatcher = Dispatcher.CurrentDispatcher; //Constructor is called from UI thread => get its dispatcher
        SemaphoreSlim Updating = new SemaphoreSlim(1, 1);
        internal const string DefaultSort = "+" + nameof(INode.LastChangeTime);
        string filter = null, sort = DefaultSort;
        NodeFilter nodeFilter = new NodeFilter("");
        volatile object lastUpdate = DateTime.MinValue;
        volatile bool itemsComplete = true; //false => Items hold only a part of the filtered result (publishing was canceled)
        volatile bool refreshPending = false; //true => files changed and no refresh published since => Items lag behind files
        int refreshQueued = 0; //1 => a data refresh is already queued and covers all changes arriving before it runs
        volatile Task dataRefreshPublished = Task.CompletedTask; //Completes when the queued data refresh has really hit the grid
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
                TaskCompletionSource published = null;
                if (dataRefresh)
                {
                    //Coalesce data refreshes - the single queued one covers all changes arriving before it runs
                    //and it never cancels a running update (a canceled publish would leave Items partial forever
                    //under a steady stream of file system events)
                    if (Interlocked.Exchange(ref refreshQueued, 1) == 1) return;
                    //Items lag behind files until a refresh from files really publishes - a user
                    //update superseding this one must refilter, never resort the stale window in place
                    refreshPending = true;
                    //This run owns the queued refresh - its end is the moment the refreshed
                    //data is really on the grid, which the "Loaded" status waits for
                    published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    dataRefreshPublished = published.Task;
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
                        | !itemsComplete //previous publishing was canceled => Items are partial and can not be resorted in place
                        | refreshPending //files changed since the last published refresh => Items are stale
                        //A sort-only change just reorders the published Items - they already hold the
                        //complete filtered result. Only a window truncated at MaxItems needs the full
                        //filtered source: the top of the new ordering may lie outside the window.
                        | (sort != newSort && Items.Count >= MaxItems);

                    // Set current filter/sorter before possible canceling
                    filter = newFilter;
                    sort = newSort;
                    nodeFilter = new NodeFilter(filter);

                    if (IsCanceled()) return;
                    var items = GetItems(refresh, IsCanceled);
                    if (items == null || IsCanceled()) return;
                    //These items were filtered from the current files => Items catch up on publish.
                    //Changes arriving from now on queue their own refresh and set the flag again.
                    if (refresh) refreshPending = false;
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
                    published?.SetResult();
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
            await UpdateSmall(changed);
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
                //Membership screen for larger batches: most changed nodes are neither
                //published nor entering the result, and IndexOf is a full scan of a
                //possibly 100k-row list - a storm batch must not pay that per node.
                //Kept in sync on insert; rows removed below only leave harmless false
                //positives (their IndexOf just returns -1).
                var published = changed.Length > 8
                    ? new HashSet<object>(items, ReferenceEqualityComparer.Instance) : null;
                var updated = false;
                var exchanging = false; //Selection snapshot taken - only once a row really moves
                List<INode> repaint = null; //Rows changed in place - repaint without touching the list
                foreach (var node in changed)
                {
                    var index = published != null && !published.Contains(node) ? -1 : items.IndexOf(node);
                    //Present in files under its path and matching the filter => it belongs to the result
                    //(the node itself is a valid key - no full path is materialized here)
                    var include = files.TryGetValue(node, out var current) && ReferenceEquals(current, node) && nf.Matches(node);
                    //Not shown and not entering the result => the view is unaffected. Most watcher
                    //events land here - they must leave the grid (and its selection) alone.
                    if (index < 0 && !include) continue;
                    //Still shown at a position consistent with the sort => only its displayed values
                    //changed. Repaint that row in place - remove+insert would churn the selection
                    //(and the SHIFT+Up/Down anchor) for a row that does not move.
                    if (index >= 0 && include
                        && (index == 0 || compare(items[index - 1], node) <= 0)
                        && (index == items.Count - 1 || compare(node, items[index + 1]) <= 0))
                    {
                        (repaint ??= new List<INode>()).Add(node);
                        continue;
                    }
                    //Remove+Insert silently drops the row from the view's selection => snapshot it
                    //once and restore after (same INode instances get re-inserted), so a selected file
                    //stays selected while it keeps changing on disk
                    if (!exchanging)
                    {
                        exchanging = true;
                        BeforeItemsExchange();
                    }
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
                            published?.Add(node);
                            updated = true;
                        }
                    }
                }
                if (exchanging)
                {
                    while (items.Count > MaxItems) items.RemoveAt(items.Count - 1);
                    AfterItemsExchange();
                }
                if (updated) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountsInfo)));
                if (repaint != null) RowsRefreshRequested?.Invoke(repaint.ToArray());
                return true;
            });
        }

        readonly struct BatchUpdateOutcome
        {
            public bool Handled { get; }
            public bool NeedsRefresh { get; }
            public BatchUpdateOutcome(bool handled, bool needsRefresh = false)
            {
                Handled = handled;
                NeedsRefresh = needsRefresh;
            }
        }

        /// <summary>
        /// Apply a larger mixed batch with one pass over the published window and one UI
        /// reset. The ItemsSource instance stays in place so the virtualized viewport does
        /// not jump. The old per-node path performs IndexOf + RemoveAt/Insert for every item;
        /// on a 100k-row newest-first view that becomes O(rows * changes). This path is
        /// O(rows + changes log changes) and preserves the same sorted/capped result.
        /// </summary>
        async Task<BatchUpdateOutcome> TryBulkIncrementalUpdate(INode[] changed)
        {
            var currentFilter = filter;
            var currentSort = sort;
            var nf = nodeFilter;
            var compare = SortComparison(currentSort);
            if (Loading || compare == null || !itemsComplete) return new BatchUpdateOutcome(false);

            return await Dispatcher.InvokeAsync(() =>
            {
                if (Loading || !itemsComplete || currentFilter != filter || currentSort != sort)
                    return new BatchUpdateOutcome(false);

                var items = Items;
                var changedSet = new HashSet<object>(changed, ReferenceEqualityComparer.Instance);
                var originalCount = items.Count;
                var capped = originalCount >= MaxItems;
                //Changed nodes already carry their new values, so the old last item is not
                //a trustworthy boundary when it belongs to this batch. Use the last
                //unchanged row; it is slightly stricter and therefore never admits a node
                //that should remain outside the capped window.
                var oldBoundary = capped
                    ? items.Reverse().FirstOrDefault(item => !changedSet.Contains(item))
                    : null;
                var publishedChanged = new HashSet<object>(ReferenceEqualityComparer.Instance);
                var kept = new List<INode>(originalCount);
                foreach (var item in items)
                {
                    if (changedSet.Contains(item)) publishedChanged.Add(item);
                    else kept.Add(item);
                }

                var candidates = new List<INode>(changed.Length);
                var needsRefresh = capped && oldBoundary == null;
                foreach (var node in changed)
                {
                    if (!files.TryGetValue(node, out var current) || !ReferenceEquals(current, node) || !nf.Matches(node))
                        continue;

                    if (oldBoundary != null && compare(node, oldBoundary) > 0)
                    {
                        //An unpublished node still lies beyond the window => no visible
                        //effect. A formerly published node moved beyond the known boundary;
                        //a row from the unknown tail must be pulled in by a coalesced refresh.
                        if (publishedChanged.Contains(node)) needsRefresh = true;
                        continue;
                    }
                    candidates.Add(node);
                }
                candidates.Sort(compare);

                var merged = MergeSortedWindow(kept, candidates, compare, MaxItems);
                if (capped && merged.Count < MaxItems) needsRefresh = true;

                var identical = merged.Count == originalCount;
                for (var i = 0; identical && i < merged.Count; i++)
                    identical = ReferenceEquals(merged[i], items[i]);
                if (identical)
                {
                    //Metadata changed without crossing a neighbour. Rebind realized rows,
                    //but leave the collection and selection completely untouched.
                    RowsRefreshRequested?.Invoke(changed);
                    return new BatchUpdateOutcome(true, needsRefresh);
                }

                BeforeItemsExchange();
                if (items is RangeObservableCollection<INode> target) target.ReplaceRange(merged);
                else
                {
                    target = new RangeObservableCollection<INode>();
                    target.AddRange(merged);
                    Items = target;
                }
                AfterItemsExchange();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountsInfo)));
                return new BatchUpdateOutcome(true, needsRefresh);
            });
        }

        /// <summary>
        /// A metadata-only watcher event can move a row only under a metadata sort. Name,
        /// path, folder and content-result ordering are invariant, so those views need no
        /// Items.IndexOf or collection mutation at all.
        /// </summary>
        internal static bool MetadataSortMayMove(string currentSort)
        {
            if (string.IsNullOrWhiteSpace(currentSort) || currentSort.Length < 2) return false;
            var key = currentSort.Substring(1);
            return key == nameof(INode.Size)
                || key == nameof(INode.LastChangeTime)
                || key == nameof(INode.LastAccessTime);
        }

        /// <summary>Merge two already sorted sequences, keeping at most one published window.</summary>
        internal static List<T> MergeSortedWindow<T>(IReadOnlyList<T> first, IReadOnlyList<T> second,
            Comparison<T> compare, int limit)
        {
            var merged = new List<T>(Math.Min(limit, first.Count + second.Count));
            var fi = 0;
            var si = 0;
            while (merged.Count < limit && (fi < first.Count || si < second.Count))
            {
                if (si >= second.Count || fi < first.Count && compare(first[fi], second[si]) <= 0)
                    merged.Add(first[fi++]);
                else
                    merged.Add(second[si++]);
            }
            return merged;
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
        /// Remove a path and every indexed descendant. FileSystemWatcher normally emits
        /// only one rename event for a directory, so removing just that directory leaves
        /// all of its old child paths searchable forever.
        /// </summary>
        INode[] RemoveTree(string path) => RemoveTrees(new[] { path });

        /// <summary>
        /// Remove several paths and their indexed descendants in ONE pass over the index -
        /// the pass costs hundreds of milliseconds on a million-file index, so deleting
        /// nine folders at once must not scan nine times (the user watched their folders
        /// leave the grid one by one, paced by exactly that scan).
        /// </summary>
        INode[] RemoveTrees(IReadOnlyList<string> paths)
        {
            var roots = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var prefixes = new List<string>(paths.Count);
            foreach (var path in paths)
            {
                //Never uproot a whole drive from an event - only a drive scan may do that.
                //A drive-root path here is a half-delivered watcher rename/delete (see IsDriveRoot).
                if (IsDriveRoot(path)) continue;
                if (!files.TryGetValue(path, out var root)) continue;
                if (!roots.Add(root)) continue;
                prefixes.Add(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar);
            }
            if (roots.Count == 0) return Array.Empty<INode>();
            var candidates = files.Values.AsParallel()
                .Where(n => roots.Contains(n) || NodePath.IsUnderAny(n, roots, prefixes))
                .ToArray();
            var removed = new List<INode>(candidates.Length);
            foreach (var candidate in candidates)
            {
                if (!files.TryRemove(candidate, out var actual)) continue;
                removed.Add(actual);
                if (!actual.IsDirectory) PropagateSizeDelta(actual.FullName, -(long)actual.Size);
            }
            return removed.ToArray();
        }

        /// <summary>
        /// Re-index descendants after a directory rename. Windows reports the directory
        /// rename but does not report a rename for every child. Returns the added nodes.
        /// </summary>
        List<INode> ReindexRenamedTree(string oldPath, string newPath, IEnumerable<INode> removed)
        {
            var removedItems = removed.ToArray();
            var archivePaths = removedItems.OfType<ZipNode>()
                .Select(n => n.ZIP.FullName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path.Length)
                .ToArray();
            var added = new List<INode> { GetOrAddNew(newPath) };
            var oldPrefix = oldPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var newPrefix = newPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var old in removedItems
                .Where(n => n is not ZipNode)
                .Where(n => !string.Equals(n.FullName, oldPath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n.IsDirectory ? 0 : 1)
                .ThenBy(n => n.FullName.Length))
            {
                if (!old.FullName.StartsWith(oldPrefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                added.Add(GetOrAddNew(newPrefix + old.FullName.Substring(oldPrefix.Length)));
            }
            // Re-open renamed archives so their entries remain real ZipNodes rather than
            // path-only FileNodes that do not exist on disk.
            foreach (var oldArchive in archivePaths)
            {
                if (!oldArchive.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                var newArchive = newPrefix + oldArchive.Substring(oldPrefix.Length);
                if (files.TryGetValue(newArchive, out var archive)) AddArchive(archive);
            }
            return added;
        }

        static bool HasArchiveChildren(INode node)
            => node != null && zipNodes.Any(z => ReferenceEquals(z.ZIP, node));

        /// <summary>
        /// Diff these directories against the disk: prune indexed entries that no longer
        /// exist, index files that appeared. The USN watcher's safety net for journal
        /// records it could not resolve to a path (no names in the unprivileged read, file
        /// reference not in the map) - the watcher batches a storm's worth of misses into
        /// one call with the affected directories deduplicated.
        /// </summary>
        async Task ReconcileDirectories(string[] dirs)
        {
            //The drive root reconciles like any directory: one index pass plus one
            //top-level listing. Handing it to the drive scan instead would reload the
            //whole MFT - and since the FRN map is empty until a scan publishes (and
            //cleared by every rescan), root-level file activity during the scan kept
            //queuing scan reruns: a self-sustaining full-reload loop.
            var targets = new List<(INode Dir, string Prefix, HashSet<string> OnDisk)>();
            foreach (var dir in dirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                if (!files.TryGetValue(dir, out var dirNode)) continue; //Unindexed parent => nothing stale under it
                try
                {
                    targets.Add((dirNode,
                        dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        new HashSet<string>(Directory.EnumerateFileSystemEntries(dir), StringComparer.OrdinalIgnoreCase)));
                }
                catch { } //Directory gone or unreadable - its own delete record handles it
            }
            if (targets.Count == 0) return;

            //Indexed descendants whose direct-child ancestor no longer exists on disk.
            //ONE pass over the index covers every directory of the call - the stale scan
            //is a full-index walk and the watcher batches a storm's worth of misses, so
            //N directories must not cost N passes.
            var changed = new List<INode>();
            var stale = files.Values.AsParallel().Where(n =>
            {
                foreach (var (dirNode, prefix, onDisk) in targets)
                {
                    if (ReferenceEquals(n, dirNode) || !NodePath.IsUnder(n, dirNode, prefix)) continue;
                    var full = n.FullName;
                    var end = full.IndexOf(Path.DirectorySeparatorChar, prefix.Length);
                    if (!onDisk.Contains(end < 0 ? full : full.Substring(0, end))) return true;
                }
                return false;
            }).ToArray();
            foreach (var candidate in stale)
            {
                if (!files.TryRemove(candidate, out var actual)) continue;
                changed.Add(actual);
                if (!actual.IsDirectory) PropagateSizeDelta(actual.FullName, -(long)actual.Size);
            }

            //Files that appeared without a resolvable record (or before the map was filled)
            foreach (var (_, _, onDisk) in targets)
                foreach (var entry in onDisk)
                    if (!files.ContainsKey(entry)) changed.Add(GetOrAddNew(entry));

            if (changed.Count > 0) await UpdateSmall(changed);
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
            //A path that does not resolve on disk must never enter the index: it carries no
            //metadata (1601 times, empty size) and would shadow nothing real. It happens for
            //watcher events of already-vanished temp files and for the ghost paths built from
            //half-delivered renames - the vanished file's delete event (or the next scan)
            //rules the index, not this stat miss.
            if (!added.Exists) return files.TryGetValue(path, out var indexed) ? indexed : added;
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
            var changed = false;
            for (var dir = Path.GetDirectoryName(path); dir != null; dir = Path.GetDirectoryName(dir))
                if (files.TryGetValue(dir, out var d) && d.IsDirectory)
                {
                    d.AddSizeDelta(delta);
                    pendingSizeRows[d] = 0;
                    changed = true;
                }
            if (changed) RefreshSizesSoon();
        }

        int sizeRefreshQueued = 0; //1 => a row repaint covering all size changes so far is already queued
        //Directories whose aggregated size changed since the last coalesced repaint
        readonly NonBlocking.ConcurrentDictionary<INode, byte> pendingSizeRows = new();
        /// <summary>
        /// Repaint the rows of directories whose aggregated size changed, coalesced to one
        /// refresh per 2s - never per event, which would saturate the dispatcher during change
        /// storms. Only the affected rows are repainted (and only when actually on screen) -
        /// unrelated file system activity must never redraw or disturb the current view.
        /// </summary>
        void RefreshSizesSoon()
        {
            if (Interlocked.CompareExchange(ref sizeRefreshQueued, 1, 0) != 0) return;
            Task.Run(async () =>
            {
                await Task.Delay(2000);
                //Reset before the snapshot: a directory added after the snapshot finds the
                //flag cleared and queues the next round - no size change is ever dropped
                Interlocked.Exchange(ref sizeRefreshQueued, 0);
                var changed = pendingSizeRows.Keys.ToArray();
                foreach (var node in changed) pendingSizeRows.TryRemove(node, out _);
                if (changed.Length > 0) RowsRefreshRequested?.Invoke(changed);
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
                Status = search.L.Format("StatusSearching", text, searched.Count, watch.Elapsed.TotalSeconds));
            var searchText = caseInsensitive ? text.ToLowerInvariant() : text;

            // Convert search text to bytes based on encoding
            ReadOnlyMemory<byte> toFind;
            try
            {
                toFind = ConvertSearchTextToBytes(searchText, encoding);
            }
            catch (Exception ex)
            {
                Status = search.L.Format("StatusSearchFailed", ex.Message);
                Searching = false;
                return;
            }

            // Snapshot on the calling (UI) thread - Items can be exchanged/appended during the search
            var nodes = Items.Where(x => !x.IsDirectory).ToArray();
            try
            {
                await Task.Run(() => nodes.AsParallel().WithCancellation(thisFind.Token)
                    .ForAll(
                    n => searched[n] = FindFileContents(n.FullName, toFind, caseInsensitive)
                    ));
            }
            catch (OperationCanceledException) { }

            // Show results
            var result = search.L.Text(thisFind.IsCancellationRequested ? "SearchCanceled" : "SearchDone");
            var counts = searched.Values.GroupBy(x => x).ToDictionary(x => $"{x.Key}", x => x.Count()); // null can not be key in dictionary => string
            thisFind.Cancel();
            await update;
            if (ReferenceEquals(thisFind, lastFind)) //Do not overwrite state of a newer search
            {
                Searching = false;
                Status = search.L.Format("StatusSearchFinished", text, result, counts.Get("True"), counts.Get("False"), counts.Get(""), watch.Elapsed.TotalSeconds);
                UIRefreshRequested?.Invoke();

                // The parallel search and its result aggregation can leave substantial
                // short-lived data in Gen2/LOH. Compact it once the current search has
                // published its final state, never for a search superseded by a newer one.
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
            }
        }

        internal static bool? FindFileContents(string path, ReadOnlyMemory<byte> search, bool caseInsensitive = false)
        {
            const int StackBufferLength = 1 << 16;
            byte[] rented = null;
            try
            {
                if (search.Length == 0) return true;
                // The UI normally supplies a short needle, so keep the hot path allocation-
                // free. Pool only the exceptional buffer that must be larger than 64 KiB:
                // it needs room for the whole retained needle plus at least one new byte.
                var bufferLength = search.Length < StackBufferLength
                    ? StackBufferLength
                    : checked(search.Length + 1);
                scoped Span<byte> buf;
                if (bufferLength == StackBufferLength)
                    buf = stackalloc byte[StackBufferLength];
                else
                {
                    rented = ArrayPool<byte>.Shared.Rent(bufferLength);
                    buf = rented.AsSpan(0, bufferLength);
                }
                using var s = File.OpenRead(path);
                int start = 0; //Overlap kept from the previous block
                while (true)
                {
                    var read = s.Read(buf.Slice(start));
                    var len = start + read;
                    var bufSlice = buf.Slice(0, len);

                    if (caseInsensitive)
                    {
                        // Fold in place: the original bytes are not needed after matching,
                        // and the retained overlap may safely remain folded for the next read.
                        for (int i = 0; i < len; i++)
                        {
                            var b = bufSlice[i];
                            if (b >= 'A' && b <= 'Z') bufSlice[i] = (byte)(b + ('a' - 'A'));
                        }
                    }

                    if (bufSlice.IndexOf(search.Span) != -1) return true;
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
            finally
            {
                if (rented != null) ArrayPool<byte>.Shared.Return(rented);
            }
        }

        // Registrations are added on the UI thread and completed on the FS change processing thread => lock
        Dictionary<string, List<TaskCompletionSource<INode>>> OnFileCreated =
            new Dictionary<string, List<TaskCompletionSource<INode>>>(StringComparer.InvariantCultureIgnoreCase);
        Task<INode> WaitForFileCreationIf(string path, Func<bool> p)
        {
            var t = new TaskCompletionSource<INode>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (OnFileCreated)
            {
                if (!OnFileCreated.TryGetValue(path, out var l)) l = OnFileCreated[path] = new List<TaskCompletionSource<INode>>();
                l.Add(t);
            }
            bool created;
            try { created = p(); }
            catch
            {
                RemoveCreationWaiter(path, t);
                throw;
            }
            if (!created)
            {
                RemoveCreationWaiter(path, t);
                t.TrySetCanceled();
            }
            else if (!t.Task.IsCompleted && (File.Exists(path) || Directory.Exists(path)))
            {
                // FileSystemWatcher is a notification mechanism, not a delivery guarantee.
                // Complete from the filesystem after a successful operation so a dropped or
                // coalesced Created event can never leave zip/unzip awaiting forever.
                var node = GetOrAddNew(path);
                RemoveCreationWaiter(path, t);
                t.TrySetResult(node);
            }
            return t.Task;
        }

        void RemoveCreationWaiter(string path, TaskCompletionSource<INode> waiter)
        {
            lock (OnFileCreated)
            {
                if (!OnFileCreated.TryGetValue(path, out var waiters)) return;
                waiters.Remove(waiter);
                if (waiters.Count == 0) OnFileCreated.Remove(path);
            }
        }

        public SearchModel()
        {
            FSChangeProcessor.ShouldIndex = DriveSelectionStore.IsEnabled;
            FSChangeProcessor.Lookup = FindByPath;
            FSChangeProcessor.ReconcileDirs = ReconcileDirectories;
            FSChangeProcessor.Run(async d => await InitFromNTFS(d)
            , async events =>
            {
                //Mutate the index in event order, but publish the complete watcher batch once.
                //Reference sets collapse a hot file's repeated notifications to one grid row.
                var structural = new List<INode>();
                var metadata = new List<INode>();
                var structuralSet = new HashSet<object>(ReferenceEqualityComparer.Instance);
                var metadataSet = new HashSet<object>(ReferenceEqualityComparer.Instance);
                void RecordStructuralNodes(IEnumerable<INode> nodes)
                {
                    foreach (var node in nodes)
                    {
                        if (node == null) continue;
                        metadataSet.Remove(node);
                        if (structuralSet.Add(node)) structural.Add(node);
                    }
                }
                void RecordStructuralNode(INode node) => RecordStructuralNodes(new[] { node });
                void RecordMetadata(INode node)
                {
                    if (node != null && !structuralSet.Contains(node) && metadataSet.Add(node)) metadata.Add(node);
                }

                //Deletes coalesce across the batch until a non-delete needs their effect:
                //their tree removals share ONE index pass and ONE grid update, so deleting
                //nine folders empties nine rows at once instead of one by one, each paced
                //by its own full-index scan.
                var pendingDeletes = new List<string>();
                void FlushDeletes()
                {
                    if (pendingDeletes.Count == 0) return;
                    var trees = new List<string>();
                    var removed = new List<INode>();
                    foreach (var path in pendingDeletes)
                    {
                        var indexed = files.TryGetValue(path, out var i) ? i : null;
                        if (indexed?.IsDirectory == true || HasArchiveChildren(indexed)) trees.Add(path);
                        else removed.Add(Remove(path));
                    }
                    pendingDeletes.Clear();
                    if (trees.Count > 0) removed.AddRange(RemoveTrees(trees));
                    RecordStructuralNodes(removed);
                }

                foreach (var e in events)
                {
                    try
                    {
                        //Status = $"WATCHED {++watched}. changes => last {DateTime.Now.TimeOfDay} {e.FullPath}";
                        switch (e.ChangeType)
                        {
                            case WatcherChangeTypes.Created:
                                //Trace.WriteLine($"+{e.FullPath}");
                                FlushDeletes(); //A delete of this very path must apply first
                                var node = GetOrAddNew(e.FullPath);
                                RecordStructuralNode(node);
                                List<TaskCompletionSource<INode>> tcs = null;
                                lock (OnFileCreated)
                                {
                                    if (OnFileCreated.TryGetValue(e.FullPath, out tcs)) OnFileCreated.Remove(e.FullPath);
                                }
                                if (tcs != null) foreach (var x in tcs) x.TrySetResult(node);
                                break;
                            case WatcherChangeTypes.Changed:
                                //Change attributes and size
                                FlushDeletes();
                                var refreshed = Refresh(e.FullPath);
                                if (refreshed != null) RecordMetadata(refreshed);
                                else RecordStructuralNode(GetOrAddNew(e.FullPath));
                                break;
                            case WatcherChangeTypes.Renamed:
                                //Trace.WriteLine($"{e.OldFullPath}->{e.FullPath}");
                                FlushDeletes();
                                //Under watcher buffer pressure ReadDirectoryChangesW can lose one half
                                //of a rename pair; .NET then raises Renamed with an empty name whose
                                //FullPath/OldFullPath is the watcher root. Tree surgery keyed on the
                                //drive root would remove the whole index and re-add it under ghost
                                //paths (1601 times, empty sizes) - rescan the drive instead.
                                if (IsDriveRoot(e.OldFullPath) || IsDriveRoot(e.FullPath))
                                {
                                    _ = InitFromNTFS(Path.GetPathRoot(e.FullPath));
                                    break;
                                }
                                var oldRoot = files.TryGetValue(e.OldFullPath, out var indexedOld) ? indexedOld : null;
                                if (oldRoot?.IsDirectory == true || HasArchiveChildren(oldRoot))
                                {
                                    var removedTree = RemoveTree(e.OldFullPath);
                                    var added = ReindexRenamedTree(e.OldFullPath, e.FullPath, removedTree);
                                    RecordStructuralNodes(removedTree.Concat(added));
                                }
                                else
                                {
                                    //Remove first so a same-directory rename nets to zero on the shared ancestors
                                    RecordStructuralNodes(new[] { Remove(e.OldFullPath), GetOrAddNew(e.FullPath) });
                                }
                                break;
                            case WatcherChangeTypes.Deleted:
                                //Trace.WriteLine($"-{e.FullPath}");
                                //A drive-root delete is a mangled event (see the rename case) - a
                                //really vanished drive is handled by its own scan, never from here
                                if (IsDriveRoot(e.FullPath))
                                {
                                    _ = InitFromNTFS(Path.GetPathRoot(e.FullPath));
                                    break;
                                }
                                pendingDeletes.Add(e.FullPath);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Windows.MessageBox.Show(ex.ToString(), $"FS change {e.FullPath} failed");
                        await Log($"FS change {e} failed: {ex}");
                    }
                }
                try
                {
                    FlushDeletes();
                    await UpdateSmall(structural.Concat(metadata), metadataSet.Cast<INode>());
                }
                catch (Exception ex) { await Log($"FS change batch failed: {ex}"); }
            });
        }

        /// <summary>
        /// Publish a batch of added/removed/changed nodes to the grid. Pure removals of any
        /// size leave the published rows in one in-place pass. Small mixed batches use
        /// sorted row surgery; larger batches use one linear merge of the published window.
        /// </summary>
        async Task UpdateSmall(IEnumerable<INode> changed, IEnumerable<INode> metadataOnly = null)
        {
            var unique = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var nodeList = new List<INode>();
            foreach (var node in changed)
                if (node != null && unique.Add(node)) nodeList.Add(node);
            //File-size deltas mutate their ancestor directory nodes too. They must join a
            //Size-sort batch; otherwise the "kept" side of the merge is no longer sorted
            //and directory rows remain at their old positions.
            if (sort?.Length > 1 && sort.Substring(1) == nameof(INode.Size))
                foreach (var directory in pendingSizeRows.Keys)
                    if (directory != null && unique.Add(directory)) nodeList.Add(directory);
            var nodes = nodeList.ToArray();
            if (nodes.Length == 0) return;

            //Changed does not alter path-based filter membership. A non-matching node can
            //never be published; under a non-metadata sort a matching node cannot move,
            //so only ask the UI to repaint it if its container is actually realized.
            if (metadataOnly != null)
            {
                var metadata = new HashSet<object>(metadataOnly, ReferenceEqualityComparer.Instance);
                if (metadata.Count > 0)
                {
                    var filterNow = nodeFilter;
                    var mayMove = MetadataSortMayMove(sort);
                    var repaint = new List<INode>();
                    var work = new List<INode>(nodes.Length);
                    foreach (var node in nodes)
                    {
                        if (!metadata.Contains(node))
                        {
                            work.Add(node);
                            continue;
                        }
                        if (!filterNow.Matches(node)) continue;
                        if (mayMove) work.Add(node);
                        else repaint.Add(node);
                    }
                    if (repaint.Count > 0) RowsRefreshRequested?.Invoke(repaint.ToArray());
                    nodes = work.ToArray();
                    if (nodes.Length == 0) return;
                }
            }

            if (await TryBulkRemove(nodes)) return;
            if (nodes.Length <= 8)
            {
                if (await TryIncrementalUpdate(nodes)) return;
            }
            else
            {
                var bulk = await TryBulkIncrementalUpdate(nodes);
                if (bulk.Handled)
                {
                    if (bulk.NeedsRefresh) _ = Update();
                    return;
                }
            }
            var nf = nodeFilter;
            if (nodes.Any(nf.Matches)) _ = Update();
        }

        /// <summary>
        /// Remove a batch of no-longer-indexed nodes from the published rows in one pass -
        /// unlike the per-node sorted surgery this needs no IndexOf per node, so it stays
        /// instant for a folder of any size. Pure removals cannot disturb the sort order,
        /// so only completeness of the published window is revalidated.
        /// </summary>
        /// <returns>false => not a pure-removal batch (or Items are partial) - the caller
        /// must fall back to the incremental/full update</returns>
        async Task<bool> TryBulkRemove(INode[] removed)
        {
            if (Loading || !itemsComplete) return false;
            //Still indexed under its path => an addition/change, not a removal
            if (removed.Any(n => files.TryGetValue(n, out var current) && ReferenceEquals(current, n))) return false;

            var needsRefresh = false;
            var handled = await Dispatcher.InvokeAsync(() =>
            {
                if (!itemsComplete) return false; //Revalidate on the UI thread - Items are exchanged only here
                var items = Items;
                var set = new HashSet<object>(removed, ReferenceEqualityComparer.Instance);
                List<int> hits = null;
                for (var i = 0; i < items.Count; i++)
                    if (set.Contains(items[i])) (hits ??= new List<int>()).Add(i);

                //A capped window needs rows pulled in from beyond MaxItems after a visible
                //removal. Deleted files also changed surviving ancestor directory sizes,
                //which can invalidate a size ordering even when no deleted row is visible.
                needsRefresh = BulkRemovalNeedsRefresh(items.Count, sort, hits != null);
                if (hits == null) return true; //Nothing published - the grid (and selection) stays untouched

                BeforeItemsExchange();
                if (hits.Count <= 300)
                {
                    //Few visible rows - remove in place (backwards: earlier indexes stay valid)
                    for (var i = hits.Count - 1; i >= 0; i--) items.RemoveAt(hits[i]);
                }
                else
                {
                    //Each RemoveAt shifts the tail of a possibly 100k-row list - beyond a few
                    //hundred rows one rebuilt collection is cheaper than the shifts
                    var target = new RangeObservableCollection<INode>();
                    target.AddRange(items.Where(x => !set.Contains(x)));
                    Items = target;
                }
                AfterItemsExchange();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountsInfo)));
                return true;
            });

            //Keep the deletion instant, then let the existing coalesced refresh refill or
            //resort in the background without holding up this drive's event queue.
            if (handled && needsRefresh) _ = Update();
            return handled;
        }

        internal static bool BulkRemovalNeedsRefresh(int itemCount, string currentSort, bool visibleRowsRemoved)
            => visibleRowsRemoved && itemCount >= MaxItems
                || currentSort?.Length > 1 && currentSort.Substring(1) == nameof(INode.Size);

        /// <summary>
        /// True for "C:\" (any drive/watcher root) and for null/empty. Watcher events carrying
        /// such a path are half-delivered renames/deletes - ReadDirectoryChangesW lost the other
        /// half of the pair to buffer pressure and .NET substituted the watcher root.
        /// </summary>
        internal static bool IsDriveRoot(string path)
            => string.IsNullOrEmpty(path) || string.IsNullOrEmpty(Path.GetDirectoryName(path));

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

        // Per-drive pending scan requests - each drive loads and publishes independently, so a
        // hung network walk can never gate an NTFS drive's results or block its refresh
        readonly NonBlocking.ConcurrentDictionary<string, int[]> driveRequests = new(StringComparer.OrdinalIgnoreCase);
        // Per-drive load origin for the status tooltip - survives across scans
        readonly NonBlocking.ConcurrentDictionary<string, MftOrigin> origins = new(StringComparer.OrdinalIgnoreCase);
        readonly object publishLock = new();     //Serializes finished-drive merges into files
        readonly object loadStatusLock = new();  //Guards the load burst bookkeeping below
        int scanningDrives;                      //Number of drives currently scanning
        long loadingNodes;                       //Nodes streamed in by running scans, not yet published to files
        Stopwatch loadWatch;                     //Duration of the load burst (first drive in => last drive out)
        CancellationTokenSource loadProgress;
        Task loadProgressTask;

        /// <summary>
        /// Refresh the files of one drive (root form "C:\"). Concurrent requests for the same
        /// drive are coalesced into a single scan; different drives scan independently, so a
        /// slow or hung drive (dead network mapping, SSHFS walk) only ever costs itself.
        /// </summary>
        Task InitFromNTFS(string root)
        {
            var pending = driveRequests.GetOrAdd(root, _ => new int[1]);
            //The already queued/running scan of this drive covers this request too
            if (Interlocked.Increment(ref pending[0]) > 1) return Task.CompletedTask;
            return Task.Run(async () =>
            {
                BeginDriveScan();
                try
                {
                    while (true)
                    {
                        await Task.Delay(500); //Batch the burst of watcher (re)starts
                        var requested = Volatile.Read(ref pending[0]);
                        $"drive {root} scan started (covering {requested} requests)".Debug();
                        var key = root.TrimEnd(Path.DirectorySeparatorChar);
                        var streamed = 0L;
                        try
                        {
                            var fresh = new NonBlocking.ConcurrentDictionary<object, INode>(NodePath.KeyComparer);
                            var drive = new DriveInfo(root);
                            //IsReady of an unreachable network drive blocks in SMB timeouts for
                            //tens of seconds - probe with a deadline so a dead mapping only costs
                            //itself; the readiness probe must run before the selection check,
                            //whose DriveFormat query would block on the same dead mappings
                            var skip = !IsReadyFast(drive) ? "not ready"
                                : !DriveSelectionStore.IsEnabled(root) ? "not selected for indexing"
                                : null;
                            if (skip == null)
                            {
                                var entries = DriveEntries(drive, out var origin);
                                //The node itself is the key - no full path string is ever built or stored
                                foreach (var n in entries)
                                {
                                    fresh[n] = n;
                                    //Feed the loading status while the drive streams in
                                    streamed++;
                                    Interlocked.Increment(ref loadingNodes);
                                }
                                origins[key] = origin;
                            }
                            else
                            {
                                $"drive {root} {skip} => its entries are dropped".Debug();
                                origins.TryRemove(key, out _);
                            }
                            PublishDrive(root, fresh, streamed); //Empty for a skipped drive => stale entries pruned
                            streamed = 0; //Deducted from the loading counter at the publish
                        }
                        catch { } //A failed scan keeps the drive's last good entries
                        finally { Interlocked.Add(ref loadingNodes, -streamed); } //Not published => not counted by files

                        //Update filtered files
                        await Update();

                        //Quit when no new request for this drive arrived during the scan
                        if (Interlocked.CompareExchange(ref pending[0], 0, requested) == requested) return;
                        $"drive {root} scan rerun: {Volatile.Read(ref pending[0]) - requested} new requests arrived during the scan".Debug();
                    }
                }
                finally
                {
                    await EndDriveScan();
                }
            });
        }

        /// <summary>
        /// Swap one drive's subtree in the shared index: everything indexed under the drive is
        /// replaced by the fresh scan while all other drives' entries stay untouched. A finished
        /// drive shows immediately - a fast MFT drive never waits on the slowest drive's walk.
        /// </summary>
        void PublishDrive(string root, NonBlocking.ConcurrentDictionary<object, INode> fresh, long streamed = 0)
        {
            //Nothing new and nothing indexed under the drive => skip the index rebuild
            //(the common case for skipped/deselected drives on every refresh)
            if (fresh.IsEmpty && !files.ContainsKey(root))
            {
                Interlocked.Add(ref loadingNodes, -streamed);
                return;
            }
            lock (publishLock)
            {
                files.TryGetValue(root, out var oldRoot); //Indexed drive root => identity checks on chained nodes
                var merged = new NonBlocking.ConcurrentDictionary<object, INode>(NodePath.KeyComparer);
                foreach (var kv in files)
                    if (!NodePath.IsUnder(kv.Value, oldRoot, root)) merged[kv.Key] = kv.Value;
                foreach (var kv in fresh) merged[kv.Key] = kv.Value;
                files = merged;
                //files counts the streamed nodes from this very moment => deduct them from the
                //streaming counter in the same breath, or the loading status double-counts them
                //for as long as the archive re-add and exe recompute below take
                Interlocked.Add(ref loadingNodes, -streamed);

                // Re-add previously expanded archives that still exist
                var zn = zipNodes.ToArray();
                zipNodes.Clear();
                zn.Select(x => x.ZIP.FullName).Distinct().OrderBy(x => x).ForEach(x =>
                  {
                      if (files.TryGetValue(x, out var n)) AddArchive(n);
                  });
            }
            exes = files.Values.AsParallel()
                .Where(n => !n.IsDirectory && NodePath.LeafEndsWith(n, ".exe")).ToArray();
            //MFT nodes carry their file reference numbers - hand them to the drive's USN
            //watcher so deleted/renamed files resolve to paths (journal records have no names)
            FSChangeProcessor.PopulateFrnMap(root, fresh.Values);
            LoadStatusTooltip = OriginsInfo(origins).Trim();
        }

        /// <summary>
        /// First drive of a load burst => show the loading status and start its ticker
        /// </summary>
        void BeginDriveScan()
        {
            lock (loadStatusLock)
            {
                if (++scanningDrives != 1) return;
                Loading = true;
                LoadStatusTooltip = null;
                ntfsWalked = false;
                loadWatch = Stopwatch.StartNew();
                loadProgress = new CancellationTokenSource();
                var ct = loadProgress.Token;
                var watch = loadWatch;
                //A dedicated thread, not an async pool loop: the drive walks can saturate the
                //thread pool at startup and a starved ticker freezes the status at "0 files - 0s"
                //(LoadStatus binds via INPC => no UI refresh needed)
                loadProgressTask = Task.Factory.StartNew(() =>
                {
                    while (!ct.IsCancellationRequested)
                    {
                        //The larger of indexed vs streaming-in: their sum would double-count a
                        //rescan, whose fresh nodes replace indexed ones instead of adding to them
                        var count = Math.Max(files.Count, Volatile.Read(ref loadingNodes));
                        LoadStatus = search.L.Format("StatusLoadingDrives", count, (int)watch.Elapsed.TotalSeconds);
                        ct.WaitHandle.WaitOne(1000);
                    }
                }, TaskCreationOptions.LongRunning);
            }
        }

        /// <summary>
        /// Last drive of a load burst => final status + memory cleanup. Drives still scanning
        /// (e.g. a hung network walk) keep the ticker alive without blocking anything else.
        /// </summary>
        async Task EndDriveScan()
        {
            Task ticker;
            Stopwatch watch;
            lock (loadStatusLock)
            {
                if (--scanningDrives != 0) return;
                loadProgress.Cancel();
                ticker = loadProgressTask;
                watch = loadWatch;
                Loading = false;
            }
            await ticker;
            //The scan's last Update may have been covered by an already queued refresh =>
            //report "Loaded" only once the refreshed results are actually on the grid
            await dataRefreshPublished;
            LoadStatus = search.L.Format("StatusLoadedDrives", files.Count, watch.Elapsed.TotalSeconds);
            LoadStatusTooltip = OriginsInfo(origins).Trim();
            LoadStatus.Debug();
            // Clean freed memory
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
            UIRefreshRequested?.Invoke();
        }

        async Task Log(string text)
        {
            var tid = Thread.CurrentThread.ManagedThreadId; //Save logging thread id
            await StorageMaintenance.AppendLogAsync("log.txt", $"{DateTime.Now} {tid} {text}{Environment.NewLine}");
        }

        /// <summary>
        /// DriveInfo.IsReady with a deadline: an unreachable network drive blocks the query
        /// in SMB timeouts for tens of seconds - abandon the probe and report not ready.
        /// Local fixed drives are queried directly: they cannot hang, and they must never be
        /// lost to a probe that started late - after a reboot the network mappings' probes
        /// can occupy the whole thread pool long enough to time the deadline out for C: itself
        /// </summary>
        static bool IsReadyFast(DriveInfo d)
        {
            try
            {
                if (d.DriveType == DriveType.Fixed) return d.IsReady;
                //A dedicated thread, not the pool: the deadline must measure the probe itself,
                //not its queue time, and an abandoned probe must not hold a pool thread
                var ready = false;
                var probe = new Thread(() => { try { ready = d.IsReady; } catch { } }) { IsBackground = true };
                probe.Start();
                return probe.Join(TimeSpan.FromSeconds(5)) && ready;
            }
            catch { return false; }
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
                // A crafted archive may contain rooted or parent-traversing names. Do not
                // let those entries escape the archive's virtual subtree (or abort adding
                // every otherwise valid entry in the archive).
                var src = a.Entries.AsParallel().Where(e => ZipExtensions.IsSafeArchivePath(e.Key));
                // Trim the trailing separator that directory entries carry ("Logs/PHP/") so an
                // explicit directory entry is recognised as the same folder later synthesized
                // from its children - otherwise both are indexed and one shows as a blank row.
                var keys = src.Select(e => e.Key.AsMemory().TrimEnd("/\\")).Where(x => !x.IsEmpty);
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

                // Aggregate uncompressed entry sizes into their ancestor archive folders so
                // directory rows show a real size instead of 0 (mirrors how the MFT reader and
                // DirectoryWalker size on-disk folders). Stop at the archive root - the .zip's own
                // node keeps its on-disk (compressed) size and real filesystem folders are untouched.
                var root = n.FullName;
                foreach (var zn in zipNodes.Where(z => ReferenceEquals(z.ZIP, n) && !z.IsDirectory))
                    for (var dir = Path.GetDirectoryName(zn.FullName);
                         dir != null && dir.Length > root.Length;
                         dir = Path.GetDirectoryName(dir))
                        if (files.TryGetValue(dir, out var d) && d is ZipNode dzn && dzn.IsDirectory)
                            dzn.AddSize(zn.Size);
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
                return WaitForFileCreationIf(dir, () => z.Unzip(dir, call7zip));
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

        public void ReplaceRange(IEnumerable<T> replacements)
        {
            Items.Clear();
            foreach (var x in replacements) Items.Add(x);
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

}
