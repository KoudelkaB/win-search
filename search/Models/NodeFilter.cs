using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Linq;
using System.IO;

namespace search.Models
{
    public class NodeFilter
    {
        /// <summary>
        /// Handles patterns in name search
        /// Format: [:]text[:][|]...
        /// : - means that the name should start/end or both (be equal) to text
        /// | is or in the pattern
        /// Matched against spans: an MFT row's name is tested in place in the name blob,
        /// so a filter pass over millions of rows allocates nothing.
        /// </summary>
        class Pattern
        {
            readonly string pattern;
            [Flags] enum Pos { CONTAINS = 0, STARTS = 1, ENDS = 2, EQUALS = STARTS | ENDS }
            readonly (string Text, Pos Position)[] alternatives;

            /// <summary>
            /// True when every alternative is a plain contains-match without '\' - such a
            /// pattern can never span a path separator, so matching it against each path
            /// component is exactly equivalent to matching it against the full path string
            /// (and needs no full path to be built).
            /// </summary>
            public readonly bool PlainContains;

            /// <summary>
            /// True when no alternative contains '\' - the pattern cannot span a path
            /// separator, so it is matched against each path component, with anchors
            /// binding to a single component name (e.g. ":download:" = a component named
            /// exactly "download") rather than to the whole path string.
            /// </summary>
            public readonly bool ComponentMatch;

            public Pattern(string pattern)
            {
                this.pattern = pattern;
                PlainContains = pattern.Split('|').All(x => !x.StartsWith(':') && !x.EndsWith(':') && !x.Contains('\\'));
                ComponentMatch = !pattern.Contains('\\');
                alternatives = pattern.Split('|').Distinct()
                    //Order by probability to match (from shortest containing to longest equal)
                    .OrderBy(x => (x.Count(c => c == ':') << 10) + x.Length)
                    .Select(x => (x.Trim(':'),
                        (x.StartsWith(':') ? Pos.STARTS : 0) | (x.EndsWith(':') ? Pos.ENDS : 0)))
                    .ToArray();
            }

            public bool Matches(string text) => Matches(text.AsSpan());

            public bool Matches(ReadOnlySpan<char> text)
            {
                for (var i = 0; i < alternatives.Length; i++)
                {
                    var (value, position) = alternatives[i];
                    var hit = position switch
                    {
                        Pos.CONTAINS => text.Contains(value, StringComparison.OrdinalIgnoreCase),
                        Pos.STARTS => text.StartsWith(value, StringComparison.OrdinalIgnoreCase),
                        Pos.ENDS => text.EndsWith(value, StringComparison.OrdinalIgnoreCase),
                        _ => text.Equals(value, StringComparison.OrdinalIgnoreCase)
                    };
                    if (hit) return true;
                }
                return false;
            }

            public bool Matches(NameSpan name)
            {
                Span<char> scratch = stackalloc char[NameSpan.MaxChars];
                return Matches(name.Chars(scratch));
            }

            public override string ToString() => pattern;

            //Implicit casts
            public static implicit operator Pattern(string p) => new Pattern(p);
            public static implicit operator String(Pattern p) => p.ToString();
        }

        /// <summary>
        /// A directory criterion: the filter path plus the indexed node it resolves to.
        /// Resolved once per NodeFilter instance (a new instance is created for every
        /// filter change), so node matching is pointer walks instead of string prefixes.
        /// </summary>
        class DirCriterion
        {
            public readonly string Path;
            public readonly string Prefix; // Path + '\' for the textual fallback
            public readonly bool Recursive;
            INode node;
            public INode Terminal { get; private set; }
            bool resolved;

            public DirCriterion(string path, bool recursive)
            {
                Path = path;
                Prefix = path + '\\';
                Recursive = recursive;
            }

            public INode Node
            {
                get
                {
                    if (!resolved)
                    {
                        node = Resolve(Path);
                        Terminal = NodePath.TerminalOf(node);
                        resolved = true;
                    }
                    return node;
                }
            }
        }

        /// <summary>
        /// Resolves a filter directory path to its indexed node (hook for tests)
        /// </summary>
        internal static Func<string, INode> Resolve = SearchModel.FindByPath;

