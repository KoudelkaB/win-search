using Avalonia.Controls;
using Avalonia.Interactivity;

namespace search
{
    public enum FileTransferAction
    {
        Copy,
        Move,
        SymbolicLink,
        HardLink
    }

    public partial class FileTransferDialog : Window
    {
        public FileTransferDialog() => InitializeComponent();

        public FileTransferDialog(
            string[] sources,
            int destinationCount,
            FileTransferAction defaultAction = FileTransferAction.Copy) : this()
        {
            Prompt.Text = sources.Length == 1
                ? L.Format("ItemToFolders", FileOperationText.DescribeItems(sources), destinationCount)
                : L.Format("ItemsToFolders", sources.Length, destinationCount);
            CopyAction.IsChecked = defaultAction == FileTransferAction.Copy;
            MoveAction.IsChecked = defaultAction == FileTransferAction.Move;
            SymbolicLinkAction.IsChecked = defaultAction == FileTransferAction.SymbolicLink;
            HardLinkAction.IsChecked = defaultAction == FileTransferAction.HardLink;
            Opened += (_, __) => OkButton.Focus();
        }

        public FileTransferAction Action =>
            MoveAction.IsChecked == true ? FileTransferAction.Move :
            SymbolicLinkAction.IsChecked == true ? FileTransferAction.SymbolicLink :
            HardLinkAction.IsChecked == true ? FileTransferAction.HardLink :
            FileTransferAction.Copy;

        void Ok_Click(object sender, RoutedEventArgs e) => Close(true);
        void Cancel_Click(object sender, RoutedEventArgs e) => Close(false);
    }
}
