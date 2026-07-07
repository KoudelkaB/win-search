using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace search
{
    internal static class WindowLayoutStore
    {
        private static readonly string LayoutPath = UserDataPaths.For("window-layout.json");

        public static WindowLayout Load()
        {
            try
            {
                if (!File.Exists(LayoutPath))
                    return null;

                var json = File.ReadAllText(LayoutPath);
                return JsonSerializer.Deserialize<WindowLayout>(json);
            }
            catch
            {
                return null;
            }
        }

        public static void Save(Window window, IReadOnlyList<double> columnWidths)
        {
            try
            {
                var bounds = window.WindowState == WindowState.Normal
                    ? new Rect(window.Left, window.Top, window.Width, window.Height)
                    : window.RestoreBounds;

                var layout = new WindowLayout
                {
                    Left = bounds.Left,
                    Top = bounds.Top,
                    Width = bounds.Width,
                    Height = bounds.Height,
                    WindowState = window.WindowState == WindowState.Minimized ? WindowState.Normal : window.WindowState,
                    ColumnWidths = columnWidths == null ? null : new List<double>(columnWidths)
                };

                Directory.CreateDirectory(Path.GetDirectoryName(LayoutPath));
                File.WriteAllText(LayoutPath, JsonSerializer.Serialize(layout, new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
            }
            catch
            {
                // Best effort only.
            }
        }
    }

    internal sealed class WindowLayout
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public WindowState WindowState { get; set; } = WindowState.Normal;
        public List<double> ColumnWidths { get; set; }
    }
}
