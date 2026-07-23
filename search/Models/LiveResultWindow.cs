using System;
using System.Collections.Generic;

namespace search.Models
{
    /// <summary>
    /// Materialized, sorted prefix of the current filtered result. The UI shows only the
    /// first VisibleLimit nodes; the following ReserveLimit nodes are a private tail used to
    /// fill holes without walking the authoritative multi-million-node index.
    ///
    /// All mutations are owned by the UI dispatcher. Filesystem workers hand it coalesced
    /// node identities, never mutate this object directly.
    /// </summary>
    internal sealed class LiveResultWindow<T> where T : class
    {
        internal enum OperationKind { RemoveAt, Insert }

        internal readonly record struct Operation(OperationKind Kind, int Index, T Item = null);

        readonly int visibleLimit;
        readonly int reserveLimit;
        readonly int reserveLowWatermark;
        List<T> known = new();
        HashSet<T> knownIdentities = new(ReferenceEqualityComparer.Instance);
        bool hasUnknownTail;

        public LiveResultWindow(int visibleLimit, int reserveLimit, int reserveLowWatermark)
        {
            if (visibleLimit <= 0) throw new ArgumentOutOfRangeException(nameof(visibleLimit));
            if (reserveLimit < 0) throw new ArgumentOutOfRangeException(nameof(reserveLimit));
            if (reserveLowWatermark < 0 || reserveLowWatermark > reserveLimit)
                throw new ArgumentOutOfRangeException(nameof(reserveLowWatermark));
            this.visibleLimit = visibleLimit;
            this.reserveLimit = reserveLimit;
            this.reserveLowWatermark = reserveLowWatermark;
        }

        public int Capacity => visibleLimit + reserveLimit;
        public int KnownCount => known.Count;
        public int VisibleCount => Math.Min(visibleLimit, known.Count);
        public int ReserveCount => Math.Max(0, known.Count - visibleLimit);
        public bool HasUnknownTail => hasUnknownTail;
        public bool IsTruncated => hasUnknownTail || known.Count > visibleLimit;
        public bool NeedsRefill => hasUnknownTail && ReserveCount < reserveLowWatermark;
        public bool IsVisibleIncomplete => hasUnknownTail && known.Count < visibleLimit;

        /// <summary>Adopt an authoritative already-sorted prefix produced off the UI thread.</summary>
        public void Reset(List<T> sortedPrefix, bool unknownTail)
        {
            sortedPrefix ??= new List<T>();
            if (sortedPrefix.Count > Capacity)
                sortedPrefix.RemoveRange(Capacity, sortedPrefix.Count - Capacity);
            known = sortedPrefix;
            knownIdentities = new HashSet<T>(known, ReferenceEqualityComparer.Instance);
            hasUnknownTail = unknownTail;
        }

        /// <summary>Current visible prefix; used only while already on the UI dispatcher.</summary>
        public T[] VisibleSnapshot()
        {
            var count = VisibleCount;
            var result = new T[count];
            known.CopyTo(0, result, 0, count);
            return result;
        }

