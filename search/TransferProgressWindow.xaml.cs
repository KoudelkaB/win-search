using System.ComponentModel;
using System.Threading;
using System.Windows;

namespace search
{
    public partial class TransferProgressWindow : Window
    {
        readonly CancellationTokenSource cancellation = new();
        bool completed;

        public TransferProgressWindow(string operation, int total)
        {
            InitializeComponent();
            Operation.Text = operation;
            Progress.Maximum = total;
        }

        public CancellationToken Token => cancellation.Token;

        public void Report(int completedCount, string currentItem)
        {
            Progress.Value = completedCount;
            CurrentItem.Text = currentItem;
        }

        public void Complete()
        {
            completed = true;
            Close();
        }

        void Cancel_Click(object sender, RoutedEventArgs e)
        {
            cancellation.Cancel();
            CancelButton.IsEnabled = false;
            CancelButton.Content = "Cancelling...";
        }

        void Window_Closing(object sender, CancelEventArgs e)
        {
            if (completed)
                return;
            cancellation.Cancel();
            CancelButton.IsEnabled = false;
            CancelButton.Content = "Cancelling...";
            e.Cancel = true;
        }
    }
}
