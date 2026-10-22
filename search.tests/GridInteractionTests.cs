using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using search.Models;
using Xunit;

namespace search.Tests
{
    public class GridInteractionTests
    {
        [AvaloniaFact]
        public void EveryResultHeaderRaisesTheModelSortRequest()
        {
            //Use the actual configured columns, with the grid's built-in sort intercepted
            //as in MainWindow. Template columns otherwise silently ignore header clicks.
            var main = new MainWindow(startFileSystem: false);
            var source = main.FindControl<DataGrid>("filesView");
            var columns = source.Columns.ToArray();
            source.Columns.Clear();
            var grid = new DataGrid { CanUserSortColumns = true, ItemsSource = Array.Empty<INode>() };
            foreach (var column in columns) grid.Columns.Add(column);
            var window = new Window { Content = grid, Width = 1200, Height = 200 };
            DataGridColumn requested = null;
            grid.Sorting += (_, e) => { requested = e.Column; e.Handled = true; };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                foreach (var column in columns)
                {
                    requested = null;
                    var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
                        .Single(h => ReferenceEquals(DataGridColumn.GetColumnContainingElement(h), column));
                    var point = header.TranslatePoint(new Avalonia.Point(header.Bounds.Width / 2, 12), window).Value;
                    window.MouseDown(point, MouseButton.Left);
                    window.MouseUp(point, MouseButton.Left);
                    Dispatcher.UIThread.RunJobs();
                    Assert.Same(column, requested);
                }
            }
            finally { window.Close(); main.Close(); }
        }

        [AvaloniaFact]
        public void InitialSortIndicatorKeepsTheLocalizedColumnCaption()
        {
            var window = new MainWindow(startFileSystem: false);
            try
            {
                var grid = window.FindControl<DataGrid>("filesView");
                var changed = grid.Columns.Single(c => Equals(c.Tag, nameof(INode.LastChangeTime)));
                var header = Assert.IsType<StackPanel>(changed.Header);
                Assert.Equal(L.Text("Changed"), Assert.IsType<TextBlock>(header.Children[0]).Text);
            }
            finally { window.Close(); }
        }

        [Theory]
        [InlineData("Black", "Transparent")]
        [InlineData("Blue", "Blue")]
        [InlineData("Green", "Green")]
        [InlineData("Red", "Red")]
        public void RowMarkersLeaveOrdinaryFilesEmptyAndPreserveStatusColours(string input, string expected)
        {
            var converter = new RowMarkerBrushConverter();
            var result = Assert.IsAssignableFrom<ISolidColorBrush>(
                converter.Convert(Brush.Parse(input), typeof(IBrush), null, null));
            Assert.Equal(Color.Parse(expected), result.Color);
        }
    }
}