        List<Pattern> inName = new List<Pattern>();
        List<Pattern> inParentName = new List<Pattern>();
        List<Pattern> inParentsName = new List<Pattern>();
        // Directories with recursion flag
        List<DirCriterion> dirs = new List<DirCriterion>();

        /// <summary>
        /// Create the filter from text - list of OR directories and AND values separated by spaces except in quotes "..."
        /// if some items contains \ than it is taken as a directory name
        /// </summary>
        /// <param name="text"></param>
        public NodeFilter(string text)
        {
            if (text == null) return;
            foreach (Match m in Regex.Matches(text, "\"[^\"]+\"|[^ \"]+"))
            {
                var raw = m.Value.Trim('\"');
                var val = raw.AsSpan();
                if (val.IndexOf(":\\") == 1)
                {
                    // Count trailing backslashes
                    int trailing = 0;
                    for (int i = val.Length - 1; i >= 0 && val[i] == '\\'; i--) trailing++;
                    bool recursive = trailing >= 2; // double backslash => recursive subtree
                    var path = raw.TrimEnd('\\');
                    if (!dirs.Any(d => d.Recursive == recursive && string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase)))
                        dirs.Add(new DirCriterion(path, recursive));
                }
                else
                {
                    // Name filter tokens:
                    //  trailing 0 backslashes => inName (matches file/dir name)
                    //  trailing 1 backslash  => inParentName (matches immediate parent directory name)
                    //  trailing >=2          => inParentsName (matches any parent in full path)
                    int trailing = 0;
                    for (int i = val.Length - 1; i >= 0 && val[i] == '\\'; i--) trailing++;
                    var core = raw.TrimEnd('\\');
                    if (trailing >= 2) inParentsName.Add(core);
                    else if (trailing == 1) inParentName.Add(core);
                    else inName.Add(core);
                }
            }
        }

        /// <summary>
        /// Remove last filter Move all dirs level up or clear the filter completely
        /// </summary>
        /// <returns></returns>
        public NodeFilter Up()
        {
            if (inName.Count > 0) inName.RemoveAt(inName.Count - 1); //Remove last name filter
            else if (inParentName.Count > 0) inParentName.RemoveAt(inParentName.Count - 1); //Remove last parent name filter
            else if (inParentsName.Count > 0) inParentsName.RemoveAt(inParentsName.Count - 1); //Remove last parents name filter
            else if (dirs.Count > 0)
            {
                dirs.RemoveAt(dirs.Count - 1); // Remove last directory criterion
            }
            return this;
        }

        /// <summary>
        /// True when the filter carries no criteria at all (empty search box) - every node
        /// matches, so bulk passes may skip the per-node filtering entirely
        /// </summary>
        public bool MatchesAll => inName.Count == 0 && inParentName.Count == 0
            && inParentsName.Count == 0 && dirs.Count == 0;

        /// <summary>
        /// Explicit directory criteria cache the resolved directory-node identity. Replacing
        /// a directory therefore requires a fresh filter snapshot; name/path-component-only
        /// filters can safely accept directory additions and removals as ordinary deltas.
        /// </summary>
        internal bool DependsOnDirectoryIdentity => dirs.Count != 0;

        /// <summary>
        /// If the node is matching the filter
        /// </summary>
        /// <param name="n"></param>
        /// <returns></returns>
        public bool Matches(INode n)
        {
            if (n is MftNode h) return Matches(h.Table, h.Row);
            //Plain loops on purpose: this runs once per indexed node on every keystroke
            //(millions of calls). LINQ All/Any with a closure over n allocated two objects
            //per node per criterion list - hundreds of MB of garbage per filter change.
            if (inName.Count > 0)
            {
                var name = n.Name;
                for (var i = 0; i < inName.Count; i++)
                    if (!inName[i].Matches(name)) return false;
            }
            if (inParentName.Count > 0)
            {
                var parentName = n.ParentName;
                for (var i = 0; i < inParentName.Count; i++)
                    if (!inParentName[i].Matches(parentName)) return false;
            }
            for (var i = 0; i < inParentsName.Count; i++)
                if (!MatchesPath(inParentsName[i], n)) return false;

            if (dirs.Count == 0) return true;

            //With a name term every directory criterion means "somewhere below"; without
            //one a single '\' means the immediate parent only.
            var alwaysRecursive = inName.Count > 0;
            for (var i = 0; i < dirs.Count; i++)
            {
                var d = dirs[i];
                if ((alwaysRecursive || d.Recursive) ? IsUnder(n, d) : HasParent(n, d)) return true;
            }
            return false;
        }

