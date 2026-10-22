using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;

namespace search
{
    internal enum LayoutWindowState { Normal = 0, Minimized = 1, Maximized = 2 }

    internal static class WindowLayoutStore
    {
        private static readonly string LayoutPath = UserDataPaths.For("window-layout.json");

        /// <summary>Layouts written by the WPF version (no version field) hold device-independent positions</summary>
        internal const int PixelVersion = 2;

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

        /// <summary>The saved top-left corner in screen pixels</summary>
        public static PixelPoint PixelPosition(WindowLayout layout)
        {
            if (layout.Version >= PixelVersion)
                return new PixelPoint((int)Math.Round(layout.Left), (int)Math.Round(layout.Top));
            //WPF stored device-independent units; the primary monitor's scale is the best guess
            var scaling = 1.0;
            try
            {
                if (OperatingSystem.IsWindows()) scaling = GetDpiForSystem() / 96.0;
            }
            catch { }
            return new PixelPoint((int)Math.Round(layout.Left * scaling), (int)Math.Round(layout.Top * scaling));
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern uint GetDpiForSystem();

        /// <summary>At least a part of the window lies on a connected monitor</summary>
        public static bool IsVisibleOnAnyScreen(Screens screens, PixelPoint position, WindowLayout layout)
        {
            if (layout.Width <= 0 || layout.Height <= 0 || screens == null)
                return false;
            var bounds = new PixelRect(position, new PixelSize((int)Math.Max(1, layout.Width), (int)Math.Max(1, layout.Height)));
            return screens.All.Any(s => s.WorkingArea.Intersects(bounds));
        }

        public static void Save(Window window, IReadOnlyList<double> columnWidths)
        {
            try
            {
                var previous = Load();
                var normal = window.WindowState == WindowState.Normal;
                var layout = new WindowLayout
                {
                    Version = PixelVersion,
                    //A maximized window keeps the size and place it returns to
                    Left = normal || previous == null ? window.Position.X : PixelPosition(previous).X,
                    Top = normal || previous == null ? window.Position.Y : PixelPosition(previous).Y,
                    Width = normal || previous == null ? window.Width : previous.Width,
                    Height = normal || previous == null ? window.Height : previous.Height,
                    WindowState = window.WindowState == WindowState.Maximized ? LayoutWindowState.Maximized : LayoutWindowState.Normal,
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
        public int Version { get; set; }
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public LayoutWindowState WindowState { get; set; } = LayoutWindowState.Normal;
        public List<double> ColumnWidths { get; set; }
    }
}
