using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using search.Models;
using Xunit;

namespace search.Tests
{
    public class GridKeyboardSelectionTests
    {
        [AvaloniaTheory]
        [InlineData(Key.LeftShift)]
        [InlineData(Key.RightShift)]
        public async Task HoldingShiftExtendsAndShrinksTheSelectionAfterAMouseClick(Key shift)
        {
            var window = new MainWindow(startFileSystem: false);
            try
            {
                window.Show();
                var model = (SearchModel)window.DataContext;
                await model.Update("");
                var nodes = Enumerable.Range(0, 8).Select(i => new TestNode(i)).ToArray();
                foreach (var node in nodes) model.Items.Add(node);
                Dispatcher.UIThread.RunJobs();
                var grid = window.FindControl<DataGrid>("filesView");
                var name = grid.Columns.Single(c => Equals(c.Tag, "Name"));
                var row = grid.GetVisualDescendants().OfType<DataGridRow>()
                    .Single(r => ReferenceEquals(r.DataContext, nodes[2]));
                var content = name.GetCellContent(row);
                var point = content.TranslatePoint(new Point(12, 10), window).Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();

                void Selection(params int[] expected) => Assert.Equal(expected,
                    grid.SelectedItems.Cast<INode>().Select(n => Array.IndexOf(nodes, n)).OrderBy(i => i));
                Selection(2);
                //The Shift key itself must go through the real window/grid event handlers.
                //Sending only an arrow event with the Shift flag misses the command conflict.
                Press(window, shift, RawInputModifiers.Shift);
                Tap(window, Key.Down, RawInputModifiers.Shift);
                Selection(2, 3);
                Release(window, shift);
                Press(window, shift, RawInputModifiers.Shift);
                Tap(window, Key.Down, RawInputModifiers.Shift);
                Selection(2, 3, 4);
                Press(window, Key.Down, RawInputModifiers.Shift);
                Selection(2, 3, 4, 5);
                Press(window, Key.Down, RawInputModifiers.Shift); //Auto-repeat while both keys stay held.
                Selection(2, 3, 4, 5, 6);
                Release(window, Key.Down, RawInputModifiers.Shift);
                Tap(window, Key.Up, RawInputModifiers.Shift);
                Selection(2, 3, 4, 5);
                Tap(window, Key.Up, RawInputModifiers.Shift);
                Selection(2, 3, 4);
                Tap(window, Key.Up, RawInputModifiers.Shift);
                Selection(2, 3);
                Tap(window, Key.Up, RawInputModifiers.Shift);
                Selection(2);
                Tap(window, Key.Up, RawInputModifiers.Shift);
                Selection(1, 2);
                Tap(window, Key.Down, RawInputModifiers.Shift);
                Selection(2);
                Release(window, shift);
                Assert.True(grid.IsKeyboardFocusWithin); //Right Shift alone normally focuses the filter.
                Tap(window, Key.Down);
                Selection(3);
            }
            finally { window.Close(); }
        }

        [AvaloniaFact]
        public async Task ShiftSelectionContinuesAcrossLiveResultChanges()
        {
            using var scene = await UiScene.Open();
            var model = (SearchModel)scene.Window.DataContext;
            var rows = Enumerable.Range(0, 7).Select(i => (INode)new TestNode(i)).ToArray();
            void Exchange(Action change)
            {
                SearchModel.ExchangeItems(model.BeforeItemsExchange, change, model.AfterItemsExchange);
                scene.Settle();
            }
            void Publish(INode[] items)
                => Exchange(() => ((RangeObservableCollection<INode>)model.Items).ReplaceRange(items));
            void Selection(int first, int last)
                => Assert.Equal(rows[first..(last + 1)], scene.Selected.OrderBy(n => Array.IndexOf(rows, n)));

            Publish(rows);
            scene.ClickRow(rows[1]);
            scene.Keys(Key.LeftShift, Key.Down);
            Selection(1, 2);

            Exchange(() => model.Items.Insert(0, new TestNode(7)));
            scene.Keys(Key.LeftShift, Key.Down);
            Selection(1, 3);

            var window = scene.Window;
            Press(window, Key.LeftShift, RawInputModifiers.Shift);
            Press(window, Key.Down, RawInputModifiers.Shift);
            Selection(1, 4);
            Publish(rows);
            Press(window, Key.Down, RawInputModifiers.Shift); //Auto-repeat after a live update.
            Selection(1, 5);
            Release(window, Key.Down, RawInputModifiers.Shift);

            Tap(window, Key.Up, RawInputModifiers.Shift);
            Selection(1, 4);
            Publish(rows.Prepend(new TestNode(8)).ToArray());
            Tap(window, Key.Up, RawInputModifiers.Shift);
            Selection(1, 3);
            Release(window, Key.LeftShift);

            //If the starting row is deleted, the current row starts the next range.
            Publish(rows.Where(n => !ReferenceEquals(n, rows[1])).ToArray());
            Selection(2, 3);
            scene.Keys(Key.LeftShift, Key.Down);
            Selection(3, 4);
        }

        static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            window.KeyPress(key, modifiers, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
        }

        static void Release(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            window.KeyRelease(key, modifiers, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
        }

        static void Tap(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            Press(window, key, modifiers);
            Release(window, key, modifiers);
        }

        sealed class TestNode(int index) : INode
        {
            public override string Name => $"node-{index}.txt";
            public override string FullName => @"C:\selection\" + Name;
            public override FileAttributes Attributes { get; protected set; }
            public override ulong Size { get; protected set; }
            public override DateTime LastChangeTime { get; protected set; }
        }
    }
}
