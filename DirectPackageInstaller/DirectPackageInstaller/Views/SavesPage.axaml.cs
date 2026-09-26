using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DirectPackageInstaller.Services;
using DirectPackageInstaller.ViewModels;

namespace DirectPackageInstaller.Views
{
    /// <summary>
    /// Saves: every game's saved data on the console (all users) next to the backups in
    /// the backup folder. Back up per game or in bulk, restore a backup to any console
    /// user (after backing up what it overwrites), import and delete backups.
    /// </summary>
    public partial class SavesPage : UserControl
    {
        Func<ConsoleViewModel?> Console = () => null;
        ConsoleSaves.SaveScan? Scan;
        List<ConsoleSaves.SaveBackup> Backups = new();
        List<SaveGame> Games = new();
        bool Busy, Shown;

        public SavesPage()
        {
            InitializeComponent();
            NarrowLayout.Watch(this, Narrow => NarrowLayout.Set(SearchBox, WidthProperty, Narrow ? double.NaN : 260d, Narrow));

            BtnRead.Click += async (_, _) => await ReadConsoleAsync();
            BtnImport.Click += async (_, _) => await ImportAsync();
            BtnImport.IsVisible = App.IsDesktop;
            BtnFolder.Click += async (_, _) => await ChooseFolderAsync();
            BtnDefaultFolder.Click += (_, _) =>
            {
                App.Config.SaveBackupFolder = "";
                App.SaveSettings();
                LoadBackups();
            };
            BtnOpenFolder.IsVisible = App.IsDesktop;
            BtnOpenFolder.Click += (_, _) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ConsoleSaves.Folder) { UseShellExecute = true }); }
                catch { }
            };

            BtnSelectAll.Click += (_, _) =>
            {
                var Pickable = Visible().Where(x => x.HasConsole).ToList();
                bool All = Pickable.All(x => x.Selected);
                foreach (var Game in Pickable)
                    Game.Selected = !All;
            };
            BtnBackupSelected.Click += async (_, _) => await BackupAsync(Games.Where(x => x.Selected && x.HasConsole).ToList());

            SearchBox.TextChanged += (_, _) => ApplyFilter();
            foreach (var Chip in new[] { FilterAll, FilterConsole, FilterNotBacked, FilterBackups })
                Chip.IsCheckedChanged += (_, _) => ApplyFilter();

            BtnRestoreCancel.Click += (_, _) => { if (!Busy) RestorePanel.IsVisible = false; };
            BtnRestoreGo.Click += async (_, _) => await RestoreAsync();
            RestoreSafety.IsCheckedChanged += (_, _) => SyncRestore();

            ConsoleStatus.Instance.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(ConsoleStatus.FtpOpen) or nameof(ConsoleStatus.RunningTitleId))
                    Avalonia.Threading.Dispatcher.UIThread.Post(SyncButtons);
            };
            SyncButtons();
        }

        /// <summary>Names and covers come from the On PS4 page's titles, then the library.</summary>
        public void Attach(Func<ConsoleViewModel?> Console) => this.Console = Console;

        public async void OnShown()
        {
            LoadBackups();
            if (Shown)
                return;
            Shown = true;
            if (ConsoleStatus.Instance.FtpOpen)
                await ReadConsoleAsync();
        }

        /// <summary>From elsewhere (On PS4's right-click): this game's saves.</summary>
        public void ShowTitle(string TitleId)
        {
            FilterAll.IsChecked = true;
            SearchBox.Text = TitleId;
        }

        string Ip => App.Config.PSIP?.Trim() ?? "";
        bool Ftp => ConsoleStatus.Instance.FtpOpen && Ip.Length > 0;
        static string NoFtp => "Needs GoldHEN's FTP server running on the console (GoldHEN › Server Settings)";

        (string Name, string? Icon) Describe(string TitleId, string? Fallback = null)
        {
            var Model = Console();
            if (Model?.AllTitles.FirstOrDefault(x => string.Equals(x.TitleId, TitleId, StringComparison.OrdinalIgnoreCase)) is { } Title)
                return (Title.Name, Title.Title.IconFile);
            var Entry = Model?.LibraryItems().Where(x => string.Equals(x.TitleId, TitleId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Category == "gd" ? 0 : 1).FirstOrDefault();
            if (Entry != null && !string.IsNullOrWhiteSpace(Entry.Title))
                return (Entry.Title, Entry.IconFile);
            return (Fallback ?? TitleId, null);
        }

        // ----- building the list

        void LoadBackups()
        {
            Backups = ConsoleSaves.ListBackups();
            Rebuild();
        }

        void Rebuild()
        {
            var Old = Games.ToDictionary(x => x.TitleId, StringComparer.OrdinalIgnoreCase);
            var ByTitle = new Dictionary<string, SaveGame>(StringComparer.OrdinalIgnoreCase);

            SaveGame Get(string TitleId, string? Fallback)
            {
                if (ByTitle.TryGetValue(TitleId, out var Game))
                    return Game;
                var (Name, Icon) = Describe(TitleId, Fallback);
                Game = new SaveGame(TitleId, Name, Icon);
                if (Old.TryGetValue(TitleId, out var Before))
                {
                    Game.Expanded = Before.Expanded;
                    Game.Selected = Before.Selected;
                    Game.Status = Before.Status;
                }
                return ByTitle[TitleId] = Game;
            }

            foreach (var Title in Scan?.Titles ?? new())
                Get(Title.TitleId, null).OnConsole = Title;
            foreach (var Group in Backups.GroupBy(x => x.TitleId, StringComparer.OrdinalIgnoreCase))
            {
                var Game = Get(Group.Key, Group.Select(x => x.GameName).FirstOrDefault(x => !string.Equals(x, Group.Key, StringComparison.OrdinalIgnoreCase)));
                Game.Backups = Group.OrderByDescending(x => x.Created).Select(x => new SaveBackupItem(x)).ToList();
            }

            foreach (var Game in Games)
                Game.PropertyChanged -= GameChanged;
            // what needs attention first: saves not backed up, then by name
            Games = ByTitle.Values
                .OrderBy(x => x.HasConsole ? (x.UpToDate && x.HasBackups ? 1 : 0) : 2)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var Game in Games)
                Game.PropertyChanged += GameChanged;

            UpdateSummary();
            ApplyFilter();
        }

        void GameChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SaveGame.Selected))
                SyncButtons();
        }

        void UpdateSummary()
        {
            var Parts = new List<string>();
            if (Scan != null)
            {
                int Titles = Scan.Titles.Count;
                Parts.Add(Titles == 0 ? "No saves on the console" :
                    $"{Titles} {(Titles == 1 ? "game" : "games")} with saves on the console ({Host.TransferProgressInfo.FormatBytes(Scan.Titles.Sum(x => x.Size))}), {Scan.Users.Count} {(Scan.Users.Count == 1 ? "user" : "users")}");
                int Missing = Games.Count(x => x.HasConsole && !x.HasBackups);
                int Changed = Games.Count(x => x.HasConsole && x.HasBackups && !x.UpToDate);
                if (Missing > 0)
                    Parts.Add($"{Missing} not backed up");
                if (Changed > 0)
                    Parts.Add($"{Changed} changed since the last backup");
            }
            else
                Parts.Add("Read the console to see its saves");
            Parts.Add(Backups.Count == 1 ? "1 backup" : $"{Backups.Count} backups");
            SummaryText.Text = string.Join("  ·  ", Parts) + ".";
        }

        IEnumerable<SaveGame> Visible()
        {
            var Search = (SearchBox.Text ?? "").Trim();
            return Games.Where(x =>
                (Search.Length == 0 || x.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) || x.TitleId.Contains(Search, StringComparison.OrdinalIgnoreCase))
                && (FilterConsole.IsChecked != true || x.HasConsole)
                && (FilterNotBacked.IsChecked != true || x.HasConsole && (!x.HasBackups || !x.UpToDate))
                && (FilterBackups.IsChecked != true || !x.HasConsole));
        }

        void ApplyFilter()
        {
            var Shown_ = Visible().ToList();
            GameList.ItemsSource = Shown_;
            EmptyPanel.IsVisible = Shown_.Count == 0;
            if (Games.Count == 0)
            {
                EmptyTitle.Text = "Your saves, backed up";
                EmptyText.Text = Busy ? "Reading the console…"
                    : Scan != null ? "No saves on the console yet, and no backups in the backup folder."
                    : "Read the console to list every game's saves for every user. Back them up here, and restore them later to the same user or another one."
                      + (App.IsDesktop ? "" : " Save zips copied into the backup folder show up here too.");
            }
            else
            {
                EmptyTitle.Text = "Nothing matches";
                EmptyText.Text = "Try another search or filter.";
            }
            SyncButtons();
        }

        void SetStatus(string Text) => StatusText.Text = Text;

        void SyncButtons()
        {
            bool CanFtp = Ftp;
            var Running = ConsoleStatus.Instance.RunningTitleId;

            BtnRead.IsEnabled = CanFtp && !Busy;
            BtnRead.Content = Scan == null ? "Read the console" : "Read again";
            ToolTip.SetTip(BtnRead, Busy ? "Working…" : CanFtp ? "List every save on the console (read-only)" : NoFtp);
            BtnImport.IsEnabled = !Busy;

            FolderText.Text = ConsoleSaves.Folder;
            BtnFolder.IsEnabled = !Busy;
            BtnDefaultFolder.IsVisible = !string.IsNullOrWhiteSpace(App.Config.SaveBackupFolder);
            BtnDefaultFolder.IsEnabled = !Busy;
            ToolTip.SetTip(BtnDefaultFolder, "Back to " + ConsoleSaves.DefaultFolder);
            BtnOpenFolder.IsEnabled = Directory.Exists(ConsoleSaves.Folder);
            ToolTip.SetTip(BtnOpenFolder, BtnOpenFolder.IsEnabled ? "Open the backup folder" : "Nothing backed up there yet");

            var Pickable = Visible().Where(x => x.HasConsole).ToList();
            int Picked = Games.Count(x => x.Selected && x.HasConsole);
            BtnSelectAll.IsEnabled = !Busy && Pickable.Count > 0;
            BtnSelectAll.Content = Pickable.Count > 0 && Pickable.All(x => x.Selected) ? "Select none" : "Select all";
            ToolTip.SetTip(BtnSelectAll, Pickable.Count == 0 ? "No saves on the console listed here" : "Pick every game listed");
            BtnBackupSelected.IsEnabled = CanFtp && !Busy && Picked > 0;
            BtnBackupSelected.Content = Picked > 1 ? $"Back up {Picked} games" : "Back up selected";
            ToolTip.SetTip(BtnBackupSelected, Busy ? "Working…" : !CanFtp ? NoFtp
                : Picked == 0 ? "Tick the games to back up" : $"Zips their saves into {ConsoleSaves.Folder}");

            foreach (var Game in Games)
            {
                bool IsRunning = string.Equals(Game.TitleId, Running, StringComparison.OrdinalIgnoreCase);
                Game.CanBackup = CanFtp && !Busy && Game.HasConsole && !IsRunning;
                Game.BackupTip = Busy ? "Working…" : !CanFtp ? NoFtp
                    : IsRunning ? "It's running on the console: close it first, so its saves aren't copied half-written"
                    : $"Zip {Game.UsersText}'s saves into {ConsoleSaves.Folder}";
                foreach (var Backup in Game.Backups)
                    Backup.Sync(Busy, CanFtp && !IsRunning,
                        Busy ? "Working…" : !CanFtp ? NoFtp
                        : IsRunning ? "It's running on the console: close the game first"
                        : "Write this backup to a user on the console");
            }
        }

        // ----- console

        async Task ReadConsoleAsync()
        {
            if (Busy || !Ftp)
                return;
            Busy = true;
            SyncButtons();
            ApplyFilter();
            try
            {
                SetStatus("Reading the saves on the console…");
                Scan = await ConsoleSaves.ListAllAsync(Ip, new Progress<string>(SetStatus));
                SetStatus("");
            }
            catch (Exception ex)
            {
                SetStatus("Couldn't read the saves: " + ex.Message);
            }
            finally
            {
                Busy = false;
                Backups = ConsoleSaves.ListBackups();
                Rebuild();
            }
        }

        /// <summary>Re-read one game's saves after a backup or restore.</summary>
        async Task RefreshTitleAsync(string TitleId)
        {
            if (Scan == null)
                return;
            try
            {
                var Users = await ConsoleSaves.FindAsync(Ip, TitleId);
                Scan.Titles.RemoveAll(x => string.Equals(x.TitleId, TitleId, StringComparison.OrdinalIgnoreCase));
                if (Users.Count > 0)
                    Scan.Titles.Add(new ConsoleSaves.TitleSaves(TitleId, Users));
            }
            catch { /* the list shows what it read before */ }
        }

        async Task BackupAsync(List<SaveGame> Picked)
        {
            if (Busy || Picked.Count == 0 || !Ftp)
                return;

            var Running = ConsoleStatus.Instance.RunningTitleId;
            Busy = true;
            SyncButtons();
            int Done = 0;
            string LastDone = "";
            var Skipped = new List<string>();
            try
            {
                foreach (var Game in Picked)
                {
                    if (Game.OnConsole == null)
                        continue;
                    if (string.Equals(Game.TitleId, Running, StringComparison.OrdinalIgnoreCase))
                    {
                        Skipped.Add($"{Game.Name} (running)");
                        continue;
                    }
                    SetStatus(Picked.Count > 1 ? $"Backing up {Done + 1} of {Picked.Count}: {Game.Name}…" : $"Backing up {Game.Name}…");
                    Game.Status = "Backing up…";
                    try
                    {
                        await ConsoleSaves.BackupAsync(Ip, Game.TitleId, Game.Name, Game.OnConsole.Users,
                            new Progress<string>(Text => Game.Status = Text), IconFile: Game.IconPath);
                        Game.Status = "";
                        Game.Selected = false;
                        LastDone = Game.Name;
                        Done++;
                    }
                    catch (Exception ex)
                    {
                        Game.Status = "Couldn't back up: " + ex.Message;
                        Skipped.Add($"{Game.Name} (failed)");
                    }
                }
                SetStatus($"Backed up {Done} {(Done == 1 ? "game" : "games")} to {ConsoleSaves.Folder}."
                          + (Skipped.Count > 0 ? $" Not backed up: {string.Join(", ", Skipped)}." : ""));
                if (Done > 0)
                    Notices.Post("Saves backed up", Done == 1 ? LastDone : $"{Done} games");
            }
            finally
            {
                Busy = false;
                LoadBackups();
            }
        }

        void BackupClick(object? sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.Tag is SaveGame Game)
                _ = BackupAsync(new List<SaveGame> { Game });
        }

        void ToggleClick(object? sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.Tag is SaveGame Game)
                Game.Expanded = !Game.Expanded;
        }

        // ----- restore

        SaveBackupItem? Restoring;
        SaveGame? RestoringGame;

        async void RestoreClick(object? sender, RoutedEventArgs e)
        {
            if (Busy || (sender as Control)?.Tag is not SaveBackupItem Item)
                return;
            RestoringGame = Games.FirstOrDefault(x => x.Backups.Contains(Item));
            Restoring = Item;

            // the users to restore to come from the console
            if (Scan == null)
            {
                await ReadConsoleAsync();
                if (Scan == null)
                    return;
            }

            var B = Item.Backup;
            RestoreGame.Text = RestoringGame?.Name ?? B.GameName;
            RestoreFrom.Text = $"Backup of {B.UserName} from {Item.DateText}  ·  {B.Files} files, {Host.TransferProgressInfo.FormatBytes(B.Size)}";
            RestoreUsers.Children.Clear();
            foreach (var User in Scan.Users)
            {
                bool Same = string.Equals(User.Id, B.UserId, StringComparison.OrdinalIgnoreCase);
                var HasSaves = Scan.Titles.FirstOrDefault(x => string.Equals(x.TitleId, B.TitleId, StringComparison.OrdinalIgnoreCase))?
                    .Users.FirstOrDefault(x => string.Equals(x.UserId, User.Id, StringComparison.OrdinalIgnoreCase));
                var Option = new RadioButton
                {
                    GroupName = "restoreUser",
                    Tag = User,
                    IsChecked = Same,
                    Content = new TextBlock
                    {
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Text = $"{User.Name}  ·  {User.Id}" + (Same ? "  ·  the backup's user" : "")
                               + (HasSaves != null ? $"  ·  has saves ({HasSaves.Slots} {(HasSaves.Slots == 1 ? "slot" : "slots")})" : "  ·  no saves for this game")
                    }
                };
                Option.IsCheckedChanged += (_, _) => SyncRestore();
                RestoreUsers.Children.Add(Option);
            }
            if (RestoreUsers.Children.OfType<RadioButton>().All(x => x.IsChecked != true) && RestoreUsers.Children.Count == 1)
                ((RadioButton)RestoreUsers.Children[0]).IsChecked = true;

            RestoreSafety.IsChecked = true;
            RestoreStatus.IsVisible = false;
            RestorePanel.IsVisible = true;
            SyncRestore();
        }

        ConsoleSaves.ConsoleUser? TargetUser => RestoreUsers.Children.OfType<RadioButton>().FirstOrDefault(x => x.IsChecked == true)?.Tag as ConsoleSaves.ConsoleUser;

        void SyncRestore()
        {
            var Target = TargetUser;
            var Source = Restoring?.Backup;
            bool Other = Target != null && Source != null && !string.Equals(Target.Id, Source.UserId, StringComparison.OrdinalIgnoreCase);
            bool UnknownUser = Source != null && Scan?.Users.All(x => !string.Equals(x.Id, Source.UserId, StringComparison.OrdinalIgnoreCase)) == true;
            RestoreWarning.IsVisible = Other || UnknownUser;
            RestoreWarning.Text = UnknownUser
                ? $"This backup's user ({Source!.UserId}) isn't on this console: it may come from another console or a deleted user. The game may refuse it unless it's resigned with Apollo Save Tool."
                : Other ? $"This backup belongs to {Source!.UserName}. Games often refuse another account's saves; if it doesn't load, resign it with Apollo Save Tool." : "";

            bool Running = Source != null && string.Equals(Source.TitleId, ConsoleStatus.Instance.RunningTitleId, StringComparison.OrdinalIgnoreCase);
            BtnRestoreGo.IsEnabled = !Busy && Target != null && Ftp && !Running;
            ToolTip.SetTip(BtnRestoreGo, Busy ? "Restoring…" : !Ftp ? NoFtp : Running ? "The game is running on the console: close it first"
                : Target == null ? "Pick the user to restore to" : $"Write the saves to {Target.Name}");
            BtnRestoreCancel.IsEnabled = !Busy;
            RestoreUsers.IsEnabled = !Busy;
            RestoreSafety.IsEnabled = !Busy;
        }

        void SetRestoreStatus(string Text)
        {
            RestoreStatus.Text = Text;
            RestoreStatus.IsVisible = Text.Length > 0;
        }

        async Task RestoreAsync()
        {
            if (Busy || Restoring == null || TargetUser is not { } Target)
                return;
            var Backup = Restoring.Backup;
            var Game = RestoringGame;

            Busy = true;
            SyncButtons();
            SyncRestore();
            bool Done = false;
            try
            {
                // a game writing its saves while they're replaced would corrupt them
                SetRestoreStatus("Checking the game isn't running…");
                if (await ConsoleScanner.RunningAppAsync(Ip) is { } App_ && string.Equals(App_.TitleId, Backup.TitleId, StringComparison.OrdinalIgnoreCase))
                {
                    SetRestoreStatus("The game is running on the console. Close it, then restore.");
                    return;
                }

                if (RestoreSafety.IsChecked == true)
                {
                    SetRestoreStatus($"Backing up {Target.Name}'s current saves first…");
                    var Current = await ConsoleSaves.FindAsync(Ip, Backup.TitleId, OnlyUser: Target.Id);
                    if (Current.Count > 0)
                        await ConsoleSaves.BackupAsync(Ip, Backup.TitleId, Game?.Name ?? Backup.GameName, Current,
                            new Progress<string>(SetRestoreStatus), Tag: ConsoleSaves.BeforeRestoreTag, IconFile: Game?.IconPath);
                }

                await ConsoleSaves.RestoreAsync(Ip, Backup, Target.Id, new Progress<string>(SetRestoreStatus));
                Done = true;
                await RefreshTitleAsync(Backup.TitleId);
                RestorePanel.IsVisible = false;
                var Name = Game?.Name ?? Backup.GameName;
                SetStatus($"Restored {Name}'s saves from {Restoring.DateText} to {Target.Name}.");
                Notices.Post("Saves restored", $"{Name} → {Target.Name}");
            }
            catch (Exception ex)
            {
                SetRestoreStatus((Done ? "Restored, but " : "Couldn't restore: ") + ex.Message);
            }
            finally
            {
                Busy = false;
                LoadBackups();
                SyncRestore();
            }
        }

        // ----- backup files

        async void DeleteClick(object? sender, RoutedEventArgs e)
        {
            if (Busy || (sender as Control)?.Tag is not SaveBackupItem Item)
                return;
            var Where = LibraryTidy.CanRecycle ? (OperatingSystem.IsWindows() ? "the Recycle Bin" : "the Trash") : "nowhere: it's deleted for good";
            var Reply = await MessageBox.ShowAsync($"Delete the backup of {Item.Backup.UserName} from {Item.DateText}?\n\nIt goes to {Where}.",
                "Delete backup", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (Reply != DialogResult.Yes)
                return;
            try
            {
                ConsoleSaves.Delete(Item.Backup);
            }
            catch (Exception ex)
            {
                SetStatus("Couldn't delete it: " + ex.Message);
            }
            LoadBackups();
        }

        async Task ImportAsync()
        {
            if (Busy || TopLevel.GetTopLevel(this) is not { } Top)
                return;
            var Files = await Top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import save backups",
                AllowMultiple = true,
                FileTypeFilter = new[] { new FilePickerFileType("Save backup (.zip)") { Patterns = new[] { "*.zip" } } }
            });
            int Added = 0;
            var Failed = new List<string>();
            foreach (var File in Files)
            {
                var Path_ = File.TryGetLocalPath();
                if (Path_ == null)
                    continue;
                try
                {
                    ConsoleSaves.Import(Path_, Tid => Describe(Tid).Name);
                    Added++;
                }
                catch (Exception ex)
                {
                    Failed.Add($"{System.IO.Path.GetFileName(Path_)}: {ex.Message}");
                }
            }
            if (Added + Failed.Count == 0)
                return;
            SetStatus((Added > 0 ? $"Imported {Added} {(Added == 1 ? "backup" : "backups")}. " : "") + string.Join(" ", Failed));
            LoadBackups();
        }

        async Task ChooseFolderAsync()
        {
            string? Folder = null;
            if (App.IsSingleView)
            {
                var Picker = new FilePicker { FolderMode = true };
                await Picker.OpenDir(App.RootDir);
                await SingleView.CallView(Picker, false);
                Folder = Picker.SelectedFiles.FirstOrDefault();
            }
            else if (TopLevel.GetTopLevel(this) is { } Top)
            {
                var Picked = await Top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Choose where save backups go"
                });
                Folder = Picked.FirstOrDefault()?.TryGetLocalPath();
            }
            if (string.IsNullOrWhiteSpace(Folder))
                return;

            App.Config.SaveBackupFolder = Folder;
            App.SaveSettings();
            LoadBackups();
        }
    }
}
