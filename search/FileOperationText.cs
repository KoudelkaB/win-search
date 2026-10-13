using System.Collections.Generic;
using System.Globalization;

namespace search
{
    internal static class FileOperationText
    {
        public static string DescribeItems(IReadOnlyList<string> paths)
            => DescribeItems(paths, CultureInfo.CurrentUICulture);

        internal static string DescribeItems(IReadOnlyList<string> paths, CultureInfo culture) =>
            paths.Count == 1
                ? $"\"{paths[0]}\""
                : string.Format(culture, L.Text("ItemCount", culture), paths.Count);
    }
}
