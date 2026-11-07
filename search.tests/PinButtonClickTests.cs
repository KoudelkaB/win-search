using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace search.Tests
{
    /// <summary>
    /// A press moves the focus, and the focus picks the hints shown beside the filter row.
    /// Resized by them, the panel slid the Pin button from under the pointer before the
    /// release, and the click was lost.
    /// </summary>
    public class PinButtonClickTests
    {
        [AvaloniaTheory]
        [InlineData("filter", 3.0, false)]
        [InlineData("filter", -3.0, false)]
        [InlineData("grid", 3.0, false)]
        [InlineData("grid", -3.0, false)]
        [InlineData("find", 3.0, false)]
        [InlineData("find", -3.0, false)]
        // A held key shows its sequence in the hints - the press ends it
        [InlineData("grid", 3.0, true)]
        [InlineData("grid", -3.0, true)]
        public void ClickPinsWhereverTheFocusWas(string focused, double fromEdge, bool shiftHeld)
        {
            var window = Open();
            try
            {
                Focus(window, focused);
                var modifiers = RawInputModifiers.None;
                if (shiftHeld)
                {
                    modifiers = RawInputModifiers.Shift;
                    window.KeyPress(Key.LeftShift, modifiers, PhysicalKey.ShiftLeft, null);
                    Dispatcher.UIThread.RunJobs();
                }

                var pin = PinButton(window);
                var slot = pin.Bounds;
                var bounds = WindowBounds(window, pin);
                var at = new Point(fromEdge > 0 ? bounds.Left + fromEdge : bounds.Right + fromEdge, bounds.Center.Y);
                window.MouseMove(at, modifiers);
                window.MouseDown(at, MouseButton.Left, modifiers);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(slot, pin.Bounds);
                window.MouseUp(at, MouseButton.Left, modifiers);
                Dispatcher.UIThread.RunJobs();
                if (shiftHeld)
                {
                    window.KeyRelease(Key.LeftShift, RawInputModifiers.None, PhysicalKey.ShiftLeft, null);
                    Dispatcher.UIThread.RunJobs();
                }

                Assert.Single(window.PinnedFilters);
            }
            finally { window.Close(); }
        }

        [AvaloniaFact]
        public void HintsPanelFollowsTheFocusAgainAfterTheClick()
        {
            var window = Open();
            try
            {
                var help = window.FindControl<StackPanel>("help");
                Focus(window, "filter");
                var withFilterHints = help.Bounds.Width;

                Click(window, WindowBounds(window, PinButton(window)).Center);

                // The name editor of the new pin has the focus, the filter hints are gone
                Assert.Single(window.PinnedFilters);
                Assert.True(help.Bounds.Width < withFilterHints);
            }
            finally { window.Close(); }
        }

        [AvaloniaFact]
        public void OpenEncodingListStaysUnderItsBox()
        {
            var window = Open();
            try
            {
                var help = window.FindControl<StackPanel>("help");
                var combo = window.FindControl<ComboBox>("encodingComboBox");
                Focus(window, "find");
                var withSearchHints = help.Bounds.Width;

                Click(window, WindowBounds(window, combo).TopLeft + new Point(3, 10));
                Dispatcher.UIThread.RunJobs();

                Assert.True(combo.IsDropDownOpen);
                var list = (Control)combo.GetVisualDescendants().OfType<Popup>().Single().Child;
                Assert.Equal(combo.PointToScreen(new Point()).X, list.PointToScreen(new Point()).X);

                combo.IsDropDownOpen = false;
                Dispatcher.UIThread.RunJobs();
                Assert.True(help.Bounds.Width < withSearchHints);
            }
            finally { window.Close(); }
        }

        static MainWindow Open()
        {
            var window = new MainWindow(startFileSystem: false);
            window.PinnedFilters.Clear();
            // The headless font is wide - room for the filter column
            window.Width = 2400;
            window.Height = 800;
            window.Show();
            Dispatcher.UIThread.RunJobs();
            return window;
        }

        static void Focus(MainWindow window, string focused)
        {
            switch (focused)
            {
                case "filter": window.FindControl<AutoCompleteTextBox>("filterTextBox").Focus(); break;
                case "find": window.FindControl<AutoCompleteTextBox>("findTextBox").Focus(); break;
                default: window.FindControl<DataGrid>("filesView").Focus(); break;
            }
            Dispatcher.UIThread.RunJobs();
        }

        static Button PinButton(Window window) => window.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Parent is Grid && Grid.GetRow(b) == 0 && Grid.GetColumn(b) == 2);

        static Rect WindowBounds(Window window, Control control) =>
            new Rect(control.TranslatePoint(new Point(), window).Value, control.Bounds.Size);

        static void Click(Window window, Point at)
        {
            window.MouseMove(at);
            window.MouseDown(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            window.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
