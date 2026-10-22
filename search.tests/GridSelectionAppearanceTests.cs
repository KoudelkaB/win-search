using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
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
    public class GridSelectionAppearanceTests
    {
        [AvaloniaFact]
        public async Task SelectedRowsUseTheSharedBlueAccentWithTheStandardHoverAndFocusStates()
        {
            var window = new MainWindow(startFileSystem: false);
            try
            {
                window.Show();
                var model = (SearchModel)window.DataContext;
                await model.Update("");
                var node = new TestNode();
                model.Items.Add(node);
                Dispatcher.UIThread.RunJobs();

                Assert.True(Application.Current.TryGetResource("SystemAccentColor", window.ActualThemeVariant, out var accent));
                Assert.Equal(Color.Parse("#6BB9DE"), Assert.IsType<Color>(accent));
                var grid = window.FindControl<DataGrid>("filesView");
                var row = grid.GetVisualDescendants().OfType<DataGridRow>()
                    .Single(r => ReferenceEquals(r.DataContext, node));
                var name = grid.Columns.Single(c => Equals(c.Tag, "Name"));
                var point = name.GetCellContent(row).TranslatePoint(new Point(12, 10), window).Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                Assert.True(row.IsSelected);
                Assert.True(grid.IsKeyboardFocusWithin);
                var background = row.GetVisualDescendants().OfType<Rectangle>()
                    .Single(r => r.Name == "BackgroundRectangle" && ReferenceEquals(r.TemplatedParent, row));

                void CheckBackground(double opacity)
                {
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(accent, Assert.IsAssignableFrom<ISolidColorBrush>(background.Fill).Color);
                    Assert.Equal(opacity, background.Opacity);
                }

                window.MouseMove(new Point(1, 1));
                Assert.False(row.IsPointerOver);
                CheckBackground(0.6);
                window.MouseMove(point);
                Assert.True(row.IsPointerOver);
                CheckBackground(0.8);
                Assert.True(window.FindControl<AutoCompleteTextBox>("filterTextBox").Focus());
                Assert.False(grid.IsKeyboardFocusWithin);
                CheckBackground(0.8);
                window.MouseMove(new Point(1, 1));
                CheckBackground(0.6);
                Assert.True(row.IsSelected);
            }
            finally { window.Close(); }
        }

        sealed class TestNode : INode
        {
            public override string Name => "selected.txt";
            public override string FullName => @"C:\selection\selected.txt";
            public override FileAttributes Attributes { get; protected set; }
            public override ulong Size { get; protected set; }
            public override DateTime LastChangeTime { get; protected set; }
        }
    }
}
