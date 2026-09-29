using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DirectPackageInstaller.Host;
using DirectPackageInstaller.Services;
using DirectPackageInstaller.Tasks;
using DirectPackageInstaller.ViewModels;

namespace DirectPackageInstaller.Views
{
    /// <summary>
    /// Updates: search any PS4 game on orbispatches.com for its full update history, or read the
    /// console and see which installed games are behind the latest update. Download the latest
    /// straight from PlayStation Network (into the Queue), or open any version on the site.
    /// </summary>
    public partial class UpdatesPage : UserControl
    {
        Func<ConsoleViewModel?> Console = () => null;
        CancellationTokenSource? Work;
        UpdateGame? Current;
        List<InstalledUpdate> Installed = new();
        bool Busy, ConsoleChecked;

        public UpdatesPage()
        {
            InitializeComponent();
            NarrowLayout.Watch(this, Narrow => NarrowLayout.Set(SearchBox, WidthProperty, Narrow ? double.NaN : 320d, Narrow));

            BtnSearch.Click += async (_, _) => await SearchAsync();
            SearchBox.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await SearchAsync(); } };
            BtnBack.Click += (_, _) => CloseDetail();
            BtnSite.Click += (_, _) => Open(OrbisPatches.Site);

            ModeSearch.IsCheckedChanged += (_, _) => SetMode();
            ModeConsole.IsCheckedChanged += (_, _) => SetMode();
            BtnReadConsole.Click += async (_, _) => await ReadConsoleAsync();
            OnlyNeedsUpdate.IsCheckedChanged += (_, _) => ApplyConsoleFilter();

            BtnFolder.Click += async (_, _) => await ChooseFolderAsync();
            BtnOpenFolder.IsVisible = App.IsDesktop;
            BtnOpenFolder.Click += (_, _) => OpenFolder(DisplayFolder);
            BtnDefaultFolder.Click += (_, _) =>
            {
                App.Config.UpdateFolder = "";
                App.SaveSettings();
                RefreshFolder();
            };

