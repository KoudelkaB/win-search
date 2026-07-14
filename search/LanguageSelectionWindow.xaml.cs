using System.Linq;
using System.Windows;

namespace search
{
    public partial class LanguageSelectionWindow : Window
    {
        public string SelectedCulture => (languages.SelectedItem as LanguageOption)?.Culture;
        public LanguageSelectionWindow(string selected = null)
        {
            InitializeComponent(); languages.ItemsSource = Languages.All;
            languages.SelectedItem = Languages.All.FirstOrDefault(x => x.Culture == selected) ?? Languages.All[0];
        }
        void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
