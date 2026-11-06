using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using search.Models;
using Xunit;

namespace search.Tests
{
    /// <summary>
    /// Newest first, the grid scrolls past the rows of a wrong future time: the first row not
    /// in the future lands about in the middle of the view - after a click on the Changed
    /// header, a new filter and a finished drive load
    /// </summary>
    public class ScrollToNowTests
    {
        static readonly DateTime Now = new(2026, 11, 6, 12, 0, 0);

        sealed class TimedNode : INode
        {
            readonly string name;
            public TimedNode(string name, DateTime time) { this.name = name; LastChangeTime = time; }
            public override string FullName => "/data/" + name;
            public override string Name => name;
            public override FileAttributes Attributes { get; protected set; } = FileAttributes.Normal;
            public override ulong Size { get; protected set; }
            public override DateTime LastChangeTime { get; protected set; }
        }

        /// <summary>A newest-first list: the future rows (days ahead), then the past ones</summary>
        static List<INode> NewestFirst(int future, int past, DateTime now)
            => Enumerable.Range(0, future).Select(i => (INode)new TimedNode($"future{i}", now.AddDays(future - i)))
                .Concat(Enumerable.Range(0, past).Select(i => (INode)new TimedNode($"past{i}", now.AddMinutes(-i))))
                .ToList();

        [Theory]
        [InlineData(0, 5, 0)]   //Nothing in the future: the top is now
        [InlineData(3, 5, 3)]
        [InlineData(5, 0, -1)]  //Only future rows
        [InlineData(0, 0, -1)]
        public void NowIsTheFirstRowNotInTheFuture(int future, int past, int expected)
            => Assert.Equal(expected, MainWindow.FirstRowNotAfter(NewestFirst(future, past, Now), Now));

        [Fact]
        public void ARowOfExactlyNowIsNotInTheFuture()
            => Assert.Equal(2, MainWindow.FirstRowNotAfter(NewestFirst(2, 3, Now), Now)); //past0 is Now itself

        [Theory]
        [InlineData("+LastChangeTime", true)]   //The Changed column's first click (the default sort): newest first
        [InlineData("-LastChangeTime", false)]
        [InlineData("+Name", false)]
        [InlineData(null, false)]
        public void OnlyTheChangedColumnNewestFirstScrollsToNow(string sort, bool newestFirst)
            => Assert.Equal(newestFirst, MainWindow.IsNewestFirst(sort));

        [AvaloniaTheory]
        [InlineData(100, false)]
        [InlineData(100, true)]  //From wherever the view was
        [InlineData(0, true)]    //Nothing in the future: the top
        public void NowLandsInTheMiddleOfTheViewWithTheFutureAboveIt(int future, bool fromTheBottom)
        {
            var items = NewestFirst(future, past: 200, Now);
            var grid = new DataGrid { IsReadOnly = true, ItemsSource = new ObservableCollection<INode>(items) };
            grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new Binding(nameof(INode.Name)) });
            var window = new Window { Content = grid, Width = 400, Height = 400 };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                grid.ScrollIntoView(fromTheBottom ? items[^1] : items[0], null);
                Dispatcher.UIThread.RunJobs();

                MainWindow.ScrollToNow(grid, items, Now);
                Dispatcher.UIThread.RunJobs();

