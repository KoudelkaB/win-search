using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace search
{
    /// <summary>
    /// Interaction logic for EditValueWindow.xaml
    /// </summary>
    public partial class EditValueWindow : Window
    {
        public EditValueWindow(string title)
        {
            Title = title;
            InitializeComponent();   
        }

        private void Button_Click(object sender, RoutedEventArgs e) => DialogResult = true;

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            //Focus text
            Value.SelectionStart = 0;
            Value.SelectionLength = System.IO.Path.GetFileNameWithoutExtension(Value.Text).Length;
            Value.Focus();
        }
    }
}
