using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace search.Models
{
    /// <summary>
    /// Path index sharded by filesystem root. A completed drive scan becomes the published
    /// shard in one reference swap instead of copying every entry into a second global hash
    /// table. Reads capture the immutable shard array and remain lock-free; short watcher/UI
    /// mutations serialize only while selecting and changing their shard.
    /// </summary>
    internal sealed class DriveNodeIndex : IEnumerable<KeyValuePair<object, INode>>
    {
        internal sealed class Shard
        {
            public readonly string Root;
            public readonly NonBlocking.ConcurrentDictionary<object, INode> Map;
            public volatile IReadOnlyList<INode> DenseNodes;

            public Shard(string root, NonBlocking.ConcurrentDictionary<object, INode> map,
                IReadOnlyList<INode> denseNodes = null)
            {
                Root = root;
                Map = map;
                DenseNodes = denseNodes;
            }
        }

        readonly object mutationLock = new();
        volatile Shard[] shards = Array.Empty<Shard>();

        public int Count
        {
            get
            {
                var total = 0;
                foreach (var shard in shards) total += shard.Map.Count;
                return total;
            }
        }

        public bool IsEmpty
        {
            get
            {
                foreach (var shard in shards)
                    if (!shard.Map.IsEmpty) return false;
                return true;
            }
        }

        public IEnumerable<INode> Values
        {
            get
            {
                var snapshot = shards;
                foreach (var shard in snapshot)
                    foreach (var pair in shard.Map)
                        yield return pair.Value;
            }
        }

        public bool ContainsKey(object key) => TryGetValue(key, out _);

        public bool TryGetValue(object key, out INode node)
        {
            var snapshot = shards;
            var routed = Find(snapshot, RootOf(key));
            if (routed != null && routed.Map.TryGetValue(key, out node)) return true;
            //Root-only spelling ("C:" vs "C:\\"), malformed paths and a publication race
            //fall back across the handful of selected drives without changing semantics.
            foreach (var shard in snapshot)
                if (!ReferenceEquals(shard, routed) && shard.Map.TryGetValue(key, out node)) return true;
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
                    shard.Map[key] = value;
                    shard.DenseNodes = null;
                }
            }
        }

        public INode GetOrAdd(object key, INode value)
        {
            lock (mutationLock)
            {
                var shard = GetOrCreateShardLocked(RootOf(key));
                var result = shard.Map.GetOrAdd(key, value);
                if (ReferenceEquals(result, value)) shard.DenseNodes = null;
                return result;
            }
        }

        public INode AddOrUpdate(object key, Func<object, INode> add,
            Func<object, INode, INode> update)
        {
            lock (mutationLock)
            {
                var shard = GetOrCreateShardLocked(RootOf(key));
                var result = shard.Map.AddOrUpdate(key, add, update);
                shard.DenseNodes = null;
                return result;
            }
        }

        public bool TryRemove(object key, out INode node)
        {
            lock (mutationLock)
            {
                var routed = Find(shards, RootOf(key));
                if (routed != null && routed.Map.TryRemove(key, out node))
                {
                    routed.DenseNodes = null;
                    return true;
                }
                foreach (var shard in shards)
                    if (!ReferenceEquals(shard, routed) && shard.Map.TryRemove(key, out node))
                    {
                        shard.DenseNodes = null;
                        return true;
                    }
                node = null;
                return false;
            }
        }

        /// <summary>Atomically replace one drive while every other drive keeps its shard.</summary>
        public void ReplaceDrive(string root, NonBlocking.ConcurrentDictionary<object, INode> replacement,
            IReadOnlyList<INode> denseNodes = null)
        {
            root = NormalizeRoot(root);
            lock (mutationLock)
            {
                var current = shards;
                var at = Array.FindIndex(current, x => string.Equals(x.Root, root, StringComparison.OrdinalIgnoreCase));
                if (replacement.IsEmpty)
                {
                    if (at < 0) return;
                    var reduced = new Shard[current.Length - 1];
                    if (at > 0) Array.Copy(current, 0, reduced, 0, at);
                    if (at + 1 < current.Length) Array.Copy(current, at + 1, reduced, at, current.Length - at - 1);
                    shards = reduced;
                    return;
                }

                var next = at < 0 ? new Shard[current.Length + 1] : (Shard[])current.Clone();
                if (at < 0)
                {
                    Array.Copy(current, next, current.Length);
                    at = current.Length;
                }
                next[at] = new Shard(root, replacement, denseNodes);
                shards = next;
            }
        }

        /// <summary>
        /// Zero-copy stable enumeration of freshly published drive arrays. Any membership
        /// mutation dirties its shard; callers then fall back to copying the authoritative maps.
        /// </summary>
        public bool TryGetDenseSnapshot(out IReadOnlyList<INode> nodes)
        {
            var snapshot = shards;
            var lists = new IReadOnlyList<INode>[snapshot.Length];
            for (var i = 0; i < snapshot.Length; i++)
            {
                var dense = snapshot[i].DenseNodes;
                if (dense == null || dense.Count != snapshot[i].Map.Count)
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

        public IEnumerator<KeyValuePair<object, INode>> GetEnumerator()
        {
            var snapshot = shards;
            foreach (var shard in snapshot)
                foreach (var pair in shard.Map)
                    yield return pair;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        Shard GetOrCreateShardLocked(string root)
        {
            root = NormalizeRoot(root);
            var found = Find(shards, root);
            if (found != null) return found;
            var map = new NonBlocking.ConcurrentDictionary<object, INode>(NodePath.KeyComparer);
            var next = new Shard[shards.Length + 1];
            Array.Copy(shards, next, shards.Length);
            next[^1] = found = new Shard(root, map);
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