        /// <summary>
        /// Apply one changed identity and return the small ordered-list operations needed to
        /// keep the external visible list equal to this window's first VisibleLimit nodes.
        /// A mutable sort key is safe: identity removal happens before the new key is compared.
        /// </summary>
        public void Apply(T item, bool include, Comparison<T> compare, List<Operation> operations)
        {
            if (item == null) return;
            if (compare == null) throw new ArgumentNullException(nameof(compare));
            if (operations == null) throw new ArgumentNullException(nameof(operations));

            //The overwhelming majority of filesystem events concern rows outside the
            //104k materialized prefix. Avoid a linear identity scan for those events; only
            //a confirmed member pays ReferenceIndexOf to obtain its mutable list index.
            var oldIndex = knownIdentities.Contains(item) ? ReferenceIndexOf(known, item) : -1;
            //The mutable key still fits between its neighbours: membership and position did
            //not change, so the caller only has to repaint a realized row.
            if (oldIndex >= 0 && include
                && (oldIndex == 0 || compare(known[oldIndex - 1], item) <= 0)
                && (oldIndex == known.Count - 1 || compare(item, known[oldIndex + 1]) <= 0))
                return;
            if (oldIndex >= 0)
            {
                known.RemoveAt(oldIndex);
                knownIdentities.Remove(item);
                if (oldIndex < visibleLimit)
                {
                    operations.Add(new Operation(OperationKind.RemoveAt, oldIndex));
                    //The first reserve row immediately fills a visible hole.
                    if (known.Count >= visibleLimit)
                        operations.Add(new Operation(OperationKind.Insert, visibleLimit - 1,
                            known[visibleLimit - 1]));
                }
            }

            if (!include) return;
            var insertAt = BinaryIndex(known, item, compare);
            //After a known row was removed, the next unmaterialized source row belongs at
            //the end of this prefix. A changed/new node sorting after every still-known row
            //cannot safely fill that hole: an unknown row may precede it. Keep the exact
            //prefix shorter and let the reserve refill supply the real successor.
            if (hasUnknownTail && known.Count < Capacity && insertAt == known.Count) return;
            //A node beyond the known prefix has no visible effect. Remember that an
            //authoritative tail still exists so a later reserve refill remains possible.
            if (insertAt >= Capacity && known.Count >= Capacity)
            {
                hasUnknownTail = true;
                return;
            }

            var visibleBeforeInsert = VisibleCount;
            if (insertAt < visibleLimit)
            {
                if (visibleBeforeInsert == visibleLimit)
                    operations.Add(new Operation(OperationKind.RemoveAt, visibleLimit - 1));
                operations.Add(new Operation(OperationKind.Insert, insertAt, item));
            }

            known.Insert(insertAt, item);
            knownIdentities.Add(item);
            if (known.Count > Capacity)
            {
                var dropped = known[known.Count - 1];
                known.RemoveAt(known.Count - 1);
                knownIdentities.Remove(dropped);
                hasUnknownTail = true;
            }
        }

        /// <summary>
        /// Small-batch variant that still emits targeted UI operations. Every changed
        /// identity is removed before any new mutable key is binary-inserted; otherwise two
        /// adjacent nodes whose keys changed in the same watcher batch could temporarily
        /// leave the known prefix unsorted and invalidate the second binary search.
        /// </summary>
        public void ApplySmallBatch(IReadOnlyList<T> changed, Func<T, bool> include,
            Comparison<T> compare, List<Operation> operations)
        {
            if (changed == null) throw new ArgumentNullException(nameof(changed));
            if (include == null) throw new ArgumentNullException(nameof(include));
            if (compare == null) throw new ArgumentNullException(nameof(compare));
            if (operations == null) throw new ArgumentNullException(nameof(operations));

            var unique = new HashSet<T>(ReferenceEqualityComparer.Instance);
            var candidates = new List<T>(changed.Count);
            foreach (var item in changed)
            {
                if (item == null || !unique.Add(item)) continue;
                var keep = include(item);
                Apply(item, false, compare, operations);
                if (keep) candidates.Add(item);
            }
            candidates.Sort(compare);
            foreach (var item in candidates) Apply(item, true, compare, operations);
        }

        /// <summary>
        /// Apply a large coalesced delta with one pass over the known prefix and one sort of
        /// the changed candidates. This is incremental with respect to the source index:
        /// O(window + delta log delta), independent of the number of indexed files.
        /// </summary>
        public T[] ApplyBatch(IReadOnlyList<T> changed, Func<T, bool> include,
            Comparison<T> compare)
        {
            if (changed == null) throw new ArgumentNullException(nameof(changed));
            if (include == null) throw new ArgumentNullException(nameof(include));
            if (compare == null) throw new ArgumentNullException(nameof(compare));

            var changedSet = new HashSet<T>(changed, ReferenceEqualityComparer.Instance);
            var kept = new List<T>(known.Count);
            foreach (var item in known)
                if (!changedSet.Contains(item)) kept.Add(item);

            var candidates = new List<T>(changed.Count);
            var candidateSet = new HashSet<T>(ReferenceEqualityComparer.Instance);
            foreach (var item in changed)
                if (item != null && candidateSet.Add(item) && include(item)) candidates.Add(item);
            candidates.Sort(compare);

            if (hasUnknownTail && kept.Count < Capacity)
            {
                //Only candidates no worse than the last unchanged known row have a proven
                //position in this partial source prefix. Later candidates must not masquerade
                //as reserve rows while better, unmaterialized nodes may exist between them.
                if (kept.Count == 0) candidates.Clear();
                else
                {
                    var boundary = kept[kept.Count - 1];
                    candidates.RemoveAll(item => compare(boundary, item) < 0);
                }
            }

            var totalKnown = kept.Count + candidates.Count;
            known = MergePrefix(kept, candidates, compare, Capacity);
            knownIdentities = new HashSet<T>(known, ReferenceEqualityComparer.Instance);
            if (totalKnown > Capacity) hasUnknownTail = true;
            return VisibleSnapshot();
        }

