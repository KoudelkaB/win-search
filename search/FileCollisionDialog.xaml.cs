using System.Windows;

namespace search
{
    public enum FileCollisionAction
    {
        Overwrite,
        Skip,
        Rename,
        Cancel
    }

    public partial class FileCollisionDialog : Window
    {
        public FileCollisionDialog(string destination)
        {
            InitializeComponent();
            Destination.Text = destination;
        }

        public FileCollisionAction Action { get; private set; } = FileCollisionAction.Cancel;
        public bool ShouldApplyToAll => ApplyToAll.IsChecked == true;

        void Complete(FileCollisionAction action)
        {
            Action = action;
            DialogResult = true;
        }

        void Overwrite_Click(object sender, RoutedEventArgs e) => Complete(FileCollisionAction.Overwrite);
        void Skip_Click(object sender, RoutedEventArgs e) => Complete(FileCollisionAction.Skip);
        void Rename_Click(object sender, RoutedEventArgs e) => Complete(FileCollisionAction.Rename);
        void Cancel_Click(object sender, RoutedEventArgs e) => Complete(FileCollisionAction.Cancel);
    }
}
