using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DirectPackageInstaller.Services;
using DirectPackageInstaller.ViewModels;

namespace DirectPackageInstaller.Views
{
    public partial class ConsolePage : UserControl
    {
        ConsoleViewModel Model => (ConsoleViewModel)DataContext!;

        public ConsolePage()
        {
            InitializeComponent();
        }

        /// <summary>Called by the window once the library exists (to show "N PKGs in library").</summary>
        public void Attach(Func<IReadOnlyList<LibraryEntry>> LibraryEntries)
        {
            DataContext = new ConsoleViewModel(LibraryEntries);

            BtnRefresh.Click += async (_, _) => await Model.RefreshAsync();
            BtnRefreshIntro.Click += async (_, _) => await Model.RefreshAsync();

            FilterAll.IsCheckedChanged += (_, _) => { if (FilterAll.IsChecked == true) Model.Filter = ConsoleFilter.All; };
            FilterGames.IsCheckedChanged += (_, _) => { if (FilterGames.IsChecked == true) Model.Filter = ConsoleFilter.Games; };
            FilterApps.IsCheckedChanged += (_, _) => { if (FilterApps.IsChecked == true) Model.Filter = ConsoleFilter.Apps; };
            FilterExt.IsCheckedChanged += (_, _) => { if (FilterExt.IsChecked == true) Model.Filter = ConsoleFilter.Extended; };
        }

        /// <summary>Read the console the first time the page is shown.</summary>
        public async void OnShown()
        {
            if (DataContext is ConsoleViewModel { Loaded: false, IsLoading: false } && !string.IsNullOrWhiteSpace(App.Config.PSIP))
                await Model.RefreshAsync();
        }

        static ConsoleTitleItem? ItemOf(object? Sender) => (Sender as Control)?.Tag as ConsoleTitleItem;

        void MenuShowInLibraryClick(object? sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is { } Item)
                MainWindow.Instance?.ShowInLibrary(Item.TitleId);
        }

        async void MenuCopyIdClick(object? sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is { } Item && TopLevel.GetTopLevel(this)?.Clipboard is { } Clipboard)
                await Clipboard.SetTextAsync(Item.TitleId);
        }

        void MenuUninstallClick(object? sender, RoutedEventArgs e) => Uninstall(ItemOf(sender), "gd");

        void MenuUninstallUpdateClick(object? sender, RoutedEventArgs e) => Uninstall(ItemOf(sender), "gp");

        /// <summary>Uninstall through RPI after an explicit confirmation, then read the console again.</summary>
        async void Uninstall(ConsoleTitleItem? Item, string Category)
        {
            if (Item == null || string.IsNullOrWhiteSpace(App.Config.PSIP))
                return;

            var What = Category == "gp" ? $"the installed update of {Item.Name}" : Item.Name;
            var Reply = await MessageBox.ShowAsync(
                $"Uninstall {What} ({Item.TitleId}) from the console?\n\nIt has to be downloaded again to play. Saved data is kept.",
                "Uninstall from PS4", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (Reply != DialogResult.Yes)
                return;

            var Error = await ConsoleActions.UninstallAsync(App.Config.PSIP, new LibraryEntry
            {
                Category = Category,
                TitleId = Item.TitleId,
                Title = Item.Name
            });

            if (Error != null)
            {
                await MessageBox.ShowAsync("Couldn't uninstall:\n" + Error, "Uninstall from PS4", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            await Model.RefreshAsync();
        }
    }
}
