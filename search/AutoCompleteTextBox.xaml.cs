using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace search
{
    /// <summary>
    /// Interaction logic for AutoCompleteTextBox.xaml
    /// </summary>
    public partial class AutoCompleteTextBox : UserControl
    {
        public AutoCompleteTextBox() => InitializeComponent();

        new public bool Focus() => TextBox.Focus();

        /// <summary>  
        /// Auto suggestion list property.  
        /// </summary>  
        public Func<IEnumerable<string>> SuggestionList { get; set; }

        /// <summary>
        /// Callback to delete an item from the suggestion list
        /// </summary>
        public Action<string> DeleteItem { get; set; }

        public void Home() => TextBox.CaretIndex = 0;
        public void End() => TextBox.CaretIndex = TextBox.Text.Length;

        public string Text
        {
            get => TextBox.Text;
            set => TextBox.Text = value;
        }

        public event TextChangedEventHandler TextChanged;
        public event Action<string> TextSelected;

        /// <summary>
        /// Is the suggestion popup open?
        /// </summary>
        public bool IsListOpen => autoListPopup.IsOpen;

        /// <summary>  
        ///  Open Auto Suggestion box method  
        /// </summary>  
        void OpenList()
        {
            try
            {
                // Enable.  
                autoListPopup.Visibility = Visibility.Visible;
                autoListPopup.IsOpen = true;
                autoList.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                $"AutoCompleteTextBox.OpenList exception {ex}".Debug();
                MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Console.Write(ex);
            }
        }

        /// <summary>  
        ///  Close Auto Suggestion box method  
        /// </summary>  
        void CloseList()
        {
            try
            {
                // Enable.  
                autoListPopup.Visibility = Visibility.Collapsed;
                autoListPopup.IsOpen = false;
                autoList.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                $"AutoCompleteTextBox.CloseList exception {ex}".Debug();
                MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Console.Write(ex);
            }
        }

        /// <summary>
        /// Update suggestions based on current text
        /// </summary>
        void UpdateSuggestions()
        {
            if (SuggestionList == null) return;
            
            try
            {
                var text = TextBox.Text.Substring(0, TextBox.CaretIndex);
                var suggestions = (string.IsNullOrWhiteSpace(text) ? SuggestionList() :
                    SuggestionList().Where(p => p.Contains(text, StringComparison.OrdinalIgnoreCase))).ToList();
                autoList.ItemsSource = suggestions;
            }
            catch (Exception ex)
            {
                $"AutoCompleteTextBox.UpdateSuggestions exception {ex}".Debug();
                MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Console.Write(ex);
            }
        }

        /// <summary>  
        ///  Auto Text Box text changed method.  
        /// </summary>  
        /// <param name="sender">Sender parameter</param>  
        /// <param name="e">Event parameter</param>  
        void AutoTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            TextChanged?.Invoke(sender, e);

            // Update suggestions when text changes
            if (SuggestionList != null)
            {
                try
                {
                    UpdateSuggestions();
                    var text = TextBox.Text.Substring(0, TextBox.CaretIndex);

                    // Show dropdown only while the user is typing in the box (not on programmatic changes)
                    if (string.IsNullOrWhiteSpace(text)) CloseList();
                    else if (autoList.Items.Count > 0 && TextBox.IsKeyboardFocusWithin)
                    {
                        autoList.SelectedIndex = 0; // Select first item automatically
                        OpenList();
                    }
                    else CloseList(); // No suggestions => do not show an empty popup
                }
                catch (Exception ex)
                {
                    $"AutoCompleteTextBox.AutoTextBox_TextChanged exception {ex}".Debug();
                    MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    Console.Write(ex);
                }
            }
        }

        /// <summary>
        /// Handle text box got focus to show suggestions for empty text
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void AutoTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (SuggestionList != null && string.IsNullOrWhiteSpace(TextBox.Text))
            {
                try
                {
                    var suggestions = SuggestionList().ToList();
                    if (suggestions.Count > 0)
                    {
                        autoList.ItemsSource = suggestions;
                        // Don't auto-open on focus, let user use Down arrow or start typing
                    }
                }
                catch (Exception ex)
                {
                    $"AutoCompleteTextBox.AutoTextBox_GotFocus exception {ex}".Debug();
                    Console.Write(ex);
                }
            }
        }

        private void AutoTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (SuggestionList == null) return;

            switch (e.Key)
            {
                case Key.Up:
                    if (autoList.IsVisible && autoList.Items.Count > 0)
                    {
                        // If no item is selected, select the last one
                        if (autoList.SelectedIndex == -1)
                        {
                            autoList.SelectedIndex = autoList.Items.Count - 1;
                            OpenList();
                        }
                        // If first item is selected, close the dropdown
                        else if (autoList.SelectedIndex == 0)
                        {
                            CloseList();
                            autoList.SelectedIndex = -1;
                        }
                        // Otherwise move up one item
                        else
                        {
                            autoList.SelectedIndex--;
                            OpenList();
                        }
                    }
                    break;
                case Key.Down:
                    // Show all suggestions if text is empty and dropdown not visible
                    if (TextBox.Text.Length == 0 && !autoList.IsVisible)
                    {
                        autoList.ItemsSource = SuggestionList();
                        if (autoList.Items.Count > 0)
                        {
                            autoList.SelectedIndex = 0;
                            OpenList();
                        }
                    }
                    else if (autoList.IsVisible && autoList.Items.Count > 0)
                    {
                        // If no item is selected yet, select the first one
                        if (autoList.SelectedIndex == -1)
                        {
                            autoList.SelectedIndex = 0;
                        }
                        // If an item is selected and it's not the last one, move to next
                        else if (autoList.SelectedIndex < autoList.Items.Count - 1)
                        {
                            autoList.SelectedIndex++;
                        }
                        // If at the last item, wrap to first
                        else
                        {
                            autoList.SelectedIndex = 0;
                        }
                        OpenList();
                    }
                    else if (!autoList.IsVisible)
                    {
                        // Show filtered suggestions
                        UpdateSuggestions();
                        if (autoList.Items.Count > 0)
                        {
                            autoList.SelectedIndex = 0;
                            OpenList();
                        }
                    }
                    break;
                case Key.Return:
                    if (autoList.IsVisible)
                    {
                        autoList_Clicked(null, null);
                        e.Handled = true;
                    }
                    break;
                case Key.Delete:
                    if (autoList.IsVisible && autoList.SelectedItem != null && DeleteItem != null)
                    {
                        var itemToDelete = autoList.SelectedItem.ToString();
                        var selectedIndex = autoList.SelectedIndex;
                        
                        // Delete the item from history
                        DeleteItem(itemToDelete);
                        
                        // Refresh the suggestions list
                        UpdateSuggestions();
                        
                        // Restore selection to same index or previous if it was the last item
                        if (autoList.Items.Count > 0)
                        {
                            autoList.SelectedIndex = Math.Min(selectedIndex, autoList.Items.Count - 1);
                            OpenList();
                        }
                        else
                        {
                            CloseList();
                        }
                        
                        e.Handled = true;
                    }
                    break;
                case Key.Escape:
                    if (autoList.IsVisible)
                    {
                        CloseList();
                        autoList.SelectedIndex = -1;
                        e.Handled = true;
                    }
                    break;
            }
        }

        private void autoList_Clicked(object sender, MouseButtonEventArgs e)
        {
            var text = autoList.SelectedItem?.ToString();
            if (text != null)
            {
                TextSelected?.Invoke(text);
                TextBox.Text = text;
                CloseList();
                autoList.SelectedIndex = -1;
            }
        }
    }
}
