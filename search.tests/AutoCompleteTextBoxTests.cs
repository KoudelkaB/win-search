using System;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;

namespace search.Tests
{
    public class AutoCompleteTextBoxTests
    {
        [AvaloniaFact]
        public void EnterHandledByTheOwnerStillTakesTheChosenSuggestion()
        {
            WithSearchBox(false, (window, box, searched) =>
            {
                Press(window, Key.Down); // Opens the history with the first term selected
                Press(window, Key.Down); // Moves to the second one
                Assert.True(box.IsListOpen);

                Press(window, Key.Enter);

                Assert.Equal("second term", searched());
                Assert.False(box.IsListOpen);

                // Without the list Enter keeps the typed text
                box.Text = "typed";
                Press(window, Key.Enter);
                Assert.Equal("typed", searched());

                // Typing only offers the history - Enter searches what was typed...
                box.Text = "";
                Type(window, box, "term");
                Assert.True(box.IsListOpen);
                Press(window, Key.Enter);
                Assert.Equal("term", searched());
                Assert.False(box.IsListOpen);

                // ...until a suggestion is chosen with the arrows
                box.Text = "";
                Type(window, box, "s");
                Press(window, Key.Down);
                Press(window, Key.Enter);
                Assert.Equal("first term", searched());
            });
        }

        [AvaloniaFact]
        public void BoxApplyingTheTypedTextOffersTheTopSuggestionToEnter()
        {
            WithSearchBox(true, (window, box, searched) =>
            {
                Type(window, box, "term");
                Assert.True(box.IsListOpen);
                Press(window, Key.Enter);
                Assert.Equal("first term", searched());
            });
        }

        [AvaloniaFact]
        public void SettingTheTextFromCodeDoesNotOpenTheList()
        {
            WithSearchBox(true, (window, box, searched) =>
            {
                box.Text = "term";
                Dispatcher.UIThread.RunJobs();
                Assert.False(box.IsListOpen);
            });
        }

        /// <summary>
        /// A box in a shown window whose owner handles Enter before the inner text box, as in
        /// MainWindow. The test gets the text the last Enter used.
        /// </summary>
        static void WithSearchBox(bool selectFirstSuggestion, Action<Window, AutoCompleteTextBox, Func<string>> test)
        {
            var box = new AutoCompleteTextBox
            {
                SuggestionList = () => new[] { "first term", "second term" },
                SelectFirstSuggestion = selectFirstSuggestion
            };
            string searched = null;
            box.AddHandler(InputElement.KeyDownEvent, (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                box.AcceptSuggestion();
                e.Handled = true;
                searched = box.Text;
            }, RoutingStrategies.Tunnel);
            var window = new Window { Content = box, Width = 300, Height = 100 };
            try
            {
                window.Show();
                box.Focus();
                Dispatcher.UIThread.RunJobs();
                test(window, box, () => searched);
            }
            finally { window.Close(); }
        }

        static void Press(Window window, Key key)
        {
            window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
            window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
            // Let the popup open or close
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>
        /// Text entered as by the keyboard - the list opens only for the user typing in the focused box
        /// </summary>
        static void Type(Window window, AutoCompleteTextBox box, string text)
        {
            box.Focus();
            box.End();
            window.KeyTextInput(text);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