            ConsoleStatus.Instance.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ConsoleStatus.FtpOpen))
                    Avalonia.Threading.Dispatcher.UIThread.Post(SyncConsoleButtons);
            };

            RefreshFolder();
            SetMode();
        }

        /// <summary>Reuse the On PS4 page's already-read titles (and its cached covers) when it has them.</summary>
        public void Attach(Func<ConsoleViewModel?> Console) => this.Console = Console;

        public void OnShown() { SyncConsoleButtons(); RefreshFolder(); }

        string Ip => App.Config.PSIP?.Trim() ?? "";
        bool Ftp => ConsoleStatus.Instance.FtpOpen && Ip.Length > 0;
        static string NoFtp => "Needs GoldHEN's FTP server running on the console (GoldHEN › Server Settings)";

        bool ConsoleMode => ModeConsole.IsChecked == true;

        // ----- where updates are saved (a folder in the PKG library)

        static string Fallback => App.IsAndroid
            ? "/storage/emulated/0/Download/DPI Updates"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DPI Updates");

        IReadOnlyList<LibraryEntry> Library => Console()?.LibraryItems() ?? LibraryService.Cached;

        /// <summary>A configured folder, else a PKG library folder (one with "pkg" in its path wins), else the fallback.</summary>
        string DefaultFolder
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(App.Config.UpdateFolder))
                    return App.Config.UpdateFolder!;
                var Folders = LibraryService.Folders;
                if (Folders.Count > 0)
                    return Folders.FirstOrDefault(f => f.Replace('\\', '/').ToLowerInvariant().Contains("pkg")) ?? Folders[0];
                return Fallback;
            }
        }

        /// <summary>What the folder card shows.</summary>
        string DisplayFolder => DefaultFolder;

        /// <summary>Where this title's update is saved: next to its base game when it's in the library, else the default folder.</summary>
        string TargetFolderFor(string TitleId)
        {
            if (string.IsNullOrWhiteSpace(App.Config.UpdateFolder))
            {
                var Base = Library.FirstOrDefault(e => e.Error == null && e.Kind == "Game"
                    && string.Equals(e.TitleId, TitleId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(e.Path));
                if (Base != null && Path.GetDirectoryName(Base.Path) is { Length: > 0 } Dir)
                    return Dir;
            }
            return DefaultFolder;
        }

        void RefreshFolder()
        {
            FolderText.Text = DisplayFolder;
            BtnDefaultFolder.IsVisible = !string.IsNullOrWhiteSpace(App.Config.UpdateFolder);
            BtnOpenFolder.IsEnabled = App.IsDesktop && Directory.Exists(DisplayFolder);
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
                var Picked = await Top.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = "Choose the PKG library folder to save updates into"
                });
                Folder = Picked.FirstOrDefault()?.TryGetLocalPath();
            }
            if (string.IsNullOrWhiteSpace(Folder))
                return;
            App.Config.UpdateFolder = Folder;
            App.SaveSettings();
            RefreshFolder();
        }

        static void OpenFolder(string Folder)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Folder) { UseShellExecute = true });
            }
            catch { }
        }

        // ----- view state

        void SetMode()
        {
            CloseDetailState();
            SearchBar.IsVisible = !ConsoleMode;
            ConsoleBar.IsVisible = ConsoleMode;
            SummaryText.Text = ConsoleMode
                ? "Read the console and see which installed games have a newer update on orbispatches.com."
                : "Search any PS4 game's official update history, from orbispatches.com.";
            if (ConsoleMode)
            {
                SyncConsoleButtons();
                ShowConsole();
            }
            else
            {
                ShowResults();
            }
        }

        void ShowConsole()
        {
            bool Has = Installed.Count > 0;
            ConsoleScroll.IsVisible = Has;
            ResultsScroll.IsVisible = false;
            DetailScroll.IsVisible = false;
            EmptyPanel.IsVisible = !Has;
            BtnBack.IsVisible = false;
            if (!Has)
            {
                EmptyTitle.Text = ConsoleChecked ? "Nothing installed" : "See what needs updating";
                EmptyText.Text = ConsoleChecked
                    ? "The console didn't report any installed games."
                    : "Read the console to list its games, then check each against orbispatches.com for a newer update.";
            }
        }

        void CloseDetailState()
        {
            Current = null;
            DetailScroll.IsVisible = false;
        }

        void CloseDetail()
        {
            CloseDetailState();
            if (ConsoleMode) ShowConsole(); else ShowResults();
        }

        // ----- search (name / CUSA)

        async Task SearchAsync()
        {
            var Term = (SearchBox.Text ?? "").Trim();
            if (Term.Length < 2 || Busy)
                return;

            Work?.Cancel();
            Work = new CancellationTokenSource();
            var Token = Work.Token;

            if (OrbisPatches.IsTitleId(Term))
            {
                await OpenGameAsync(new UpdateGame(new OrbisPatches.Game(Term.ToUpperInvariant(), Term.ToUpperInvariant(), "", null)), Token);
                return;
            }

            Busy = true;
            SetBusy("Searching…");
            try
            {
                var Games = await OrbisPatches.SearchAsync(Term, Token);
                if (Token.IsCancellationRequested)
                    return;
                ResultsList.ItemsSource = Games.Select(x => new UpdateGame(x)).ToList();
                ShowResults();
                if (Games.Count == 0)
                {
                    EmptyTitle.Text = "No games found";
                    EmptyText.Text = $"Nothing on orbispatches.com matches “{Term}”. Try another name, or the CUSA ID.";
                    EmptyPanel.IsVisible = true;
                    ResultsScroll.IsVisible = false;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { ShowError("Couldn't search orbispatches.com: " + ex.Message); }
            finally { Busy = false; }
        }

        void GameClick(object? sender, PointerPressedEventArgs e)
        {
            if ((sender as Control)?.Tag is UpdateGame Game && !Busy)
            {
                Work?.Cancel();
                Work = new CancellationTokenSource();
                _ = OpenGameAsync(Game, Work.Token);
            }
        }

        // ----- installed on the console

        async Task ReadConsoleAsync()
        {
            if (Busy || !Ftp)
                return;
            Busy = true;
            SyncConsoleButtons();
            SetConsoleStatus("Reading the console…");
            try
            {
                var Titles = await LoadTitlesAsync(CancellationToken.None);
                ConsoleChecked = true;
                if (Titles == null)
                {
                    SetConsoleStatus("Couldn't read the console" + (ConsoleTitles.LastError is { } E ? ": " + E : "") + ".");
                    Installed = new();
                    ShowConsole();
                    return;
                }

                Installed = Titles
                    .Where(x => x.TitleId.Length > 0)
                    .OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new InstalledUpdate(x)).ToList();
                ApplyConsoleFilter();
                ShowConsole();
                await CheckUpdatesAsync();
            }
            catch (Exception ex)
            {
                SetConsoleStatus("Couldn't read the console: " + ex.Message);
            }
            finally
            {
                Busy = false;
                SyncConsoleButtons();
            }
        }

        /// <summary>The On PS4 page's titles when it has them, else a fresh FTP read.</summary>
        async Task<List<InstalledTitle>?> LoadTitlesAsync(CancellationToken Token)
        {
            if (Console() is { Loaded: true } Vm && Vm.AllTitles.Count > 0)
                return Vm.AllTitles.Select(x => x.Title).ToList();
            return await ConsoleTitles.QueryAsync(Ip, new Progress<string>(SetConsoleStatus), Token);
        }

        /// <summary>Look each installed title up on orbispatches (limited concurrency) and mark the ones behind.</summary>
        async Task CheckUpdatesAsync()
        {
            var Items = Installed.ToList();
            if (Items.Count == 0)
                return;

            int Done = 0;
            SetConsoleStatus($"Checking {Items.Count} {(Items.Count == 1 ? "game" : "games")} for updates…");
            await Parallel.ForEachAsync(Items, new ParallelOptions { MaxDegreeOfParallelism = 5 }, async (Item, ct) =>
            {
                bool Failed = false;
                OrbisPatches.Patch? Latest = null;
                try { Latest = await OrbisPatches.LatestPatchAsync(Item.TitleId, ct); }
                catch { Failed = true; }
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    Item.Resolve(Latest, Failed);
                    int N = Interlocked.Increment(ref Done);
                    if (N == Items.Count)
                        FinishCheck();
                    else if (N % 3 == 0)
                        SetConsoleStatus($"Checking… {N} of {Items.Count}");
                });
            });
        }

        void FinishCheck()
        {
            int Behind = Installed.Count(x => x.NeedsUpdate);
            int Unknown = Installed.Count(x => x.Status == UpdateStatus.Unknown);
            SetConsoleStatus(Behind == 0
                ? $"All {Installed.Count} checked — everything's up to date."
                    + (Unknown > 0 ? $" ({Unknown} couldn't be checked.)" : "")
                : $"{Behind} of {Installed.Count} {(Behind == 1 ? "game needs" : "games need")} an update."
                    + (Unknown > 0 ? $" {Unknown} couldn't be checked." : ""));
            ApplyConsoleFilter();
        }

        void ApplyConsoleFilter()
        {
            var Shown = OnlyNeedsUpdate.IsChecked == true ? Installed.Where(x => x.NeedsUpdate).ToList() : Installed;
            ConsoleList.ItemsSource = Shown is List<InstalledUpdate> L ? L : Shown.ToList();
            if (ConsoleMode)
                ShowConsole();
        }

        async void SaveInstalledClick(object? sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.Tag is InstalledUpdate Item)
                await GetAsync(Item.TitleId, Item.Name, null, Send: false);
        }

        async void SendInstalledClick(object? sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.Tag is InstalledUpdate Item)
                await GetAsync(Item.TitleId, Item.Name, null, Send: true);
        }

        void InstalledDetailsClick(object? sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.Tag is not InstalledUpdate Item)
                return;
            Work?.Cancel();
            Work = new CancellationTokenSource();
            var Game = new UpdateGame(new OrbisPatches.Game(Item.TitleId, Item.Name, "", Item.Title.IconFile));
            _ = OpenGameAsync(Game, Work.Token);
        }

        // ----- one game's patch history

        async Task OpenGameAsync(UpdateGame Game, CancellationToken Token)
        {
            Current = Game;
            Busy = true;
            DetailName.Text = Game.Name;
            DetailMeta.Text = Game.Meta;
            PatchList.ItemsSource = null;
            SetDetailStatus("Loading updates…");
            ShowDetail();
            try
            {
                var Patches = await OrbisPatches.LoadPatchesAsync(Game.TitleId, Token);
                if (Token.IsCancellationRequested)
                    return;

                PatchList.ItemsSource = Patches.Select(x => new UpdatePatch(x)).ToList();
                if (Patches.Count == 0)
                    SetDetailStatus("No updates found for this title on orbispatches.com.");
                else
                {
                    var Latest = Patches.FirstOrDefault(x => x.IsLatest) ?? Patches[0];
                    SetDetailStatus($"{Patches.Count} {(Patches.Count == 1 ? "update" : "updates")}  ·  latest {Latest.Version}"
                        + (Latest.FirmwareText.Length > 0 ? $" needs {Latest.FirmwareText}" : ""));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { SetDetailStatus("Couldn't load the updates: " + ex.Message); }
            finally { Busy = false; }
        }

        // ----- save (to the library) / send (to the console) / open

        async void SavePatchClick(object? sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.Tag is not UpdatePatch Item || Current is not { } Game || Item.Busy)
                return;
            Item.Busy = true;
            try { await GetAsync(Game.TitleId, Game.Name, Item.Patch.Version, Send: false); }
            finally { Item.Busy = false; }
        }

        async void SendPatchClick(object? sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.Tag is not UpdatePatch Item || Current is not { } Game || Item.Busy)
                return;
            Item.Busy = true;
            try { await GetAsync(Game.TitleId, Game.Name, Item.Patch.Version, Send: true); }
            finally { Item.Busy = false; }
        }

        /// <summary>
        /// Download the latest update from Sony into the PKG library folder. When <paramref name="Send"/>,
        /// enqueue it to the console (like the Library's Send) once the download finishes. If Sony has no
        /// direct download, offer to open the version on orbispatches.com instead.
        /// </summary>
        async Task GetAsync(string TitleId, string Name, string? WantedVersion, bool Send)
        {
            try
            {
                var Patch = await UpdateDownloads.LatestAsync(TitleId);
                if (Patch == null)
                {
                    var Reply = await MessageBox.ShowAsync(
                        $"PlayStation Network doesn't have {Name}'s update for direct download (Sony may have replaced or pulled it). " +
                        "Open it on orbispatches.com to download it in your browser?",
                        "Update", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (Reply == DialogResult.Yes)
                        Open(WantedVersion != null ? VersionPage(TitleId, WantedVersion) : OrbisPatches.PageFor(TitleId));
                    return;
                }

                var Folder = TargetFolderFor(TitleId);
                var Firmware = Patch.SystemVersion.Length > 0 ? $"Needs console firmware {Patch.SystemVersion} or newer.\n" : "";
                var Confirm = await MessageBox.ShowAsync(
                    (Send
                        ? $"Download {Name} update {Patch.Version} from PlayStation Network and send it to the console?\n\n"
                        : $"Download {Name} update {Patch.Version} from PlayStation Network into your library?\n\n") +
                    $"Size: {TransferProgressInfo.FormatBytes(Patch.Size)}\n" + Firmware +
                    $"Saved to: {Folder}\n\n" +
                    (Send ? "It's sent to the console once the download finishes. " : "") +
                    "Updates only install over a matching base game.",
                    Send ? "Send update to PS4" : "Save update", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (Confirm != DialogResult.Yes)
                    return;

                var Download = UpdateDownloads.Instance.Start(Patch, Name, Folder);
                if (Send)
                    SendWhenDone(Download);
                AppShell.Current?.ShowQueue();
            }
            catch (Exception ex)
            {
                await MessageBox.ShowAsync("Couldn't start the download: " + ex.Message, "Update", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>When this download finishes, read the update PKG it saved and enqueue it to the console.</summary>
        void SendWhenDone(UpdateDownload Download)
        {
            void Handler(UpdateDownload Done)
            {
                if (Done != Download)
                    return;
                UpdateDownloads.Instance.Finished -= Handler;
                if (Done.State != DownloadState.Done)
                    return;
                _ = Task.Run(() =>
                {
                    try
                    {
                        // the update landed as one or more PKGs in the folder; enqueue the update entry
                        var Entries = Directory.EnumerateFiles(Done.Folder, "*.pkg")
                            .Select(LibraryService.ReadPackage)
                            .OfType<LibraryEntry>()
                            .Where(x => x.Error == null && string.Equals(x.TitleId, Done.Patch.TitleId, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        var Entry = Entries.FirstOrDefault(x => x.Kind == "Update") ?? Entries.FirstOrDefault();
                        if (Entry != null)
                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            {
                                SendQueue.Instance.Enqueue(new[] { Entry });
                                AppShell.Current?.ShowQueue();
                            });
                    }
                    catch { }
                });
            }
            UpdateDownloads.Instance.Finished += Handler;
        }

        void OpenClick(object? sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.Tag is UpdatePatch Item && Current is { } Game)
                Open(VersionPage(Game.TitleId, Item.Patch.Version));
        }

        static string VersionPage(string TitleId, string Version) => $"{OrbisPatches.PageFor(TitleId)}?v={Uri.EscapeDataString(Version)}";

        async void Open(string Url)
        {
            try
            {
                if (TopLevel.GetTopLevel(this)?.Launcher is { } Launcher)
                    await Launcher.LaunchUriAsync(new Uri(Url));
                else if (App.IsDesktop)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Url) { UseShellExecute = true });
            }
            catch { }
        }

        // ----- shared view helpers

        void ShowResults()
        {
            var Has = (ResultsList.ItemsSource as System.Collections.IEnumerable)?.Cast<object>().Any() == true;
            ResultsScroll.IsVisible = Has;
            ConsoleScroll.IsVisible = false;
            DetailScroll.IsVisible = false;
            EmptyPanel.IsVisible = !Has;
            BtnBack.IsVisible = false;
            if (!Has)
            {
                EmptyTitle.Text = "Find a game's updates";
                EmptyText.Text = "Search by name or CUSA ID to see every official update version, its size, the firmware it needs and what changed.";
            }
        }

        void ShowDetail()
        {
            DetailScroll.IsVisible = true;
            ResultsScroll.IsVisible = false;
            ConsoleScroll.IsVisible = false;
            EmptyPanel.IsVisible = false;
            // "back" returns to whichever list we came from, when it has anything
            BtnBack.IsVisible = ConsoleMode
                ? Installed.Count > 0
                : (ResultsList.ItemsSource as System.Collections.IEnumerable)?.Cast<object>().Any() == true;
        }

        void SetBusy(string Text)
        {
            EmptyPanel.IsVisible = true;
            ResultsScroll.IsVisible = false;
            ConsoleScroll.IsVisible = false;
            DetailScroll.IsVisible = false;
            EmptyTitle.Text = Text;
            EmptyText.Text = "";
        }

        void ShowError(string Text)
        {
            EmptyPanel.IsVisible = true;
            ResultsScroll.IsVisible = false;
            ConsoleScroll.IsVisible = false;
            DetailScroll.IsVisible = false;
            EmptyTitle.Text = "Something went wrong";
            EmptyText.Text = Text;
        }

        void SyncConsoleButtons()
        {
            if (!ConsoleMode)
                return;
            BtnReadConsole.IsEnabled = Ftp && !Busy;
            BtnReadConsole.Content = ConsoleChecked ? "Read again" : "Read the console";
            ToolTip.SetTip(BtnReadConsole, Busy ? "Working…" : Ftp ? "List the console's games and check each for an update" : NoFtp);
        }

        void SetConsoleStatus(string Text) => Avalonia.Threading.Dispatcher.UIThread.Post(() => ConsoleStatusText.Text = Text);
        void SetDetailStatus(string Text) { DetailStatus.Text = Text; DetailStatus.IsVisible = Text.Length > 0; }
    }
}
