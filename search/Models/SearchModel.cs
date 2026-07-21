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

        /// <summary>Health of automatic filesystem-to-grid delivery.</summary>
        public LiveUpdateHealthState LiveHealthState { get; private set; } = LiveUpdateHealthState.Healthy;
        public long LiveUpdateLatencyMs { get; private set; }
        public string LiveUpdateHealthText { get; private set; } = "";
        public string LiveUpdateHealthDetails { get; private set; } = "";
        public bool LiveUpdateHealthMessageVisible { get; private set; }

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
        static readonly DriveNodeIndex files = new();
        static ConcurrentBag<ZipNode> zipNodes = new();
        static INode[] exes;
        Dispatcher Dispatcher = Dispatcher.CurrentDispatcher; //Constructor is called from UI thread => get its dispatcher
        SemaphoreSlim Updating = new SemaphoreSlim(1, 1);
        readonly LiveUpdateHealthMonitor health;
        internal const string DefaultSort = "+" + nameof(INode.LastChangeTime);
        string filter = null, sort = DefaultSort;
        NodeFilter nodeFilter = new NodeFilter("");
        /// <summary>
        /// Post-filter snapshot of the index, reused while index MEMBERSHIP and the filter
        /// are unchanged - repeated column sorts and coalesced refreshes then skip the
        /// full-dictionary copy, the most memory-bound step of a query. Node metadata may
        /// mutate freely underneath: the list holds references and every sort reads live
        /// values. Small add/remove batches advance <see cref="filesVersion"/> and patch an
        /// immutable cache overlay; large changes (and directory changes for an explicit
        /// directory filter) retire it. A drive publish additionally brackets its replacement with a
        /// bulk-mutation epoch, so a partially replaced index is never cached or published.
        /// </summary>
        internal sealed class SnapshotCache
        {
            const int MaxPatchedNodes = 1024;
            public readonly string Filter;
            public readonly int Version;
            public readonly IReadOnlyList<INode> Nodes;
            public readonly NodeFilter Matcher;
            public readonly INode[] Added;
            public readonly HashSet<INode> Removed;
            public bool HasDeltas => Added.Length != 0 || Removed.Count != 0;

            public SnapshotCache(string filter, int version, IReadOnlyList<INode> nodes, NodeFilter matcher,
                INode[] added = null, HashSet<INode> removed = null)
            {
                Filter = filter;
                Version = version;
                Nodes = nodes;
                Matcher = matcher;
                Added = added ?? Array.Empty<INode>();
                Removed = removed ?? new HashSet<INode>(ReferenceEqualityComparer.Instance);
            }

            public SnapshotCache Patch(int version, IReadOnlyList<INode> added, IReadOnlyList<INode> removed)
            {
                if (Added.Length + Removed.Count + added.Count + removed.Count > MaxPatchedNodes)
                    return null; //A large structural change is cheaper and safer to rebuild.

                var nextAdded = new List<INode>(Added);
                var nextRemoved = new HashSet<INode>(Removed, ReferenceEqualityComparer.Instance);
                bool Matches(INode node) => Matcher.MatchesAll || Matcher.Matches(node);

                foreach (var node in removed)
                {
                    if (node == null) continue;
                    //Always remember a removal. The node may no longer match after mutable
                    //metadata/path state changed, and it may already be present in Added.
                    //Removing from both sides is harmless when the base never contained it.
                    var at = nextAdded.FindIndex(x => ReferenceEquals(x, node));
                    if (at >= 0) nextAdded.RemoveAt(at);
                    nextRemoved.Add(node);
                }
                foreach (var node in added)
                {
                    if (node == null || !Matches(node)) continue;
                    //Keep the addition even when the same instance was removed. Materialize
                    //de-duplicates it against the base, which also closes the narrow race where
                    //dictionary insertion becomes visible just before its version bump.
                    nextRemoved.Remove(node);
                    if (!nextAdded.Any(x => ReferenceEquals(x, node))) nextAdded.Add(node);
                }
                return new SnapshotCache(Filter, version, Nodes, Matcher, nextAdded.ToArray(), nextRemoved);
            }

            public IReadOnlyList<INode> Materialize(Func<bool> isCanceled)
            {
                if (!HasDeltas) return Nodes;
                var result = new List<INode>(Math.Max(0, Nodes.Count + Added.Length - Removed.Count));
                var pendingAdded = Added.Length == 0 ? null
                    : new HashSet<INode>(Added, ReferenceEqualityComparer.Instance);
                var seen = 0;
                foreach (var node in Nodes)
                {
                    if ((++seen & 0x1FFF) == 0 && isCanceled()) return null;
                    if (Removed.Contains(node)) continue;
                    result.Add(node);
                    pendingAdded?.Remove(node);
                }
                if (pendingAdded != null)
                    foreach (var node in Added)
                        if (pendingAdded.Contains(node)) result.Add(node);
                return result;
            }
        }
        static readonly object snapshotCacheLock = new();
        static volatile SnapshotCache snapshotCache;
        static int filesVersion; //Bumped by every index membership change
        static void BumpFilesVersion()
        {
            lock (snapshotCacheLock)
            {
                Interlocked.Increment(ref filesVersion);
                snapshotCache = null;
            }
        }
        static void PatchFilesVersion(IReadOnlyList<INode> added, IReadOnlyList<INode> removed)
        {
            lock (snapshotCacheLock)
            {
                var previous = Volatile.Read(ref filesVersion);
                var version = Interlocked.Increment(ref filesVersion);
                var cache = snapshotCache;
                try
                {
                    //Only an explicit directory criterion caches ancestor identity. Other
                    //filters can patch small directory changes just like file changes.
                    var changesResolvedDirectory = cache?.Matcher.DependsOnDirectoryIdentity == true
                        && (added.Any(n => n?.IsDirectory == true)
                        || removed.Any(n => n?.IsDirectory == true));
                    snapshotCache = !changesResolvedDirectory && cache != null && cache.Version == previous
                        ? cache.Patch(version, added, removed)
                        : null;
                }
                catch
                {
                    //Cache maintenance must never break the authoritative index mutation.
                    snapshotCache = null;
                }
            }
        }
        static void PatchFileAdded(INode node)
            => PatchFilesVersion(new[] { node }, Array.Empty<INode>());
        static void PatchFileRemoved(INode node)
            => PatchFilesVersion(Array.Empty<INode>(), new[] { node });
        static int bulkFilesMutations;
        static int bulkFilesVersion;
        static void BeginBulkFilesMutation() => Interlocked.Increment(ref bulkFilesMutations);
        static void EndBulkFilesMutation()
        {
            //Publish the completed epoch before readers are allowed to observe an idle index.
            Interlocked.Increment(ref bulkFilesVersion);
            Interlocked.Decrement(ref bulkFilesMutations);
        }
        static bool TryCaptureBulkFilesVersion(out int version)
        {
            version = 0;
            if (Volatile.Read(ref bulkFilesMutations) != 0) return false;
            version = Volatile.Read(ref bulkFilesVersion);
            return Volatile.Read(ref bulkFilesMutations) == 0;
        }
        static bool IsBulkFilesVersionStable(int version)
            => Volatile.Read(ref bulkFilesMutations) == 0
                && Volatile.Read(ref bulkFilesVersion) == version;

        volatile object lastUpdate = DateTime.MinValue;
        volatile bool itemsComplete = true; //false => Items hold only a part of the filtered result (publishing was canceled)
        //True means Items are only the published MaxItems window. Unlike Items.Count this
        //survives incremental removals, so a column change never sorts an incomplete window.
        volatile bool itemsTruncated;
        volatile bool refreshPending = false; //true => files changed and no refresh published since => Items lag behind files
        int refreshQueued = 0; //1 => a data refresh is already queued and covers all changes arriving before it runs
        int deferredReconciliationQueued; //1 => a fast local sort has already queued its authoritative refresh
        int tailReconciliationQueued; //1 => one quiet-time refill of a capped window is pending
        long tailReconciliationRequestedAt;
        volatile Task dataRefreshPublished = Task.CompletedTask; //Completes when the queued data refresh has really hit the grid
        const int MaxItems = 100000; //Publish just the first 100 000 filtered items
        internal const int IncrementalBatchLimit = 64;
        internal const int TailReconciliationQuietMs = 2500;
        internal const int MaxDataRefreshVersionRetries = 2;

        public Action BeforeItemsExchange = () => { };
        public Action AfterItemsExchange = () => { };

        public Task Update(string newFilter = null, string newSort = null)
            => UpdateCore(newFilter, newSort, healthRecovery: false, skipDataDebounce: false);

        async Task UpdateCore(string newFilter, string newSort, bool healthRecovery, bool skipDataDebounce)
        {
            var dataRefreshRequest = (newFilter ?? newSort) == null;
            if (dataRefreshRequest && !healthRecovery) health?.GridUpdatePending();
            //In degraded mode filesystem events still mutate the authoritative index, but
            //automatic presentation work collapses to one recovery refresh. User filter/sort
            //commands carry a non-null argument and remain available.
            if (dataRefreshRequest && !healthRecovery && health?.SuspendAutomaticGridUpdates == true)
            {
                refreshPending = true;
                return;
            }

            // A data refresh is already queued and covers this change too => skip the Task.Run
            // (change storms during a load would otherwise spawn thousands of tasks per second)
            if (dataRefreshRequest && Volatile.Read(ref refreshQueued) == 1)
            {
                if (!healthRecovery) return;
                //A refresh can own the queue bit for a few instructions before publishing
                //its completion task. Wait without recursive polling; degraded mode prevents
                //new automatic refreshes from continually taking the bit behind it.
                while (Volatile.Read(ref refreshQueued) == 1)
                {
                    var outstanding = dataRefreshPublished;
                    if (outstanding.IsCompleted) await Task.Delay(25);
                    else await outstanding;
                }
            }

            // Return immediately => do not slow down UI!!!
            await Task.Run(async () =>
            {
                // null in both filter and sort means unchanged => pure data refresh
                var dataRefresh = (newFilter ?? newSort) == null;
                var created = DateTime.Now;
                object update = created;
                TaskCompletionSource published = null;
                var authoritativePublished = false;
                var plannedDelayMs = 0L;
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
                    if (!skipDataDebounce)
                    {
                        var delayWatch = Stopwatch.StartNew();
                        await Task.Delay(1000); //Batch bursts of changes
                        plannedDelayMs = delayWatch.ElapsedMilliseconds;
                    }
                }
                else
                {
                    //User change - take over the pipeline immediately, the running update is canceled
                    lastUpdate = update;
                    Filtering = true;

                    //Debounce typing - the new update starts after a short pause
                    if (newFilter != null && filter != newFilter)
                    {
                        var delayWatch = Stopwatch.StartNew();
                        await Task.Delay(150);
                        plannedDelayMs = delayWatch.ElapsedMilliseconds;
                        if (!ReferenceEquals(update, lastUpdate)) return;
                    }
                }

                // Do not run for out dated filter/sort/files
                bool IsCanceled() => !ReferenceEquals(update, lastUpdate);

                var waitWatch = Stopwatch.StartNew();
                var queueDeferredReconciliation = false;
                var queueTailReconciliation = false;
                await Updating.WaitAsync();
                var waitMs = waitWatch.ElapsedMilliseconds;
                var refreshGeneration = health?.RefreshStarted(plannedDelayMs, waitMs) ?? 0;
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
                    var filterChanged = filter != newFilter;
                    var sortChanged = sort != newSort;
                    //A complete small result can be re-sorted immediately even when files
                    //changed. The authoritative refilter follows asynchronously, so a rare
                    //full-index reconciliation never holds the header click for seconds.
                    var deferReconciliation = CanResortPublishedItems(dataRefresh, filterChanged,
                        sortChanged, itemsComplete, itemsTruncated, refreshPending);
                    var refresh = dataRefresh
                        | filterChanged //refilter also on new filter
                        | !itemsComplete //previous publishing was canceled => Items are partial and can not be resorted in place
                        | (refreshPending && !deferReconciliation) //small complete views reconcile after their immediate sort
                        //A sort-only change can reorder Items only when they hold the complete
                        //filtered result. The persistent flag matters after visible removals make
                        //a capped window temporarily smaller than MaxItems.
                        | SortChangeNeedsFullSource(sort, newSort, itemsTruncated);
                    var refreshReasons = new List<string>();
                    if (dataRefresh) refreshReasons.Add("data");
                    if (filterChanged) refreshReasons.Add("filter");
                    if (!itemsComplete) refreshReasons.Add("incomplete");
                    if (refreshPending && !deferReconciliation) refreshReasons.Add("pending");
                    if (SortChangeNeedsFullSource(sort, newSort, itemsTruncated)) refreshReasons.Add("truncated-sort");
                    var refreshReason = deferReconciliation ? "items+deferred"
                        : refreshReasons.Count == 0 ? "items" : string.Join("+", refreshReasons);

                    // Set current filter/sorter before possible canceling
                    filter = newFilter;
                    sort = newSort;
                    nodeFilter = new NodeFilter(filter);

                    if (IsCanceled()) return;
                    var queryWatch = Stopwatch.StartNew();
                    var query = GetItems(refresh, IsCanceled,
                        dataRefresh ? MaxDataRefreshVersionRetries : int.MaxValue);
                    if (query == null || IsCanceled())
                    {
                        //A membership storm can invalidate an expensive 100k-row selection
                        //again and again. Incremental delivery is already keeping the visible
                        //window live; abandon this run after a bounded number of retries and
                        //reconcile once the change stream has been quiet for a moment.
                        if (dataRefresh && !IsCanceled()) queueTailReconciliation = true;
                        return;
                    }
                    var items = query.Value.Items;
                    var queryMs = queryWatch.ElapsedMilliseconds;
                    health?.RefreshQueryCompleted(queryMs, items.Count, query.Value.CacheStatus);
                    //These items were filtered from the current files => Items catch up on publish.
                    //Changes arriving from now on queue their own refresh and set the flag again.
                    if (refresh) refreshPending = false;
                    //Items is mutated on the UI thread (incremental updates) => the comparison may
                    //see a torn list; publish on any doubt instead of dropping the refresh
                    try
                    {
                        if (items.IsIdentical(Items))
                        {
                            itemsTruncated = query.Value.Truncated;
                            authoritativePublished = refresh;
                            queueDeferredReconciliation = deferReconciliation;
                            $"update {(dataRefresh ? "refresh" : "user")} sort={sort} rows={items.Count}: wait {waitMs} ms, reason={refreshReason}, cache={query.Value.CacheStatus}, query {queryMs} ms, publish 0 ms".Debug();
                            return;
                        }
                    }
                    catch { }

                    //Publish in ONE pass on the existing collection. Every extra batch costs a
                    //full collection Reset - viewport regeneration plus a render pass - and
                    //exchanging the ItemsSource instance also resets the scroll and defeats
                    //container recycling. The filter+sort is fast now, so this single Reset is
                    //the moment the user perceives as the sort completing. The UI thread block
                    //is only the raw list copy - WPF processes the Reset at render time.
                    var applied = false;
                    var uiAppliedAt = 0L;
                    itemsComplete = false;
                    var dispatcherWatch = Stopwatch.StartNew();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        var dispatcherWaitMs = dispatcherWatch.ElapsedMilliseconds;
                        var applyWatch = Stopwatch.StartNew();
                        try
                        {
                            if (IsCanceled()) return; //Do not flash outdated results
                            BeforeItemsExchange();
                            if (Items is RangeObservableCollection<INode> target) target.ReplaceRange(items);
                            else
                            {
                                var fresh = new RangeObservableCollection<INode>();
                                fresh.AddRange(items);
                                Items = fresh;
                            }
                            itemsTruncated = query.Value.Truncated;
                            applied = true;
                            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountsInfo)));
                            AfterItemsExchange();
                        }
                        finally
                        {
                            health?.RefreshApplied(dispatcherWaitMs, applyWatch.ElapsedMilliseconds);
                            uiAppliedAt = Environment.TickCount64;
                        }
                    }, DispatcherPriority.Background);
                    if (applied && health != null && uiAppliedAt != 0)
                    {
                        //A collection Reset schedules DataBind/render work after this callback.
                        //Measure when all higher-priority WPF work has drained without holding
                        //the refresh semaphore or altering presentation behavior.
                        try
                        {
                            _ = Dispatcher.BeginInvoke(() => health.RefreshUiSettled(refreshGeneration,
                                Math.Max(0, Environment.TickCount64 - uiAppliedAt)),
                                DispatcherPriority.ContextIdle);
                        }
                        catch { }
                    }
                    itemsComplete = applied && !IsCanceled();
                    authoritativePublished = refresh && itemsComplete;
                    queueDeferredReconciliation = applied && deferReconciliation;
                    $"update {(dataRefresh ? "refresh" : "user")} sort={sort} rows={items.Count}: wait {waitMs} ms, reason={refreshReason}, cache={query.Value.CacheStatus}, query {queryMs} ms, publish {queryWatch.ElapsedMilliseconds - queryMs} ms".Debug();
                }
                catch (Exception e)
                {
                    await Log($"Exception: {e.Message}");
                }
                finally
                {
                    health?.RefreshCompleted();
                    if (authoritativePublished) health?.GridUpdateReflected();
                    if (!IsCanceled()) Filtering = false; //When canceled the newer update owns the indicator
                    Updating.Release(); //Allow next change
                    published?.SetResult();
                    if (queueDeferredReconciliation) QueueDeferredReconciliation();
                    if (queueTailReconciliation) QueueTailReconciliation();
                }
            });
        }

        internal static bool CanResortPublishedItems(bool dataRefresh, bool filterChanged, bool sortChanged,
            bool complete, bool truncated, bool refreshPending)
            => !dataRefresh && !filterChanged && sortChanged && complete && !truncated && refreshPending;

        internal static bool SortChangeNeedsFullSource(string currentSort, string newSort, bool truncated)
            => truncated && currentSort != newSort;

        void QueueDeferredReconciliation()
        {
            //The recovery refresh is authoritative and already covers this work. Spawning a
            //new immediately-suppressed task from every finally block would form a hot loop.
            if (health?.SuspendAutomaticGridUpdates == true) return;
            if (Interlocked.Exchange(ref deferredReconciliationQueued, 1) == 1) return;
            //A data refresh that the user sort superseded still owns refreshQueued until its
            //finally block completes. Wait for that exact task, then enqueue one fresh run
            //under the newly selected sort without extending the header-click await.
            var supersededRefresh = dataRefreshPublished;
            _ = Task.Run(async () =>
            {
                try
                {
                    await supersededRefresh;
                    if (refreshPending) await Update();
                }
                finally
                {
                    Interlocked.Exchange(ref deferredReconciliationQueued, 0);
                    //A newer user sort may have canceled the run while this queue bit was
                    //owned. Hand the still-pending reconciliation to a fresh worker.
                    if (refreshPending) QueueDeferredReconciliation();
                }
            });
        }

        void ApplyLiveUpdateHealth(LiveUpdateHealthSnapshot snapshot)
        {
            LiveHealthState = snapshot.State;
            LiveUpdateLatencyMs = snapshot.DisplayLatencyMs;
            LiveUpdateHealthMessageVisible = snapshot.State != LiveUpdateHealthState.Healthy;
            LiveUpdateHealthText = snapshot.State switch
            {
                LiveUpdateHealthState.Delayed => L.Format("LiveUpdatesDelayed", snapshot.DisplayLatencyMs),
                LiveUpdateHealthState.Suspended => L.Text("LiveUpdatesSuspended"),
                LiveUpdateHealthState.Recovering => L.Text("LiveUpdatesRecovering"),
                _ => ""
            };
            var queue = string.IsNullOrWhiteSpace(snapshot.QueueRoot)
                ? snapshot.QueueDepth.ToString()
                : $"{snapshot.QueueRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)} "
                    + $"{snapshot.QueueRootDepth}/{snapshot.QueueDepth}";
            LiveUpdateHealthDetails = L.Format("LiveUpdatesHealthDetails",
                snapshot.DisplayLatencyMs, snapshot.DispatcherDelayMs,
                snapshot.ReflectionLatencyMs, queue, snapshot.OldestEventAgeMs,
                snapshot.RefreshDurationMs, snapshot.CpuCores,
                snapshot.PrivateMemoryBytes / (1024d * 1024));
        }

        async Task<bool> CatchUpLiveGrid()
        {
            try
            {
                //The ten-sample quiet window already coalesced the incident; another one-second
                //debounce would only prolong the intentionally stale red interval.
                await UpdateCore(null, null, healthRecovery: true, skipDataDebounce: true);
                return true;
            }
            catch (Exception e)
            {
                await Log($"Live grid catch-up failed: {e}");
                return false;
            }
        }

        public void Dispose() => health?.Dispose();

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
            //Medium batches remain targeted even while a drive scan runs. The old Loading
            //fallback converted every ordinary watcher notification into a complete Reset;
            //WPF's deferred DataBind/render work then accumulated faster than it could drain.
            //Large loading batches are intercepted by UpdateSmall before reaching this path.
            if (compare == null || !itemsComplete) return false;

            var dispatcherWatch = Stopwatch.StartNew();
            return await Dispatcher.InvokeAsync(() =>
            {
                var dispatcherWaitMs = dispatcherWatch.ElapsedMilliseconds;
                var applyWatch = Stopwatch.StartNew();
                try
                {
                    //Revalidate on the UI thread - Items are exchanged only here
                    if (!itemsComplete || currentFilter != filter || currentSort != sort) return false;
                    var items = Items;
                    //Membership screen for medium batches: most changed nodes are neither
                    //published nor entering the result, and IndexOf is a full scan of a
                    //possibly 100k-row list. One hash pass avoids that per off-screen node.
                    //Kept in sync on insert; rows removed below only leave harmless false
                    //positives (their IndexOf just returns -1).
                    HashSet<object> published = null;
                    if (changed.Length > 8)
                    {
                        //Do not allocate a 100k-entry set for a 14-node batch. Scan the dense
                        //published list once and retain only the changed identities that occur.
                        var changedSet = new HashSet<object>(changed, ReferenceEqualityComparer.Instance);
                        published = new HashSet<object>(ReferenceEqualityComparer.Instance);
                        foreach (var item in items)
                            if (changedSet.Contains(item)) published.Add(item);
                    }
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
                            //A new match arriving when the complete result already fills the
                            //window creates an unknown tail even when it sorts beyond the window.
                            if (index < 0 && !itemsTruncated && items.Count >= MaxItems)
                                itemsTruncated = true;
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
                }
                finally
                {
                    health?.GridMutationCompleted("incremental", changed.Length, Items.Count,
                        dispatcherWaitMs, applyWatch.ElapsedMilliseconds);
                }
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

            var dispatcherWatch = Stopwatch.StartNew();
            var dispatcherWaitMs = 0L;
            var applyMs = 0L;
            var publishedRowCount = 0;
            var gridMode = "bulk-skipped";
            var outcome = await Dispatcher.InvokeAsync(() =>
            {
                dispatcherWaitMs = dispatcherWatch.ElapsedMilliseconds;
                var applyWatch = Stopwatch.StartNew();
                try
                {
                    if (Loading || !itemsComplete || currentFilter != filter || currentSort != sort)
                        return new BatchUpdateOutcome(false);

                    var items = Items;
                    publishedRowCount = items.Count;
                    var changedSet = new HashSet<object>(changed, ReferenceEqualityComparer.Instance);
                    var originalCount = items.Count;
                    var capped = itemsTruncated || originalCount >= MaxItems;
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
                    var matchingChanged = 0;
                    var needsRefresh = capped && oldBoundary == null;
                    foreach (var node in changed)
                    {
                        if (!files.TryGetValue(node, out var current) || !ReferenceEquals(current, node) || !nf.Matches(node))
                            continue;
                        matchingChanged++;

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
                    //When the previous result was complete, kept plus every matching changed
                    //node is the complete post-change set (including new nodes beyond the old
                    //boundary that were intentionally not added to candidates).
                    itemsTruncated = itemsTruncated || kept.Count + matchingChanged > MaxItems;

                    var identical = merged.Count == originalCount;
                    for (var i = 0; identical && i < merged.Count; i++)
                        identical = ReferenceEquals(merged[i], items[i]);
                    if (identical)
                    {
                        //Metadata changed without crossing a neighbour. Rebind realized rows,
                        //but leave the collection and selection completely untouched.
                        gridMode = "bulk-repaint";
                        RowsRefreshRequested?.Invoke(changed);
                        return new BatchUpdateOutcome(true, needsRefresh);
                    }

                    gridMode = "bulk-reset";
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
                    publishedRowCount = Items.Count;
                    return new BatchUpdateOutcome(true, needsRefresh);
                }
                finally { applyMs = applyWatch.ElapsedMilliseconds; }
            });
            health?.GridMutationCompleted(gridMode, changed.Length, publishedRowCount,
                dispatcherWaitMs, applyMs);
            return outcome;
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
                || key == nameof(INode.LastChangeTime);
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
                case nameof(INode.FullName): key = NodePath.ByPath.Compare; ascending = up; break;
                case nameof(INode.Folder): key = NodePath.ByFolderThenName.Compare; ascending = up; break;
                default: return null;
            }
            return ascending ? key : (a, b) => key(b, a);
        }

        int FoundRank(INode x) => FoundIn(x) switch { true => 1, false => 2, null => x.IsDirectory ? 4 : 3 };

        readonly struct QueryResult
        {
            public readonly List<INode> Items;
            public readonly bool Truncated;
            public readonly string CacheStatus;
            public QueryResult(List<INode> items, bool truncated, string cacheStatus)
            {
                Items = items;
                Truncated = truncated;
                CacheStatus = cacheStatus;
            }
        }

        static IReadOnlyList<INode> CopyFilesCancellable(Func<bool> isCanceled)
        {
            //A freshly published MFT shard already owns a dense immutable node array.
            //Reuse it directly: no 2M-entry dictionary walk, list allocation or reference copy.
            if (files.TryGetDenseSnapshot(out var dense)) return dense;
            //NonBlocking.ConcurrentDictionary.Values eagerly copies the entire dictionary
            //inside one non-cancellable call. Its live enumerator lets a superseding user
            //sort stop this copy every few thousand nodes; version validation below turns
            //any concurrent membership change into a retry.
            var result = new List<INode>(files.Count);
            var seen = 0;
            foreach (var pair in files)
            {
                if ((++seen & 0x0FFF) == 0 && isCanceled()) return null;
                result.Add(pair.Value);
            }
            return result;
        }

        /// <summary>
        /// Get items according to current filter and sort. Truncated records whether the
        /// returned rows are only the MaxItems window of a larger filtered result.
        /// </summary>
        /// <param name="refresh">refilter from files (false - use Items)</param>
        /// <param name="IsCanceled">Check if to cancel</param>
        /// <returns>null when canceled</returns>
        QueryResult? GetItems(bool refresh, Func<bool> IsCanceled,
            int maxVersionRetries = int.MaxValue)
        {
            //Cancel
            var cts = new CancellationTokenSource();
            T CancelOr<T>(T v)
            {
                if (IsCanceled()) cts.Cancel();
                return v;
            }
            var retries = 0;
            while (!IsCanceled())
            {
                try
                {
                    IReadOnlyList<INode> all;
                    var truncated = itemsTruncated;
                    var bulkVersion = 0;
                    var sourceVersion = 0;
                    var cacheStatus = "items";
                    SnapshotCache builtCache = null;
                    if (refresh)
                    {
                        //A drive subtree is replaced in place. Wait until no replacement is
                        //active, then verify the same completed epoch after filtering/sorting;
                        //otherwise a point-in-time Values copy could still be a partial drive.
                        if (!TryCaptureBulkFilesVersion(out bulkVersion))
                        {
                            Thread.Sleep(1);
                            continue;
                        }
                        var cache = snapshotCache;
                        sourceVersion = Volatile.Read(ref filesVersion);
                        if (cache != null && cache.Version == sourceVersion && cache.Filter == filter)
                        {
                            cacheStatus = cache.HasDeltas ? "hit+delta" : "hit";
                            all = cache.Materialize(IsCanceled);
                            if (all == null) return null;
                            //Compact the overlay after this reconciliation so later sorts
                            //reuse one dense filtered list again.
                            if (cache.HasDeltas)
                                builtCache = new SnapshotCache(filter, sourceVersion, all, cache.Matcher);
                        }
                        else
                        {
                            cacheStatus = "miss";
                            all = CopyFilesCancellable(IsCanceled);
                            if (all == null) return null;
                            if (sourceVersion != Volatile.Read(ref filesVersion)
                                || !IsBulkFilesVersionStable(bulkVersion))
                            {
                                if (++retries > maxVersionRetries) return null;
                                continue;
                            }
                            var nf = nodeFilter;
                            //An empty search box matches everything - never pay 2M delegate calls for it
                            if (filter != null && !nf.MatchesAll)
                                all = all.AsParallel().WithCancellation(cts.Token)
                                    .Where(x => CancelOr(nf.Matches(x))).ToList();
                            builtCache = new SnapshotCache(filter, sourceVersion, all, nf);
                        }
                        if (sourceVersion != Volatile.Read(ref filesVersion))
                        {
                            if (++retries > maxVersionRetries) return null;
                            continue;
                        }
                        truncated = all.Count > MaxItems;
                    }
                    else
                    {
                        //Items mutates on the UI thread - the enumerator's version check
                        //turns a torn read into the InvalidOperationException retry below
                        var snapshot = new List<INode>(Items.Count);
                        var seen = 0;
                        foreach (var item in Items)
                        {
                            if ((++seen & 0x07FF) == 0 && IsCanceled()) return null;
                            snapshot.Add(item);
                        }
                        all = snapshot;
                    }
                    if (IsCanceled()) return null;
                    var compare = SortComparison(sort);
                    List<INode> result;
                    if (compare == null)
                        result = all.Take(MaxItems).ToList(); //No (known) sort => first matches in any order
                    else
                    {
                        //Bounded selection instead of a full sort: only the first MaxItems are ever
                        //published, so sorting the whole multi-million-node index would burn CPU and
                        //spill its sort buffers as garbage on every refresh - and a change storm
                        //refreshes every second. One linear pass keeps the same window in the same order.
                        result = SelectTop(all, compare, MaxItems, IsCanceled, SortScalarKey(sort));
                    }
                    if (result == null || IsCanceled()) return null;
                    if (refresh && (sourceVersion != Volatile.Read(ref filesVersion)
                        || !IsBulkFilesVersionStable(bulkVersion)))
                    {
                        if (++retries > maxVersionRetries) return null;
                        continue;
                    }
                    if (builtCache != null)
                    {
                        lock (snapshotCacheLock)
                            if (builtCache.Version == Volatile.Read(ref filesVersion)
                                && IsBulkFilesVersionStable(bulkVersion))
                                snapshotCache = builtCache;
                    }
                    if (retries != 0) cacheStatus += $"+retry{retries}";
                    return new QueryResult(result, truncated, cacheStatus);
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
        /// The first limit elements of src exactly as a full sort would order them, without
        /// sorting the rest. A sorted sample estimates the limit-th quantile of the sort key;
        /// nodes at or better than that threshold are collected with ONE comparison each,
        /// in parallel, and only that sliver is sorted. Any node outside the collection
        /// compares worse than every node inside it, so whenever the collection holds at
        /// least limit nodes its head IS the exact window - the count check makes an
        /// unlucky sample fall back (to the exact bounded heap) instead of ever showing a
        /// wrong window. Order among equal keys may differ from a full sort, but src
        /// enumerates a concurrent index in arbitrary order anyway.
        /// </summary>
        /// <returns>null when canceled</returns>
        internal static List<INode> SelectTop(IEnumerable<INode> src, Comparison<INode> compare, int limit, Func<bool> IsCanceled = null, Func<INode, ulong> scalarKey = null)
        {
            //The passes below need a stable, index-partitionable view twice (sample, then
            //threshold filter) - impossible over a live concurrent stream. A source that is
            //already a materialized list (files.Values snapshot, the Items copy) is used
            //as-is; only a lazy source (a PLINQ filter query) materializes here, executing
            //its filter in parallel on the way in.
            var all = src as IReadOnlyList<INode> ?? src.ToList();
            if (IsCanceled?.Invoke() == true) return null;
            if (all.Count > limit)
            {
                //Scalar keys (sizes, dates) sweep and sort densely: one node touch per node,
                //no delegate comparison, no pointer chasing across the heap in the sort phase
                if (scalarKey != null && ScalarTop(all, scalarKey, limit, IsCanceled) is { } top)
                    return top;
                if (IsCanceled?.Invoke() == true) return null;
                var candidates = ThresholdCandidates(all, compare, limit, IsCanceled);
                if (IsCanceled?.Invoke() == true) return null;
                //Sampling could not prune (heavy ties at the threshold, unlucky sample) =>
                //the sequential heap pass is exact for any key distribution
                if (candidates == null) return HeapTop(all, compare, limit, IsCanceled);
                all = candidates;
            }
            //Bounded leftover (at most ~3x the window) - a parallel sort of it stays small
            return all.AsParallel().OrderBy(x => x, Comparer<INode>.Create(compare)).Take(limit).ToList();
        }

        /// <summary>
        /// Monotone unsigned key producing exactly SortComparison's order for the metadata
        /// sorts (smaller key = earlier row; '+' shows largest values first, so it inverts
        /// the key). Null for the comparer-based sorts - names, paths, content rank.
        /// </summary>
        static Func<INode, ulong> SortScalarKey(string sort)
        {
            if (string.IsNullOrWhiteSpace(sort) || sort.Length < 2) return null;
            Func<INode, ulong> key = sort.Substring(1) switch
            {
                nameof(INode.Size) => n => n.Size,
                nameof(INode.LastChangeTime) => n => (ulong)Math.Max(0, n.LastChangeTime.Ticks),
                _ => null
            };
            if (key == null) return null;
            return sort[0] == '+' ? n => ulong.MaxValue - key(n) : key;
        }

        /// <summary>
        /// The scalar-key twin of ThresholdCandidates + the final sort: the sweep extracts
        /// each node's key once and everything after runs on dense key/node pairs. Returns
        /// null when the quantile guarantee fails (massive ties, unlucky sample) - the
        /// caller then falls back to the exact comparer-based paths.
        /// </summary>
        static List<INode> ScalarTop(IReadOnlyList<INode> all, Func<INode, ulong> key, int limit, Func<bool> IsCanceled)
        {
            var stride = Math.Max(1, all.Count >> 12);
            var sample = new List<ulong>(all.Count / stride + 1);
            for (var i = 0; i < all.Count; i += stride) sample.Add(key(all[i]));
            sample.Sort();
            var at = (int)Math.Min(sample.Count - 1.0, sample.Count * 1.5 * limit / all.Count);
            var threshold = sample[at];

            var cap = limit * 3 + 1024;
            var candidates = new List<(ulong Key, INode Node)>(Math.Min(cap, all.Count));
            var total = 0;
            Parallel.ForEach(Partitioner.Create(0, all.Count, 16384), () => new List<(ulong, INode)>(),
                (range, state, local) =>
                {
                    if (state.IsStopped || IsCanceled?.Invoke() == true) { state.Stop(); return local; }
                    var found = 0;
                    for (var i = range.Item1; i < range.Item2; i++)
                    {
                        var node = all[i];
                        var k = key(node);
                        if (k <= threshold) { local.Add((k, node)); found++; }
                    }
                    if (Interlocked.Add(ref total, found) > cap) state.Stop();
                    return local;
                },
                local => { lock (candidates) candidates.AddRange(local); });
            if (total > cap || candidates.Count < limit) return null;

            candidates.Sort((a, b) => a.Key.CompareTo(b.Key)); //Dense - no node access at all
            var result = new List<INode>(limit);
            for (var i = 0; i < limit; i++) result.Add(candidates[i].Node);
            return result;
        }

        /// <summary>
        /// Nodes at or better than a sampled quantile threshold: a verified superset of the
        /// published window, or null when the guarantee fails - the candidate cap overflowed
        /// (massive ties at the threshold; low-cardinality keys defeat quantiles) or fewer
        /// than limit nodes passed (the sample misjudged the quantile). Runs the expensive
        /// comparisons (culture-aware names, path chains) across all cores.
        /// </summary>
        static List<INode> ThresholdCandidates(IReadOnlyList<INode> all, Comparison<INode> compare, int limit, Func<bool> IsCanceled)
        {
            //A ~4k sample nails the quantile within a few percent; aiming the threshold at
            //1.5x limit leaves an unlucky sample still admitting the whole window
            var stride = Math.Max(1, all.Count >> 12);
            var sample = new List<INode>(all.Count / stride + 1);
            for (var i = 0; i < all.Count; i += stride) sample.Add(all[i]);
            sample.Sort(compare);
            var at = (int)Math.Min(sample.Count - 1.0, sample.Count * 1.5 * limit / all.Count);
            var threshold = sample[at];

            var cap = limit * 3 + 1024;
            var candidates = new List<INode>(Math.Min(cap, all.Count));
            var total = 0;
            Parallel.ForEach(Partitioner.Create(0, all.Count, 16384), () => new List<INode>(),
                (range, state, local) =>
                {
                    if (state.IsStopped || IsCanceled?.Invoke() == true) { state.Stop(); return local; }
                    var found = 0;
                    for (var i = range.Item1; i < range.Item2; i++)
                        if (compare(all[i], threshold) <= 0) { local.Add(all[i]); found++; }
                    //Cap enforcement is per range - the overshoot stays a few ranges' worth
                    if (Interlocked.Add(ref total, found) > cap) state.Stop();
                    return local;
                },
                local => { lock (candidates) candidates.AddRange(local); });
            return total > cap || candidates.Count < limit ? null : candidates;
        }

        /// <summary>
        /// Exact top-limit for any key distribution: a worst-out heap of at most limit
        /// nodes streams over the snapshot - O(N log limit), sequential.
        /// </summary>
        static List<INode> HeapTop(IReadOnlyList<INode> all, Comparison<INode> compare, int limit, Func<bool> IsCanceled)
        {
            //Inverted comparer => the heap root is the worst kept node, evicted first
            var heap = new PriorityQueue<INode, INode>(Comparer<INode>.Create((a, b) => compare(b, a)));
            var seen = 0;
            foreach (var node in all)
            {
                if ((++seen & 0xFFFF) == 0 && IsCanceled?.Invoke() == true) return null;
                if (heap.Count < limit) heap.Enqueue(node, node);
                //Compares against the root first - a node beyond the window never sifts the heap
                else heap.EnqueueDequeue(node, node);
            }
            var result = new List<INode>(heap.Count);
            foreach (var (node, _) in heap.UnorderedItems) result.Add(node);
            result.Sort(compare);
            return result;
        }

        /// <summary>
        /// Remove node and subtract a removed file from its ancestor directory sizes
        /// </summary>
        /// <param name="path"></param>
        INode Remove(string path)
        {
            //Apply the delta while this entry and its drive shard are still current. A
            //path-backed watcher node has no parent references and resolves directories
            //through the index; doing that after TryRemove allowed a concurrent MFT publish
            //to place the lookup and the visible rows in different shard generations.
            if (!files.TryRemove(path, SubtractFileSize, out var n)) return null;
            PatchFileRemoved(n);
            //Directories are routed through RemoveTrees by the batch handler. That method
            //subtracts their remaining aggregate once and removes every indexed descendant;
            //doing it here as well would double-count a recursive delete.
            return n;
        }

        void SubtractFileSize(INode node)
        {
            if (!node.IsDirectory) PropagateUnsignedSizeDelta(node, node.Size, subtract: true);
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
            var candidatesForRoot = new List<(string Path, INode Node)>(paths.Count);
            var distinctRoots = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var path in paths)
            {
                //Never uproot a whole drive from an event - only a drive scan may do that.
                //A drive-root path here is a half-delivered watcher rename/delete (see IsDriveRoot).
                if (IsDriveRoot(path)) continue;
                if (!files.TryGetValue(path, out var root)) continue;
                if (!distinctRoots.Add(root)) continue;
                candidatesForRoot.Add((path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root));
            }
            if (candidatesForRoot.Count == 0) return Array.Empty<INode>();

            //USN may report one delete for the parent directory and more for directories
            //inside it. Keep only top-level roots: subtracting both aggregates would count
            //the nested tree twice. Ordering by path length lets a small accepted-path set
            //identify ancestors in O(number of paths * directory depth), not O(paths²).
            var acceptedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var treeRoots = new List<(string Path, INode Node)>();
            foreach (var candidate in candidatesForRoot.OrderBy(x => x.Path.Length))
            {
                var nested = false;
                for (var parent = Path.GetDirectoryName(candidate.Path);
                    !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                    if (acceptedPaths.Contains(parent.TrimEnd(Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar)))
                    {
                        nested = true;
                        break;
                    }
                if (nested) continue;
                acceptedPaths.Add(candidate.Path);
                treeRoots.Add(candidate);
            }

            var roots = new HashSet<object>(treeRoots.Select(x => x.Node), ReferenceEqualityComparer.Instance);
            var prefixes = treeRoots.Select(x => x.Path + Path.DirectorySeparatorChar).ToArray();

            //The directory node already stores the aggregate of every indexed file below
            //it. Subtract that value once from surviving ancestors. Walking every removed
            //file was both expensive and dependent on receiving every descendant USN event.
            foreach (var (_, root) in treeRoots) SubtractTreeSizeFromAncestors(root);

            var candidates = files.Values.AsParallel()
                .Where(n => roots.Contains(n) || NodePath.IsUnderAny(n, roots, prefixes))
                .ToArray();
            var removed = new List<INode>(candidates.Length);
            foreach (var candidate in candidates)
            {
                if (!files.TryRemove(candidate, out var actual)) continue;
                removed.Add(actual);
            }
            if (removed.Count > 0) PatchFilesVersion(Array.Empty<INode>(), removed);
            return removed.ToArray();
        }

        void SubtractTreeSizeFromAncestors(INode root)
            => PropagateUnsignedSizeDelta(root, root?.Size ?? 0, subtract: true);

        void PropagateUnsignedSizeDelta(INode node, ulong size, bool subtract)
        {
            var remaining = size;
            //PropagateSizeDelta is signed; a practical tree fits in one pass, while the
            //loop also keeps the arithmetic correct for aggregates beyond Int64.MaxValue.
            while (remaining > 0)
            {
                var part = Math.Min(remaining, (ulong)long.MaxValue);
                PropagateSizeDelta(node, subtract ? -(long)part : (long)part);
                remaining -= part;
            }
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
                if (!files.TryRemove(candidate, SubtractFileSize, out var actual)) continue;
                changed.Add(actual);
            }
            if (changed.Count > 0) PatchFilesVersion(Array.Empty<INode>(), changed);

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
                PropagateSizeDelta(n, (n.IsDirectory ? 0L : (long)n.Size) - oldSize);
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
            if (ReferenceEquals(node, added)) PatchFileAdded(node);
            //Only a first sighting contributes - an already indexed node was counted by the scan
            if (ReferenceEquals(node, added) && !node.IsDirectory) PropagateSizeDelta(node, (long)node.Size);
            return node;
        }

        /// <summary>
        /// Update aggregated ancestor directory sizes for a file-size delta. Ordinary MFT
        /// nodes take the allocation-free parent-chain path below; hard-link ambiguity is
        /// healed automatically by the USN watcher's quiet-window MFT rebuild.
        /// </summary>
        void PropagateSizeDelta(INode node, long delta)
        {
            if (delta == 0) return;
            var changed = false;
            var generation = Interlocked.Increment(ref sizeChangeGeneration);
            //MFT nodes already carry their parent chain. A cleanup storm can delete hundreds
            //of thousands of files; walking these references avoids Path.GetDirectoryName
            //allocations and a hash lookup for every ancestor of every deleted file.
            if (node?.PathParent != null)
            {
                changed = ApplySizeDeltaToParentChain(node, delta,
                    dir => pendingSizeRows[dir] = generation) != 0;
            }
            else
            {
                var path = node?.FullName;
                for (var dir = Path.GetDirectoryName(path); dir != null; dir = Path.GetDirectoryName(dir))
                    if (files.TryGetValue(dir, out var d) && d.IsDirectory)
                    {
                        d.AddSizeDelta(delta);
                        pendingSizeRows[d] = generation;
                        changed = true;
                    }
            }
            if (changed) RefreshSizesSoon();
        }

        internal static int ApplySizeDeltaToParentChain(INode node, long delta, Action<INode> markChanged)
        {
            var changed = 0;
            var depth = 0;
            for (var dir = node?.PathParent; dir != null && depth++ < 256; dir = dir.PathParent)
            {
                if (!dir.IsDirectory) continue;
                dir.AddSizeDelta(delta);
                markChanged?.Invoke(dir);
                changed++;
            }
            return changed;
        }

        int sizeRefreshQueued = 0; //1 => a row repaint covering all size changes so far is already queued
        long sizeChangeGeneration;
        //Directories whose aggregated size changed since the last coalesced repaint
        readonly NonBlocking.ConcurrentDictionary<INode, long> pendingSizeRows = new();
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
                var pending = pendingSizeRows.ToArray();
                var changed = pending.Select(change => change.Key).ToArray();
                //Remove only the exact generation captured above. If another delete changed
                //the same directory meanwhile, its newer generation remains queued instead
                //of being accidentally consumed by this older repaint batch.
                foreach (var change in pending) ConsumePendingSizeChange(pendingSizeRows, change);
                if (changed.Length > 0)
                {
                    if (health?.SuspendAutomaticGridUpdates == true) refreshPending = true;
                    else if (sort?.Length > 1 && sort.Substring(1) == nameof(INode.Size))
                        await UpdateSmall(changed);
                    else RowsRefreshRequested?.Invoke(changed);
                }
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
            health = new LiveUpdateHealthMonitor(Dispatcher,
                FSChangeProcessor.GetHealthSnapshot,
                FSChangeProcessor.GetRecentEvents,
                () => new HealthWorkState(Loading, Filtering, Searching),
                ApplyLiveUpdateHealth,
                CatchUpLiveGrid);
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
                    //Discover every tree before mutating anything. USN commonly reports a
                    //directory delete together with deletes of its children; those children
                    //are covered by the directory's stored aggregate and must not each walk
                    //their parent chain as well.
                    var trees = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var path in pendingDeletes)
                    {
                        var indexed = files.TryGetValue(path, out var i) ? i : null;
                        if (indexed?.IsDirectory == true || HasArchiveChildren(indexed))
                            trees.Add(path.TrimEnd(Path.DirectorySeparatorChar,
                                Path.AltDirectorySeparatorChar));
                    }

                    var removed = new List<INode>();
                    foreach (var path in pendingDeletes)
                    {
                        var normalized = path.TrimEnd(Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar);
                        if (trees.Contains(normalized) || IsBelowPendingTree(normalized, trees))
                            continue;
                        removed.Add(Remove(path));
                    }
                    pendingDeletes.Clear();
                    if (trees.Count > 0) removed.AddRange(RemoveTrees(trees.ToArray()));
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
            health?.GridUpdatePending();
            if (health?.SuspendAutomaticGridUpdates == true)
            {
                refreshPending = true;
                return;
            }
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
            if (nodes.Length == 0)
            {
                health?.GridUpdateReflected();
                return;
            }

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
                    if (nodes.Length == 0)
                    {
                        health?.GridUpdateReflected();
                        return;
                    }
                }
            }

            if (await TryBulkRemove(nodes))
            {
                health?.GridUpdateReflected();
                return;
            }
            //A real loading storm is covered by the drive's authoritative publish. Do not
            //reset a 100k-row grid while the scan is still producing more changes; small
            //batches below stay live through the targeted path.
            if (DefersLoadingBatch(Loading, nodes.Length))
            {
                refreshPending = true;
                return;
            }
            if (UsesIncrementalBatch(nodes.Length))
            {
                if (await TryIncrementalUpdate(nodes))
                {
                    health?.GridUpdateReflected();
                    return;
                }
                if (Loading)
                {
                    //Unknown sort/partial view: the final drive publish is authoritative.
                    refreshPending = true;
                    return;
                }
            }
            else
            {
                var bulk = await TryBulkIncrementalUpdate(nodes);
                if (bulk.Handled)
                {
                    if (bulk.NeedsRefresh) QueueTailReconciliation();
                    health?.GridUpdateReflected();
                    return;
                }
            }
            var nf = nodeFilter;
            if (nodes.Any(nf.Matches)) _ = Update();
            else health?.GridUpdateReflected();
        }

        internal static bool UsesIncrementalBatch(int count)
            => count >= 0 && count <= IncrementalBatchLimit;

        internal static bool DefersLoadingBatch(bool loading, int count)
            => loading && !UsesIncrementalBatch(count);

        /// <summary>
        /// A capped grid may temporarily lose rows only at its invisible tail. Refill it once
        /// filesystem activity has been quiet for a moment instead of refiltering millions of
        /// index entries after every cache-file deletion.
        /// </summary>
        void QueueTailReconciliation()
        {
            refreshPending = true;
            Interlocked.Exchange(ref tailReconciliationRequestedAt, Environment.TickCount64);
            if (Interlocked.Exchange(ref tailReconciliationQueued, 1) == 1) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (refreshPending && health?.SuspendAutomaticGridUpdates != true)
                    {
                        var requested = Interlocked.Read(ref tailReconciliationRequestedAt);
                        var remaining = TailReconciliationQuietMs
                            - (Environment.TickCount64 - requested);
                        if (remaining > 0) await Task.Delay((int)Math.Min(remaining, int.MaxValue));
                        if (requested != Interlocked.Read(ref tailReconciliationRequestedAt)) continue;
                        if (!refreshPending || health?.SuspendAutomaticGridUpdates == true) return;

                        //The quiet-time delay already supplied the coalescing window.
                        await UpdateCore(null, null, healthRecovery: false, skipDataDebounce: true);
                        return;
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref tailReconciliationQueued, 0);
                    //Close the race where a new tail hole arrived between the final timestamp
                    //check and releasing the ownership bit.
                    if (refreshPending && health?.SuspendAutomaticGridUpdates != true
                        && Environment.TickCount64 - Interlocked.Read(ref tailReconciliationRequestedAt)
                            < TailReconciliationQuietMs)
                        QueueTailReconciliation();
                }
            });
        }

        internal static bool ConsumePendingSizeChange(
            NonBlocking.ConcurrentDictionary<INode, long> pending,
            KeyValuePair<INode, long> change)
            => pending.TryRemove(change);

        /// <summary>Whether a deleted path is covered by a directory delete in this batch.</summary>
        internal static bool IsBelowPendingTree(string path, IReadOnlySet<string> trees)
        {
            if (string.IsNullOrEmpty(path) || trees == null || trees.Count == 0) return false;
            for (var parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent);
                parent = Path.GetDirectoryName(parent))
                if (trees.Contains(parent.TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar))) return true;
            return false;
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
            var visibleRowsRemoved = false;
            var dispatcherWatch = Stopwatch.StartNew();
            var dispatcherWaitMs = 0L;
            var applyMs = 0L;
            var publishedRowCount = 0;
            var gridMode = "remove-scan";
            var handled = await Dispatcher.InvokeAsync(() =>
            {
                dispatcherWaitMs = dispatcherWatch.ElapsedMilliseconds;
                var applyWatch = Stopwatch.StartNew();
                try
                {
                    if (!itemsComplete) return false; //Revalidate on the UI thread - Items are exchanged only here
                    var items = Items;
                    publishedRowCount = items.Count;
                    var set = new HashSet<object>(removed, ReferenceEqualityComparer.Instance);
                    List<int> hits = null;
                    for (var i = 0; i < items.Count; i++)
                        if (set.Contains(items[i])) (hits ??= new List<int>()).Add(i);
                    visibleRowsRemoved = hits != null;

                    //A capped window needs rows pulled in from beyond MaxItems after a visible
                    //removal. Surviving ancestor size rows are reordered independently by the
                    //coalesced targeted size update; they do not require a 100k-row query here.
                    needsRefresh = BulkRemovalNeedsRefresh(itemsTruncated, sort, hits != null);
                    if (hits == null) return true; //Nothing published - the grid (and selection) stays untouched

                    BeforeItemsExchange();
                    if (hits.Count <= 300)
                    {
                        gridMode = "remove-in-place";
                        //Few visible rows - remove in place (backwards: earlier indexes stay valid)
                        for (var i = hits.Count - 1; i >= 0; i--) items.RemoveAt(hits[i]);
                    }
                    else
                    {
                        gridMode = "remove-reset";
                        //Each RemoveAt shifts the tail of a possibly 100k-row list - beyond a few
                        //hundred rows one rebuilt collection is cheaper than the shifts
                        var target = new RangeObservableCollection<INode>();
                        target.AddRange(items.Where(x => !set.Contains(x)));
                        Items = target;
                    }
                    AfterItemsExchange();
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountsInfo)));
                    publishedRowCount = Items.Count;
                    return true;
                }
                finally { applyMs = applyWatch.ElapsedMilliseconds; }
            });
            health?.GridMutationCompleted(gridMode, removed.Length, publishedRowCount,
                dispatcherWaitMs, applyMs);

            //Keep the deletion instant, then let one quiet-window refresh refill a capped
            //tail without repeatedly selecting 100k rows during a deletion storm.
            if (handled && needsRefresh)
            {
                if (CanDeferTailReconciliation(itemsTruncated, sort, visibleRowsRemoved))
                    QueueTailReconciliation();
                else _ = Update();
            }
            return handled;
        }

        internal static bool BulkRemovalNeedsRefresh(bool truncated, string currentSort, bool visibleRowsRemoved)
            => visibleRowsRemoved && truncated;

        internal static bool CanDeferTailReconciliation(bool truncated, string currentSort,
            bool visibleRowsRemoved)
            => truncated && visibleRowsRemoved;

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
                            IFrnNodeSource frnNodes = null;
                            IReadOnlyList<INode> denseNodes = null;
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
                                frnNodes = entries as IFrnNodeSource;
                                denseNodes = frnNodes?.DenseNodes;
                                //MFT results know their exact live count. Allocate the final
                                //shard at its target capacity once instead of walking every
                                //power-of-two LOH resize on the way there.
                                if (entries.TryGetNonEnumeratedCount(out var entryCount) && entryCount > 0)
                                    fresh = new NonBlocking.ConcurrentDictionary<object, INode>(
                                        Environment.ProcessorCount, entryCount, NodePath.KeyComparer);
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
                            PublishDrive(root, fresh, streamed, frnNodes, denseNodes); //Empty for a skipped drive => stale entries pruned
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
        void PublishDrive(string root, NonBlocking.ConcurrentDictionary<object, INode> fresh,
            long streamed = 0, IEnumerable<INode> frnNodes = null,
            IReadOnlyList<INode> denseNodes = null)
        {
            //Nothing new and nothing indexed under the drive => skip the subtree swap
            //(the common case for skipped/deselected drives on every refresh)
            if (fresh.IsEmpty && !files.ContainsKey(root))
            {
                Interlocked.Add(ref loadingNodes, -streamed);
                return;
            }
            lock (publishLock)
            {
                BeginBulkFilesMutation();
                try
                {
                    //The completed scan already is the final per-drive hash table. Publish it
                    //by replacing one shard instead of copying every node to a second map.
                    files.ReplaceDrive(root, fresh, denseNodes);
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
                finally
                {
                    //Invalidate membership caches even when an exceptional drive node aborts
                    //the replacement, then expose the completed bulk epoch to waiting queries.
                    BumpFilesVersion();
                    EndBulkFilesMutation();
                }
            }
            exes = files.Values.AsParallel()
                .Where(n => !n.IsDirectory && NodePath.LeafEndsWith(n, ".exe")).ToArray();
            //MFT nodes carry their file reference numbers - hand them to the drive's USN
            //watcher so deleted/renamed files resolve to paths (journal records have no names)
            FSChangeProcessor.PopulateFrnMap(root, frnNodes ?? fresh.Values);
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
            if (health?.SuspendAutomaticGridUpdates == true) refreshPending = true;
            else UIRefreshRequested?.Invoke();
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
                BumpFilesVersion();

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