                if (future == 0) Assert.Equal(0, Top(grid, items[0]));
                else
                {
                    Assert.InRange(Share(grid, items[future]), 0.3, 0.7);
                    Assert.InRange(Top(grid, items[future - 1]), 0, Height(grid)); //The last future row is in sight above it
                }
            }
            finally { window.Close(); }
        }

        [AvaloniaFact]
        public async Task ShiftArrowAfterScrollingToNowSelectsFromNow()
        {
            var (window, grid, nodes) = await Results(future: 100, past: 200);
            try
            {
                Click(window, grid, nodes[0]); //The grid's own anchor: the top row

                window.ScrollResultsToStart();
                Dispatcher.UIThread.RunJobs();
                Press(window, Key.LeftShift, RawInputModifiers.Shift);
                Press(window, Key.Down, RawInputModifiers.Shift);

                Assert.Equal(new[] { 100, 101 }, Selected(grid, nodes));
            }
            finally { window.Close(); }
        }

        [AvaloniaFact]
        public async Task ShiftArrowAfterEndSelectsFromTheEnd()
        {
            var (window, grid, nodes) = await Results(future: 0, past: 50);
            try
            {
                Click(window, grid, nodes[2]);
                Press(window, Key.End);
                Press(window, Key.LeftShift, RawInputModifiers.Shift);
                Press(window, Key.Up, RawInputModifiers.Shift);

                Assert.Equal(new[] { 48, 49 }, Selected(grid, nodes));
            }
            finally { window.Close(); }
        }

        [AvaloniaFact]
        public async Task AFinishedLoadStartsTheResultsAtNowUntilTheUserMovesThem()
        {
            var (window, grid, nodes) = await Results(future: 100, past: 200);
            try
            {
                window.ResultsLoaded();
                Dispatcher.UIThread.RunJobs();
                Assert.InRange(Share(grid, nodes[100]), 0.3, 0.7);

                //A look at the future rows - the next load (a rescan) leaves the view there
                var row = grid.GetVisualDescendants().OfType<DataGridRow>().First(r => r.IsVisible);
                window.MouseWheel(row.TranslatePoint(new Point(20, 5), window).Value, new Vector(0, 1));
                grid.ScrollIntoView(nodes[0], null);
                Dispatcher.UIThread.RunJobs();
                window.ResultsLoaded();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(0, Top(grid, nodes[0]));
            }
            finally { window.Close(); }
        }

        [AvaloniaFact]
        public async Task NewFilterResultsStartAtNowUnlessASelectionCarriedOver()
        {
            var (window, grid, nodes) = await Results(future: 100, past: 200);
            try
            {
                window.ResultsFiltered();
                Dispatcher.UIThread.RunJobs();
                Assert.InRange(Share(grid, nodes[100]), 0.3, 0.7);

                Click(window, grid, nodes[110]);
                grid.ScrollIntoView(nodes[0], null);
                Dispatcher.UIThread.RunJobs();
                window.ResultsFiltered();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(0, Top(grid, nodes[0]));
            }
            finally { window.Close(); }
        }

        [AvaloniaFact]
        public async Task TheRowPickedForASelectionThatLeftGoesToNow()
        {
            var (window, grid, nodes) = await Results(future: 100, past: 200);
            try
            {
                Click(window, grid, nodes[110]);
                Assert.True(grid.IsKeyboardFocusWithin);
                //Enter into a folder: the folder leaves the results, the app selects the row
                //that took its place
                Exchange(window, nodes[110]);
                Assert.Equal(new[] { 111 }, Selected(grid, nodes));

                window.ResultsFiltered();
                Dispatcher.UIThread.RunJobs();

                Assert.InRange(Share(grid, nodes[100]), 0.3, 0.7);
                Assert.Equal(new[] { 100 }, Selected(grid, nodes));
                Press(window, Key.LeftShift, RawInputModifiers.Shift);
                Press(window, Key.Down, RawInputModifiers.Shift);
                Assert.Equal(new[] { 100, 101 }, Selected(grid, nodes));
            }
            finally { window.Close(); }
        }

        [AvaloniaFact]
        public async Task ThePickStaysThePickWhileItSurvivesARefinedFilter()
        {
            var (window, grid, nodes) = await Results(future: 100, past: 200);
            try
            {
                Click(window, grid, nodes[110]);
                Exchange(window, nodes[110]);
                window.ResultsFiltered(); //The pick goes to "now": nodes[100]
                Dispatcher.UIThread.RunJobs();

                //A refined filter: 60 future rows go, "now" moves from index 100 to 40
                Exchange(window, nodes.Take(60).ToArray());
                window.ResultsFiltered();
                Dispatcher.UIThread.RunJobs();
                Assert.InRange(Share(grid, nodes[100]), 0.3, 0.7);
                Assert.Equal(new[] { 100 }, Selected(grid, nodes));

                //Chosen by the user, a row holds the view
                Click(window, grid, nodes[105]);
                Exchange(window, nodes.Skip(60).Take(5).ToArray());
                var top = Top(grid, nodes[105]);
                window.ResultsFiltered();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(top, Top(grid, nodes[105]));
                Assert.Equal(new[] { 105 }, Selected(grid, nodes));
            }
            finally { window.Close(); }
        }

        [AvaloniaFact]
        public async Task AClickOnThePickedRowMakesItTheUsersChoice()
        {
            var (window, grid, nodes) = await Results(future: 100, past: 200);
            try
            {
                Click(window, grid, nodes[110]);
                Exchange(window, nodes[110]);
                window.ResultsFiltered(); //The pick goes to "now": nodes[100]
                Dispatcher.UIThread.RunJobs();
                Assert.Same(nodes[100], grid.SelectedItem);

                Click(window, grid, nodes[100]); //Already selected: no selection change
                //A newer file arrives, then a refined filter
                var model = (SearchModel)window.DataContext;
                var newer = new TimedNode("newer", DateTime.Now);
                Publish(window, model.Items.Take(100).Append(newer).Concat(model.Items.Skip(100)).ToArray());
                Exchange(window, nodes.Take(10).ToArray());
                window.ResultsFiltered();
                Dispatcher.UIThread.RunJobs();

                Assert.Same(nodes[100], Assert.Single(grid.SelectedItems));
            }
            finally { window.Close(); }
        }

        /// <summary>New results without these rows, published as the model does</summary>
        static void Exchange(MainWindow window, params INode[] gone)
            => Publish(window, ((SearchModel)window.DataContext).Items.Except(gone).ToArray());

        /// <summary>
        /// New results, published as the model does: one Reset - not changes of single rows
        /// the grid keeps the view over
        /// </summary>
        static void Publish(MainWindow window, INode[] items)
        {
            var model = (SearchModel)window.DataContext;
            SearchModel.ExchangeItems(model.BeforeItemsExchange,
                () => ((RangeObservableCollection<INode>)model.Items).ReplaceRange(items), model.AfterItemsExchange);
            Dispatcher.UIThread.RunJobs();
        }

        [AvaloniaFact]
        public async Task KeysThatMoveNothingDoNotHoldBackTheStartAtNow()
        {
            var window = new MainWindow(startFileSystem: false);
            try
            {
                window.Show();
                var model = await Opened(window);
                var grid = window.FindControl<DataGrid>("filesView");
                grid.Focus();
                Dispatcher.UIThread.RunJobs();
                Assert.True(grid.IsKeyboardFocusWithin);
                Press(window, Key.Down); //Before the results: nothing to move
                var nodes = NewestFirst(100, 200, DateTime.Now.AddMinutes(-1));
                foreach (var node in nodes) model.Items.Add(node);
                Dispatcher.UIThread.RunJobs();
                Press(window, Key.LeftCtrl, RawInputModifiers.Control); //No navigation
                window.KeyRelease(Key.LeftCtrl, RawInputModifiers.None, PhysicalKey.None, null);

                window.ResultsLoaded();
                Dispatcher.UIThread.RunJobs();

                Assert.InRange(Share(grid, nodes[100]), 0.3, 0.7);
            }
            finally { window.Close(); }
        }

        /// <summary>
        /// The window's model once the results its opening asked for are on the grid - they
        /// would replace rows added sooner - and emptied of them (the index is shared with
        /// other tests)
        /// </summary>
        static async Task<SearchModel> Opened(MainWindow window)
        {
            Dispatcher.UIThread.RunJobs();
            var model = (SearchModel)window.DataContext;
            await model.UserUpdatesSettled();
            Dispatcher.UIThread.RunJobs();
            model.Items.Clear();
            return model;
        }

        /// <summary>The app's window with these rows in its results, sorted newest first</summary>
        static async Task<(MainWindow, DataGrid, List<INode>)> Results(int future, int past)
        {
            var window = new MainWindow(startFileSystem: false);
            window.Show();
            var model = await Opened(window);
            var nodes = NewestFirst(future, past, DateTime.Now.AddMinutes(-1));
            foreach (var node in nodes) model.Items.Add(node);
            Dispatcher.UIThread.RunJobs();
            return (window, window.FindControl<DataGrid>("filesView"), nodes);
        }

        static DataGridRowsPresenter Presenter(DataGrid grid)
            => grid.GetVisualDescendants().OfType<DataGridRowsPresenter>().Single();

        static double Height(DataGrid grid) => Presenter(grid).Bounds.Height;

        /// <summary>Where the row's top is in the view</summary>
        static double Top(DataGrid grid, INode node) => grid.GetVisualDescendants().OfType<DataGridRow>()
            .Where(r => r.IsVisible && ReferenceEquals(r.DataContext, node))
            .Select(r => r.TranslatePoint(new Point(0, 0), Presenter(grid)).Value.Y)
            .Single();

        /// <summary>Where the row's top is, as a share of the view's height</summary>
        static double Share(DataGrid grid, INode node) => Top(grid, node) / Height(grid);

        static int[] Selected(DataGrid grid, List<INode> nodes)
            => grid.SelectedItems.Cast<INode>().Select(n => nodes.IndexOf(n)).OrderBy(i => i).ToArray();

        static void Click(Window window, DataGrid grid, INode node)
        {
            grid.ScrollIntoView(node, null);
            Dispatcher.UIThread.RunJobs();
            var name = grid.Columns.Single(c => Equals(c.Tag, "Name"));
            var row = grid.GetVisualDescendants().OfType<DataGridRow>().Single(r => r.IsVisible && ReferenceEquals(r.DataContext, node));
            var point = name.GetCellContent(row).TranslatePoint(new Point(12, 10), window).Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            window.KeyPress(key, modifiers, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
