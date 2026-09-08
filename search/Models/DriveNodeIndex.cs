using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace search.Models
{
    /// <summary>
    /// Path index sharded by filesystem root. A completed drive scan is retained as one
    /// immutable compact table containing only indexes into its dense node array. Watcher/UI
    /// mutations live in a small overlay (node or tombstone), so the multi-million-node base
    /// never pays mutable dictionary key+value/node overhead.
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
        /// Immutable open-addressed path table. Slots retain only an index into the existing
        /// dense node array; an eight-bit hash fingerprint rejects nearly all probe collisions
        /// without walking parent chains. Double hashing avoids primary clustering at 70% load.
        /// </summary>
        internal sealed class CompactPathIndex : IReadOnlyCollection<INode>
        {
            const int LoadNumerator = 7;
            const int LoadDenominator = 10;
            readonly IReadOnlyList<INode> nodes;
            readonly INode[] nodeArray;
            readonly int[] slots; //dense index + 1; 0 = empty
            readonly byte[] fingerprints;

            public CompactPathIndex(IReadOnlyList<INode> nodes,
                CancellationToken cancellationToken = default)
            {
                this.nodes = nodes;
                nodeArray = nodes as INode[];
                if (nodes.Count == 0)
                {
                    slots = Array.Empty<int>();
                    fingerprints = Array.Empty<byte>();
                    return;
                }

                var capacity = 4;
                while ((long)capacity * LoadNumerator / LoadDenominator < nodes.Count)
                    capacity = checked(capacity * 2);
                slots = new int[capacity];
                fingerprints = new byte[capacity];
                var mask = capacity - 1;
                var uniqueCount = 0;

                for (var i = 0; i < nodes.Count; i++)
                {
                    if ((i & 0x0FFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var node = nodes[i] ?? throw new ArgumentException(
                        "Path index cannot contain a null node.", nameof(nodes));
                    var hash = Hash(node);
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
                        if (fingerprints[at] == fingerprint
                            && NodePath.KeyEquals(NodeAt(stored - 1), node))
                        {
                            //Same textual path: mirror dictionary assignment semantics by
                            //making the later value authoritative without another slot.
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
            }

            public int Count { get; }
            internal long StorageBytes => (long)slots.Length * sizeof(int) + fingerprints.Length;

            public bool TryGetValue(object key, out INode node)
            {
                if (key == null || slots.Length == 0)
                {
                    node = null;
                    return false;
                }
                var hash = Hash(key);
                var fingerprint = Fingerprint(hash);
                var mask = slots.Length - 1;
                var at = (int)(hash & (uint)mask);
                var step = Step(hash, mask);
                for (var probe = 0; probe < slots.Length; probe++)
                {
                    var stored = slots[at];
                    if (stored == 0)
                    {
                        node = null;
                        return false;
                    }
                    var candidate = NodeAt(stored - 1);
                    if (fingerprints[at] == fingerprint
                        && NodePath.KeyEquals(candidate, key))
                    {
                        node = candidate;
                        return true;
                    }
                    at = (at + step) & mask;
                }
                node = null;
                return false;
            }

            public bool Contains(object key) => TryGetValue(key, out _);

            public IEnumerator<INode> GetEnumerator()
            {
                foreach (var stored in slots)
                    if (stored != 0) yield return NodeAt(stored - 1);
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            INode NodeAt(int index) => nodeArray != null ? nodeArray[index] : nodes[index];

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
                //A duplicate textual path is one index entry. Do not expose a dense source
                //that contains more identities than the authoritative compact table.
                DenseNodes = denseNodes?.Count == @base.Count ? denseNodes : null;
            }

            public int Count => Base.Count;
            public bool IsEmpty => Base.Count == 0;
            public IEnumerable<INode> Values
            {
                get
                {
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

            public IEnumerable<KeyValuePair<object, INode>> Entries()
            {
                IEnumerable<INode> source = DenseNodes is { } denseNodes
                    ? denseNodes : Base;
                foreach (var node in source)
                {
                    if (Delta.ContainsKey(node)) continue;
                    yield return new KeyValuePair<object, INode>(node, node);
                }
                foreach (var pair in Delta)
                    if (pair.Value.Node != null)
                        yield return new KeyValuePair<object, INode>(pair.Key, pair.Value.Node);
            }
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
        public void ReplaceDrive(string root, PreparedDrive replacement,
            long preserveMutationsAfter = long.MaxValue, Func<string, bool?> existsOnDisk = null)
        {
            ArgumentNullException.ThrowIfNull(replacement);
            root = NormalizeRoot(root);
            lock (mutationLock)
            {
                var current = shards;
                var at = Array.FindIndex(current, x => string.Equals(x.Root, root, StringComparison.OrdinalIgnoreCase));
                var preserved = at < 0 || preserveMutationsAfter == long.MaxValue
                    ? Array.Empty<KeyValuePair<object, DeltaEntry>>()
                    : current[at].Delta.Where(pair => pair.Value.Version > preserveMutationsAfter
                            || DisagreesWithSnapshot(pair, replacement.Base, existsOnDisk))
                        .OrderBy(pair => pair.Value.Version).ToArray();
                if (replacement.IsEmpty)
                {
                    if (at < 0 && preserved.Length == 0) return;
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
                        return;
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
        /// CopySnapshot, but retain their dense immutable base for cache-friendly merging.
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
        /// Merge immutable dense bases with their small captured delta sets. This avoids
        /// enumerating a generic hash table after the first watcher mutation and keeps the
        /// full-index copy cancellable.
        /// </summary>
        public IReadOnlyList<INode> CopySnapshot(Func<bool> isCanceled)
        {
            if (TryGetDenseSnapshot(out var dense)) return dense;
            var snapshot = shards;
            var result = new List<INode>(Count);
            var seen = 0;
            foreach (var shard in snapshot)
            {
                var delta = shard.Delta.ToArray();
                HashSet<object> shadowed = null;
                if (delta.Length != 0)
                {
                    shadowed = new HashSet<object>(NodePath.KeyComparer);
                    foreach (var pair in delta) shadowed.Add(pair.Key);
                }

                IEnumerable<INode> source = shard.DenseNodes is { } denseNodes
                    ? denseNodes : shard.Base;
                foreach (var node in source)
                {
                    if ((++seen & 0x0FFF) == 0 && isCanceled()) return null;
                    if (shadowed?.Contains(node) != true) result.Add(node);
                }
                foreach (var pair in delta)
                {
                    if ((++seen & 0x0FFF) == 0 && isCanceled()) return null;
                    if (pair.Value.Node != null) result.Add(pair.Value.Node);
                }
            }
            return isCanceled() ? null : result;
        }

        /// <summary>
        /// The nodes matching a predicate, straight from the immutable dense arrays plus
        /// their small deltas. A filtered query never needs the complete node list, so this
        /// skips the multi-million-reference copy CopySnapshot would otherwise allocate on
        /// every keystroke once the first watcher mutation has shadowed a base entry. The
        /// dense ranges are matched in parallel; result order is arbitrary, like the index
        /// enumeration order the callers already expect. Null when canceled.
        /// </summary>
        public List<INode> FilterSnapshot(Func<INode, bool> match, Func<bool> isCanceled)
        {
            ArgumentNullException.ThrowIfNull(match);
            isCanceled ??= static () => false;
            var snapshot = shards;
            var result = new List<INode>();
            foreach (var shard in snapshot)
            {
                var delta = shard.Delta.ToArray();
                HashSet<object> shadowed = null;
                if (delta.Length != 0)
                {
                    shadowed = new HashSet<object>(NodePath.KeyComparer);
                    foreach (var pair in delta) shadowed.Add(pair.Key);
                }

                var dense = shard.DenseNodes;
                //A shard can carry an EMPTY dense array with live deltas: BeginSnapshot
                //marks a drive whose first scan is still reading the disk, and ReplaceDrive
                //publishes an empty base when it must preserve post-watermark mutations.
                //Partitioner.Create rejects an empty range, so only a non-empty base is
                //partitioned - the delta pass below still runs for both.
                if (dense == null)
                {
                    var seen = 0;
                    foreach (var node in shard.Base)
                    {
                        if ((++seen & 0x0FFF) == 0 && isCanceled()) return null;
                        if (shadowed?.Contains(node) == true) continue;
                        if (match(node)) result.Add(node);
                    }
                }
                else if (dense.Count != 0)
                {
                    var canceled = false;
                    System.Threading.Tasks.Parallel.ForEach(
                        System.Collections.Concurrent.Partitioner.Create(0, dense.Count, 16384),
                        () => new List<INode>(),
                        (range, state, local) =>
                        {
                            if (state.IsStopped) return local;
                            if (isCanceled())
                            {
                                canceled = true;
                                state.Stop();
                                return local;
                            }
                            for (var i = range.Item1; i < range.Item2; i++)
                            {
                                var node = dense[i];
                                if (shadowed?.Contains(node) == true) continue;
                                if (match(node)) local.Add(node);
                            }
                            return local;
                        },
                        local => { lock (result) result.AddRange(local); });
                    if (canceled) return null;
                }
                foreach (var pair in delta)
                    if (pair.Value.Node is { } added && match(added)) result.Add(added);
            }
            return isCanceled() ? null : result;
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
