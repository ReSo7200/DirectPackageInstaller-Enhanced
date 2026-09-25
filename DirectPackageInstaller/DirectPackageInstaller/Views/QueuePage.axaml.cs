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

        async void PauseClick(object? sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.Tag is QueueItem Item)
                await SendQueue.Instance.PauseAsync(Item);
        }

        async void ResumeClick(object? sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.Tag is QueueItem Item)
                await SendQueue.Instance.ResumeAsync(Item);
        }

        async void CancelClick(object? sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.Tag is QueueItem Item)
                await SendQueue.Instance.CancelAsync(Item);
        }

        void RemoveClick(object? sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.Tag is QueueItem Item)
                SendQueue.Instance.Remove(Item);
        }
    }
}
