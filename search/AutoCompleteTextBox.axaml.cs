using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace search
{
    /// <summary>
    /// Text box with a history popup. Typing offers matching history entries; Down opens
    /// the list (on an empty box the whole history), Enter takes the chosen entry, Delete
    /// removes it from the history, Escape closes the list.
    /// </summary>
    public partial class AutoCompleteTextBox : UserControl
    {
        // Keeping a popup short prevents creating containers for an unbounded history on
        // the typing path.
        const int MaxSuggestions = 50;

        /// <summary>
        /// The next text change comes from the user's keyboard. Avalonia raises TextChanged
        /// asynchronously, so a programmatic change cannot be told apart by a flag set around
        /// the assignment; instead typing, deleting and pasting mark the change as the user's.
        /// </summary>
        bool userEdited;

        public AutoCompleteTextBox()
        {
            InitializeComponent();
            TextBox.AddHandler(TextInputEvent, (_, e) => userEdited = !string.IsNullOrEmpty(e.Text), RoutingStrategies.Tunnel);
            TextBox.AddHandler(KeyDownEvent, (_, e) =>
            {
                if (e.Key is Key.Back or Key.Delete
                    || e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is Key.V or Key.X)
                    userEdited = true;
            }, RoutingStrategies.Tunnel, handledEventsToo: true);
            TextBox.AddHandler(KeyDownEvent, AutoTextBox_KeyDown, RoutingStrategies.Tunnel);
            TextBox.TextChanged += AutoTextBox_TextChanged;
            TextBox.GotFocus += AutoTextBox_GotFocus;
            autoList.AddHandler(PointerReleasedEvent, (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Left) return;
                AcceptSelected();
                TextBox.Focus();
            }, RoutingStrategies.Bubble, handledEventsToo: true);
            autoListPopup.Closed += (_, __) => autoList.SelectedIndex = -1;
            TextBox.PropertyChanged += (_, e) =>
            {
                if (e.Property == BoundsProperty) autoListPopup.MinWidth = Math.Max(200, TextBox.Bounds.Width);
            };
        }

        /// <summary>Focus the text input</summary>
        public new bool Focus() => TextBox.Focus();

        /// <summary>
        /// Auto suggestion list property.
        /// </summary>
        public Func<IEnumerable<string>> SuggestionList { get; set; }

        /// <summary>
        /// Select the first suggestion while typing, so Enter takes it. For a box that applies
        /// its text as typed (filter); where Enter uses the typed text (search) it stays off.
        /// </summary>
        public bool SelectFirstSuggestion { get; set; }

        /// <summary>
        /// Callback to delete an item from the suggestion list
        /// </summary>
        public Action<string> DeleteItem { get; set; }

        public void Home() => TextBox.CaretIndex = 0;
        public void End() => TextBox.CaretIndex = TextBox.Text?.Length ?? 0;

        public string Text
        {
            get => TextBox.Text ?? "";
            set
            {
                userEdited = false;
                TextBox.Text = value ?? "";
            }
        }

        public event EventHandler<TextChangedEventArgs> TextChanged;
        public event Action<string> TextSelected;

        /// <summary>
        /// Is the suggestion popup open?
        /// </summary>
        public bool IsListOpen => autoListPopup.IsOpen;

        void OpenList() => autoListPopup.IsOpen = true;

        void CloseList() => autoListPopup.IsOpen = false;

        IReadOnlyList<string> GetSuggestions()
        {
            if (SuggestionList == null) return Array.Empty<string>();
            var text = TextBox.Text ?? "";
            text = text.Substring(0, Math.Min(text.Length, Math.Max(0, TextBox.CaretIndex)));
            return SuggestionList()
                .Where(p => string.IsNullOrWhiteSpace(text) || p.Contains(text, StringComparison.OrdinalIgnoreCase))
                .Take(MaxSuggestions)
                .ToArray();
        }

        int Count => autoList.ItemCount;

        void UpdateSuggestions()
        {
            try { autoList.ItemsSource = GetSuggestions(); }
            catch (Exception ex) { $"AutoCompleteTextBox.UpdateSuggestions exception {ex}".Debug(); }
        }

        void AutoTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            TextChanged?.Invoke(this, e);
            var typed = userEdited;
            userEdited = false;
            if (SuggestionList == null) return;
            try
            {
                UpdateSuggestions();
                var text = TextBox.Text ?? "";
                // Show the dropdown only while the user is typing in the box (not on programmatic changes)
                if (string.IsNullOrWhiteSpace(text)) CloseList();
                else if (typed && Count > 0 && TextBox.IsKeyboardFocusWithin)
                {
                    // Unless SelectFirstSuggestion, only offer - Enter keeps the typed text until
                    // a suggestion is chosen with the arrows (Down selects the first one)
                    autoList.SelectedIndex = SelectFirstSuggestion ? 0 : -1;
                    OpenList();
                }
                else CloseList(); // No suggestions => do not show an empty popup
            }
            catch (Exception ex) { $"AutoCompleteTextBox.AutoTextBox_TextChanged exception {ex}".Debug(); }
        }

        void AutoTextBox_GotFocus(object sender, FocusChangedEventArgs e)
        {
            if (SuggestionList == null || !string.IsNullOrWhiteSpace(TextBox.Text)) return;
            try
            {
                var suggestions = GetSuggestions();
                // Don't auto-open on focus, let user use Down arrow or start typing
                if (suggestions.Count > 0) autoList.ItemsSource = suggestions;
            }
            catch (Exception ex) { $"AutoCompleteTextBox.AutoTextBox_GotFocus exception {ex}".Debug(); }
        }

        void AutoTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (SuggestionList == null) return;

            switch (e.Key)
            {
                case Key.Up:
                    if (IsListOpen && Count > 0)
                    {
                        // If no item is selected, select the last one
                        if (autoList.SelectedIndex == -1) autoList.SelectedIndex = Count - 1;
                        // If first item is selected, close the dropdown
                        else if (autoList.SelectedIndex == 0)
                        {
                            CloseList();
                            autoList.SelectedIndex = -1;
                        }
                        // Otherwise move up one item
                        else autoList.SelectedIndex--;
                        ScrollToSelection();
                        e.Handled = true;
                    }
                    break;
                case Key.Down:
                    if (!IsListOpen)
                    {
                        // Show the suggestions (all of them for an empty box)
                        UpdateSuggestions();
                        if (Count > 0)
                        {
                            autoList.SelectedIndex = 0;
                            OpenList();
                        }
                    }
                    else if (Count > 0)
                    {
                        // Next item, wrapping from the last to the first
                        autoList.SelectedIndex = autoList.SelectedIndex < Count - 1 ? autoList.SelectedIndex + 1 : 0;
                    }
                    ScrollToSelection();
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if (AcceptSuggestion()) e.Handled = true;
                    break;
                case Key.Delete:
                    if (IsListOpen && autoList.SelectedItem != null && DeleteItem != null)
                    {
                        var itemToDelete = autoList.SelectedItem.ToString();
                        var selectedIndex = autoList.SelectedIndex;
                        DeleteItem(itemToDelete);
                        UpdateSuggestions();
                        // Keep the selection at the same index, or the previous one after the last
                        if (Count > 0)
                        {
                            autoList.SelectedIndex = Math.Min(selectedIndex, Count - 1);
                            OpenList();
                        }
                        else CloseList();
                        e.Handled = true;
                    }
                    break;
                case Key.Escape:
                    if (IsListOpen)
                    {
                        CloseList();
                        autoList.SelectedIndex = -1;
                        e.Handled = true;
                    }
                    break;
            }
        }

        void ScrollToSelection()
        {
            if (autoList.SelectedIndex >= 0) autoList.ScrollIntoView(autoList.SelectedIndex);
        }

        /// <summary>
        /// Take the suggestion chosen in the open list as the text and close the list.
        /// False when no suggestion was chosen - the typed text stays.
        /// </summary>
        public bool AcceptSuggestion()
        {
            if (!IsListOpen) return false;
            var chosen = autoList.SelectedItem != null;
            AcceptSelected();
            CloseList(); // Also when nothing was chosen - the list must not outlive the key
            return chosen;
        }

        void AcceptSelected()
        {
            var text = autoList.SelectedItem?.ToString();
            if (text == null) return;
            TextSelected?.Invoke(text);
            Text = text;
            TextBox.CaretIndex = text.Length;
            CloseList();
            autoList.SelectedIndex = -1;
        }
    }
}
