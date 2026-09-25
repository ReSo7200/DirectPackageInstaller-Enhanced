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
            SortBox.SelectionChanged += (_, _) => Model.Sort = (LibrarySort)Math.Max(0, SortBox.SelectedIndex);

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

        static LibraryItem? ItemOf(object? Sender) => (Sender as Control)?.Tag as LibraryItem;

        void MenuSendClick(object? sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is { HasError: false } Item)
                SendQueue.Instance.Enqueue(new[] { Item.Entry });
        }

        async void MenuSendMissingClick(object? sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is not { } Item || string.IsNullOrEmpty(Item.Entry.TitleId))
                return;

            if (Model.SendMissing(Item.Entry.TitleId) == 0)
                await MessageBox.ShowAsync($"{Item.Entry.Title}: nothing to send. The last PS4 check found everything in your library for this title already installed.",
                    "Send what's missing", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>Download the latest official update from Sony next to the title's newest PKG.</summary>
        async void MenuDownloadUpdateClick(object? sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is not { } Item || string.IsNullOrEmpty(Item.Entry.TitleId))
                return;

            var Patch = await UpdateDownloads.LatestAsync(Item.Entry.TitleId);
            if (Patch == null)
            {
                await MessageBox.ShowAsync("Couldn't get the update from Sony's server. Check your internet connection and try again.",
                    "Download official update", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var Folder = Path.GetDirectoryName(Item.Entry.Path) ?? "";
            var Reply = await MessageBox.ShowAsync(
                $"Download {Item.Entry.Title} update {Patch.Version} from PlayStation Network?\n\n" +
                $"Size: {Host.TransferProgressInfo.FormatBytes(Patch.Size)}\n" +
                (Patch.SystemVersion.Length > 0 ? $"Needs console firmware {Patch.SystemVersion} or newer (or a backported base game).\n" : "") +
                $"Saved to: {Folder}\n\nOfficial updates only install over a matching base game.",
                "Download official update", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (Reply != DialogResult.Yes)
                return;

            UpdateDownloads.Instance.Start(Patch, Item.Entry.Title, Folder);
            MainWindow.Instance?.ShowQueue();
        }

        void MenuInspectClick(object? sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is { } Item)
                MainWindow.Instance?.OpenInDirectLink(Item.Entry.Path);
        }

        void MenuRevealClick(object? sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is not { } Item)
                return;

            try
            {
                if (App.IsWindows)
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{Item.Entry.Path}\"");
                else
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.GetDirectoryName(Item.Entry.Path)!) { UseShellExecute = true });
            }
            catch
            {
            }
        }

        /// <summary>Uninstall through RPI after an explicit confirmation, then re-check the console.</summary>
        async void MenuUninstallClick(object? sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is not { } Item || string.IsNullOrWhiteSpace(App.Config.PSIP))
                return;

            var What = Item.Entry.Kind switch
            {
                "Game" => $"the game {Item.Entry.Title} ({Item.Entry.TitleId})",
                "Update" => $"the installed update of {Item.Entry.Title} ({Item.Entry.TitleId})",
                _ => $"the DLC {Item.Entry.Title} ({Item.Entry.ContentId})"
            };

            var Reply = await MessageBox.ShowAsync(
                $"Uninstall {What} from the console?\n\nIt has to be downloaded again to play. Saved data is kept.",
                "Uninstall from PS4", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (Reply != DialogResult.Yes)
                return;

            var Error = await ConsoleActions.UninstallAsync(App.Config.PSIP, Item.Entry);
            if (Error != null)
            {
                await MessageBox.ShowAsync("Couldn't uninstall:\n" + Error, "Uninstall from PS4", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            await Model.CheckConsoleAsync();
        }

        async void MenuRenameClick(object? sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is not { } Item)
                return;

            var NewName = LibraryTidy.StandardName(Item.Entry);
            var Reply = await MessageBox.ShowAsync(
                $"Rename\n{Path.GetFileName(Item.Entry.Path)}\n\nto\n{NewName}\n\n(same folder; only the name changes)",
                "Rename package", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (Reply != DialogResult.Yes)
                return;

            try
            {
                LibraryTidy.Rename(Item.Entry, NewName);
            }
            catch (Exception ex)
            {
                await MessageBox.ShowAsync("Couldn't rename: " + ex.Message, "Rename package", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            await Model.ScanAsync();
        }

        async void MenuRecycleClick(object? sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is not { } Item)
                return;

            var Reply = await MessageBox.ShowAsync(
                $"Move this copy to the Recycle Bin?\n\n{Item.Entry.Path}\n\nThe other copy stays in the library. You can restore it from the Recycle Bin.",
                "Remove duplicate", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (Reply != DialogResult.Yes)
                return;

            try
            {
                LibraryTidy.Recycle(Item.Entry);
            }
            catch (Exception ex)
            {
                await MessageBox.ShowAsync(ex.Message, "Remove duplicate", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            await Model.ScanAsync();
        }

        async void MenuOrganizeClick(object? sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.Tag is not string Root || !Directory.Exists(Root))
                return;

            var Moves = LibraryTidy.PlanOrganize(Root, Model.Entries);
            if (Moves.Count == 0)
            {
                await MessageBox.ShowAsync("Nothing to organize: every package in this folder is already in a subfolder.",
                    "Organize into game folders", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var Folders = Moves.Select(x => Path.GetFileName(Path.GetDirectoryName(x.To)!)).Distinct().ToList();
            var Preview = string.Join("\n", Folders.Take(12).Select(x => "  " + x)) + (Folders.Count > 12 ? $"\n  … and {Folders.Count - 12} more" : "");
            var Reply = await MessageBox.ShowAsync(
                $"Move {Moves.Count} files in\n{Root}\ninto {Folders.Count} game folders?\n\n{Preview}\n\nFiles only move within this folder; nothing is deleted.",
                "Organize into game folders", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (Reply != DialogResult.Yes)
                return;

            var (Moved, Failed) = LibraryTidy.Apply(Moves);
            await Model.ScanAsync();
            if (Failed.Count > 0)
                await MessageBox.ShowAsync($"Moved {Moved} files. These couldn't be moved:\n" + string.Join("\n", Failed.Take(10)),
                    "Organize into game folders", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        void MenuOpenFolderClick(object? sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.Tag is string Folder && Directory.Exists(Folder))
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Folder) { UseShellExecute = true }); }
                catch { }
            }
        }

        async void MenuCopyClick(object? sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is { } Item && TopLevel.GetTopLevel(this)?.Clipboard is { } Clipboard)
                await Clipboard.SetTextAsync(Item.Entry.Path);
        }

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
