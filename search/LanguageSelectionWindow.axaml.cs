using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace search
{
    public partial class LanguageSelectionWindow : Window
    {
        public string SelectedCulture => (languages.SelectedItem as LanguageOption)?.Culture;

        /// <summary>True when the user confirmed with OK (closing the window keeps the default)</summary>
        public bool Accepted { get; private set; }

        public LanguageSelectionWindow() : this(null) { }

        public LanguageSelectionWindow(string selected)
        {
            InitializeComponent();
            languages.ItemsSource = Languages.All;
            languages.SelectedItem = Languages.All.FirstOrDefault(x => x.Culture == selected) ?? Languages.All[0];
            Opened += (_, __) => languages.Focus();
        }

        void Ok_Click(object sender, RoutedEventArgs e)
        {
            Accepted = true;
            Close(true);
        }
    }
}
