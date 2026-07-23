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
        int reserveRefillQueued;
        int reserveRefillNeeded;
        int reserveRefillUrgent;
        long reserveRefillRequestedAt;
        volatile Task dataRefreshPublished = Task.CompletedTask; //Completes when the queued data refresh has really hit the grid
        const int MaxItems = 100000; //Publish just the first 100 000 filtered items
        internal const int ResultReserveItems = 4096;
        internal const int ResultReserveLowWatermark = 1024;
        internal const int MaterializedWindowLimit = MaxItems + ResultReserveItems;
        internal const int IncrementalBatchLimit = 64;
        //A targeted remove/add only touches shifted virtualized indices. Above this number
        //of actually visible removals, a single Reset is the safer upper bound for WPF event
        //traffic. Most filesystem delete storms contain many off-screen descendants and
        //therefore remain far below this limit even when the source batch has thousands.
        internal const int TargetedBulkRemovalLimit = 512;
        internal const int TargetedBulkMutationLimit = 768;
        internal const int TailReconciliationQuietMs = 2500;
        internal const int MaxDataRefreshVersionRetries = 2;
        readonly LiveResultWindow<INode> liveWindow = new(MaxItems,
            ResultReserveItems, ResultReserveLowWatermark);

        sealed class MetadataRefreshSlot
        {
            public readonly string Path;
            public readonly object Gate = new();
            public long Version;
            public INode ExpectedNode;
            public bool QueuedOrRunning;

            public MetadataRefreshSlot(string path) => Path = path;
        }

        //FileInfo.Exists/LastWriteTime can block for seconds even on a local NTFS path
        //(the captured incident stalled on Windows\Prefetch). Keep those calls off the one
        //ordered C: queue, while a small fixed worker count bounds handles and I/O pressure.
        readonly ConcurrentDictionary<string, MetadataRefreshSlot> metadataRefreshSlots =
            new(StringComparer.OrdinalIgnoreCase);
        readonly BlockingCollection<MetadataRefreshSlot> metadataRefreshQueue = new();
        internal const int SlowMetadataReadLogMs = 1000;
        internal const int MaxMetadataRefreshWorkers = 4;
        //The window is sorted and filtered by the query that last published it. During a
        //user filter/sort query the public fields already contain the requested values while
        //the window still has the previous ordering, so live mutations must wait for replay.
        volatile string liveWindowFilter;
        volatile string liveWindowSort;
        const int MaxViewQueryDeltas = 131072;
        readonly object viewQueryDeltaLock = new();
        readonly HashSet<INode> viewQueryDeltas = new(ReferenceEqualityComparer.Instance);
        long viewQuerySequence;
        long activeViewQuery;
        bool viewQueryDeltaOverflow;

        public Action BeforeItemsExchange = () => { };
        public Action AfterItemsExchange = () => { };

        long BeginViewQuery()
        {
            lock (viewQueryDeltaLock)
            {
                viewQueryDeltas.Clear();
                viewQueryDeltaOverflow = false;
                return activeViewQuery = ++viewQuerySequence;
            }
        }

        bool RecordViewQueryDeltas(IEnumerable<INode> nodes)
        {
            lock (viewQueryDeltaLock)
            {
                if (activeViewQuery == 0) return false;
                //The active query will reject an overflowed journal and schedule an
                //authoritative quiet-time retry, so later nodes are already covered.
                if (viewQueryDeltaOverflow) return true;
                foreach (var node in nodes)
                {
                    if (node == null) continue;
                    viewQueryDeltas.Add(node);
                    if (viewQueryDeltas.Count <= MaxViewQueryDeltas) continue;
                    viewQueryDeltas.Clear();
                    viewQueryDeltaOverflow = true;
                    return true;
                }
                return true;
            }
        }

        (bool Valid, INode[] Nodes) SealViewQueryDeltas(long query)
        {
            lock (viewQueryDeltaLock)
            {
                if (query == 0 || query != activeViewQuery || viewQueryDeltaOverflow)
                {
                    if (query == activeViewQuery) activeViewQuery = 0;
                    viewQueryDeltas.Clear();
                    viewQueryDeltaOverflow = false;
                    return (false, Array.Empty<INode>());
                }
                var nodes = viewQueryDeltas.ToArray();
                //No update can enter this journal after the snapshot. It will instead be
                //queued behind the dispatcher callback that is currently publishing it.
                activeViewQuery = 0;
                viewQueryDeltas.Clear();
                return (true, nodes);
            }
        }

        void EndViewQuery(long query)
        {
            if (query == 0) return;
            lock (viewQueryDeltaLock)
            {
                if (query != activeViewQuery) return;
                activeViewQuery = 0;
                viewQueryDeltas.Clear();
                viewQueryDeltaOverflow = false;
            }
        }

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
                var viewQuery = 0L;
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
                    viewQuery = BeginViewQuery();
                    var query = GetItems(refresh, IsCanceled,
                        dataRefresh ? MaxDataRefreshVersionRetries : int.MaxValue);
                    if (query == null || IsCanceled())
                    {
                        //A membership storm can invalidate an expensive 100k-row selection
                        //again and again. Incremental delivery is already keeping the visible
                        //window live; abandon this run after a bounded number of retries and
                        //reconcile once the change stream has been quiet for a moment.
                        if (!IsCanceled()) queueTailReconciliation = true;
                        return;
                    }
                    //The query materializes a private reserve after the visible prefix. It is
                    //never bound to WPF; live deltas consume it to fill visible holes without
                    //another full-index query.
                    var window = query.Value.Items;
                    var queryMs = queryWatch.ElapsedMilliseconds;
                    health?.RefreshQueryCompleted(queryMs, Math.Min(MaxItems, window.Count),
                        query.Value.CacheStatus);

                    var applied = false;
                    var invalidated = false;
                    var reset = false;
                    var replayedViewDeltas = false;
                    var reserveNeedsRefill = false;
                    var publishedRows = 0;
                    var uiAppliedAt = 0L;
                    var dispatcherWatch = Stopwatch.StartNew();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        var dispatcherWaitMs = dispatcherWatch.ElapsedMilliseconds;
                        var applyWatch = Stopwatch.StartNew();
                        try
                        {
                            if (IsCanceled()) return; //Do not flash outdated results
                            //Clear the old pending marker before sealing. A mutation that
                            //arrives after the seal can set it again without this publication
                            //mistaking that newer work for part of its own snapshot.
                            if (refresh) refreshPending = false;
                            var delta = SealViewQueryDeltas(viewQuery);
                            var compare = SortComparison(sort);
                            if (!delta.Valid || delta.Nodes.Length > 0 && compare == null)
                            {
                                invalidated = true;
                                return;
                            }
                            replayedViewDeltas = delta.Nodes.Length > 0;

                            liveWindow.Reset(window, query.Value.UnknownTail);
                            if (delta.Nodes.Length > 0)
                            {
                                var nf = nodeFilter;
                                bool Include(INode node) => files.TryGetValue(node, out var current)
                                    && ReferenceEquals(current, node) && nf.Matches(node);
                                liveWindow.ApplyBatch(delta.Nodes, Include, compare);
                            }
                            liveWindowFilter = filter;
                            liveWindowSort = sort;
                            var items = liveWindow.VisibleSnapshot();
                            publishedRows = items.Length;
                            var identical = items.Length == Items.Count;
                            for (var i = 0; identical && i < items.Length; i++)
                                identical = ReferenceEquals(items[i], Items[i]);

                            if (!identical)
                            {
                                //A Reset on a 100k ObservableCollection makes WPF's existing
                                //ListCollectionView reconcile the entire old source even when a
                                //new filter has only a few hundred rows. Retire that large view
                                //for a substantial filter shrink; ordinary refreshes keep the
                                //stable collection and its scroll/selection state.
                                var replaceSource = PreferFreshItemsSource(filterChanged,
                                    Items.Count, items.Length);
                                BeforeItemsExchange();
                                itemsComplete = false;
                                if (replaceSource)
                                {
                                    var fresh = new RangeObservableCollection<INode>();
                                    fresh.AddRange(items);
                                    //The Fody-woven setter raises Items immediately, before
                                    //AfterItemsExchange checks surviving selection/focus.
                                    Items = fresh;
                                }
                                else if (Items is RangeObservableCollection<INode> target)
                                    target.ReplaceRange(items);
                                else
                                {
                                    var fresh = new RangeObservableCollection<INode>();
                                    fresh.AddRange(items);
                                    Items = fresh;
                                }
                                AfterItemsExchange();
                                reset = true;
                                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountsInfo)));
                            }
                            itemsTruncated = liveWindow.IsTruncated;
                            reserveNeedsRefill = liveWindow.NeedsRefill;
                            Volatile.Write(ref reserveRefillNeeded, reserveNeedsRefill ? 1 : 0);
                            Volatile.Write(ref reserveRefillUrgent,
                                liveWindow.IsVisibleIncomplete ? 1 : 0);
                            itemsComplete = true;
                            applied = true;
                        }
                        finally
                        {
                            health?.RefreshApplied(dispatcherWaitMs, applyWatch.ElapsedMilliseconds);
                            if (reset) uiAppliedAt = Environment.TickCount64;
                        }
                    }, DispatcherPriority.Background);
                    if (invalidated)
                    {
                        queueTailReconciliation = true;
                        return;
                    }
                    if (reserveNeedsRefill) QueueReserveRefill(
                        Volatile.Read(ref reserveRefillUrgent) != 0);
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
                    authoritativePublished = (refresh || replayedViewDeltas)
                        && applied && !IsCanceled();
                    queueDeferredReconciliation = applied && deferReconciliation;
                    $"update {(dataRefresh ? "refresh" : "user")} sort={sort} rows={publishedRows}: wait {waitMs} ms, reason={refreshReason}, cache={query.Value.CacheStatus}, query {queryMs} ms, publish {queryWatch.ElapsedMilliseconds - queryMs} ms".Debug();
                }
                catch (Exception e)
                {
                    await Log($"Exception: {e.Message}");
                }
                finally
                {
                    EndViewQuery(viewQuery);
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

        /// <summary>
        /// Replacing the collection instance is cheaper than asking WPF to Reset a very
        /// large existing CollectionView when a user filter collapses it to a small result.
        /// Keep the source stable for normal refreshes and modest changes so scrolling and
        /// selection retain their established incremental behavior.
        /// </summary>
        internal static bool PreferFreshItemsSource(bool filterChanged, int currentRows, int newRows)
            => filterChanged && currentRows >= 4096
            && (long)Math.Max(0, newRows) * 4 < currentRows;

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

        public void Dispose()
        {
            Interlocked.Exchange(ref modelDisposed, 1);
            metadataRefreshQueue.CompleteAdding();
            DriveSelectionStore.SelectionChanged -= DriveSelectionChanged;
            //The scan owns/disposes its CTS after any abandoned pipe drain has finished.
            foreach (var active in activeDriveScans.Values)
                try { active.Cancel(); } catch { }
            foreach (var retry in driveRetries.ToArray())
                if (driveRetries.TryRemove(retry.Key, out var cts))
                {
                    try { cts.Cancel(); } catch { }
                    cts.Dispose();
                }
            health?.Dispose();
        }

        void StartMetadataRefreshWorkers()
        {
            var count = Math.Min(MaxMetadataRefreshWorkers,
                Math.Max(2, Environment.ProcessorCount / 2));
            for (var i = 0; i < count; i++)
                new Thread(MetadataRefreshLoop)
                {
                    IsBackground = true,
                    Name = $"metadata refresh {i + 1}"
                }.Start();
        }

        void QueueMetadataRefresh(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || Volatile.Read(ref modelDisposed) != 0) return;
            files.TryGetValue(path, out var expected);
            while (Volatile.Read(ref modelDisposed) == 0)
            {
                var slot = metadataRefreshSlots.GetOrAdd(path,
                    p => new MetadataRefreshSlot(p));
                lock (slot.Gate)
                {
                    //The worker removes an idle slot while holding this same gate. A caller
                    //that captured that old slot retries against the dictionary's new owner.
                    if (!metadataRefreshSlots.TryGetValue(path, out var current)
                        || !ReferenceEquals(current, slot)) continue;
                    slot.ExpectedNode = expected;
                    slot.Version++;
                    if (slot.QueuedOrRunning) return;
                    slot.QueuedOrRunning = true;
                    try { metadataRefreshQueue.Add(slot); }
                    catch (InvalidOperationException) { slot.QueuedOrRunning = false; }
                    return;
                }
            }
        }

        void MetadataRefreshLoop()
        {
            foreach (var slot in metadataRefreshQueue.GetConsumingEnumerable())
            {
                while (Volatile.Read(ref modelDisposed) == 0)
                {
                    long version;
                    INode expected;
                    lock (slot.Gate)
                    {
                        version = slot.Version;
                        expected = slot.ExpectedNode;
                    }

                    var watch = Stopwatch.StartNew();
                    var found = INode.TryReadMetadata(slot.Path, out var snapshot);
                    var elapsed = watch.ElapsedMilliseconds;
                    if (elapsed >= SlowMetadataReadLogMs)
                        _ = Log($"Slow metadata read {elapsed}ms found={found} path={slot.Path}");
                    if (found && Volatile.Read(ref modelDisposed) == 0)
                        //Do not wait and do not use the urgent app-action lane. Let all four
                        //workers continue filling the normal 200-500ms window so one grid
                        //diff covers the whole VS/build burst instead of a train of four-row
                        //mutations, each scheduling another expensive WPF render.
                        _ = FSChangeProcessor.PostDeferredMetadata(FsEvent.MetadataResult(
                            slot.Path, expected, snapshot, elapsed));

                    lock (slot.Gate)
                    {
                        if (slot.Version != version) continue; //Changed again while reading
                        slot.QueuedOrRunning = false;
                        ((ICollection<KeyValuePair<string, MetadataRefreshSlot>>)metadataRefreshSlots)
                            .Remove(new KeyValuePair<string, MetadataRefreshSlot>(slot.Path, slot));
                        break;
                    }
                }
            }
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
        /// <returns>Handled plus whether the private reserve should be replenished</returns>
        async Task<BatchUpdateOutcome> TryIncrementalUpdate(INode[] changed)
        {
            var currentFilter = filter;
            var currentSort = sort;
            var nf = nodeFilter;
            var compare = SortComparison(currentSort);
            //Medium batches remain targeted even while a drive scan runs. The old Loading
            //fallback converted every ordinary watcher notification into a complete Reset;
            //WPF's deferred DataBind/render work then accumulated faster than it could drain.
            //Large loading batches are intercepted by UpdateSmall before reaching this path.
            if (compare == null || !itemsComplete
                || currentFilter != liveWindowFilter || currentSort != liveWindowSort)
                return new BatchUpdateOutcome(false);

            var dispatcherWatch = Stopwatch.StartNew();
            return await Dispatcher.InvokeAsync(() =>
            {
                var dispatcherWaitMs = dispatcherWatch.ElapsedMilliseconds;
                var applyWatch = Stopwatch.StartNew();
                try
                {
                    //Revalidate on the UI thread - Items are exchanged only here
                    if (!itemsComplete || currentFilter != filter || currentSort != sort
                        || currentFilter != liveWindowFilter || currentSort != liveWindowSort)
                        return new BatchUpdateOutcome(false);
                    var operations = new List<LiveResultWindow<INode>.Operation>();
                    bool Include(INode node) => files.TryGetValue(node, out var current)
                        && ReferenceEquals(current, node) && nf.Matches(node);
                    liveWindow.ApplySmallBatch(changed, Include, compare, operations);
                    if (operations.Count > 0)
                    {
                        BeforeItemsExchange();
                        LiveResultWindow<INode>.ApplyOperations(Items, operations);
                        AfterItemsExchange();
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountsInfo)));
                    }
                    itemsTruncated = liveWindow.IsTruncated;
                    Volatile.Write(ref reserveRefillNeeded, liveWindow.NeedsRefill ? 1 : 0);
                    Volatile.Write(ref reserveRefillUrgent,
                        liveWindow.IsVisibleIncomplete ? 1 : 0);
                    //Inserted rows already bind their current values; repainting the changed
                    //identities is still cheap and covers metadata that stayed in place.
                    RowsRefreshRequested?.Invoke(changed);
                    return new BatchUpdateOutcome(true, liveWindow.NeedsRefill,
                        liveWindow.IsVisibleIncomplete);
                }
                finally
                {
                    health?.GridMutationCompleted("window-incremental", changed.Length, Items.Count,
                        dispatcherWaitMs, applyWatch.ElapsedMilliseconds);
                }
            });
        }

        readonly struct BatchUpdateOutcome
        {
            public bool Handled { get; }
            public bool NeedsRefresh { get; }
            public bool NeedsImmediateRefresh { get; }
            public BatchUpdateOutcome(bool handled, bool needsRefresh = false,
                bool needsImmediateRefresh = false)
            {
                Handled = handled;
                NeedsRefresh = needsRefresh;
                NeedsImmediateRefresh = needsImmediateRefresh;
            }
        }

        /// <summary>
        /// Apply a larger mixed batch with one pass over the materialized window and one UI
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
            if (Loading || compare == null || !itemsComplete
                || currentFilter != liveWindowFilter || currentSort != liveWindowSort)
                return new BatchUpdateOutcome(false);

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
                    if (Loading || !itemsComplete || currentFilter != filter || currentSort != sort
                        || currentFilter != liveWindowFilter || currentSort != liveWindowSort)
                        return new BatchUpdateOutcome(false);

                    var items = Items;
                    publishedRowCount = items.Count;
                    bool Include(INode node) => files.TryGetValue(node, out var current)
                        && ReferenceEquals(current, node) && nf.Matches(node);
                    var changedSet = new HashSet<INode>(ReferenceEqualityComparer.Instance);
                    var removed = new HashSet<INode>(ReferenceEqualityComparer.Instance);
                    var pureRemovals = true;
                    foreach (var node in changed)
                    {
                        if (node == null) continue;
                        changedSet.Add(node);
                        if (Include(node))
                        {
                            pureRemovals = false;
                            continue;
                        }
                        removed.Add(node);
                    }
                    var merged = liveWindow.ApplyBatch(changed, Include, compare);
                    itemsTruncated = liveWindow.IsTruncated;
                    Volatile.Write(ref reserveRefillNeeded, liveWindow.NeedsRefill ? 1 : 0);
                    Volatile.Write(ref reserveRefillUrgent,
                        liveWindow.IsVisibleIncomplete ? 1 : 0);

                    var identical = merged.Length == items.Count;
                    for (var i = 0; identical && i < merged.Length; i++)
                        identical = ReferenceEquals(merged[i], items[i]);
                    if (identical)
                    {
                        //Metadata changed without crossing a neighbour. Rebind realized rows,
                        //but leave the collection and selection completely untouched.
                        gridMode = "bulk-repaint";
                        RowsRefreshRequested?.Invoke(changed);
                        return new BatchUpdateOutcome(true, liveWindow.NeedsRefill,
                            liveWindow.IsVisibleIncomplete);
                    }

                    //A structural delete cannot reorder surviving rows. Remove only visible
                    //victims and append promoted reserve rows; resetting the full 100k-row
                    //ItemsSource made WPF rebuild its virtualized bookkeeping for every
                    //large Explorer delete and was the direct source of red UI incidents.
                    var removalCount = pureRemovals
                        ? LiveResultWindow<INode>.PureRemovalDiffCount(items, merged, removed)
                        : -1;
                    if (removalCount >= 0 && removalCount <= TargetedBulkRemovalLimit)
                    {
                        gridMode = "bulk-remove";
                        BeforeItemsExchange();
                        LiveResultWindow<INode>.ApplyPureRemovalDiff(items, merged, removed);
                        AfterItemsExchange();
                        PropertyChanged?.Invoke(this,
                            new PropertyChangedEventArgs(nameof(CountsInfo)));
                        publishedRowCount = items.Count;
                        return new BatchUpdateOutcome(true, liveWindow.NeedsRefill,
                            liveWindow.IsVisibleIncomplete);
                    }

                    //Size/time sorts mix deleted rows with surviving ancestor metadata rows.
                    //The survivors still keep their relative order; move only those changed
                    //identities plus the small number of rows entering/leaving the 100k
                    //boundary. This covers the same delete storm without a collection Reset.
                    var targeted = LiveResultWindow<INode>.PlanTargetedDiff(
                        items, merged, changedSet);
                    if (targeted != null
                        && targeted.OperationCount <= TargetedBulkMutationLimit)
                    {
                        gridMode = "bulk-diff";
                        BeforeItemsExchange();
                        LiveResultWindow<INode>.ApplyTargetedDiff(items, merged, targeted);
                        AfterItemsExchange();
                        PropertyChanged?.Invoke(this,
                            new PropertyChangedEventArgs(nameof(CountsInfo)));
                        publishedRowCount = items.Count;
                        return new BatchUpdateOutcome(true, liveWindow.NeedsRefill,
                            liveWindow.IsVisibleIncomplete);
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
                    return new BatchUpdateOutcome(true, liveWindow.NeedsRefill,
                        liveWindow.IsVisibleIncomplete);
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
            public readonly bool UnknownTail;
            public readonly string CacheStatus;
            public QueryResult(List<INode> items, bool unknownTail, string cacheStatus)
            {
                Items = items;
                UnknownTail = unknownTail;
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
            //sort stop this copy every few thousand nodes. Ordinary membership changes are
            //replayed from the query delta journal; only an MFT shard replacement retries.
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
                    var unknownTail = false;
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
                            if (!IsBulkFilesVersionStable(bulkVersion))
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
                        unknownTail = all.Count > MaterializedWindowLimit;
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
                        result = all.Take(MaterializedWindowLimit).ToList(); //Unknown sort => bounded arbitrary prefix
                    else
                    {
                        //Bounded selection instead of a full sort: only the visible window and its
                        //small reserve are materialized. Sorting the whole multi-million-node index
                        //would burn CPU and allocate heavily on every authoritative refresh.
                        result = SelectTop(all, compare, MaterializedWindowLimit,
                            IsCanceled, SortScalarKey(sort));
                    }
                    if (result == null || IsCanceled()) return null;
                    if (refresh && !IsBulkFilesVersionStable(bulkVersion))
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
                    return new QueryResult(result, unknownTail, cacheStatus);
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
        INode Remove(string path, bool patchVersion = true)
        {
            //Apply the delta while this entry and its drive shard are still current. A
            //path-backed watcher node has no parent references and resolves directories
            //through the index; doing that after TryRemove allowed a concurrent MFT publish
            //to place the lookup and the visible rows in different shard generations.
            if (!files.TryRemove(path, SubtractFileSize, out var n)) return null;
            if (patchVersion) PatchFileRemoved(n);
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
        INode GetOrAddNew(string path, NodeMetadataSnapshot? snapshot = null)
        {
            //USN reason bits accumulate while a file handle is open, so installers and
            //OneDrive can report the same Create several times. The old code statted the
            //path before discovering that it was already indexed; under an update storm
            //those redundant disk round trips serialized the whole drive queue.
            if (files.TryGetValue(path, out var indexed)) return indexed;
            var added = snapshot.HasValue
                ? new FileNode(path, snapshot.Value)
                : new FileNode(path);
            //A path that does not resolve on disk must never enter the index: it carries no
            //metadata (1601 times, empty size) and would shadow nothing real. It happens for
            //watcher events of already-vanished temp files and for the ghost paths built from
            //half-delivered renames - the vanished file's delete event (or the next scan)
            //rules the index, not this stat miss.
            if (!added.Exists) return files.TryGetValue(path, out indexed) ? indexed : added;
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
                if (changed.Length == 0) return;
                if (health?.SuspendAutomaticGridUpdates == true)
                {
                    refreshPending = true;
                    return;
                }
                //Repaint unconditionally: a directory whose aggregate changed usually keeps
                //its row (the drive root is always the largest row of a size sort), and a
                //collection update alone leaves such a row showing its old value. Only rows
                //realized on screen are touched, so this stays cheap under any sort.
                RowsRefreshRequested?.Invoke(changed);
                //Under a size sort the new aggregates can also reorder rows.
                if (sort?.Length > 1 && sort.Substring(1) == nameof(INode.Size))
                    await UpdateSmall(changed);
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
            DriveSelectionStore.SelectionChanged += DriveSelectionChanged;
            health = new LiveUpdateHealthMonitor(Dispatcher,
                FSChangeProcessor.GetHealthSnapshot,
                FSChangeProcessor.GetRecentEvents,
                () => new HealthWorkState(Loading, Filtering, Searching),
                ApplyLiveUpdateHealth,
                CatchUpLiveGrid);
            FSChangeProcessor.ShouldIndex = DriveSelectionStore.IsEnabled;
            FSChangeProcessor.Lookup = FindByPath;
            FSChangeProcessor.ReconcileDirs = ReconcileDirectories;
            StartMetadataRefreshWorkers();
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
                var pendingDeletes = new List<FsEvent>();
                FsEvent pendingDeleteEvent = null;
                void FlushDeletes()
                {
                    if (pendingDeletes.Count == 0) return;
                    FSChangeProcessor.ReportActiveStage(pendingDeleteEvent, "delete-index");
                    //Discover every tree before mutating anything. USN commonly reports a
                    //directory delete together with deletes of its children; those children
                    //are covered by the directory's stored aggregate and must not each walk
                    //their parent chain as well.
                    var trees = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var deleted in pendingDeletes)
                    {
                        var path = deleted.FullPath;
                        var indexed = files.TryGetValue(path, out var i) ? i : null;
                        //A USN delete is exact: every descendant has its own ordered record.
                        //Fallback watchers and app echoes remain conservative and uproot the
                        //whole directory because they may report only its root. Archive
                        //members are virtual, so they always need the subtree path.
                        if ((!deleted.DescendantDeletesReported && indexed?.IsDirectory == true)
                            || HasArchiveChildren(indexed))
                            trees.Add(path.TrimEnd(Path.DirectorySeparatorChar,
                                Path.AltDirectorySeparatorChar));
                    }

                    var removed = new List<INode>();
                    var directlyRemoved = new List<INode>();
                    foreach (var deleted in pendingDeletes)
                    {
                        var path = deleted.FullPath;
                        var normalized = path.TrimEnd(Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar);
                        if (trees.Contains(normalized) || IsBelowPendingTree(normalized, trees))
                            continue;
                        var direct = Remove(path, patchVersion: false);
                        if (direct != null) directlyRemoved.Add(direct);
                    }
                    pendingDeletes.Clear();
                    pendingDeleteEvent = null;
                    if (directlyRemoved.Count > 0)
                    {
                        //One immutable cache/version patch for the complete coalesced batch;
                        //cloning its removal overlay once per USN record becomes quadratic.
                        PatchFilesVersion(Array.Empty<INode>(), directlyRemoved);
                        removed.AddRange(directlyRemoved);
                    }
                    if (trees.Count > 0) removed.AddRange(RemoveTrees(trees.ToArray()));
                    RecordStructuralNodes(removed);
                }

                //Independent paths commute inside one watcher batch: their final index and
                //directory-size state is identical regardless of which is applied first.
                //Keep such deletes pending so every deleted directory shares one full-index
                //RemoveTrees pass. Only a create/change/rename of the same path or one of its
                //ancestors/descendants needs the delete to be visible before it is handled.
                void FlushConflictingDeletes(params string[] paths)
                {
                    if (!pendingDeletes.Any(deleted => paths.Any(path =>
                        PathsOverlap(deleted.FullPath, path)))) return;
                    FlushDeletes();
                }

                foreach (var e in events)
                {
                    try
                    {
                        //Status = $"WATCHED {++watched}. changes => last {DateTime.Now.TimeOfDay} {e.FullPath}";
                        FSChangeProcessor.ReportActiveStage(e, e.ChangeType switch
                        {
                            WatcherChangeTypes.Created => "create-stat",
                            WatcherChangeTypes.Changed => e.IsMetadataResult
                                ? "metadata-apply" : "metadata-queue",
                            WatcherChangeTypes.Renamed => "rename-index",
                            WatcherChangeTypes.Deleted => "delete-queue",
                            _ => "event"
                        });
                        switch (e.ChangeType)
                        {
                            case WatcherChangeTypes.Created:
                                //Trace.WriteLine($"+{e.FullPath}");
                                FlushConflictingDeletes(e.FullPath); //A delete of this path must apply first
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
                                FlushConflictingDeletes(e.FullPath);
                                if (!e.IsMetadataResult)
                                {
                                    //The actual stat runs outside this ordered queue. A
                                    //result returns here carrying the expected node identity,
                                    //so a delete/rename/recreate that won the race cannot be
                                    //overwritten by stale metadata.
                                    QueueMetadataRefresh(e.FullPath);
                                    break;
                                }
                                var snapshot = e.MetadataSnapshot.Value;
                                if (e.MetadataNode != null)
                                {
                                    if (files.TryGetValue(e.FullPath, out var current)
                                        && ReferenceEquals(current, e.MetadataNode))
                                    {
                                        var oldSize = current.IsDirectory ? 0L : (long)current.Size;
                                        current.ApplyMetadata(snapshot);
                                        PropagateSizeDelta(current,
                                            (current.IsDirectory ? 0L : (long)current.Size) - oldSize);
                                        RecordMetadata(current);
                                    }
                                }
                                else if (!files.ContainsKey(e.FullPath))
                                    RecordStructuralNode(GetOrAddNew(e.FullPath, snapshot));
                                break;
                            case WatcherChangeTypes.Renamed:
                                //Trace.WriteLine($"{e.OldFullPath}->{e.FullPath}");
                                FlushConflictingDeletes(e.OldFullPath, e.FullPath);
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
                                pendingDeletes.Add(e);
                                pendingDeleteEvent ??= e;
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
                    if (events.Length > 0)
                        FSChangeProcessor.ReportActiveStage(events[0], "grid-publish");
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
            //An authoritative query may be traversing the source concurrently. It will
            //replay these identities onto its materialized prefix immediately before UI
            //publication instead of discarding and repeating the multi-million-node query.
            var replayQueued = RecordViewQueryDeltas(nodes);
            //A user query has already installed its requested filter/sort, but the visible
            //window still belongs to the previous query. Mutating that old sorted prefix with
            //the new comparer would corrupt it. The active query journal replays these nodes
            //onto the new authoritative prefix immediately before publication.
            if (Filtering && (filter != liveWindowFilter || sort != liveWindowSort))
            {
                //The event arrived after the publishing query sealed its journal. That
                //callback is already on the dispatcher, so reconcile once it has completed.
                if (!replayQueued) QueueTailReconciliation();
                return;
            }
            if (health?.SuspendAutomaticGridUpdates == true)
            {
                refreshPending = true;
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

            //A real loading storm is covered by the drive's authoritative publish. Do not
            //reset a 100k-row grid while the scan is still producing more changes; small
            //batches below stay live through the targeted path.
            if (DefersLoadingBatch(Loading, nodes.Length))
            {
                refreshPending = true;
                return;
            }
            BatchUpdateOutcome outcome;
            if (UsesIncrementalBatch(nodes.Length))
            {
                outcome = await TryIncrementalUpdate(nodes);
                if (!outcome.Handled && Loading)
                {
                    //Unknown sort/partial view: the final drive publish is authoritative.
                    refreshPending = true;
                    return;
                }
            }
            else
            {
                outcome = await TryBulkIncrementalUpdate(nodes);
            }
            if (outcome.Handled)
            {
                if (outcome.NeedsRefresh) QueueReserveRefill(outcome.NeedsImmediateRefresh);
                //A single huge delete can consume the complete reserve. Keep the latency
                //incident open until the immediate authoritative refill restores 100k rows.
                if (!outcome.NeedsImmediateRefresh) health?.GridUpdateReflected();
                return;
            }

            //Only an invalid/unknown sort or an incomplete authoritative publish can reach
            //this safety path. Ordinary filesystem deltas are handled solely by liveWindow
            //and never scan the multi-million-node source.
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

        /// <summary>
        /// Replenish only the private result reserve after filesystem quiet. The visible
        /// prefix is already exact, so this request deliberately does not set refreshPending
        /// or report the grid as stale while it waits.
        /// </summary>
        void QueueReserveRefill(bool urgent = false)
        {
            Volatile.Write(ref reserveRefillNeeded, 1);
            if (urgent) Volatile.Write(ref reserveRefillUrgent, 1);
            Interlocked.Exchange(ref reserveRefillRequestedAt, Environment.TickCount64);
            if (Interlocked.Exchange(ref reserveRefillQueued, 1) == 1) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (Volatile.Read(ref reserveRefillNeeded) != 0)
                    {
                        var requested = Interlocked.Read(ref reserveRefillRequestedAt);
                        if (Volatile.Read(ref reserveRefillUrgent) == 0)
                        {
                            var remaining = TailReconciliationQuietMs
                                - (Environment.TickCount64 - requested);
                            //Short slices let a later reserve-exhausting batch upgrade an
                            //already queued quiet refill without waiting the full 2.5 seconds.
                            if (remaining > 0)
                            {
                                await Task.Delay((int)Math.Min(remaining, 100));
                                continue;
                            }
                            if (requested != Interlocked.Read(ref reserveRefillRequestedAt)) continue;
                        }
                        if (Volatile.Read(ref reserveRefillNeeded) == 0
                            || health?.SuspendAutomaticGridUpdates == true) return;

                        await UpdateCore(null, null, healthRecovery: false, skipDataDebounce: true);
                        return;
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref reserveRefillQueued, 0);
                    if (Volatile.Read(ref reserveRefillNeeded) != 0
                        && health?.SuspendAutomaticGridUpdates != true
                        && (Volatile.Read(ref reserveRefillUrgent) != 0
                            || Environment.TickCount64 - Interlocked.Read(ref reserveRefillRequestedAt)
                                < TailReconciliationQuietMs))
                        QueueReserveRefill(Volatile.Read(ref reserveRefillUrgent) != 0);
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
        /// Whether applying one path mutation before another can affect its meaning. Sibling
        /// paths do not overlap even when they share a parent; equal paths and ancestor/
        /// descendant pairs do. Inputs are watcher paths and therefore already absolute.
        /// </summary>
        internal static bool PathsOverlap(string first, string second)
        {
            if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(second)) return false;
            first = first.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            second = second.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase)) return true;
            return IsDescendantPath(first, second) || IsDescendantPath(second, first);
        }

        static bool IsDescendantPath(string path, string ancestor)
            => path.Length > ancestor.Length
            && path.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase)
            && (path[ancestor.Length] == Path.DirectorySeparatorChar
                || path[ancestor.Length] == Path.AltDirectorySeparatorChar);

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
        readonly ConcurrentDictionary<string, string> unavailableDrives = new(StringComparer.OrdinalIgnoreCase);
        readonly ConcurrentDictionary<string, CancellationTokenSource> driveRetries = new(StringComparer.OrdinalIgnoreCase);
        readonly ConcurrentDictionary<string, CancellationTokenSource> activeDriveScans = new(StringComparer.OrdinalIgnoreCase);
        readonly ConcurrentDictionary<string, string> driveLogStates = new(StringComparer.OrdinalIgnoreCase);
        internal const int DriveRetryDelayMs = 15000;
        int modelDisposed;
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
                        var scanWatch = Stopwatch.StartNew();
                        var scanCts = new CancellationTokenSource();
                        activeDriveScans[root] = scanCts;
                        var scanToken = scanCts.Token;
                        await Task.Delay(500); //Batch the burst of watcher (re)starts
                        var requested = Volatile.Read(ref pending[0]);
                        $"drive {root} scan started (covering {requested} requests)".Debug();
                        var key = root.TrimEnd(Path.DirectorySeparatorChar);
                        var streamed = 0L;
                        var drivePublished = false;
                        Task deferredCancellationWork = null;
                        MftOrigin? timingOrigin = null;
                        MftLoadTiming? mftTiming = null;
                        var timingNodes = 0;
                        long sourceMs = 0, indexMs = 0, publishMs = 0, gridMs = 0;
                        try
                        {
                            scanToken.ThrowIfCancellationRequested();
                            var fresh = new NonBlocking.ConcurrentDictionary<object, INode>(NodePath.KeyComparer);
                            IFrnNodeSource frnNodes = null;
                            IReadOnlyList<INode> denseNodes = null;
                            var drive = new DriveInfo(root);
                            var hasExplicitSelection = DriveSelectionStore.TryGetExplicit(root,
                                out var explicitlyEnabled);
                            var keepExisting = false;
                            string skip;
                            if (hasExplicitSelection && !explicitlyEnabled)
                                skip = "not selected for indexing";
                            else if (!hasExplicitSelection && drive.DriveType == DriveType.Network)
                                //An unknown network drive is opt-in. Do not even probe/reconnect
                                //the SMB mapping until the user explicitly selects it.
                                skip = "not selected for indexing";
                            else if (!DriveAvailability.IsReady(drive))
                            {
                                skip = "not ready";
                                //A transiently disconnected selected mapping is not an empty
                                //drive. Keep its last good index and retry source setup + scan;
                                //publishing an empty replacement made P:/S: randomly disappear.
                                keepExisting = hasExplicitSelection && explicitlyEnabled;
                                if (keepExisting)
                                {
                                    unavailableDrives[key] = "network unavailable; retrying";
                                    ScheduleDriveRetry(root);
                                    ReportDriveState(root, "not ready; retry scheduled");
                                }
                            }
                            else if (!DriveSelectionStore.IsEnabled(root))
                                skip = "not selected for indexing";
                            else
                                skip = null;
                            if (skip == null)
                            {
                                CancelDriveRetry(root);
                                unavailableDrives.TryRemove(key, out _);
                                var phase = Stopwatch.StartNew();
                                var sourceTask = Task.Run(() => DriveEntries(drive, scanToken));
                                DriveEntryResult source;
                                try { source = await sourceTask.WaitAsync(scanToken); }
                                catch (OperationCanceledException)
                                {
                                    ObserveAbandoned(sourceTask);
                                    deferredCancellationWork = sourceTask;
                                    throw;
                                }
                                var entries = source.Entries;
                                var origin = source.Origin;
                                sourceMs = phase.ElapsedMilliseconds;
                                timingOrigin = origin;
                                frnNodes = entries as IFrnNodeSource;
                                mftTiming = frnNodes?.LoadTiming;
                                denseNodes = frnNodes?.DenseNodes;
                                //MFT results know their exact live count. Allocate the final
                                //shard at its target capacity once instead of walking every
                                //power-of-two LOH resize on the way there.
                                if (entries.TryGetNonEnumeratedCount(out var entryCount) && entryCount > 0)
                                    fresh = new NonBlocking.ConcurrentDictionary<object, INode>(
                                        Environment.ProcessorCount, entryCount, NodePath.KeyComparer);
                                //The node itself is the key - no full path string is ever built or stored.
                                //A dense MFT result is already fully materialized, so build its large
                                //path hash table across every CPU instead of serializing 1.8M inserts.
                                phase.Restart();
                                streamed = PopulateDriveIndex(fresh, entries, denseNodes, scanToken);
                                indexMs = phase.ElapsedMilliseconds;
                                origins[key] = origin;
                                scanToken.ThrowIfCancellationRequested();
                                phase.Restart();
                                drivePublished = PublishDrive(root, fresh, streamed, frnNodes, denseNodes);
                                publishMs = phase.ElapsedMilliseconds;
                                timingNodes = fresh.Count;
                                ReportDriveState(root, $"loaded {fresh.Count} entries via {origin}");
                                streamed = 0; //Deducted from the loading counter at the publish
                            }
                            else
                            {
                                $"drive {root} {skip} => {(keepExisting ? "keeping the last index and retrying" : "entries are dropped")}".Debug();
                                if (!keepExisting)
                                {
                                    CancelDriveRetry(root);
                                    unavailableDrives.TryRemove(key, out _);
                                    origins.TryRemove(key, out _);
                                    drivePublished = PublishDrive(root, fresh); //Explicitly disabled => prune stale entries
                                    ReportDriveState(root, skip);
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            ReportDriveState(root, "load canceled");
                            $"drive {root} load canceled by selection change".Debug();
                        }
                        catch (Exception e)
                        {
                            //A failed selected network scan keeps the last good entries and
                            //gets another bounded attempt instead of silently vanishing forever.
                            if (DriveSelectionStore.TryGetExplicit(root, out var enabled) && enabled)
                            {
                                unavailableDrives[key] = "scan failed; retrying";
                                ScheduleDriveRetry(root);
                                ReportDriveState(root, $"scan failed; retry scheduled ({e.Message})");
                            }
                            $"drive {root} scan failed: {e.Message}".Debug();
                        }
                        finally { Interlocked.Add(ref loadingNodes, -streamed); } //Not published => not counted by files

                        //Update filtered files
                        if (drivePublished && !scanToken.IsCancellationRequested)
                        {
                            var phase = Stopwatch.StartNew();
                            var updateTask = Update();
                            try { await updateTask.WaitAsync(scanToken); }
                            catch (OperationCanceledException)
                            {
                                ObserveAbandoned(updateTask);
                                deferredCancellationWork = deferredCancellationWork == null
                                    ? updateTask : Task.WhenAll(deferredCancellationWork, updateTask);
                            }
                            gridMs = phase.ElapsedMilliseconds;
                        }
                        if (timingOrigin.HasValue && !scanToken.IsCancellationRequested)
                            ReportDriveTiming(root, timingOrigin.Value, timingNodes,
                                sourceMs, indexMs, publishMs, gridMs, scanWatch.ElapsedMilliseconds,
                                mftTiming);

                        ((ICollection<KeyValuePair<string, CancellationTokenSource>>)activeDriveScans)
                            .Remove(new KeyValuePair<string, CancellationTokenSource>(root, scanCts));
                        if (deferredCancellationWork == null) scanCts.Dispose();
                        else _ = deferredCancellationWork.ContinueWith(t =>
                        {
                            _ = t.Exception;
                            scanCts.Dispose();
                        },
                            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);

                        //Quit when no new request for this drive arrived during the scan
                        if (Interlocked.CompareExchange(ref pending[0], 0, requested) == requested) return;
                        $"drive {root} scan rerun: {Volatile.Read(ref pending[0]) - requested} new requests arrived during the scan".Debug();
                    }
                }
                finally
                {
                    if (activeDriveScans.TryRemove(root, out var active))
                    {
                        try { active.Cancel(); } catch { }
                        active.Dispose();
                    }
                    await EndDriveScan();
                }
            });
        }

        void ScheduleDriveRetry(string root)
        {
            if (Volatile.Read(ref modelDisposed) != 0
                || !DriveSelectionStore.TryGetExplicit(root, out var enabled) || !enabled) return;
            var cts = new CancellationTokenSource();
            if (!driveRetries.TryAdd(root, cts))
            {
                cts.Dispose();
                return;
            }

            _ = Task.Run(async () =>
            {
                try { await Task.Delay(DriveRetryDelayMs, cts.Token); }
                catch (OperationCanceledException) { return; }

                var pair = new KeyValuePair<string, CancellationTokenSource>(root, cts);
                if (!((ICollection<KeyValuePair<string, CancellationTokenSource>>)driveRetries).Remove(pair))
                    return;
                cts.Dispose();
                if (Volatile.Read(ref modelDisposed) == 0
                    && DriveSelectionStore.TryGetExplicit(root, out enabled) && enabled)
                {
                    //Probe while the app remains in its normal Loaded state. Only a mapping
                    //that can now reconnect starts watcher setup and a visible drive scan;
                    //an offline share therefore does not make the status bar pulse forever.
                    var ready = false;
                    try { ready = DriveAvailability.IsReady(new DriveInfo(root)); } catch { }
                    if (ready) FSChangeProcessor.RefreshDrive(root);
                    else ScheduleDriveRetry(root);
                }
            });
        }

        void DriveSelectionChanged(IReadOnlyList<string> changedRoots)
        {
            foreach (var root in changedRoots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!DriveSelectionStore.TryGetExplicit(root, out var enabled) || enabled) continue;
                if (activeDriveScans.TryGetValue(root, out var scan))
                {
                    try { scan.Cancel(); } catch (ObjectDisposedException) { }
                    ReportDriveState(root, "load cancellation requested");
                }
                CancelDriveRetry(root);
            }
        }

        void CancelDriveRetry(string root)
        {
            if (!driveRetries.TryRemove(root, out var cts)) return;
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        void ReportDriveState(string root, string state)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(state)) return;
            if (driveLogStates.TryGetValue(root, out var previous) && previous == state) return;
            driveLogStates[root] = state;
            _ = Log($"Drive {root} {state}");
        }

        void ReportDriveTiming(string root, MftOrigin origin, int nodes,
            long sourceMs, long indexMs, long publishMs, long gridMs, long totalMs,
            MftLoadTiming? mft)
            => _ = Log($"Drive {root} timing: source={origin} {sourceMs}ms; "
                + $"index={indexMs}ms; publish={publishMs}ms; grid={gridMs}ms; "
                + $"total={totalMs}ms; nodes={nodes}"
                + (mft.HasValue
                    ? $"; MFT(read/parse={mft.Value.ReadParseMs}ms, link={mft.Value.LinkMs}ms, "
                        + $"sizes/hash={mft.Value.AggregateHashMs}ms, dense={mft.Value.DenseMs}ms)"
                    : ""));

        static void ObserveAbandoned(Task task)
            => _ = task.ContinueWith(t => _ = t.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted
                    | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        long PopulateDriveIndex(NonBlocking.ConcurrentDictionary<object, INode> target,
            IEnumerable<INode> nodes, IReadOnlyList<INode> denseNodes,
            CancellationToken cancellationToken)
        {
            if (denseNodes != null)
            {
                var count = denseNodes.Count;
                if (count == 0) return 0;
                var workers = Math.Max(1, Environment.ProcessorCount);
                var rangeSize = Math.Max(4096, (count + workers * 8 - 1) / (workers * 8));
                long denseReported = 0;
                try
                {
                    Parallel.ForEach(Partitioner.Create(0, count, rangeSize),
                        new ParallelOptions { CancellationToken = cancellationToken }, range =>
                    {
                        for (var i = range.Item1; i < range.Item2; i++)
                        {
                            var node = denseNodes[i];
                            target[node] = node;
                        }
                        //Progress is informational; publish once per range instead of one
                        //contended atomic operation for every MFT record.
                        var completed = range.Item2 - range.Item1;
                        Interlocked.Add(ref denseReported, completed);
                        Interlocked.Add(ref loadingNodes, completed);
                    });
                }
                catch
                {
                    Interlocked.Add(ref loadingNodes, -Volatile.Read(ref denseReported));
                    throw;
                }
                return count;
            }

            //Directory walks are lazy and cannot be partitioned without buffering. Keep
            //streaming them, but update the progress counter in cache-friendly batches.
            const int ProgressBatch = 4096;
            long total = 0, reported = 0;
            try
            {
                foreach (var node in nodes)
                {
                    if ((total & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                    target[node] = node;
                    total++;
                    if (total - reported < ProgressBatch) continue;
                    Interlocked.Add(ref loadingNodes, ProgressBatch);
                    reported += ProgressBatch;
                }
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Add(ref loadingNodes, total - reported);
                return total;
            }
            catch
            {
                Interlocked.Add(ref loadingNodes, -reported);
                throw;
            }
        }

        /// <summary>
        /// Swap one drive's subtree in the shared index: everything indexed under the drive is
        /// replaced by the fresh scan while all other drives' entries stay untouched. A finished
        /// drive shows immediately - a fast MFT drive never waits on the slowest drive's walk.
        /// </summary>
        bool PublishDrive(string root, NonBlocking.ConcurrentDictionary<object, INode> fresh,
            long streamed = 0, IEnumerable<INode> frnNodes = null,
            IReadOnlyList<INode> denseNodes = null)
        {
            //Nothing new and nothing indexed under the drive => skip the subtree swap
            //(the common case for skipped/deselected drives on every refresh)
            if (fresh.IsEmpty && !files.ContainsKey(root))
            {
                Interlocked.Add(ref loadingNodes, -streamed);
                return false;
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
            return true;
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

        volatile bool ntfsWalked; //An NTFS drive had to fall back to the walk => hint how to get MFT indexing

        /// <summary>
        /// Get all files of the drive: NTFS via the raw $MFT (direct when elevated -> broker -> service),
        /// anything else - or NTFS with no MFT source available - via the directory walk
        /// </summary>
        /// <param name="drive"></param>
        /// <returns></returns>
        readonly record struct DriveEntryResult(IEnumerable<INode> Entries, MftOrigin Origin);

        DriveEntryResult DriveEntries(DriveInfo drive, CancellationToken cancellationToken)
        {
            var origin = MftOrigin.Walk;
            cancellationToken.ThrowIfCancellationRequested();
            //The caller already performed a bounded readiness/reconnect probe. In particular,
            //a persistent SMB mapping can have an accessible root while DriveInfo.IsReady
            //still reports its old disconnected state; testing it again would discard the walk.
            if (drive == null) return new DriveEntryResult(Enumerable.Empty<INode>(), origin);

            if (CanUseMftSource(drive.DriveType, drive.DriveFormat))
            {
                var nodes = MftSource.TryGetNodes(drive, out origin, cancellationToken);
                if (nodes != null) return new DriveEntryResult(nodes, origin);
                ntfsWalked = true;
            }

            origin = MftOrigin.Walk;
            return new DriveEntryResult(DirectoryWalker.Walk(drive, cancellationToken), origin);
        }

        internal static bool CanUseMftSource(DriveType driveType, string format)
            => driveType != DriveType.Network
            && string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// "(C: service, D: folder walk)" tooltip text for the load status, with a hint
        /// when an NTFS drive had to be walked
        /// </summary>
        string OriginsInfo(NonBlocking.ConcurrentDictionary<string, MftOrigin> origins)
        {
            var keys = origins.Keys.Union(unavailableDrives.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            if (keys.Length == 0) return "";
            var parts = string.Join(", ", keys.Select(key =>
            {
                if (unavailableDrives.TryGetValue(key, out var unavailable))
                    return $"{key} {unavailable}";
                var origin = origins.TryGetValue(key, out var loadedOrigin)
                    ? loadedOrigin : MftOrigin.Walk;
                var originText = origin switch
                {
                    MftOrigin.Service => "service",
                    MftOrigin.Direct => "direct",
                    MftOrigin.Broker => "admin helper",
                    _ => "folder walk"
                };
                return $"{key} {originText}";
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
