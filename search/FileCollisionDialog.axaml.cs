using Avalonia.Controls;
using Avalonia.Interactivity;

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
        public FileCollisionDialog() => InitializeComponent();

        public FileCollisionDialog(string destination) : this()
        {
            Destination.Text = destination;
            Opened += (_, __) => RenameButton.Focus();
        }

        public FileCollisionAction Action { get; private set; } = FileCollisionAction.Cancel;
        public bool ShouldApplyToAll => ApplyToAll.IsChecked == true;

        void Complete(FileCollisionAction action)
        {
            Action = action;
            Close(true);
        }

        void Overwrite_Click(object sender, RoutedEventArgs e) => Complete(FileCollisionAction.Overwrite);
        void Skip_Click(object sender, RoutedEventArgs e) => Complete(FileCollisionAction.Skip);
        void Rename_Click(object sender, RoutedEventArgs e) => Complete(FileCollisionAction.Rename);
        void Cancel_Click(object sender, RoutedEventArgs e) => Complete(FileCollisionAction.Cancel);
    }
}
