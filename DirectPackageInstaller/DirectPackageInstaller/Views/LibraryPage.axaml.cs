using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DirectPackageInstaller.Services;
using DirectPackageInstaller.ViewModels;

namespace DirectPackageInstaller.Views
{
    public partial class LibraryPage : UserControl
    {
        LibraryViewModel Model => (LibraryViewModel)DataContext!;

        public LibraryPage()
        {
            InitializeComponent();
            DataContext = new LibraryViewModel();

            BtnAddFolder.Click += AddFolderClick;
            BtnAddFolderEmpty.Click += AddFolderClick;
            BtnRescan.Click += async (_, _) => await Model.ScanAsync();
            BtnCheck.Click += async (_, _) => await Model.CheckConsoleAsync();
            BtnClearSelection.Click += (_, _) => Model.Selected.Clear();
            BtnSelectFamily.Click += (_, _) => SelectFamilies();
            BtnSend.Click += (_, _) => Model.SendSelected();

            FilterAll.IsCheckedChanged += (_, _) => { if (FilterAll.IsChecked == true) Model.Filter = LibraryFilter.All; };
            FilterGames.IsCheckedChanged += (_, _) => { if (FilterGames.IsChecked == true) Model.Filter = LibraryFilter.Games; };
            FilterUpdates.IsCheckedChanged += (_, _) => { if (FilterUpdates.IsChecked == true) Model.Filter = LibraryFilter.Updates; };
            FilterDlc.IsCheckedChanged += (_, _) => { if (FilterDlc.IsChecked == true) Model.Filter = LibraryFilter.DLC; };

            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);
            KeyDown += OnKeyDown;
        }

        /// <summary>Rescan on first show so new files appear without a click.</summary>
        bool Scanned;
        public async void OnShown()
        {
            if (Scanned)
                return;
            Scanned = true;

            if (Model.HasFolders)
                await Model.ScanAsync();
        }

        public async void AutoCheck() => await Model.AutoCheckAsync();

        async void AddFolderClick(object? sender, RoutedEventArgs e)
        {
            var Top = TopLevel.GetTopLevel(this);
            if (Top == null)
                return;

            var Folders = await Top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose a folder with PKG files",
                AllowMultiple = true
            });

            foreach (var Folder in Folders)
            {
                var Path = Folder.TryGetLocalPath();
                if (Path != null)
                    await Model.AddFolderAsync(Path);
            }
        }

        async void RemoveFolderClick(object? sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.Tag is string Folder)
                await Model.RemoveFolderAsync(Folder);
        }

        /// <summary>Selected games pull in their updates and DLC (same title ID).</summary>
        void SelectFamilies()
        {
            var TitleIds = Model.Selected.Where(x => x.Entry.Kind == "Game").Select(x => x.Entry.TitleId).ToHashSet();
            foreach (var Item in Model.Items.Where(x => TitleIds.Contains(x.Entry.TitleId) && !Model.Selected.Contains(x)).ToList())
                Model.Selected.Add(Item);
        }

        void OnDragOver(object? sender, DragEventArgs e)
        {
            e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        /// <summary>Dropped folders join the library; dropped .pkg files go straight to the queue.</summary>
        async void OnDrop(object? sender, DragEventArgs e)
        {
            var Paths = e.Data.GetFiles()?.Select(x => x.TryGetLocalPath()).Where(x => x != null).Cast<string>().ToList();
            if (Paths == null || Paths.Count == 0)
                return;

            foreach (var Folder in Paths.Where(Directory.Exists))
                await Model.AddFolderAsync(Folder);

            var Packages = Paths.Where(x => File.Exists(x) && x.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase)).ToList();
            if (Packages.Count > 0)
            {
                var Known = Model.Items.Select(x => x.Entry).ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
                SendQueue.Instance.Enqueue(Packages.Select(x => Known.TryGetValue(x, out var Entry) ? Entry : new LibraryEntry
                {
                    Path = x,
                    Title = Path.GetFileNameWithoutExtension(x),
                    Size = new FileInfo(x).Length
                }));
            }
        }

        void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && Model.HasSelection)
            {
                Model.Selected.Clear();
                e.Handled = true;
            }
        }
    }
}
