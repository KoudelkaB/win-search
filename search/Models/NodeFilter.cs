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

            public Pattern(string pattern)
            {
                this.pattern = pattern;

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

    List<Pattern> inName = new List<Pattern>();
    List<Pattern> inParentName = new List<Pattern>();
    List<Pattern> inParentsName = new List<Pattern>();
    // Directories list with recursion flag (Path, Recursive)
    List<(string Path, bool Recursive)> dirs = new List<(string Path, bool Recursive)>();

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
                        dirs.Add((path, recursive));
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
            if (inParentsName.Count > 0 && !inParentsName.All(x => x.Matches(n.FullName))) return false;

            if (dirs.Count == 0) return true;

            if (inName.Count == 0)
            {
                var parentDir = Path.GetDirectoryName(n.FullName)?.TrimEnd('\\');
                return dirs.Any(d => (!d.Recursive && string.Equals(parentDir, d.Path, StringComparison.OrdinalIgnoreCase))
                                  || (d.Recursive && n.FullName.StartsWith(d.Path + '\\', StringComparison.OrdinalIgnoreCase)));
            }
            else
            {
                return dirs.Any(d => n.FullName.StartsWith(d.Path + '\\', StringComparison.OrdinalIgnoreCase));
            }
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
