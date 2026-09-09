using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace search.Models
{
    /// <summary>
    /// Path identity, ordering and containment for INodes without materializing full-path
    /// strings. A node's canonical path is its PathParent chain of names on top of the
    /// terminal node's FullName ("C:\" for MFT roots, a stored path for FileNode/ZipNode);
    /// the terminal string itself decomposes into a base prefix plus '\'-separated
    /// segments, so two nodes with the same textual path are equal no matter how they are
    /// represented. MFT rows are walked straight through their table's parent column - no
    /// handle is created for an ancestor just to compare or hash a path. KeyComparer lets a
    /// single dictionary be keyed by nodes (no path strings held) yet still be queried by
    /// plain path strings.
    /// </summary>
    internal static class NodePath
    {
        // Caps walks over corrupt parent cycles; mirrors the old FullName depth cap.
        // Materialize, hashing, equality and ordering all cut over to the same fallback
        // at the same depth, so they stay mutually consistent even for degenerate chains.
        internal const int MaxWalk = 256;

        /// <summary>
        /// Ordering of one path COMPONENT. Every component of every path passes through
        /// here, so the rule is chosen by measurement: a culture-aware collation costs
        /// ~2.3x per comparison (cs-CZ, 2M nodes: bounded window sort 270 -> 622 ms, full
        /// index threshold scan 3 -> 41 ms), which is a visible slowdown of the path and
        /// folder sorts. Case-insensitive, like everything else about Windows paths.
        /// </summary>
        internal const StringComparison ComponentOrder = StringComparison.CurrentCultureIgnoreCase;

        /// <summary>
        /// Ordering of a leaf NAME the way the user reads it in a column. This comparison
        /// was already culture-aware, so keeping the collation and only dropping case
        /// sensitivity costs nothing (measured slightly faster than the previous
        /// case-sensitive compare) - accented names keep their alphabet position instead
        /// of falling to the end in code-point order.
        /// </summary>
        internal const StringComparison NameOrder = StringComparison.CurrentCultureIgnoreCase;

        //Path IDENTITY (KeyEquals/KeyHashCode and the Leaf/Component helpers) stays
        //ordinal-ignore-case: it keys the index, so it must match its hash exactly and must
        //never merge two paths a culture collator happens to call equal.
        public static readonly IEqualityComparer<object> KeyComparer = new PathKeyComparer();
        public static readonly IComparer<INode> ByPath = Comparer<INode>.Create(Compare);
        public static readonly IComparer<INode> ByFolderThenName =
            Comparer<INode>.Create((a, b) =>
            {
                var c = CompareCursors(Cursor.Folder(a), Cursor.Folder(b));
                return c != 0 ? c : CompareNames(a, b);
            });

        /// <summary>Leaf names in NameOrder, straight from the name blob for MFT rows.</summary>
        internal static int CompareNames(INode a, INode b)
        {
            if (a is MftNode x)
            {
                var xn = x.Table.NameAt(x.Row);
                return b is MftNode y ? xn.CompareTo(y.Table.NameAt(y.Row), NameOrder) : xn.CompareTo(b.Name, NameOrder);
            }
            if (b is MftNode z) return -z.Table.NameAt(z.Row).CompareTo(a.Name, NameOrder);
            return string.Compare(a.Name, b.Name, NameOrder);
        }

        /// <summary>The row's leaf name against a node's, in NameOrder.</summary>
        internal static int CompareNameRow(MftTable table, int row, INode other)
        {
            var name = table.NameAt(row);
            return other is MftNode o ? name.CompareTo(o.Table.NameAt(o.Row), NameOrder) : name.CompareTo(other.Name, NameOrder);
        }

        /// <summary>
        /// Filesystem root of a node without materializing a chained MFT path.
        /// </summary>
        internal static string RootOf(INode n)
        {
            if (n is MftNode h)
                try { return Path.GetPathRoot(h.Table.Root); }
                catch { return null; }
            var terminal = n;
            for (var guard = 0; terminal.PathParent != null && guard < MaxWalk; guard++)
                terminal = terminal.PathParent;
            try { return Path.GetPathRoot(terminal.FullName); }
            catch { return null; }
        }

        /// <summary>
        /// Build the full path of a chained node: terminal path + '\'-joined names.
        /// This is the single definition of a chained node's FullName - hashing,
        /// equality and ordering mirror this walk exactly.
        /// </summary>
        public static string Materialize(INode n)
        {
            if (n is MftNode h) return h.Table.FullName(h.Row);
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

        /// <summary>A table row's path against a node's path, in ByPath order.</summary>
        internal static int CompareRow(MftTable table, int row, INode other)
            => CompareCursors(Cursor.ForRow(table, row), Cursor.For(other));

        /// <summary>A table row against a node in ByFolderThenName order.</summary>
        internal static int CompareFolderThenNameRow(MftTable table, int row, INode other)
        {
            var c = CompareCursors(Cursor.FolderOfRow(table, row), Cursor.Folder(other));
            return c != 0 ? c : CompareNameRow(table, row, other);
        }

        static int CompareCursors(Cursor x, Cursor y) => CompareAligned(x, x.Count(), y, y.Count());

        static int CompareAligned(Cursor x, int dx, Cursor y, int dy)
        {
            // The longer path's extra segments make it sort after its own prefix
            if (dx > dy) { var c = CompareAligned(x.Up(), dx - 1, y, dy); return c != 0 ? c : 1; }
            if (dx < dy) { var c = CompareAligned(x, dx, y.Up(), dy - 1); return c != 0 ? c : -1; }
            if (x.SameAs(y)) return 0;
            if (dx == 0) return x.SegmentCompare(y, ComponentOrder);

            var parents = CompareAligned(x.Up(), dx - 1, y.Up(), dy - 1);
            return parents != 0 ? parents : x.SegmentCompare(y, ComponentOrder);
        }

        /// <summary>
        /// Path equality regardless of representation (chain node, table row, path-backed node)
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
                if (xb || yb) return xb && yb && x.SegmentEquals(y);
                if (!x.SegmentEquals(y)) return false;
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
            => IsUnder(n, dir, dirPrefixWithSlash, TerminalOf(dir));

        internal static INode TerminalOf(INode node)
        {
            if (node is MftNode h) return h.Table.Handle(h.Table.RootRow < 0 ? h.Row : h.Table.RootRow);
            for (var guard = 0; node?.PathParent != null && guard < MaxWalk; guard++)
                node = node.PathParent;
            return node;
        }

        internal static bool IsUnder(INode n, INode dir, string dirPrefixWithSlash, INode directoryTerminal)
        {
            if (n is MftNode h) return IsUnderRow(h.Table, h.Row, dir, dirPrefixWithSlash);
            var m = n;
            for (var guard = 0; m.PathParent != null && guard < MaxWalk; guard++)
            {
                m = m.PathParent;
                if (dir != null && ReferenceEquals(m, dir)) return true;
            }
            //Within one immutable tree, the identity walk is conclusive. NodeFilter
            //caches the criterion's terminal once, keeping its normal hot loop unchanged.
            if (dir != null && ReferenceEquals(m, directoryTerminal)) return false;
            if (m.FullName.StartsWith(dirPrefixWithSlash, StringComparison.OrdinalIgnoreCase)) return true;

            //A live overlay can retain a child from the preceding scan while its
            //directory comes from the new one. Only mixed trees need path comparison.
            return IsUnderByPath(Cursor.For(n), dir, dirPrefixWithSlash);
        }

        /// <summary>
        /// <see cref="IsUnder(INode, INode, string, INode)"/> for a table row: ancestor
        /// rows are compared by row number when the directory is a row of the same table,
        /// which is conclusive for that immutable tree; anything else (a directory from
        /// another scan generation or a path-backed node) compares paths.
        /// </summary>
        internal static bool IsUnderRow(MftTable table, int row, INode dir, string dirPrefixWithSlash)
        {
            var dirRow = dir == null ? -1 : table.RowOf(dir);
            for (var parent = table.Parent[row]; parent >= 0; parent = table.Parent[parent])
                if (parent == dirRow) return true;
            if (dirRow >= 0) return false;
            if (table.Root.StartsWith(dirPrefixWithSlash, StringComparison.OrdinalIgnoreCase)) return true;
            return IsUnderByPath(Cursor.ForRow(table, row), dir, dirPrefixWithSlash);
        }

        static bool IsUnderByPath(Cursor cursor, INode dir, string dirPrefixWithSlash)
        {
            var target = dir != null ? Cursor.For(dir) : Cursor.ForDirectoryPrefix(dirPrefixWithSlash);
            for (var guard = 0; !cursor.IsBase && guard < MaxWalk * 2; guard++)
            {
                cursor = cursor.Up();
                if (CursorsEqual(cursor, target)) return true;
            }
            return false;
        }

        /// <summary>
        /// <see cref="IsUnder"/> against several directories in one chain walk - removing
        /// N sibling trees must not cost N passes over a chain (or N index scans upstream).
        /// dirs holds the directories' indexed nodes with KeyComparer, prefixes their
        /// paths with a trailing slash for the textual fallback.
        /// </summary>
        public static bool IsUnderAny(INode n, HashSet<object> dirs, IReadOnlyList<string> prefixes)
        {
            var m = n;
            for (var guard = 0; m.PathParent != null && guard < MaxWalk; guard++)
            {
                m = m.PathParent;
                if (dirs.Contains(m)) return true;
            }
            var full = m.FullName;
            for (var i = 0; i < prefixes.Count; i++)
                if (full.StartsWith(prefixes[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// <see cref="IsUnderAny"/> for a table row whose directories were resolved to rows
        /// of the same table beforehand (the caller resolves each directory path through the
        /// drive's immutable base, so a directory replaced in the live overlay still matches).
        /// </summary>
        internal static bool IsUnderAnyRow(MftTable table, int row, HashSet<int> dirRows, IReadOnlyList<string> prefixes)
        {
            for (var parent = table.Parent[row]; parent >= 0; parent = table.Parent[parent])
                if (dirRows.Contains(parent)) return true;
            var root = table.Root;
            for (var i = 0; i < prefixes.Count; i++)
                if (root.StartsWith(prefixes[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// True when the node's immediate parent is dir (by identity or by path)
        /// </summary>
        public static bool HasParent(INode n, INode dir)
        {
            if (n is MftNode h) return HasParentRow(h.Table, h.Row, dir);
            return n.PathParent is INode p && dir != null && (ReferenceEquals(p, dir) || PathEquals(p, dir));
        }

        internal static bool HasParentRow(MftTable table, int row, INode dir)
        {
            var parent = table.Parent[row];
            if (parent < 0 || dir == null) return false;
            var dirRow = table.RowOf(dir);
            if (dirRow >= 0) return dirRow == parent;
            return CursorsEqual(Cursor.ForRow(table, parent), Cursor.For(dir));
        }

        /// <summary>
        /// The node's leaf name equals name; for path-backed nodes the stored path
        /// must end with '\' + name (same thing, no allocation)
        /// </summary>
        public static bool LeafEquals(INode n, string name)
        {
            if (n is MftNode h) return h.Table.NameAt(h.Row).EqualsIgnoreCase(name);
            return n.PathParent != null
                ? string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)
                : n.FullName.Length > name.Length
                  && n.FullName[^(name.Length + 1)] == '\\'
                  && n.FullName.EndsWith(name, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The node's leaf name ends with the suffix (e.g. ".exe"), without allocation
        /// </summary>
        public static bool LeafEndsWith(INode n, string suffix)
        {
            if (n is MftNode h) return h.Table.NameAt(h.Row).EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
            return (n.PathParent != null ? n.Name : n.FullName).EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when some directory component of the path equals name
        /// (the equivalent of FullName.Contains("\" + name + "\"))
        /// </summary>
        public static bool HasPathComponent(INode n, string name)
        {
            if (n is MftNode h) return HasPathComponentRow(h.Table, h.Row, name);
            var m = n;
            for (var guard = 0; m.PathParent != null && guard < MaxWalk; guard++)
            {
                m = m.PathParent;
                if (m.PathParent != null && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return m.FullName.Contains($"\\{name}\\", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool HasPathComponentRow(MftTable table, int row, string name)
        {
            for (var parent = table.Parent[row]; parent >= 0; parent = table.Parent[parent])
                if (table.Parent[parent] >= 0 && table.NameAt(parent).EqualsIgnoreCase(name))
                    return true;
            return table.Root.Contains($"\\{name}\\", StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------
        // Hashing - must equal HashChars over the node's FullName string
        // ------------------------------------------------------------------

        internal const uint FnvSeed = 2166136261;
        internal const uint FnvPrime = 16777619;

        static int HashPath(INode n) => n.TryGetPathHash(out var hash)
            ? hash : (int)HashUp(n, MaxWalk).Hash;

        /// <summary>Compute a path hash before an immutable node publishes its cached value.</summary>
        internal static int ComputePathHash(INode n) => (int)HashUp(n, MaxWalk).Hash;

        /// <summary>
        /// Compute a child's canonical path hash from an already finalized parent hash.
        /// </summary>
        internal static int ComputeChildPathHash(INode parent, string name)
        {
            if (parent == null) return (int)HashChars(FnvSeed, name ?? "");
            var hash = parent.TryGetPathHash(out var cached)
                ? (uint)cached : HashUp(parent, MaxWalk).Hash;
            var tail = parent.PathParent == null ? parent.FullName : parent.Name;
            if (string.IsNullOrEmpty(tail) || tail[^1] != '\\')
                hash = (hash ^ '\\') * FnvPrime;
            return (int)HashChars(hash, name ?? "");
        }

        static (uint Hash, char Last) HashUp(INode n, int budget)
        {
            if (n is MftNode h)
            {
                var table = h.Table;
                var last = table.Parent[h.Row] < 0 ? '\\' : table.NameAt(h.Row).Last;
                return ((uint)table.PathHash[h.Row], last);
            }
            var prefix = n.PathParent == null ? n.FullName : budget <= 0 ? n.Name : null;
            if (prefix != null)
                return (HashChars(FnvSeed, prefix), prefix.Length > 0 ? prefix[^1] : '\0');

            var (hash, previous) = HashUp(n.PathParent, budget - 1);
            if (previous != '\\')
            {
                hash = (hash ^ '\\') * FnvPrime;
                previous = '\\';
            }
            var name = n.Name;
            return (HashChars(hash, name), name.Length > 0 ? name[^1] : previous);
        }

        internal static uint HashChars(uint hash, ReadOnlySpan<char> s)
        {
            foreach (var c in s)
                hash = (hash ^ char.ToUpperInvariant(c)) * FnvPrime;
            return hash;
        }

        internal static bool KeyEquals(object a, object b)
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

        /// <summary>Path equality of a table row against an index key (node or path string).</summary>
        internal static bool KeyEqualsRow(MftTable table, int row, object key)
        {
            switch (key)
            {
                case MftNode h when ReferenceEquals(h.Table, table) && h.Row == row:
                    return true;
                case string s:
                    return CursorsEqual(Cursor.ForRow(table, row), Cursor.ForString(s));
                case INode n:
                    return CursorsEqual(Cursor.ForRow(table, row), Cursor.For(n));
                default:
                    return false;
            }
        }

        internal static int KeyHashCode(object key) => key switch
        {
            string s => (int)HashChars(FnvSeed, s),
            INode n => HashPath(n),
            _ => 0
        };

        sealed class PathKeyComparer : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) => KeyEquals(a, b);

            public int GetHashCode(object key) => KeyHashCode(key);
        }

        // ------------------------------------------------------------------
        // Cursor - one path segment plus everything above it, walked leaf->root.
        // Table mode walks an MftTable's parent column; chain mode follows PathParent;
        // when the terminal is reached its FullName continues to decompose in string
        // mode, so every node yields the same segment sequence as its materialized path.
        // ------------------------------------------------------------------

        readonly struct Cursor
        {
            readonly MftTable table; // Table mode: segment = table.NameAt(row), invariant table.Parent[row] >= 0
            readonly int row;
            readonly INode node;     // Chain mode: segment = node.Name, invariant node.PathParent != null
            readonly int budget;     // Chain mode: remaining hops before the cycle guard cuts the chain
            readonly string s;       // String mode: the path prefix is s[0..end)
            readonly int end;
            readonly int firstSep;   // Index of the first '\' in s (the root separator stays in the base)

            Cursor(MftTable table, int row)
            {
                this.table = table;
                this.row = row;
                node = null;
                budget = 0;
                s = null;
                end = 0;
                firstSep = 0;
            }

            Cursor(INode node, int budget)
            {
                table = null;
                row = 0;
                this.node = node;
                this.budget = budget;
                s = null;
                end = 0;
                firstSep = 0;
            }

            Cursor(string s, int end)
            {
                table = null;
                row = 0;
                node = null;
                budget = 0;
                this.s = s;
                this.end = end;
                firstSep = s.IndexOf('\\');
            }

            public static Cursor For(INode n)
            {
                if (n is MftNode h) return ForRow(h.Table, h.Row);
                return n.PathParent != null ? new Cursor(n, MaxWalk) : ForString(n.FullName);
            }

            /// <summary>A table row; a path-terminal row (the drive root) decomposes as its string.</summary>
            public static Cursor ForRow(MftTable table, int row)
                => table.Parent[row] >= 0 ? new Cursor(table, row) : ForString(table.FullName(row));

            public static Cursor ForString(string path) => new Cursor(path, path.Length);

            public static Cursor ForDirectoryPrefix(string path)
            {
                var end = path.Length;
                var rootEnd = path.IndexOf('\\') + 1;
                while (end > rootEnd && path[end - 1] == '\\') end--;
                return new Cursor(path, end);
            }

            /// <summary>The node's path without its leaf segment (its folder); empty for a base-only path</summary>
            public static Cursor Folder(INode n)
            {
                var c = For(n);
                return c.IsBase ? ForString("") : c.Up();
            }

            public static Cursor FolderOfRow(MftTable table, int row)
            {
                var c = ForRow(table, row);
                return c.IsBase ? ForString("") : c.Up();
            }

            bool IsTable => table != null;

            public bool IsBase => table == null && node == null && (firstSep < 0 || end <= firstSep + 1);

            /// <summary>Chars of the segment in chain or string mode (table mode uses the name blob directly)</summary>
            ReadOnlySpan<char> Span
            {
                get
                {
                    if (node != null) return node.Name;
                    if (IsBase) return s.AsSpan(0, end);
                    var sep = s.LastIndexOf('\\', end - 1);
                    return s.AsSpan(sep + 1, end - sep - 1);
                }
            }

            NameSpan Name => table.NameAt(row);

            public bool SegmentEquals(Cursor y)
            {
                if (IsTable) return y.IsTable ? Name.EqualsIgnoreCase(y.Name) : Name.EqualsIgnoreCase(y.Span);
                if (y.IsTable) return y.Name.EqualsIgnoreCase(Span);
                return Span.Equals(y.Span, StringComparison.OrdinalIgnoreCase);
            }

            public int SegmentCompare(Cursor y, StringComparison comparison)
            {
                if (IsTable) return y.IsTable ? Name.CompareTo(y.Name, comparison) : Name.CompareTo(y.Span, comparison);
                if (y.IsTable) return -y.Name.CompareTo(Span, comparison);
                return Span.CompareTo(y.Span, comparison);
            }

            public Cursor Up()
            {
                if (table != null) return ForRow(table, table.Parent[row]);
                if (node != null)
                {
                    var p = node.PathParent;
                    if (p is MftNode h) return ForRow(h.Table, h.Row);
                    if (p.PathParent == null) return ForString(p.FullName);
                    if (budget <= 1) return ForString(p.Name); // Cycle guard: same cut as Materialize
                    return new Cursor(p, budget - 1);
                }
                var sep = s.LastIndexOf('\\', end - 1);
                return new Cursor(s, sep == firstSep ? sep + 1 : sep);
            }

            public bool SameAs(Cursor y)
            {
                if (table != null) return ReferenceEquals(table, y.table) && row == y.row;
                if (node != null) return ReferenceEquals(node, y.node);
                return y.table == null && y.node == null && ReferenceEquals(s, y.s) && end == y.end;
            }

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
                => table != null ? table.FullName(row)
                    : node != null ? Materialize(node) : s.Substring(0, end);
        }
    }
}
