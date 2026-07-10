using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace search.Models
{
    /// <summary>
    /// Path identity, ordering and containment for INodes without materializing full-path
    /// strings. A node's canonical path is its PathParent chain of names on top of the
    /// terminal node's FullName ("C:\" for MFT roots/orphans, a stored path for
    /// FileNode/ZipNode); the terminal string itself decomposes into a base prefix plus
    /// '\'-separated segments, so two nodes with the same textual path are equal no matter
    /// how they are represented. KeyComparer lets a single dictionary be keyed by nodes
    /// (no path strings held) yet still be queried by plain path strings.
    /// </summary>
    internal static class NodePath
    {
        // Caps walks over corrupt parent cycles; mirrors the old FullName depth cap.
        // Materialize, hashing, equality and ordering all cut over to the same fallback
        // at the same depth, so they stay mutually consistent even for degenerate chains.
        const int MaxWalk = 256;

        public static readonly IEqualityComparer<object> KeyComparer = new PathKeyComparer();
        public static readonly IComparer<INode> ByPath = Comparer<INode>.Create(Compare);
        public static readonly IComparer<INode> ByFolderThenName =
            Comparer<INode>.Create((a, b) =>
            {
                var c = CompareCursors(Cursor.Folder(a), Cursor.Folder(b));
                return c != 0 ? c : string.Compare(a.Name, b.Name);
            });

        /// <summary>
        /// Build the full path of a chained node: terminal path + '\'-joined names.
        /// This is the single definition of a chained node's FullName - hashing,
        /// equality and ordering mirror this walk exactly.
        /// </summary>
        public static string Materialize(INode n)
        {
            if (n.PathParent == null) return n.FullName;

            var names = new List<string>(8);
            var m = n;
            while (m.PathParent != null && names.Count < MaxWalk)
            {
                names.Add(m.Name);
                m = m.PathParent;
            }

            var prefix = m.PathParent == null ? m.FullName : m.Name; // Name only when the cycle guard tripped
            var result = new StringBuilder(prefix.Length + names.Count * 12).Append(prefix);
            for (var i = names.Count - 1; i >= 0; i--)
            {
                if (result.Length == 0 || result[^1] != '\\') result.Append('\\');
                result.Append(names[i]);
            }
            return result.ToString();
        }

        /// <summary>
        /// Same order as comparing FullName strings component-wise with OrdinalIgnoreCase
        /// (a folder always groups with its content), computed from the chains alone.
        /// </summary>
        public static int Compare(INode a, INode b)
            => ReferenceEquals(a, b) ? 0 : CompareCursors(Cursor.For(a), Cursor.For(b));

        static int CompareCursors(Cursor x, Cursor y) => CompareAligned(x, x.Count(), y, y.Count());

        static int CompareAligned(Cursor x, int dx, Cursor y, int dy)
        {
            // The longer path's extra segments make it sort after its own prefix
            if (dx > dy) { var c = CompareAligned(x.Up(), dx - 1, y, dy); return c != 0 ? c : 1; }
            if (dx < dy) { var c = CompareAligned(x, dx, y.Up(), dy - 1); return c != 0 ? c : -1; }
            if (x.SameAs(y)) return 0;
            if (dx == 0) return x.Span.CompareTo(y.Span, StringComparison.OrdinalIgnoreCase);

            var parents = CompareAligned(x.Up(), dx - 1, y.Up(), dy - 1);
            return parents != 0 ? parents : x.Span.CompareTo(y.Span, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Path equality regardless of representation (chain node, path-backed node)
        /// </summary>
        public static bool PathEquals(INode a, INode b)
            => ReferenceEquals(a, b) || CursorsEqual(Cursor.For(a), Cursor.For(b));

        static bool PathEquals(INode a, string path) => CursorsEqual(Cursor.For(a), Cursor.ForString(path));

        static bool CursorsEqual(Cursor x, Cursor y)
        {
            for (var guard = 0; guard < MaxWalk * 2; guard++)
            {
                if (x.SameAs(y)) return true;
                bool xb = x.IsBase, yb = y.IsBase;
                if (xb || yb) return xb && yb && x.Span.Equals(y.Span, StringComparison.OrdinalIgnoreCase);
                if (!x.Span.Equals(y.Span, StringComparison.OrdinalIgnoreCase)) return false;
                x = x.Up();
                y = y.Up();
            }
            return string.Equals(x.MaterializeRest(), y.MaterializeRest(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when the node lies strictly below dir. Chained ancestors match by identity
        /// (they are the indexed instances); a path-backed node - or a chain rooted in one -
        /// falls back to the textual prefix, exactly like the old FullName.StartsWith.
        /// </summary>
        public static bool IsUnder(INode n, INode dir, string dirPrefixWithSlash)
        {
            var m = n;
            for (var guard = 0; m.PathParent != null && guard < MaxWalk; guard++)
            {
                m = m.PathParent;
                if (dir != null && ReferenceEquals(m, dir)) return true;
            }
            return m.FullName.StartsWith(dirPrefixWithSlash, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when the node's immediate parent is dir (by identity or by path)
        /// </summary>
        public static bool HasParent(INode n, INode dir)
            => n.PathParent is INode p && dir != null && (ReferenceEquals(p, dir) || PathEquals(p, dir));

        /// <summary>
        /// The node's leaf name equals name; for path-backed nodes the stored path
        /// must end with '\' + name (same thing, no allocation)
        /// </summary>
        public static bool LeafEquals(INode n, string name)
            => n.PathParent != null
                ? string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)
                : n.FullName.Length > name.Length
                  && n.FullName[^(name.Length + 1)] == '\\'
                  && n.FullName.EndsWith(name, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The node's leaf name ends with the suffix (e.g. ".exe"), without allocation
        /// </summary>
        public static bool LeafEndsWith(INode n, string suffix)
            => (n.PathParent != null ? n.Name : n.FullName).EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True when some directory component of the path equals name
        /// (the equivalent of FullName.Contains("\" + name + "\"))
        /// </summary>
        public static bool HasPathComponent(INode n, string name)
        {
            var m = n;
            for (var guard = 0; m.PathParent != null && guard < MaxWalk; guard++)
            {
                m = m.PathParent;
                if (m.PathParent != null && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return m.FullName.Contains($"\\{name}\\", StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------
        // Hashing - must equal HashChars over the node's FullName string
        // ------------------------------------------------------------------

        const uint FnvSeed = 2166136261;
        const uint FnvPrime = 16777619;

        static int HashPath(INode n) => (int)HashUp(n, MaxWalk).Hash;

        static (uint Hash, char Last) HashUp(INode n, int budget)
        {
            var prefix = n.PathParent == null ? n.FullName : budget <= 0 ? n.Name : null;
            if (prefix != null)
                return (HashChars(FnvSeed, prefix), prefix.Length > 0 ? prefix[^1] : '\0');

            var (hash, last) = HashUp(n.PathParent, budget - 1);
            if (last != '\\')
            {
                hash = (hash ^ '\\') * FnvPrime;
                last = '\\';
            }
            var name = n.Name;
            return (HashChars(hash, name), name.Length > 0 ? name[^1] : last);
        }

        static uint HashChars(uint hash, string s)
        {
            foreach (var c in s)
                hash = (hash ^ char.ToUpperInvariant(c)) * FnvPrime;
            return hash;
        }

        sealed class PathKeyComparer : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b)
            {
                if (ReferenceEquals(a, b)) return true;
                if (a is string sa)
                    return b is string sb
                        ? string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase)
                        : b is INode nb && PathEquals(nb, sa);
                if (a is INode na)
                    return b is string s ? PathEquals(na, s) : b is INode n && PathEquals(na, n);
                return false;
            }

            public int GetHashCode(object key) => key switch
            {
                string s => (int)HashChars(FnvSeed, s),
                INode n => HashPath(n),
                _ => 0
            };
        }

        // ------------------------------------------------------------------
        // Cursor - one path segment plus everything above it, walked leaf->root.
        // Chain mode follows PathParent; when the terminal node is reached its
        // FullName continues to decompose in string mode, so every node yields
        // the same segment sequence as its materialized path.
        // ------------------------------------------------------------------

        readonly struct Cursor
        {
            readonly INode node;   // Chain mode: segment = node.Name, invariant node.PathParent != null
            readonly int budget;   // Chain mode: remaining hops before the cycle guard cuts the chain
            readonly string s;     // String mode: the path prefix is s[0..end)
            readonly int end;
            readonly int firstSep; // Index of the first '\' in s (the root separator stays in the base)

            Cursor(INode node, int budget) { this.node = node; this.budget = budget; s = null; end = 0; firstSep = 0; }

            Cursor(string s, int end)
            {
                node = null;
                budget = 0;
                this.s = s;
                this.end = end;
                firstSep = s.IndexOf('\\');
            }

            public static Cursor For(INode n)
                => n.PathParent != null ? new Cursor(n, MaxWalk) : ForString(n.FullName);

            public static Cursor ForString(string path) => new Cursor(path, path.Length);

            /// <summary>The node's path without its leaf segment (its folder); empty for a base-only path</summary>
            public static Cursor Folder(INode n)
            {
                var c = For(n);
                return c.IsBase ? ForString("") : c.Up();
            }

            public bool IsBase => node == null && (firstSep < 0 || end <= firstSep + 1);

            public ReadOnlySpan<char> Span
            {
                get
                {
                    if (node != null) return node.Name;
                    if (IsBase) return s.AsSpan(0, end);
                    var sep = s.LastIndexOf('\\', end - 1);
                    return s.AsSpan(sep + 1, end - sep - 1);
                }
            }

            public Cursor Up()
            {
                if (node != null)
                {
                    var p = node.PathParent;
                    if (p.PathParent == null) return ForString(p.FullName);
                    if (budget <= 1) return ForString(p.Name); // Cycle guard: same cut as Materialize
                    return new Cursor(p, budget - 1);
                }
                var sep = s.LastIndexOf('\\', end - 1);
                return new Cursor(s, sep == firstSep ? sep + 1 : sep);
            }

            public bool SameAs(Cursor y)
                => node != null
                    ? ReferenceEquals(node, y.node)
                    : y.node == null && ReferenceEquals(s, y.s) && end == y.end;

            /// <summary>Segments above the base; O(chain depth + separators in the terminal string)</summary>
            public int Count()
            {
                var count = 0;
                var c = this;
                while (!c.IsBase && count < MaxWalk * 2)
                {
                    count++;
                    c = c.Up();
                }
                return count;
            }

            public string MaterializeRest()
                => node != null ? Materialize(node) : s.Substring(0, end);
        }
    }
}
