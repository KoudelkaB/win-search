using System.Windows;

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
        public FileTransferDialog(int sourceCount, int destinationCount, FileTransferAction defaultAction = FileTransferAction.Copy)
        {
            InitializeComponent();
            Prompt.Text = L.Format("ItemsToFolders", sourceCount, destinationCount);
            CopyAction.IsChecked = defaultAction == FileTransferAction.Copy;
            MoveAction.IsChecked = defaultAction == FileTransferAction.Move;
            SymbolicLinkAction.IsChecked = defaultAction == FileTransferAction.SymbolicLink;
            HardLinkAction.IsChecked = defaultAction == FileTransferAction.HardLink;
        }

        public FileTransferAction Action =>
            MoveAction.IsChecked == true ? FileTransferAction.Move :
            SymbolicLinkAction.IsChecked == true ? FileTransferAction.SymbolicLink :
            HardLinkAction.IsChecked == true ? FileTransferAction.HardLink :
            FileTransferAction.Copy;

        void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
