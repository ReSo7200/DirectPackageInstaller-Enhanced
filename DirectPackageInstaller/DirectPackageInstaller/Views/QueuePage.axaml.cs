using Avalonia.Controls;
using Avalonia.Interactivity;
using DirectPackageInstaller.Services;

namespace DirectPackageInstaller.Views
{
    public partial class QueuePage : UserControl
    {
        public QueuePage()
        {
            InitializeComponent();
            DataContext = SendQueue.Instance;

            BtnClearFinished.Click += (_, _) => SendQueue.Instance.ClearFinished();

            SendQueue.Instance.Items.CollectionChanged += (_, _) => UpdateEmpty();
            UpdateEmpty();
        }

        void UpdateEmpty() => EmptyState.IsVisible = SendQueue.Instance.Items.Count == 0;

        void RetryClick(object? sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.Tag is QueueItem Item)
                SendQueue.Instance.Retry(Item);
        }

        void RemoveClick(object? sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.Tag is QueueItem Item)
                SendQueue.Instance.Remove(Item);
        }
    }
}