        /// <summary>
        /// <see cref="Matches(INode)"/> for one MFT table row, evaluated in place: names are
        /// compared inside the name blob and directory criteria walk the parent column, so
        /// no handle or string exists for a row that does not match.
        /// </summary>
        internal bool Matches(MftTable table, int row)
        {
            if (inName.Count > 0)
            {
                var name = table.NameAt(row);
                for (var i = 0; i < inName.Count; i++)
                    if (!inName[i].Matches(name)) return false;
            }
            if (inParentName.Count > 0)
            {
                var parent = table.Parent[row];
                for (var i = 0; i < inParentName.Count; i++)
                    if (parent < 0 ? !inParentName[i].Matches("") : !inParentName[i].Matches(table.NameAt(parent)))
                        return false;
            }
            for (var i = 0; i < inParentsName.Count; i++)
                if (!MatchesPathRow(inParentsName[i], table, row)) return false;

            if (dirs.Count == 0) return true;

            var alwaysRecursive = inName.Count > 0;
            for (var i = 0; i < dirs.Count; i++)
            {
                var d = dirs[i];
                if ((alwaysRecursive || d.Recursive)
                    ? NodePath.IsUnderRow(table, row, d.Node, d.Prefix)
                    : NodePath.HasParentRow(table, row, d.Node))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The pattern matched against the path's components, without building the path:
        /// a '\'-less pattern cannot span a separator, so each component is tested on its
        /// own - plain terms behave exactly as a full-path contains, and anchors bind to
        /// a single component name (":download:" = a component named exactly "download").
        /// Only '\'-crossing patterns are matched against the materialized full path.
        /// </summary>
        static bool MatchesPath(Pattern p, INode n)
        {
            if (!p.ComponentMatch) return p.Matches(n.FullName);

            var m = n;
            for (var guard = 0; m.PathParent != null && guard < 512; guard++)
            {
                if (p.Matches(m.Name)) return true;
                m = m.PathParent;
            }
            if (p.PlainContains) return p.Matches(m.FullName);

            // Anchored: test the remaining path-backed prefix component by component
            foreach (var part in m.FullName.Split('\\'))
                if (part.Length > 0 && p.Matches(part)) return true;
            return false;
        }

        static bool MatchesPathRow(Pattern p, MftTable table, int row)
        {
            if (!p.ComponentMatch) return p.Matches(table.FullName(row));

            var m = row;
            for (var guard = 0; table.Parent[m] >= 0 && guard < 512; guard++)
            {
                if (p.Matches(table.NameAt(m))) return true;
                m = table.Parent[m];
            }
            //m is the path-terminal row (the drive root): its own full name is the prefix
            var terminal = m == table.RootRow ? table.Root : table.FullName(m);
            if (p.PlainContains) return p.Matches(terminal);

            // Anchored: test the remaining path-backed prefix component by component
            foreach (var part in terminal.Split('\\'))
                if (part.Length > 0 && p.Matches(part)) return true;
            return false;
        }

        /// <summary>
        /// The node lies strictly inside the directory subtree - by ancestor identity for
        /// indexed chains, by path prefix for path-backed nodes (zip entries, walked drives)
        /// </summary>
        static bool IsUnder(INode n, DirCriterion d) => NodePath.IsUnder(n, d.Node, d.Prefix, d.Terminal);

        /// <summary>
        /// The directory is the node's immediate parent
        /// </summary>
        static bool HasParent(INode n, DirCriterion d)
        {
            if (n.PathParent != null) return NodePath.HasParent(n, d.Node);
            var parentDir = Path.GetDirectoryName(n.FullName)?.TrimEnd('\\');
            return string.Equals(parentDir, d.Path, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        ///String represenation of the filter
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            var tokens = dirs.Select(d => d.Path + (d.Recursive ? "\\\\" : ""))
                .Concat(inParentsName.Select(x => $"{x}\\\\"))
                .Concat(inParentName.Select(x => $"{x}\\"))
                .Concat(inName.Select(x => $"{x}"));
            return string.Join(" ", tokens.Select(x => x.IndexOf(' ') == -1 ? x : $"\"{x}\""));
        }
    }
}