        public static void ApplyOperations(IList<T> visible, IReadOnlyList<Operation> operations)
        {
            foreach (var operation in operations)
            {
                if (operation.Kind == OperationKind.RemoveAt) visible.RemoveAt(operation.Index);
                else visible.Insert(operation.Index, operation.Item);
            }
        }

        /// <summary>
        /// Validate the cheap external-list diff for a pure removal batch. Every surviving
        /// row must remain the prefix of the new visible window; any remaining rows in
        /// <paramref name="next"/> are reserve rows that fill holes at the end. This lets a
        /// delete storm update only the affected ListView indices instead of raising Reset
        /// for the complete 100k-row ItemsSource.
        /// </summary>
        internal static int PureRemovalDiffCount(IList<T> visible, IReadOnlyList<T> next,
            ISet<T> removed)
        {
            if (visible == null || next == null || removed == null) return -1;
            var nextIndex = 0;
            var removalCount = 0;
            for (var i = 0; i < visible.Count; i++)
            {
                var item = visible[i];
                if (removed.Contains(item))
                {
                    removalCount++;
                    continue;
                }
                if (nextIndex >= next.Count || !ReferenceEquals(item, next[nextIndex]))
                    return -1;
                nextIndex++;
            }
            return nextIndex <= next.Count ? removalCount : -1;
        }

        /// <summary>Apply a diff already validated by <see cref="PureRemovalDiffCount"/>.</summary>
        internal static void ApplyPureRemovalDiff(IList<T> visible, IReadOnlyList<T> next,
            ISet<T> removed)
        {
            for (var i = visible.Count - 1; i >= 0; i--)
                if (removed.Contains(visible[i])) visible.RemoveAt(i);
            while (visible.Count < next.Count) visible.Add(next[visible.Count]);
        }

        internal sealed class TargetedDiff
        {
            public readonly HashSet<T> Remove;
            public readonly int OperationCount;

            public TargetedDiff(HashSet<T> remove, int operationCount)
            {
                Remove = remove;
                OperationCount = operationCount;
            }
        }

        /// <summary>
        /// Plan a bounded remove/insert transformation for a mixed batch. ApplyBatch keeps
        /// every unchanged identity in relative order, so removing changed/moved rows and
        /// rows evicted at the tail must leave a subsequence of the new window. Returning
        /// null protects callers if that invariant does not hold for any future algorithm.
        /// </summary>
        internal static TargetedDiff PlanTargetedDiff(IList<T> visible, IReadOnlyList<T> next,
            ISet<T> changed)
        {
            if (visible == null || next == null || changed == null) return null;
            var nextSet = new HashSet<T>(next, ReferenceEqualityComparer.Instance);
            var remove = new HashSet<T>(ReferenceEqualityComparer.Instance);
            foreach (var item in visible)
                if (changed.Contains(item) || !nextSet.Contains(item)) remove.Add(item);

            //All remaining identities must occur in the target in the same relative order.
            var nextIndex = 0;
            var survivors = 0;
            foreach (var item in visible)
            {
                if (remove.Contains(item)) continue;
                while (nextIndex < next.Count && !ReferenceEquals(next[nextIndex], item))
                    nextIndex++;
                if (nextIndex >= next.Count) return null;
                nextIndex++;
                survivors++;
            }
            return new TargetedDiff(remove, remove.Count + next.Count - survivors);
        }

        /// <summary>Apply a plan returned by <see cref="PlanTargetedDiff"/>.</summary>
        internal static void ApplyTargetedDiff(IList<T> visible, IReadOnlyList<T> next,
            TargetedDiff plan)
        {
            for (var i = visible.Count - 1; i >= 0; i--)
                if (plan.Remove.Contains(visible[i])) visible.RemoveAt(i);
            for (var i = 0; i < next.Count; i++)
                if (i >= visible.Count || !ReferenceEquals(visible[i], next[i]))
                    visible.Insert(i, next[i]);
        }

        internal static List<T> MergePrefix(IReadOnlyList<T> first, IReadOnlyList<T> second,
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

        static int ReferenceIndexOf(IReadOnlyList<T> values, T item)
        {
            for (var i = 0; i < values.Count; i++)
                if (ReferenceEquals(values[i], item)) return i;
            return -1;
        }

        static int BinaryIndex(IReadOnlyList<T> values, T item, Comparison<T> compare)
        {
            var lo = 0;
            var hi = values.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (compare(values[mid], item) <= 0) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }
    }
}
