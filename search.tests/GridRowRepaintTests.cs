using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using search.Models;
using Xunit;

namespace search.Tests
{
    /// <summary>
    /// A directory's aggregated size changes while its row keeps its position - the drive
    /// root is always the largest row of a size sort and therefore never moves. Nothing but
    /// the targeted row repaint can make that new value visible, and INode raises no change
    /// notifications, so the repaint must really reach a GridView cell built from a
    /// CellTemplate (which binds against the cell presenter's Content, not the row's
    /// DataContext).
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

        [Fact]
        public void RepaintingARowShowsTheNodesNewSize()
        {
            RunSta(() =>
            {
                var node = new GrowingNode(100);
                var view = BuildSizeGrid(node);
                var row = (ListViewItem)view.ItemContainerGenerator.ContainerFromIndex(0);
                Assert.Equal("100", SizeCellText(row));

                node.Grow(23);
                MainWindow.RefreshRow(row);
                view.UpdateLayout();

                Assert.Equal("123", SizeCellText(row));
            });
        }

        [Fact]
        public void ARowBeingRenamedInlineKeepsItsCellVisuals()
        {
            RunSta(() =>
            {
                var node = new GrowingNode(100);
                var view = BuildSizeGrid(node);
                var row = (ListViewItem)view.ItemContainerGenerator.ContainerFromIndex(0);
                var cell = FindText(row);

                node.Grow(23);
                MainWindow.RefreshRow(row, renaming: node);
                view.UpdateLayout();

                //Same TextBlock instance => the inline editor next to it survived
                Assert.Same(cell, FindText(row));
            });
        }

        /// <summary>One column, a CellTemplate binding to Size - the real grid's Size column</summary>
        static ListView BuildSizeGrid(INode node)
        {
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding(nameof(INode.Size)));
            var template = new DataTemplate { VisualTree = text };
            template.Seal();

            var grid = new GridView();
            grid.Columns.Add(new GridViewColumn { CellTemplate = template, Width = 100 });
            var view = new ListView { View = grid, ItemsSource = new[] { node } };
            view.Measure(new Size(400, 400));
            view.Arrange(new Rect(0, 0, 400, 400));
            view.UpdateLayout();
            return view;
        }

        static string SizeCellText(DependencyObject row) => FindText(row)?.Text;

        static TextBlock FindText(DependencyObject parent)
        {
            if (parent == null) return null;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is TextBlock found) return found;
                if (FindText(child) is { } nested) return nested;
            }
            return null;
        }

        static void RunSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null) throw failure;
        }
    }
}
