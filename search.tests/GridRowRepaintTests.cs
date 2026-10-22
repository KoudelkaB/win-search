using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using search.Models;
using Xunit;

namespace search.Tests
{
    /// <summary>
    /// A directory's aggregated size changes while its row keeps its position - the drive
    /// root is always the largest row of a size sort and therefore never moves. Nothing but
    /// the targeted row repaint can make that new value visible, and INode raises no change
    /// notifications, so the repaint must really reach a cell built from a template.
    /// </summary>
    public class GridRowRepaintTests
    {
        sealed class GrowingNode : INode
        {
            public GrowingNode(ulong size) => Size = size;
            public override string FullName => @"C:\";
            public override string Name => "C:";
            public override FileAttributes Attributes { get; protected set; } = FileAttributes.Directory;
            public override ulong Size { get; protected set; }
            public override DateTime LastChangeTime { get; protected set; }
            public void Grow(ulong by) => Size += by;
        }

        [AvaloniaFact]
        public void RepaintingARowShowsTheNodesNewSize()
        {
            var node = new GrowingNode(100);
            var (window, grid) = BuildSizeGrid(node);
            try
            {
                var row = Row(grid);
                Assert.Equal("100", SizeCellText(row));

                node.Grow(23);
                Assert.Equal("100", SizeCellText(row)); //No notification - the cell is stale
                MainWindow.RefreshCells(grid, row);
                Dispatcher.UIThread.RunJobs();

                Assert.Equal("123", SizeCellText(row));
            }
            finally { window.Close(); }
        }

        [AvaloniaFact]
        public void RepaintKeepsTheCellVisuals()
        {
            var node = new GrowingNode(100);
            var (window, grid) = BuildSizeGrid(node);
            try
            {
                var row = Row(grid);
                var cell = FindText(row);

                node.Grow(23);
                MainWindow.RefreshCells(grid, row);
                Dispatcher.UIThread.RunJobs();

                //Same TextBlock instance => an inline editor next to it would survive
                Assert.Same(cell, FindText(row));
            }
            finally { window.Close(); }
        }

        /// <summary>One column, a template binding to Size - the real grid's Size column</summary>
        static (Window, DataGrid) BuildSizeGrid(INode node)
        {
            var grid = new DataGrid { IsReadOnly = true, ItemsSource = new ObservableCollection<INode> { node } };
            grid.Columns.Add(new DataGridTemplateColumn
            {
                Width = new DataGridLength(100),
                CellTemplate = new FuncDataTemplate<INode>((_, _) =>
                    new TextBlock { [!TextBlock.TextProperty] = new Binding(nameof(INode.Size)) })
            });
            var window = new Window { Content = grid, Width = 300, Height = 200 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            return (window, grid);
        }

        static DataGridRow Row(DataGrid grid) => grid.GetVisualDescendants().OfType<DataGridRow>().First();

        static TextBlock FindText(DataGridRow row) => row.GetVisualDescendants().OfType<TextBlock>().First();

        static string SizeCellText(DataGridRow row) => FindText(row).Text;
    }
}
