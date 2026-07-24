using System.Collections.Generic;

namespace search
{
    internal static class FileOperationText
    {
        public static string DescribeItems(IReadOnlyList<string> paths) =>
            paths.Count == 1
                ? $"\"{paths[0]}\""
                : $"{paths.Count} item(s)";
    }
}
