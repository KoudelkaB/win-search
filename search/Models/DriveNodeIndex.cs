using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace search.Models
{
    /// <summary>
    /// Path index sharded by filesystem root. A completed drive scan is retained as one
    /// immutable compact table containing only indexes into its dense node source - for an
    /// MFT scan that source is the columnar <see cref="MftTable"/>, so the multi-million-row
    /// base owns no objects at all. Watcher/UI mutations live in a small overlay (node or
    /// tombstone). Queries sweep rows and materialize handles only for their results.
    /// </summary>
    internal sealed class DriveNodeIndex : IEnumerable<KeyValuePair<object, INode>>
    {
        internal readonly struct DeltaEntry
        {
            public readonly INode Node; //null = tombstone hiding an immutable base entry
            public readonly long Version;
            public DeltaEntry(INode node, long version)
            {
                Node = node;
                Version = version;
            }
        }

        /// <summary>
        /// Immutable open-addressed path table. Slots retain only an index into the dense
        /// source (a table row or a list position); an eight-bit hash fingerprint rejects
        /// nearly all probe collisions without walking parent chains. Double hashing avoids
        /// primary clustering at 70% load. Table rows are hashed and compared in place - no
        /// handle is created while building or probing.
        /// </summary>
        internal sealed class CompactPathIndex : IReadOnlyCollection<INode>
        {
            const int LoadNumerator = 7;
            const int LoadDenominator = 10;
            readonly MftTable table;
            readonly IReadOnlyList<INode> nodes;
            readonly INode[] nodeArray;
            readonly int[] slots; //dense index + 1; 0 = empty
            readonly byte[] fingerprints;
            readonly int[] duplicateRows; //Sorted dense indexes hidden by a later entry with the same path

            public CompactPathIndex(IReadOnlyList<INode> nodes,
                CancellationToken cancellationToken = default)
                : this(MftTable.TryFromDense(nodes), nodes, cancellationToken) { }

            public CompactPathIndex(MftTable table, CancellationToken cancellationToken = default)
                : this(table, table.DenseNodes, cancellationToken) { }

            CompactPathIndex(MftTable table, IReadOnlyList<INode> nodes, CancellationToken cancellationToken)
            {
                this.table = table;
                this.nodes = nodes;
                nodeArray = nodes as INode[];
                if (nodes.Count == 0)
                {
                    slots = Array.Empty<int>();
                    fingerprints = Array.Empty<byte>();
                    duplicateRows = Array.Empty<int>();
                    return;
                }

                var capacity = 4;
                while ((long)capacity * LoadNumerator / LoadDenominator < nodes.Count)
                    capacity = checked(capacity * 2);
                slots = new int[capacity];
                fingerprints = new byte[capacity];
                var mask = capacity - 1;
                var uniqueCount = 0;
                List<int> duplicates = null;

                for (var i = 0; i < nodes.Count; i++)
                {
                    if ((i & 0x0FFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                    if (table == null && nodes[i] == null)
                        throw new ArgumentException("Path index cannot contain a null node.", nameof(nodes));
                    var hash = HashOf(i);
                    var fingerprint = Fingerprint(hash);
                    var at = (int)(hash & (uint)mask);
                    var step = Step(hash, mask);
                    var inserted = false;
                    for (var probe = 0; probe < capacity; probe++)
                    {
                        var stored = slots[at];
                        if (stored == 0)
                        {
                            slots[at] = i + 1;
                            fingerprints[at] = fingerprint;
                            uniqueCount++;
                            inserted = true;
                            break;
                        }
                        if (fingerprints[at] == fingerprint && SameSource(stored - 1, i))
                        {
                            //Same textual path: mirror dictionary assignment semantics by
                            //making the later value authoritative without another slot.
                            (duplicates ??= new List<int>()).Add(stored - 1);
                            slots[at] = i + 1;
                            inserted = true;
                            break;
                        }
                        at = (at + step) & mask;
                    }
                    if (!inserted) throw new InvalidOperationException("Compact path index is full.");
                }
                cancellationToken.ThrowIfCancellationRequested();
                Count = uniqueCount;
                if (duplicates == null) duplicateRows = Array.Empty<int>();
                else
                {
                    duplicates.Sort();
                    duplicateRows = duplicates.ToArray();
                }
            }

            public int Count { get; }
            public MftTable Table => table;
            /// <summary>Dense indexes that are NOT authoritative (an equal path came later); sorted.</summary>
            public int[] DuplicateRows => duplicateRows;
            internal long StorageBytes => (long)slots.Length * sizeof(int) + fingerprints.Length;

            public bool TryGetValue(object key, out INode node)
            {
                if (TryGetRow(key, out var row))
                {
                    node = NodeAt(row);
                    return true;
                }
                node = null;
                return false;
            }

            /// <summary>The dense index (table row / list position) holding this path.</summary>
            public bool TryGetRow(object key, out int row)
            {
                row = -1;
                if (key == null || slots.Length == 0) return false;
                var hash = Hash(key);
                var fingerprint = Fingerprint(hash);
                var mask = slots.Length - 1;
                var at = (int)(hash & (uint)mask);
                var step = Step(hash, mask);
                for (var probe = 0; probe < slots.Length; probe++)
                {
                    var stored = slots[at];
                    if (stored == 0) return false;
                    if (fingerprints[at] == fingerprint && KeyEquals(stored - 1, key))
                    {
                        row = stored - 1;
                        return true;
                    }
                    at = (at + step) & mask;
                }
                return false;
            }

            public bool Contains(object key) => TryGetRow(key, out _);

            public IEnumerator<INode> GetEnumerator()
            {
                foreach (var stored in slots)
                    if (stored != 0) yield return NodeAt(stored - 1);
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal INode NodeAt(int index)
                => table != null ? table.Handle(index) : nodeArray != null ? nodeArray[index] : nodes[index];

            uint HashOf(int index)
                => table != null ? unchecked((uint)table.PathHash[index]) : Hash(nodes[index]);

            bool KeyEquals(int index, object key)
                => table != null ? NodePath.KeyEqualsRow(table, index, key) : NodePath.KeyEquals(nodes[index], key);

            bool SameSource(int a, int b)
                => table != null ? NodePath.RowsEqual(table, a, b) : NodePath.KeyEquals(nodes[a], nodes[b]);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static uint Hash(object key)
                => unchecked((uint)NodePath.KeyHashCode(key));

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static byte Fingerprint(uint hash)
                => (byte)((hash >> 24) ^ (hash >> 8));

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static int Step(uint hash, int mask)
            {
                //An odd step visits every slot in a power-of-two table.
                var step = (int)(((hash >> 16) ^ hash) | 1) & mask;
                return step == 0 ? 1 : step;
            }
        }

        internal sealed class PreparedDrive
        {
            internal readonly CompactPathIndex Base;
            internal readonly IReadOnlyList<INode> DenseNodes;

            internal PreparedDrive(CompactPathIndex @base, IReadOnlyList<INode> denseNodes)
            {
                Base = @base;
                //A table always keeps its dense rows - its duplicates are listed by the base.
                //A plain list with a duplicate textual path would expose more identities than
                //the authoritative compact table, so it is not exposed as a dense source.
                DenseNodes = @base.Table != null ? denseNodes
                    : denseNodes?.Count == @base.Count ? denseNodes : null;
            }

            public MftTable Table => Base.Table;
            public int Count => Base.Count;
            public bool IsEmpty => Base.Count == 0;
            public IEnumerable<INode> Values
            {
                get
                {
                    if (Table != null)
                    {
                        var hidden = Table.Count == Base.Count ? null : new HashSet<int>(Base.DuplicateRows);
                        for (var row = 0; row < Table.Count; row++)
                            if (hidden?.Contains(row) != true) yield return Table.Handle(row);
                        yield break;
                    }
                    if (DenseNodes != null)
                    {
                        foreach (var node in DenseNodes) yield return node;
                        yield break;
                    }
                    foreach (var node in Base) yield return node;
                }
            }
        }

        internal sealed class Shard
        {
            public readonly string Root;
            public readonly CompactPathIndex Base;
            public readonly IReadOnlyList<INode> DenseNodes;
            public readonly MftTable Table;
            public readonly NonBlocking.ConcurrentDictionary<object, DeltaEntry> Delta;
            int count;
            /// <summary>
            /// A drive scan is reading the disk for this shard's replacement (set/cleared
            /// under DriveNodeIndex.mutationLock). While it runs, removing an entry the
            /// immutable base never held still leaves a tombstone: the scan may have read
            /// that path's record before the removal, and only a post-watermark tombstone
            /// keeps such a stale copy out of the published snapshot (a temp file written
            /// and renamed away mid-scan otherwise reappears in the grid as a phantom).
            /// </summary>
            public bool SnapshotPending;
            //Bounded so a runaway removal storm during a slow scan cannot grow the overlay
            //without limit; the oldest tombstones are forgotten first.
            readonly Queue<object> transientTombstones = new();
            internal const int TransientTombstoneLimit = 16_384;

            public Shard(string root, PreparedDrive prepared)
            {
                Root = root;
                Base = prepared.Base;
                DenseNodes = prepared.DenseNodes;
                Table = prepared.Table;
                Delta = new NonBlocking.ConcurrentDictionary<object, DeltaEntry>(NodePath.KeyComparer);
                count = Base.Count;
            }

            public int Count => Volatile.Read(ref count);
            public bool IsUnused => count == 0 && Base.Count == 0 && Delta.IsEmpty;

            public void EndSnapshot()
            {
                SnapshotPending = false;
                //Only a pending scan needs copies of in-place metadata mutations and
                //tombstones for paths outside the base. Release them after cancellation.
                foreach (var pair in Delta)
                    if (pair.Value.Node == null ? !Base.Contains(pair.Key)
                        : Base.TryGetValue(pair.Key, out var stored)
                            && ReferenceEquals(stored, pair.Value.Node))
                        Delta.TryRemove(pair.Key, out _);
                transientTombstones.Clear();
            }

            public bool TryGetValue(object key, out INode node)
            {
                if (!Delta.IsEmpty && Delta.TryGetValue(key, out var changed))
                {
                    node = changed.Node;
                    return node != null;
                }
                return Base.TryGetValue(key, out node);
            }

            /// <summary>Set under DriveNodeIndex.mutationLock.</summary>
            public void Set(object key, INode value, long version, bool forceDelta = false)
            {
                ArgumentNullException.ThrowIfNull(value);
                var existed = TryGetValue(key, out _);
                //Restore the immutable value without retaining a redundant delta.
                if (!forceDelta && Base.TryGetValue(key, out var stored)
                    && ReferenceEquals(stored, value))
                    Delta.TryRemove(key, out _);
                else
                    Delta[key] = new DeltaEntry(value, version);
                if (!existed) Volatile.Write(ref count, count + 1);
            }

            /// <summary>Remove under DriveNodeIndex.mutationLock.</summary>
            public bool TryRemove(object key, Action<INode> beforeRemove, long version,
                out INode node)
            {
                if (!TryGetValue(key, out node)) return false;
                beforeRemove?.Invoke(node);
                if (Base.Contains(key))
                    Delta[key] = new DeltaEntry(null, version);
                else if (SnapshotPending)
                {
                    Delta[key] = new DeltaEntry(null, version);
                    transientTombstones.Enqueue(key);
                    while (transientTombstones.Count > TransientTombstoneLimit)
                    {
                        var oldest = transientTombstones.Dequeue();
                        if (Delta.TryGetValue(oldest, out var entry) && entry.Node == null
                            && !Base.Contains(oldest))
                            Delta.TryRemove(oldest, out _);
                    }
                }
                else
                    Delta.TryRemove(key, out _);
                Volatile.Write(ref count, count - 1);
                return true;
            }

            /// <summary>
            /// Replay a mutation made after a scan started onto its freshly prepared base.
            /// The old live node is intentional: it contains the event's final metadata and
            /// aggregate values, while the scan may have observed the disk just before it.
            /// </summary>
            public void ApplyPreserved(object key, DeltaEntry entry)
            {
                if (entry.Node != null)
                {
                    Set(key, entry.Node, entry.Version, forceDelta: true);
                    return;
                }
                if (TryGetValue(key, out _))
                    TryRemove(key, null, entry.Version, out _);
            }

            /// <summary>
            /// Dense indexes of the base that a query must skip: duplicates the compact table
            /// hides plus every base entry the delta shadows (tombstone or replacement). Sorted.
            /// </summary>
            public int[] HiddenIndexes(KeyValuePair<object, DeltaEntry>[] delta)
            {
                if (delta.Length == 0) return Base.DuplicateRows;
                var hidden = new HashSet<int>(Base.DuplicateRows);
                foreach (var pair in delta)
                    if (Base.TryGetRow(pair.Key, out var row)) hidden.Add(row);
                var result = hidden.ToArray();
                Array.Sort(result);
                return result;
            }

            public IEnumerable<KeyValuePair<object, INode>> Entries()
            {
                var hidden = new HashSet<int>(HiddenIndexes(Delta.ToArray()));
                if (Table != null)
                {
                    for (var row = 0; row < Table.Count; row++)
                    {
                        if (hidden.Contains(row)) continue;
                        var node = Table.Handle(row);
                        yield return new KeyValuePair<object, INode>(node, node);
                    }
                }
                else if (DenseNodes is { } denseNodes)
                {
                    for (var i = 0; i < denseNodes.Count; i++)
                    {
                        if (hidden.Contains(i)) continue;
                        yield return new KeyValuePair<object, INode>(denseNodes[i], denseNodes[i]);
                    }
                }
                else
                {
                    foreach (var node in Base)
                    {
                        if (Delta.ContainsKey(node)) continue;
                        yield return new KeyValuePair<object, INode>(node, node);
                    }
                }
                foreach (var pair in Delta)
                    if (pair.Value.Node != null)
                        yield return new KeyValuePair<object, INode>(pair.Key, pair.Value.Node);
            }
        }

        /// <summary>
        /// A point-in-time, read-only view of every indexed node: the immutable drive rows
        /// (minus the few the live overlay hides) followed by the overlay's own nodes.
        /// Building one is O(overlay), never O(index) - no row is copied and no handle is
        /// created until a specific element is asked for. Sorting and filtering sweep the
        /// rows through <see cref="TryRow"/> and materialize only what they keep.
        /// </summary>
        internal sealed class Snapshot : IReadOnlyList<INode>
        {
            internal sealed class Segment
            {
                public readonly MftTable Table;
                public readonly IReadOnlyList<INode> List;
                public readonly int[] Hidden; //Sorted dense indexes excluded from the view
                public readonly int Count;

                public Segment(MftTable table, IReadOnlyList<INode> list, int[] hidden)
                {
                    Table = table;
                    List = list;
                    Hidden = hidden ?? Array.Empty<int>();
                    Count = (table?.Count ?? list.Count) - Hidden.Length;
                }

                /// <summary>Dense index of the local visible position: skips the hidden indexes in O(log hidden).</summary>
                public int IndexAt(int local)
                {
                    var hidden = Hidden;
                    if (hidden.Length == 0) return local;
                    //hidden[c] - c is non-decreasing; the first c with hidden[c] - c > local is
                    //the number of hidden indexes before the answer.
                    int lo = 0, hi = hidden.Length;
                    while (lo < hi)
                    {
                        var mid = (lo + hi) >> 1;
                        if (hidden[mid] - mid > local) hi = mid;
                        else lo = mid + 1;
                    }
                    return local + lo;
                }

                public bool IsVisible(int index)
                    => Hidden.Length == 0 || Array.BinarySearch(Hidden, index) < 0;

                public INode NodeAt(int local)
                {
                    var index = IndexAt(local);
                    return Table != null ? Table.Handle(index) : List[index];
                }

                public Segment Hiding(IEnumerable<int> more)
                {
                    var set = new HashSet<int>(Hidden);
                    foreach (var index in more) set.Add(index);
                    if (set.Count == Hidden.Length) return this;
                    var hidden = set.ToArray();
                    Array.Sort(hidden);
                    return new Segment(Table, List, hidden);
                }
            }

            readonly Segment[] segments;
            readonly int[] starts; //Cumulative visible counts; starts[segments.Length] = first extra
            readonly INode[] extras;

            public Snapshot(Segment[] segments, INode[] extras)
            {
                this.segments = segments;
                this.extras = extras ?? Array.Empty<INode>();
                starts = new int[segments.Length + 1];
                for (var i = 0; i < segments.Length; i++)
                    starts[i + 1] = checked(starts[i] + segments[i].Count);
                Count = checked(starts[^1] + this.extras.Length);
            }

            public int Count { get; }
            internal IReadOnlyList<Segment> Segments => segments;
            internal INode[] Extras => extras;

            public INode this[int index]
            {
                get
                {
                    if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
                    var segment = Locate(index);
                    return segment < 0 ? extras[index - starts[^1]] : segments[segment].NodeAt(index - starts[segment]);
                }
            }

            /// <summary>The table row behind a position, when it is an MFT row (not a list node or an overlay node).</summary>
            public bool TryRow(int index, out MftTable table, out int row)
            {
                var segment = Locate(index);
                if (segment >= 0 && segments[segment].Table is { } t)
                {
                    table = t;
                    row = segments[segment].IndexAt(index - starts[segment]);
                    return true;
                }
                table = null;
                row = -1;
                return false;
            }

            int Locate(int index)
            {
                for (var i = 0; i < segments.Length; i++)
                    if (index < starts[i + 1]) return i;
                return -1;
            }

            /// <summary>
            /// The same view with these nodes removed and those added (the cache overlay of
            /// small membership changes). Rows hide in place; other nodes leave or join the
            /// extras. An added node already visible in a segment is not duplicated.
            /// </summary>
            public Snapshot Patch(IReadOnlySet<INode> removed, IReadOnlyList<INode> added)
            {
                var nextSegments = (Segment[])segments.Clone();
                var nextExtras = new List<INode>(extras.Length + added.Count);
                if (removed.Count != 0)
                {
                    for (var s = 0; s < nextSegments.Length; s++)
                    {
                        var segment = nextSegments[s];
                        List<int> hide = null;
                        foreach (var node in removed)
                        {
                            var index = IndexIn(segment, node);
                            if (index >= 0) (hide ??= new List<int>()).Add(index);
                        }
                        if (hide != null) nextSegments[s] = segment.Hiding(hide);
                    }
                }
                foreach (var node in extras)
                    if (!removed.Contains(node)) nextExtras.Add(node);
                foreach (var node in added)
                {
                    if (node == null) continue;
                    var present = false;
                    foreach (var segment in nextSegments)
                    {
                        var index = IndexIn(segment, node);
                        if (index >= 0 && segment.IsVisible(index))
                        {
                            present = true;
                            break;
                        }
                    }
                    if (present) continue;
                    var duplicate = false;
                    foreach (var existing in nextExtras)
                        if (ReferenceEquals(existing, node))
                        {
                            duplicate = true;
                            break;
                        }
                    if (!duplicate) nextExtras.Add(node);
                }
                return new Snapshot(nextSegments, nextExtras.ToArray());
            }

            static int IndexIn(Segment segment, INode node)
            {
                if (segment.Table != null) return segment.Table.RowOf(node);
                var list = segment.List;
                for (var i = 0; i < list.Count; i++)
                    if (ReferenceEquals(list[i], node)) return i;
                return -1;
            }

            public IEnumerator<INode> GetEnumerator()
            {
                for (var i = 0; i < Count; i++) yield return this[i];
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        static readonly PreparedDrive EmptyPrepared = new(
            new CompactPathIndex(Array.Empty<INode>()), Array.Empty<INode>());
        public static PreparedDrive Empty => EmptyPrepared;

        readonly object mutationLock = new();
        volatile Shard[] shards = Array.Empty<Shard>();
        long mutationVersion;

        /// <summary>
        /// Watermark used by a drive scan. Mutations newer than this value are changes the
        /// immutable scan may have missed and must survive publication of its replacement.
        /// </summary>
        public long MutationVersion => Interlocked.Read(ref mutationVersion);

        /// <summary>
        /// Mark that a scan of this drive is about to read the disk and return the mutation
        /// watermark to pair with it. Until the scan publishes (ReplaceDrive) or gives up
        /// (EndSnapshot), removals of entries the current base never held keep tombstones so
        /// the snapshot cannot resurrect them - see <see cref="Shard.SnapshotPending"/>.
        /// </summary>
        public long BeginSnapshot(string root)
        {
            lock (mutationLock)
            {
                if (!string.IsNullOrEmpty(root))
                    GetOrCreateShardLocked(root).SnapshotPending = true;
                return mutationVersion;
            }
        }

        /// <summary>The scan paired with <see cref="BeginSnapshot"/> did not publish a replacement.</summary>
        public void EndSnapshot(string root)
        {
            if (string.IsNullOrEmpty(root)) return;
            lock (mutationLock)
            {
                var shard = Find(shards, root);
                if (shard == null) return;
                shard.EndSnapshot();
                //A shard created only to carry the mark for a drive that was then skipped
                //(deselected, not ready) must not linger as an empty, delta-less shard.
                if (shard.IsUnused)
                    shards = shards.Where(x => !ReferenceEquals(x, shard)).ToArray();
            }
        }

        /// <summary>Build the immutable drive base before taking the short publication lock.</summary>
        public static PreparedDrive PrepareDrive(IEnumerable<INode> nodes,
            IReadOnlyList<INode> denseNodes = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(nodes);
            //An MFT scan is indexed straight from its columns: no node list, no handles.
            var table = MftTable.TryFromDense(nodes) ?? MftTable.TryFromDense(denseNodes);
            if (table != null)
                return table.Count == 0 ? EmptyPrepared
                    : new PreparedDrive(new CompactPathIndex(table, cancellationToken), table.DenseNodes);

            IReadOnlyList<INode> indexed = denseNodes ?? nodes as IReadOnlyList<INode>;
            if (indexed == null)
            {
                var materialized = nodes.TryGetNonEnumeratedCount(out var count)
                    ? new List<INode>(count)
                    : new List<INode>();
                var seen = 0;
                foreach (var node in nodes)
                {
                    if ((seen++ & 0x0FFF) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    materialized.Add(node);
                }
                indexed = materialized;
            }
            if (indexed.Count == 0) return EmptyPrepared;
            var compact = new CompactPathIndex(indexed, cancellationToken);
            return new PreparedDrive(compact, indexed);
        }

        public int Count
        {
            get
            {
                var total = 0;
                foreach (var shard in shards) total += shard.Count;
                return total;
            }
        }

        public bool IsEmpty
        {
            get
            {
                foreach (var shard in shards)
                    if (shard.Count != 0) return false;
                return true;
            }
        }

        public IEnumerable<INode> Values
        {
            get
            {
                var snapshot = shards;
                foreach (var shard in snapshot)
                    foreach (var pair in shard.Entries())
                        yield return pair.Value;
            }
        }

        public bool ContainsKey(object key) => TryGetValue(key, out _);

        public bool TryGetValue(object key, out INode node)
        {
            var snapshot = shards;
            var routed = Find(snapshot, RootOf(key));
            if (routed != null && routed.TryGetValue(key, out node)) return true;
            //Root-only spelling ("C:" vs "C:\"), malformed paths and a publication race
            //fall back across the handful of selected drives without changing semantics.
            foreach (var shard in snapshot)
                if (!ReferenceEquals(shard, routed) && shard.TryGetValue(key, out node)) return true;
            node = null;
            return false;
        }

        public INode this[object key]
        {
            get => TryGetValue(key, out var node) ? node : throw new KeyNotFoundException();
            set
            {
                lock (mutationLock)
                {
                    var shard = GetOrCreateShardLocked(RootOf(key));
                    shard.Set(key, value, ++mutationVersion);
                }
            }
        }

        public INode GetOrAdd(object key, INode value)
        {
            lock (mutationLock)
            {
                var shard = GetOrCreateShardLocked(RootOf(key));
                if (shard.TryGetValue(key, out var current)) return current;
                shard.Set(key, value, ++mutationVersion);
                return value;
            }
        }

        public INode AddOrUpdate(object key, Func<object, INode> add,
            Func<object, INode, INode> update)
        {
            lock (mutationLock)
            {
                var shard = GetOrCreateShardLocked(RootOf(key));
                var result = shard.TryGetValue(key, out var current)
                    ? update(key, current)
                    : add(key);
                shard.Set(key, result, ++mutationVersion);
                return result;
            }
        }

        /// <summary>
        /// Mark an in-place metadata/aggregate mutation. Base nodes are normally not placed
        /// in the structural overlay; during a pending scan a delta preserves the
        /// changed identity and values if its disk snapshot predates the mutation.
        /// </summary>
        public bool Touch(object key, INode expected)
        {
            if (key == null || expected == null) return false;
            lock (mutationLock)
            {
                var routed = Find(shards, RootOf(key));
                if (routed == null || !routed.TryGetValue(key, out var current)
                    || !ReferenceEquals(current, expected)) return false;
                routed.Set(key, current, ++mutationVersion, forceDelta: routed.SnapshotPending);
                return true;
            }
        }

        public bool TryRemove(object key, out INode node)
            => TryRemove(key, null, out node);

        /// <summary>
        /// Remove one entry, optionally running a short non-structural mutation while the
        /// entry and its current drive shard are still published. Size propagation uses
        /// this to resolve path-backed ancestors before a concurrent drive scan can replace
        /// the shard between removal and the parent lookups.
        /// </summary>
        public bool TryRemove(object key, Action<INode> beforeRemove, out INode node)
        {
            lock (mutationLock)
            {
                var routed = Find(shards, RootOf(key));
                var version = ++mutationVersion;
                if (routed != null && routed.TryRemove(key, beforeRemove, version, out node))
                    return true;
                foreach (var shard in shards)
                    if (!ReferenceEquals(shard, routed)
                        && shard.TryRemove(key, beforeRemove, version, out node))
                        return true;
                node = null;
                return false;
            }
        }

        /// <summary>
        /// Atomically replace one drive while every other drive keeps its shard.
        /// Live mutations newer than the watermark always survive. An older mutation
        /// survives only when the snapshot disagrees with it - a path the live pipeline
        /// added that the snapshot lacks, or one it removed that the snapshot still
        /// holds - and the disk, asked through existsOnDisk, does not side with the
        /// snapshot (null = unknown, e.g. access denied: the live pipeline is trusted). A
        /// snapshot can legitimately lag the live pipeline: a folder walk cannot see
        /// protected subtrees the journal reports, and an MFT read is neither atomic nor
        /// guaranteed to reflect the newest metadata writes.
        /// </summary>
        /// <returns>
        /// The superseded drive table, if the drive had one and it is not the replacement.
        /// The caller retires it once every other view of the scan (the FRN map) has been
        /// switched too, so surviving handles stop pinning its columns.
        /// </returns>
        public MftTable ReplaceDrive(string root, PreparedDrive replacement,
            long preserveMutationsAfter = long.MaxValue, Func<string, bool?> existsOnDisk = null)
        {
            ArgumentNullException.ThrowIfNull(replacement);
            root = NormalizeRoot(root);
            lock (mutationLock)
            {
                var current = shards;
                var at = Array.FindIndex(current, x => string.Equals(x.Root, root, StringComparison.OrdinalIgnoreCase));
                var superseded = at < 0 ? null : current[at].Table;
                if (ReferenceEquals(superseded, replacement.Table)) superseded = null;
                var preserved = at < 0 || preserveMutationsAfter == long.MaxValue
                    ? Array.Empty<KeyValuePair<object, DeltaEntry>>()
                    : current[at].Delta.Where(pair => pair.Value.Version > preserveMutationsAfter
                            || DisagreesWithSnapshot(pair, replacement.Base, existsOnDisk))
                        .OrderBy(pair => pair.Value.Version).ToArray();
                if (replacement.IsEmpty)
                {
                    if (at < 0 && preserved.Length == 0) return null;
                    if (preserved.Length != 0)
                    {
                        replacement = EmptyPrepared;
                    }
                    else
                    {
                        var reduced = new Shard[current.Length - 1];
                        if (at > 0) Array.Copy(current, 0, reduced, 0, at);
                        if (at + 1 < current.Length)
                            Array.Copy(current, at + 1, reduced, at,
                                current.Length - at - 1);
                        shards = reduced;
                        return superseded;
                    }
                }

                var next = at < 0 ? new Shard[current.Length + 1] : (Shard[])current.Clone();
                if (at < 0)
                {
                    Array.Copy(current, next, current.Length);
                    at = current.Length;
                }
                var published = new Shard(root, replacement);
                foreach (var pair in preserved) published.ApplyPreserved(pair.Key, pair.Value);
                next[at] = published;
                shards = next;
                return superseded;
            }
        }

        static bool DisagreesWithSnapshot(KeyValuePair<object, DeltaEntry> pair,
            CompactPathIndex snapshot, Func<string, bool?> existsOnDisk)
        {
            if (existsOnDisk == null) return false;
            var inSnapshot = snapshot.Contains(pair.Key);
            if (pair.Value.Node != null == inSnapshot) return false; //Both agree on existence
            var path = pair.Key as string ?? (pair.Key as INode)?.FullName;
            if (string.IsNullOrEmpty(path)) return false;
            bool? onDisk;
            try { onDisk = existsOnDisk(path); }
            catch { onDisk = null; }
            //Keep the live add unless the disk proves it gone; keep the live tombstone
            //unless the disk proves the entry back.
            return pair.Value.Node != null ? onDisk != false : onDisk != true;
        }

        /// <summary>Compatibility helper for small callers/tests; production prepares before publish.</summary>
        public void ReplaceDrive(string root,
            NonBlocking.ConcurrentDictionary<object, INode> replacement,
            IReadOnlyList<INode> denseNodes = null)
        {
            ArgumentNullException.ThrowIfNull(replacement);
            ReplaceDrive(root, PrepareDrive(replacement.Select(pair => pair.Value), denseNodes));
        }

        /// <summary>
        /// Zero-copy stable enumeration of unmodified drive arrays. Delta-bearing shards use
        /// BuildSnapshot, which layers the small delta over the dense base without a copy.
        /// </summary>
        public bool TryGetDenseSnapshot(out IReadOnlyList<INode> nodes)
        {
            var snapshot = shards;
            var lists = new IReadOnlyList<INode>[snapshot.Length];
            for (var i = 0; i < snapshot.Length; i++)
            {
                var dense = snapshot[i].DenseNodes;
                if (!snapshot[i].Delta.IsEmpty || dense == null || dense.Count != snapshot[i].Count)
                {
                    nodes = null;
                    return false;
                }
                lists[i] = dense;
            }
            nodes = lists.Length switch
            {
                0 => Array.Empty<INode>(),
                1 => lists[0],
                _ => new ConcatReadOnlyList(lists)
            };
            return true;
        }

        /// <summary>
        /// The current membership as a <see cref="Snapshot"/>: immutable rows minus the
        /// entries the overlay hides, plus the overlay's nodes. Costs O(overlay), copies
        /// nothing and creates no handles. Null when canceled.
        /// </summary>
        public Snapshot BuildSnapshot(Func<bool> isCanceled = null)
        {
            var current = shards;
            var segments = new List<Snapshot.Segment>(current.Length);
            var extras = new List<INode>();
            foreach (var shard in current)
            {
                if (isCanceled?.Invoke() == true) return null;
                var delta = shard.Delta.ToArray();
                if (shard.Table != null)
                    segments.Add(new Snapshot.Segment(shard.Table, null, shard.HiddenIndexes(delta)));
                else if (shard.DenseNodes is { } denseNodes)
                    segments.Add(new Snapshot.Segment(null, denseNodes, shard.HiddenIndexes(delta)));
                else if (shard.Base.Count != 0)
                {
                    //A plain list with duplicate paths: the compact table is the authority
                    HashSet<object> shadowed = null;
                    if (delta.Length != 0)
                    {
                        shadowed = new HashSet<object>(NodePath.KeyComparer);
                        foreach (var pair in delta) shadowed.Add(pair.Key);
                    }
                    var list = new List<INode>(shard.Base.Count);
                    foreach (var node in shard.Base)
                        if (shadowed?.Contains(node) != true) list.Add(node);
                    segments.Add(new Snapshot.Segment(null, list, null));
                }
                foreach (var pair in delta)
                    if (pair.Value.Node is { } added) extras.Add(added);
            }
            return isCanceled?.Invoke() == true ? null : new Snapshot(segments.ToArray(), extras.ToArray());
        }

        /// <summary>Same membership as <see cref="BuildSnapshot"/>; kept for callers that only need a list.</summary>
        public IReadOnlyList<INode> CopySnapshot(Func<bool> isCanceled) => BuildSnapshot(isCanceled);

        /// <summary>
        /// The nodes matching a predicate, swept straight over the immutable rows plus the
        /// overlay. MFT rows are tested through matchRow without creating a handle; only the
        /// matches are materialized. Result order is arbitrary. Null when canceled.
        /// </summary>
        public List<INode> FilterSnapshot(Func<INode, bool> match, Func<MftTable, int, bool> matchRow,
            Func<bool> isCanceled)
        {
            ArgumentNullException.ThrowIfNull(match);
            matchRow ??= (table, row) => match(table.Handle(row));
            isCanceled ??= static () => false;
            var snapshot = BuildSnapshot(isCanceled);
            if (snapshot == null) return null;
            var result = new List<INode>();
            foreach (var segment in snapshot.Segments)
            {
                var count = segment.Table?.Count ?? segment.List.Count;
                if (count == 0) continue;
                var hidden = segment.Hidden.Length == 0 ? null : new HashSet<int>(segment.Hidden);
                var canceled = false;
                //Partitioner.Create rejects an empty range - guarded by the count check above
                Parallel.ForEach(Partitioner.Create(0, count, 16384), () => new List<INode>(),
                    (range, state, local) =>
                    {
                        if (state.IsStopped) return local;
                        if (isCanceled())
                        {
                            canceled = true;
                            state.Stop();
                            return local;
                        }
                        if (segment.Table is { } table)
                        {
                            for (var row = range.Item1; row < range.Item2; row++)
                            {
                                if (hidden?.Contains(row) == true) continue;
                                if (matchRow(table, row)) local.Add(table.Handle(row));
                            }
                        }
                        else
                        {
                            var list = segment.List;
                            for (var i = range.Item1; i < range.Item2; i++)
                            {
                                if (hidden?.Contains(i) == true) continue;
                                var node = list[i];
                                if (match(node)) local.Add(node);
                            }
                        }
                        return local;
                    },
                    local => { lock (result) result.AddRange(local); });
                if (canceled) return null;
            }
            foreach (var node in snapshot.Extras)
                if (match(node)) result.Add(node);
            return isCanceled() ? null : result;
        }

        public List<INode> FilterSnapshot(Func<INode, bool> match, Func<bool> isCanceled)
            => FilterSnapshot(match, null, isCanceled);

        /// <summary>
        /// Rows of a table's immutable base that hold these keys (nodes or path strings). A
        /// directory replaced in the live overlay still resolves to its scanned row, so a
        /// subtree sweep over the rows finds the children of the path, not of an instance.
        /// </summary>
        public HashSet<int> ResolveRows(MftTable table, IEnumerable<object> keys)
        {
            var rows = new HashSet<int>();
            foreach (var shard in shards)
            {
                if (!ReferenceEquals(shard.Table, table)) continue;
                foreach (var key in keys)
                    if (shard.Base.TryGetRow(key, out var row)) rows.Add(row);
                break;
            }
            return rows;
        }

        public IEnumerator<KeyValuePair<object, INode>> GetEnumerator()
        {
            var snapshot = shards;
            foreach (var shard in snapshot)
                foreach (var pair in shard.Entries())
                    yield return pair;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        Shard GetOrCreateShardLocked(string root)
        {
            root = NormalizeRoot(root);
            var found = Find(shards, root);
            if (found != null) return found;
            var next = new Shard[shards.Length + 1];
            Array.Copy(shards, next, shards.Length);
            next[^1] = found = new Shard(root, EmptyPrepared);
            shards = next;
            return found;
        }

        static Shard Find(Shard[] source, string root)
        {
            if (root == null) return null;
            root = NormalizeRoot(root);
            foreach (var shard in source)
                if (string.Equals(shard.Root, root, StringComparison.OrdinalIgnoreCase)) return shard;
            return null;
        }

        static string RootOf(object key)
        {
            try
            {
                return key switch
                {
                    string path => Path.GetPathRoot(path),
                    INode node => NodePath.RootOf(node),
                    _ => null
                };
            }
            catch { return null; }
        }

        static string NormalizeRoot(string root)
        {
            if (string.IsNullOrEmpty(root)) return "";
            if (root.Length == 2 && root[1] == ':') return root + Path.DirectorySeparatorChar;
            return root;
        }

        sealed class ConcatReadOnlyList : IReadOnlyList<INode>
        {
            readonly IReadOnlyList<INode>[] lists;
            readonly int count;

            public ConcatReadOnlyList(IReadOnlyList<INode>[] lists)
            {
                this.lists = lists;
                foreach (var list in lists) count += list.Count;
            }

            public int Count => count;
            public INode this[int index]
            {
                get
                {
                    if ((uint)index >= (uint)count) throw new ArgumentOutOfRangeException(nameof(index));
                    foreach (var list in lists)
                    {
                        if (index < list.Count) return list[index];
                        index -= list.Count;
                    }
                    throw new ArgumentOutOfRangeException(nameof(index));
                }
            }

            public IEnumerator<INode> GetEnumerator()
            {
                foreach (var list in lists)
                    foreach (var node in list)
                        yield return node;
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
