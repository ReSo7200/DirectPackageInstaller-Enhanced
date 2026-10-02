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

        /// <summary>Reuse the On PS4 page's already-read titles (and its cached covers) when it has them.
        /// Also listen: when that page rescans, refresh here too (or on next OnShown), so this tab
        /// isn't stale after a scan or a console-side change like an install.</summary>
        public void Attach(Func<ConsoleViewModel?> Console)
        {
            this.Console = Console;
            SubscribeToConsole();
        }

        bool ConsoleSubscribed;
        void SubscribeToConsole()
        {
            if (ConsoleSubscribed || Console() is not { } Vm)
                return;
            Vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(ConsoleViewModel.AllTitles))
                    return;
                ConsoleStale = true;
                if (ConsoleMode && IsVisible)
                    _ = ReadConsoleAsync();
            };
            ConsoleSubscribed = true;
        }

        /// <summary>True when a scan happened elsewhere (On PS4 refresh, install, uninstall) since our last read.</summary>
        bool ConsoleStale;

        public void OnShown()
        {
            SubscribeToConsole();
            SyncConsoleButtons();
            RefreshFolder();
            // switched back after the console changed: re-read silently so the list isn't stale
            if (ConsoleMode && ConsoleStale && Ftp && !Busy)
                _ = ReadConsoleAsync();
        }

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

                // flag fake bases: Sony's official update won't marry with a fake PKG (CE-36441-8 at install)
                var FakeBases = Library
                    .Where(e => e.Error == null && e.Kind == "Game" && e.Fake)
                    .Select(e => e.TitleId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                // for fake bases, the automatic remarry needs a fake update PKG in the library to re-sign.
                // Keep one per title (highest AppVersion wins), so Save/Send on fake-base rows "just work".
                // only a fake update NEWER than what's installed is useful (an equal one is already on the console)
                var VersionOrder = Comparer<string>.Create(PatchInfo.Compare);
                var LibraryUpdates = Library
                    .Where(e => e.Error == null && e.Kind == "Update" && e.Fake && !string.IsNullOrEmpty(e.TitleId))
                    .GroupBy(e => e.TitleId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.AppVersion, VersionOrder).ToList(),
                        StringComparer.OrdinalIgnoreCase);
                Installed = Titles
                    .Where(x => x.TitleId.Length > 0)
                    .OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new InstalledUpdate(x)
                    {
                        FakeBase = FakeBases.Contains(x.TitleId),
                        LibraryUpdate = LibraryUpdates.GetValueOrDefault(x.TitleId)?
                            .FirstOrDefault(u => PatchInfo.Compare(u.AppVersion, x.Version.Length > 0 ? x.Version : "01.00") > 0)
                    }).ToList();
                ConsoleStale = false;
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

        /// <summary>
        /// Always re-reads the console. We used to reuse the On PS4 tab's cached <c>AllTitles</c>
        /// when it was loaded — but those are shared <see cref="InstalledTitle"/> instances whose
        /// <c>Version</c> was mutated by whatever code ran on the first scan. If that scan happened
        /// under old logic (e.g. the orphan-patch version override), the stale Version values
        /// survive across tab switches and pressing "Read again" doesn't clear them. A fresh FTP
        /// read every time avoids every variant of that staleness.
        /// </summary>
        async Task<List<InstalledTitle>?> LoadTitlesAsync(CancellationToken Token)
            => await ConsoleTitles.QueryAsync(Ip, new Progress<string>(SetConsoleStatus), Token);

        /// <summary>Look each installed title up on orbispatches (limited concurrency) and mark the ones behind.</summary>
        async Task CheckUpdatesAsync()
        {
            var Items = Installed.ToList();
            if (Items.Count == 0)
                return;

            SetConsoleStatus($"Checking {Items.Count} {(Items.Count == 1 ? "game" : "games")} for updates…");

            // Two sources, newest wins: Sony's own title-patch server (what the Library tab uses,
            // cached 12h) and orbispatches (changelogs, sizes, full history). orbispatches alone
            // intermittently rejects bursts, and a rejected title used to read as "No updates".
            Dictionary<string, string> Sony;
            try { Sony = await PatchInfo.LatestAsync(Items.Select(x => x.TitleId)); }
            catch { Sony = new(StringComparer.OrdinalIgnoreCase); }

            var Orbis = new System.Collections.Concurrent.ConcurrentDictionary<string, OrbisPatches.Patch?>(StringComparer.OrdinalIgnoreCase);
            async Task LookupAsync(InstalledUpdate Item, CancellationToken ct)
            {
                try { Orbis[Item.TitleId] = await OrbisPatches.LatestPatchAsync(Item.TitleId, ct); }
                catch { Orbis[Item.TitleId] = null; }
            }

            int Done = 0;
            await Parallel.ForEachAsync(Items, new ParallelOptions { MaxDegreeOfParallelism = 2 }, async (Item, ct) =>
            {
                await LookupAsync(Item, ct);
                int N = Interlocked.Increment(ref Done);
                if (N % 3 == 0)
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => SetConsoleStatus($"Checking… {N} of {Items.Count}"));
            });

            // second, one-at-a-time pass for whatever orbispatches refused during the burst
            var Retry = Items.Where(x => Orbis.GetValueOrDefault(x.TitleId) == null && !OrbisPatches.IsNotListed(x.TitleId)).ToList();
            for (int i = 0; i < Retry.Count; i++)
            {
                SetConsoleStatus($"Re-checking {i + 1} of {Retry.Count} that didn't answer…");
                await Task.Delay(800);
                await LookupAsync(Retry[i], CancellationToken.None);
            }

            foreach (var Item in Items)
            {
                var FromOrbis = Orbis.GetValueOrDefault(Item.TitleId);
                Sony.TryGetValue(Item.TitleId, out var SonyVersion); // null = lookup failed, "" = no updates

                var Latest = FromOrbis;
                if (!string.IsNullOrEmpty(SonyVersion) && (Latest == null || PatchInfo.Compare(SonyVersion, Latest.Version) > 0))
                    Latest = new OrbisPatches.Patch(SonyVersion, "", PatchInfo.FirmwareFor(Item.TitleId), "", "", true);

                // a definitive "nothing" needs at least one source to have actually answered
                bool Answered = FromOrbis != null || OrbisPatches.IsNotListed(Item.TitleId) || SonyVersion != null;
                Item.Resolve(Latest, Latest == null && !Answered);
                Item.Message = Item.Status == UpdateStatus.Unknown && OrbisPatches.LastFailure.TryGetValue(Item.TitleId, out var Why)
                    ? "Couldn't check: " + Why
                    : Item.Message;
            }
            FinishCheck();
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
            if ((sender as Control)?.Tag is not InstalledUpdate Item)
                return;
            if (Item.FakeBase)
                await AutoRemarryAsync(Item, Send: false);
            else
                await GetAsync(Item.TitleId, Item.Name, null, Send: false);
        }

        async void SendInstalledClick(object? sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.Tag is not InstalledUpdate Item)
                return;
            if (Item.FakeBase)
                await AutoRemarryAsync(Item, Send: true);
            else
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

        /// <summary>
        /// Automatic fake remarry: pick a fake update PKG for this title from the local library (highest
        /// APP_VER wins), extract it, rewrite its CONTENT_ID to the installed fake base's, rebuild as a
        /// fresh fake pkg_ps4_patch, and either save it next to the base or enqueue to the console.
        /// No file picker — Save / Send to PS4 on a fake-base row routes here instead of the Sony
        /// download because retail updates can't marry a fake base (CE-36441-8).
        /// </summary>
        async Task AutoRemarryAsync(InstalledUpdate Item, bool Send)
        {
            if (Item.Busy)
                return;

            var Base = Library.FirstOrDefault(e => e.Error == null && e.Kind == "Game"
                && string.Equals(e.TitleId, Item.TitleId, StringComparison.OrdinalIgnoreCase));
            if (Base == null || !FakeRemarry.IsContentId(Base.ContentId))
            {
                Item.Message = $"DPI needs {Item.Name}'s base game in your PKG library (scan it on the Library tab) so it can read the content id to remarry to.";
                return;
            }

            var SourceEntry = Item.LibraryUpdate;
            if (SourceEntry == null || string.IsNullOrEmpty(SourceEntry.Path))
            {
                Item.Message = $"Your {Item.Name} base is a fake PKG, so Sony's retail update won't install (CE-36441-8). " +
                    $"Add a fake (fPKG) update newer than v{Item.InstalledVersion} to your library and read the console again.";
                return;
            }

            // already married to this base: nothing to rebuild, just deliver it
            if (string.Equals(SourceEntry.ContentId, Base.ContentId, StringComparison.OrdinalIgnoreCase))
            {
                if (Send)
                {
                    SendQueue.Instance.Enqueue(new[] { SourceEntry });
                    AppShell.Current?.ShowQueue();
                    Item.Message = $"Fake update v{SourceEntry.AppVersion} already matches your base  ·  sent to the queue";
                }
                else
                    Item.Message = $"Fake update v{SourceEntry.AppVersion} already matches your base and is in your library: {SourceEntry.Path}";
                return;
            }

            Item.Busy = true;
            Item.Message = $"Remarrying from library update v{SourceEntry.AppVersion}…";
            try
            {
                var OutputFolder = Send
                    ? FakeRemarry.DefaultOutputFolder
                    : Path.GetDirectoryName(Base.Path) ?? FakeRemarry.DefaultOutputFolder;

                var OutputPkg = await FakeRemarry.RemarryAsync(SourceEntry.Path, Base.ContentId, OutputFolder,
                    new Progress<string>(Text => Item.Message = Text));

                var Entry = await Task.Run(() => LibraryService.ReadPackage(OutputPkg));
                if (Entry == null || Entry.Error != null)
                {
                    Item.Message = $"Built the remarried PKG but DPI couldn't read it back ({Entry?.Error ?? "unknown error"}). File: {OutputPkg}";
                    return;
                }

                if (Send)
                {
                    SendQueue.Instance.Enqueue(new[] { Entry });
                    AppShell.Current?.ShowQueue();
                    Item.Message = $"Remarried v{Entry.AppVersion}  ·  sent to the queue";
                    Notices.Post("Update remarried → PS4", $"{Base.Title} v{Entry.AppVersion}");
                }
                else
                {
                    Item.Message = $"Remarried v{Entry.AppVersion}  ·  saved to the library";
                    Notices.Post("Update remarried", $"{Base.Title} v{Entry.AppVersion}");
                }
            }
            catch (OperationCanceledException)
            {
                Item.Message = "Cancelled.";
            }
            catch (FakeRemarryException ex)
            {
                Item.Message = ex.Message;
            }
            catch (Exception ex)
            {
                Item.Message = "Couldn't remarry: " + ex.Message;
            }
            finally
            {
                Item.Busy = false;
            }
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
                // Sony's official updates are retail-signed. If the base game in the library is a fake
                // PKG the console rejects the update at install (CE-36441-8: the update is not married
                // to that base). Only a matching fake update from the same source will install over it.
                var Base = Library.FirstOrDefault(e => e.Error == null && e.Kind == "Game"
                    && string.Equals(e.TitleId, TitleId, StringComparison.OrdinalIgnoreCase));
                var FakeWarning = Base is { Fake: true }
                    ? "\n\n⚠ Your base game for this title is a fake PKG. Sony's official update won't marry with it (CE-36441-8 at install). You need a fake update from the same source (NoPayStation / matching CUSA), not this one." : "";
                var Confirm = await MessageBox.ShowAsync(
                    (Send
                        ? $"Download {Name} update {Patch.Version} from PlayStation Network and send it to the console?\n\n"
                        : $"Download {Name} update {Patch.Version} from PlayStation Network into your library?\n\n") +
                    $"Size: {TransferProgressInfo.FormatBytes(Patch.Size)}\n" + Firmware +
                    $"Saved to: {Folder}\n\n" +
                    (Send ? "It's sent to the console once the download finishes. " : "") +
                    "Updates only install over a matching base game." + FakeWarning,
                    Send ? "Send update to PS4" : "Save update",
                    MessageBoxButtons.YesNo,
                    Base is { Fake: true } ? MessageBoxIcon.Warning : MessageBoxIcon.Question);
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
