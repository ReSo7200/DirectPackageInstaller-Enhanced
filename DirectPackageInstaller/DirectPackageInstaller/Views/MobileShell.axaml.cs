using System;
using Avalonia.Controls;
using DirectPackageInstaller.Services;
using DirectPackageInstaller.ViewModels;

namespace DirectPackageInstaller.Views
{
    /// <summary>
    /// Android/iOS: the same pages as the desktop window (Library, On PS4, Direct
    /// link, Queue, Settings) with a bottom tab bar, hosted by SingleView.
    /// </summary>
    public partial class MobileShell : UserControl, IAppShell
    {
        public MainView Main { get; }

        public MobileShell(MainView Main)
        {
            this.Main = Main;
            InitializeComponent();
            AppShell.Current = this;

            // Direct link is the existing MainView (it also owns the servers)
            LinkHost.Children.Add(Main);

            SettingsPage.DataContext = Main.DataContext;
            SettingsPage.Host = Main;

            ConsolePill.DataContext = ConsoleStatus.Instance;
            ConsolePill.Click += async (_, _) => await ConsoleStatus.Instance.RefreshAsync();

            ConsolePage.Attach(() => (LibraryPage.DataContext as LibraryViewModel)?.Entries ?? Array.Empty<LibraryEntry>());

            TabLibrary.IsCheckedChanged += (_, _) => ShowPage();
            TabConsole.IsCheckedChanged += (_, _) => ShowPage();
            TabLink.IsCheckedChanged += (_, _) => ShowPage();
            TabQueue.IsCheckedChanged += (_, _) => ShowPage();
            TabSettings.IsCheckedChanged += (_, _) => ShowPage();

            SendQueue.Instance.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SendQueue.PendingCount))
                    TabQueue.Tag = SendQueue.Instance.PendingCount > 0 ? SendQueue.Instance.PendingCount.ToString() : null;
            };

            UpdateDownloads.Instance.Finished += Download =>
            {
                if (LibraryPage.DataContext is LibraryViewModel Library)
                    _ = Library.ScanAsync();
            };
        }

        /// <summary>After MainView loaded the settings (same order as the desktop window).</summary>
        public void OnStarted()
        {
            LibraryPage.OnShown();
            ConsoleStatus.Instance.Start();
            LibraryPage.AutoCheck();
        }

        void ShowPage()
        {
            bool LeavingSettings = SettingsPage.IsVisible && TabSettings.IsChecked != true;

            LibraryPage.IsVisible = TabLibrary.IsChecked == true;
            ConsolePage.IsVisible = TabConsole.IsChecked == true;
            LinkHost.IsVisible = TabLink.IsChecked == true;
            QueuePage.IsVisible = TabQueue.IsChecked == true;
            SettingsPage.IsVisible = TabSettings.IsChecked == true;

            if (LeavingSettings)
            {
                App.SaveSettings();
                _ = ConsoleStatus.Instance.RefreshAsync();
            }

            if (LibraryPage.IsVisible)
                LibraryPage.OnShown();
            if (ConsolePage.IsVisible)
                ConsolePage.OnShown();
        }

        public void ShowInLibrary(string TitleId)
        {
            TabLibrary.IsChecked = true;
            if (LibraryPage.DataContext is LibraryViewModel Library)
                Library.Search = TitleId;
        }

        public void OpenInDirectLink(string Source)
        {
            TabLink.IsChecked = true;
            Main.OpenSource(Source);
        }

        public void ShowQueue() => TabQueue.IsChecked = true;
    }
}
