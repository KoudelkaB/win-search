using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Xunit;

namespace search.Tests
{
    /// <summary>
    /// The suggestion popup closes when its window loses activation - kept away from other tests showing windows
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class WpfFocusCollection
    {
        public const string Name = "WPF focus";
    }

    [Collection(WpfFocusCollection.Name)]
    public class AutoCompleteTextBoxTests
    {
        [Fact]
        public void EnterHandledByTheOwnerStillTakesTheChosenSuggestion()
        {
            WithSearchBox(false, (box, searched) =>
            {
                Press(box, Key.Down); // Opens the history with the first term selected
                Press(box, Key.Down); // Moves to the second one
                Assert.True(box.IsListOpen);

                Press(box, Key.Enter);

                Assert.Equal("second term", searched());
                Assert.False(box.IsListOpen);

                // Without the list Enter keeps the typed text
                box.Text = "typed";
                Press(box, Key.Enter);
                Assert.Equal("typed", searched());

                // Typing only offers the history - Enter searches what was typed...
                box.Text = "";
                Type(box, "term");
                Assert.True(box.IsListOpen);
                Press(box, Key.Enter);
                Assert.Equal("term", searched());
                Assert.False(box.IsListOpen);

                // ...until a suggestion is chosen with the arrows
                box.Text = "";
                Type(box, "s");
                Press(box, Key.Down);
                Press(box, Key.Enter);
                Assert.Equal("first term", searched());
            });
        }

        [Fact]
        public void BoxApplyingTheTypedTextOffersTheTopSuggestionToEnter()
        {
            WithSearchBox(true, (box, searched) =>
            {
                Type(box, "term");
                Assert.True(box.IsListOpen);
                Press(box, Key.Enter);
                Assert.Equal("first term", searched());
            });
        }

        /// <summary>
        /// A box in a shown window whose owner handles Enter before the inner text box, as in
        /// MainWindow. The test gets the text the last Enter used.
        /// </summary>
        static void WithSearchBox(bool selectFirstSuggestion, Action<AutoCompleteTextBox, Func<string>> test)
        {
            RunSta(() =>
            {
                var box = new AutoCompleteTextBox
                {
                    SuggestionList = () => new[] { "first term", "second term" },
                    SelectFirstSuggestion = selectFirstSuggestion
                };
                string searched = null;
                box.PreviewKeyDown += (_, e) =>
                {
                    if (e.Key != Key.Enter) return;
                    box.AcceptSuggestion();
                    e.Handled = true;
                    searched = box.Text;
                };
                var window = new Window { Content = box, Width = 300, Height = 100, ShowActivated = true };
                try
                {
                    window.Show();
                    box.Focus();
                    test(box, () => searched);
                }
                finally { window.Close(); }
            });
        }

        static void Press(AutoCompleteTextBox box, Key key)
        {
            var target = box.TextBox;
            var source = PresentationSource.FromVisual(target);
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            // Let the popup open or close
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }

        /// <summary>
        /// Text entered as by the keyboard - the list opens only for the user typing in the focused box
        /// </summary>
        static void Type(AutoCompleteTextBox box, string text)
        {
            Keyboard.Focus(box.TextBox);
            box.TextBox.CaretIndex = box.TextBox.Text.Length;
            TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, box.TextBox, text));
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }

        static void RunSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null) throw failure;
        }
    }
}
