using System;
using System.Collections.Generic;
using System.IO;

namespace search.Models
{
    /// <summary>
    /// Platform rules for path text. Windows paths use '\' and compare case-insensitively;
    /// Linux paths use '/' and two names differing only in case are different files.
    /// Every index identity (hashing, equality, containment) goes through these so the
    /// rules cannot drift between the dictionary hash and its equality.
    /// </summary>
    internal static class PathText
    {
        public static readonly char Sep = Path.DirectorySeparatorChar;
        public static readonly string SepString = Path.DirectorySeparatorChar.ToString();
        public static readonly bool IgnoreCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        public static readonly StringComparison Comparison =
            IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        public static readonly StringComparer Comparer =
            IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        public static bool Equal(string a, string b) => string.Equals(a, b, Comparison);

        public static bool Equal(ReadOnlySpan<char> a, ReadOnlySpan<char> b) => a.Equals(b, Comparison);

        /// <summary>
        /// A filter token that names an absolute folder: "C:\Work" on Windows, "/home" on Linux
        /// </summary>
        public static bool IsAbsoluteFilterPath(ReadOnlySpan<char> value)
            => OperatingSystem.IsWindows()
                ? value.IndexOf(":\\") == 1
                : value.Length > 0 && value[0] == '/';

        /// <summary>The path without trailing separators, but a bare root keeps its one</summary>
        public static string TrimEndSeparators(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var trimmed = path.TrimEnd(Sep, Path.AltDirectorySeparatorChar);
            if (trimmed.Length == 0) return SepString; //"/"
            if (OperatingSystem.IsWindows() && trimmed.Length == 2 && trimmed[1] == ':') return trimmed + Sep;
            return trimmed;
        }

        /// <summary>True when path lies strictly below ancestor</summary>
        public static bool IsBelow(string path, string ancestor)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(ancestor)) return false;
            var prefix = ancestor[^1] == Sep ? ancestor : ancestor + Sep;
            return path.Length > prefix.Length && path.StartsWith(prefix, Comparison);
        }

        public static HashSet<string> NewSet() => new(Comparer);
    }
}
