using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Linq;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using Ex = System.Linq.Expressions.Expression;

namespace search.Models
{
    public class NodeFilter
    {
        /// <summary>
        /// Handles patterns in name search
        /// Format: [:]text[:][|]...
        /// : - means that the name should start/end or both (be equal) to text
        /// | is or in the pattern
        /// </summary>
        class Pattern
        {
            string pattern;
            public readonly Func<string, bool> Matches;
            [Flags] enum Pos { CONTAINS = 0, STARTS = 1, ENDS = 2, EQUALS = STARTS | ENDS }

            //TODO: optmize by precislely constructed regex or seach function i.e. search the string only once for all
            // e.g. construct starting/equal char table, construct ending table (merge the tables together and add containing)
            // e.g. ".cs:|.cpp:" would result in ".c(s|pp)$" - would be better to do without regex (because of the risk of backtracking)
            // i.e. I need to search for .c and branch the search when found...
            //=> construct expression:
            //  c=='.' && (++c=='c' || c=='C') && ((++c=='s' || c=='S') && b(0) || (c=='p'|| c=='P') && (++c=='p'|| c=='P'))
            // kde "b" je backtracking on false:
            //      bool b(char x) { if (++c != x) { c--; return false; } return true; };
            // Obecný mechanismus? <= nejdříve SEŘADIT dle abecedy.
            //=> lepší bude sestavit "statement expression" jako z lambdy s {}
            //aby bylo napřímo a nemusela se tam volat b() funkce
            //Porvnat s rychlostí regulárů... Jak moc se skutečně regulár kompiluje?
            //https://en.wikipedia.org/wiki/Nondeterministic_finite_automaton

            //Method by expression tree
            static MethodInfo MethodCalled(Expression<Action> e) => (e.Body as MethodCallExpression)?.Method;
            static readonly MethodInfo miContains = MethodCalled(() => "".Contains("", (StringComparison)0));

            //Method by reflection
            static readonly Type[] cmpTypes = { typeof(string), typeof(StringComparison) };
            static readonly MethodInfo miStartsWith = typeof(string).GetMethod("StartsWith", cmpTypes);
            static readonly MethodInfo miEndsWith = typeof(string).GetMethod("EndsWith", cmpTypes);
            static readonly MethodInfo miEquals = typeof(string).GetMethod("Equals", cmpTypes);

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

                //Create and compile expression for comparison
                ParameterExpression text = Ex.Parameter(typeof(string));
                ConstantExpression cmp = Ex.Constant(StringComparison.OrdinalIgnoreCase);
                var ex = Ex.Lambda(pattern.Split('|').Distinct()
                    //Order by pobability to match (from shortest containing to longest equal)
                    .OrderBy(x => (x.Where(c => c == ':').Count() << 10) + x.Length)
                    .Select(x => (Ex)Ex.Call(text,
                     ((x.StartsWith(':') ? Pos.STARTS : 0) | (x.EndsWith(':') ? Pos.ENDS : 0)) switch
                     {
                         Pos.CONTAINS => miContains,
                         Pos.STARTS => miStartsWith,
                         Pos.ENDS => miEndsWith,
                         Pos.EQUALS => miEquals,
                         _ => throw new InvalidOperationException("Unknown pattern position")
                     }, Ex.Constant(x.Trim(':')), cmp)).Aggregate((s, n) => Ex.OrElse(s, n)), text);
                Matches = (Func<string, bool>)ex.Compile();
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
        /// If the node is matching the filter
        /// </summary>
        /// <param name="n"></param>
        /// <returns></returns>
        public bool Matches(INode n)
        {
            if (inName.Count > 0 && !inName.All(x => x.Matches(n.Name))) return false;
            if (inParentName.Count > 0 && !inParentName.All(x => x.Matches(n.ParentName))) return false;
            if (inParentsName.Count > 0 && !inParentsName.All(x => MatchesPath(x, n))) return false;

            if (dirs.Count == 0) return true;

            if (inName.Count == 0)
                return dirs.Any(d => d.Recursive ? IsUnder(n, d) : HasParent(n, d));
            return dirs.Any(d => IsUnder(n, d));
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

        /// <summary>
        /// The node lies strictly inside the directory subtree - by ancestor identity for
        /// indexed chains, by path prefix for path-backed nodes (zip entries, walked drives)
        /// </summary>
        static bool IsUnder(INode n, DirCriterion d) => NodePath.IsUnder(n, d.Node, d.Prefix);

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
