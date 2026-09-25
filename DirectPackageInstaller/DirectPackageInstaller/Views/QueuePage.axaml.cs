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
            // phones / narrow windows: pages restyle through the "narrow" class
            NarrowLayout.Watch(this);
            DataContext = SendQueue.Instance;

            BtnClearFinished.Click += (_, _) => SendQueue.Instance.ClearFinished();

            SendQueue.Instance.Items.CollectionChanged += (_, _) => UpdateEmpty();
            DownloadsList.ItemsSource = UpdateDownloads.Instance.Items;
            UpdateDownloads.Instance.Items.CollectionChanged += (_, _) => UpdateEmpty();
            UpdateEmpty();
        }

        void UpdateEmpty()
        {
            DownloadsPanel.IsVisible = UpdateDownloads.Instance.Items.Count > 0;
            EmptyState.IsVisible = SendQueue.Instance.Items.Count == 0 && UpdateDownloads.Instance.Items.Count == 0;
        }

        void CancelDownloadClick(object? sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.Tag is UpdateDownload Download)
                UpdateDownloads.Instance.Cancel(Download);
        }

        void RetryDownloadClick(object? sender, RoutedEventArgs e)
        {
            // continues from the partial file
            if ((sender as Control)?.Tag is UpdateDownload Download)
            {
                UpdateDownloads.Instance.Remove(Download);
                UpdateDownloads.Instance.Start(Download.Patch, Download.Title, Download.Folder);
            }
        }

        void RemoveDownloadClick(object? sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.Tag is UpdateDownload Download)
                UpdateDownloads.Instance.Remove(Download);
        }

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
