using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DirectPackageInstaller.Host;
using DirectPackageInstaller.Services;
using ReactiveUI;

namespace DirectPackageInstaller.ViewModels
{
    public enum LibraryFilter { All, Games, Updates, DLC }

    public enum LibrarySort { Title, Newest, Largest }

    /// <summary>A library folder chip: USB drives and network shares can be offline.</summary>
    public sealed class LibraryFolder
    {
        public LibraryFolder(string Path, bool Available, int Count)
        {
            this.Path = Path;
            this.Available = Available;
            this.Count = Count;
        }

        public string Path { get; }
        public bool Available { get; }
        public int Count { get; }
        public string Label => Available ? $"{Path}  ·  {Count}" : $"{Path}  ·  offline";
        public string Tip => Available
            ? $"{Path}\n{Count} PKG files (subfolders included)"
            : $"{Path}\nNot reachable right now: plug in the drive or connect to the network share, then press Rescan. Its games are hidden until then.";
    }

    /// <summary>One cover in the library grid.</summary>
    public sealed class LibraryItem : ReactiveObject
    {
        public LibraryItem(LibraryEntry Entry, string? FamilyIcon = null)
        {
            this.Entry = Entry;
            IconFile = Entry.IconFile ?? FamilyIcon;
        }

        /// <summary>Own ICON0, else the base game's (updates often ship without one).</summary>
        internal readonly string? IconFile;

        public LibraryEntry Entry { get; }

        public string Title => Entry.Title;
        public string Kind => Entry.Kind.ToUpperInvariant();
        public string TitleId => string.IsNullOrEmpty(Entry.TitleId) ? "—" : Entry.TitleId;
        public string VersionText => string.IsNullOrEmpty(Entry.AppVersion) ? "" : "v" + Entry.AppVersion;
        public string SizeText => TransferProgressInfo.FormatBytes(Entry.Size) + (Entry.Parts > 1 ? $" · {Entry.Parts} parts" : "");
        public string Tooltip => $"{Entry.Title}\n{Entry.ContentId}\n{Entry.Path}"
                                 + (string.IsNullOrEmpty(Entry.SystemVersion) ? "" : $"\nRequires firmware {Entry.SystemVersion}")
                                 + (Entry.Error != null ? $"\n{Entry.Error}" : "")
                                 + (NewerOfficial.Length > 0 ? $"\nSony has a newer update: v{NewerOfficial}" : "");
        public bool HasError => Entry.Error != null;

        /// <summary>Single-file packages with readable details can be renamed to the standard name.</summary>
        public bool CanRename => Entry.Parts <= 1 && Entry.Error == null && Entry.TitleId.Length > 0
                                 && !string.Equals(Path.GetFileName(Entry.Path), LibraryTidy.StandardName(Entry), StringComparison.OrdinalIgnoreCase);

        public IBrush KindBrush => Entry.Kind switch
        {
            "Game" => Brush("Text"),
            "Update" => Brush("Amber"),
            "DLC" => Brush("Violet"),
            _ => Brush("Muted")
        };

        Bitmap? _Cover;
        bool CoverRequested;
        /// <summary>
        /// ICON0, decoded small on a background thread the first time a card
        /// asks for it (so a big library doesn't freeze the window).
        /// </summary>
        public Bitmap? Cover
        {
            get
            {
                if (!CoverRequested)
                {
                    CoverRequested = true;
                    if (IconFile != null)
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                if (!File.Exists(IconFile))
                                    return;
                                using var Stream = File.OpenRead(IconFile);
                                var Decoded = Bitmap.DecodeToWidth(Stream, 256);
                                Dispatcher.UIThread.Post(() =>
                                {
                                    _Cover = Decoded;
                                    this.RaisePropertyChanged(nameof(Cover));
                                    this.RaisePropertyChanged(nameof(HasCover));
                                });
                            }
                            catch { }
                        });
                }
                return _Cover;
            }
        }
        public bool HasCover => _Cover != null;

        string _NewerOfficial = "";
        /// <summary>Latest official update version when it's newer than every local PKG of this title ("" otherwise).</summary>
        public string NewerOfficial
        {
            get => _NewerOfficial;
            set
            {
                this.RaiseAndSetIfChanged(ref _NewerOfficial, value);
                this.RaisePropertyChanged(nameof(HasNewerOfficial));
                this.RaisePropertyChanged(nameof(NewerOfficialText));
                this.RaisePropertyChanged(nameof(DownloadOfficialText));
                this.RaisePropertyChanged(nameof(Tooltip));
            }
        }
        public bool HasNewerOfficial => NewerOfficial.Length > 0;
        public string NewerOfficialText
        {
            get
            {
                if (!HasNewerOfficial)
                    return "";
                var Firmware = PatchInfo.FirmwareFor(Entry.TitleId);
                return Firmware.Length > 0 ? $"Update {NewerOfficial} is out · FW {Firmware}" : $"Update {NewerOfficial} is out";
            }
        }
        public string DownloadOfficialText => $"Download official update {NewerOfficial}…";

        /// <summary>Shown when the state can't be known (RPI can't see update versions or DLC).</summary>
        public string UnknownText { get; set; } = "";

        InstallState _State = InstallState.Unknown;
        public InstallState State
        {
            get => _State;
            set
            {
                this.RaiseAndSetIfChanged(ref _State, value);
                RaiseRail();
            }
        }

        QueueItem? _Queued;
        /// <summary>Active queue entry for this file, if any.</summary>
        public QueueItem? Queued
        {
            get => _Queued;
            set
            {
                if (_Queued != null)
                    _Queued.PropertyChanged -= QueuedChanged;
                _Queued = value;
                if (_Queued != null)
                    _Queued.PropertyChanged += QueuedChanged;
                RaiseRail();
            }
        }

        void QueuedChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RaiseRail();

        bool IsSending => Queued is { State: QueueState.Pushing or QueueState.Queued or QueueState.Downloading };

        /// <summary>Rail background: the install state.</summary>
        public IBrush RailBrush => State switch
        {
            InstallState.Installed => Brush("Go"),
            InstallState.NewerInstalled => Brush("Go"),
            InstallState.UpdateAvailable => Brush("AmberHatch"),
            InstallState.BaseMissing => Brush("Violet"),
            _ => Brush("Line")
        };

        /// <summary>Rail foreground: send progress, 0..1.</summary>
        public double RailFill => IsSending ? Math.Max(0.04, (Queued!.Progress) / 100.0) : 0;
        public bool ShowRailFill => IsSending;

        /// <summary>Cover/rail width in the grid, in px.</summary>
        public const double CardWidth = 176;
        public double RailFillWidth => CardWidth * RailFill;

        public string StatusText
        {
            get
            {
                if (Queued is { } Q && !Q.IsFinished)
                    return Q.State == QueueState.Downloading ? $"Sending {Q.Progress:0}%" : Q.StateText;
                if (Queued is { State: QueueState.Failed })
                    return "Send failed";

                return State switch
                {
                    InstallState.Installed => OnExtended ? "On PS4 · ext storage" : "On PS4",
                    InstallState.NewerInstalled => OnExtended ? "Newer on PS4 · ext" : "Newer on PS4",
                    InstallState.UpdateAvailable => Entry.Kind == "Update" ? "Update not installed" : "Update available",
                    InstallState.BaseMissing => "Needs base game",
                    InstallState.NotInstalled => InstallingOnConsole && Entry.Kind == "Game" ? "Installing on PS4" : "Not on PS4",
                    _ => UnknownText
                };
            }
        }

        public IBrush StatusBrush
        {
            get
            {
                if (Queued is { State: QueueState.Failed })
                    return Brush("Alarm");
                if (IsSending)
                    return Brush("Signal");
                return State switch
                {
                    InstallState.Installed or InstallState.NewerInstalled => Brush("Go"),
                    InstallState.UpdateAvailable => Brush("Amber"),
                    InstallState.BaseMissing => Brush("Violet"),
                    _ => Brush("Muted")
                };
            }
        }

        /// <summary>Another file in the library is the same package (content ID, type and version).</summary>
        bool _IsDuplicate;
        public bool IsDuplicate
        {
            get => _IsDuplicate;
            set => this.RaiseAndSetIfChanged(ref _IsDuplicate, value);
        }

        /// <summary>The console is still installing this title (e.g. to extended storage).</summary>
        public bool InstallingOnConsole { get; set; }

        /// <summary>The title is installed on extended storage (USB), not the internal drive.</summary>
        public bool OnExtended { get; set; }

        /// <summary>The last PS4 check saw some update installed for this title.</summary>
        public bool PatchOnConsole { get; set; }

        /// <summary>The last PS4 check found this on the console (so it can be uninstalled).</summary>
        public bool CanUninstall => State is InstallState.Installed or InstallState.NewerInstalled
                                    // an update of this title is installed, even if not this version
                                    || (Entry.Kind == "Update" && PatchOnConsole && State is InstallState.UpdateAvailable or InstallState.Unknown);

        void RaiseRail()
        {
            this.RaisePropertyChanged(nameof(CanUninstall));
            this.RaisePropertyChanged(nameof(RailBrush));
            this.RaisePropertyChanged(nameof(RailFill));
            this.RaisePropertyChanged(nameof(RailFillWidth));
            this.RaisePropertyChanged(nameof(ShowRailFill));
            this.RaisePropertyChanged(nameof(StatusText));
            this.RaisePropertyChanged(nameof(StatusBrush));
        }

        internal static IBrush Brush(string Key) =>
            Application.Current?.TryGetResource(Key, Application.Current.ActualThemeVariant, out var Value) == true && Value is IBrush B
                ? B
                : Brushes.Gray;
    }

    public sealed class LibraryViewModel : ReactiveObject
    {
        readonly List<LibraryItem> All = new();

        public ObservableCollection<LibraryItem> Items { get; } = new();

        /// <summary>Every scanned package (for other pages, e.g. "On PS4").</summary>
        public IReadOnlyList<LibraryEntry> Entries => All.Select(x => x.Entry).ToList();
        public ObservableCollection<LibraryItem> Selected { get; } = new();
        public ObservableCollection<LibraryFolder> Folders { get; } = new();

        ConsoleSnapshot? Snapshot;

        public LibraryViewModel()
        {
            RefreshFolders();

            Load(LibraryService.Cached);

            Selected.CollectionChanged += (_, _) =>
            {
                this.RaisePropertyChanged(nameof(SelectionText));
                this.RaisePropertyChanged(nameof(HasSelection));
            };

            SendQueue.Instance.Items.CollectionChanged += (_, _) => LinkQueue();
        }

        public bool HasFolders => Folders.Count > 0;
        public bool IsEmpty => Folders.Count > 0 && All.Count == 0 && !IsScanning;
        public bool HasNoMatches => All.Count > 0 && Items.Count == 0;

        string _Search = "";
        public string Search
        {
            get => _Search;
            set { this.RaiseAndSetIfChanged(ref _Search, value); ApplyFilter(); }
        }

        LibraryFilter _Filter = LibraryFilter.All;
        public LibraryFilter Filter
        {
            get => _Filter;
            set { this.RaiseAndSetIfChanged(ref _Filter, value); ApplyFilter(); }
        }

        LibrarySort _Sort = LibrarySort.Title;
        /// <summary>Title keeps game families together; the others sort files individually.</summary>
        public LibrarySort Sort
        {
            get => _Sort;
            set { this.RaiseAndSetIfChanged(ref _Sort, value); ApplyFilter(); }
        }

        bool _OnlyDuplicates;
        /// <summary>Show only packages stored more than once.</summary>
        public bool OnlyDuplicates
        {
            get => _OnlyDuplicates;
            set { this.RaiseAndSetIfChanged(ref _OnlyDuplicates, value); ApplyFilter(); }
        }

        int _DuplicateCount;
        public int DuplicateCount
        {
            get => _DuplicateCount;
            private set
            {
                this.RaiseAndSetIfChanged(ref _DuplicateCount, value);
                this.RaisePropertyChanged(nameof(HasDuplicates));
            }
        }
        public bool HasDuplicates => DuplicateCount > 0;

        bool _OnlyMissing;
        /// <summary>Hide what the PS4 already has.</summary>
        public bool OnlyMissing
        {
            get => _OnlyMissing;
            set { this.RaiseAndSetIfChanged(ref _OnlyMissing, value); ApplyFilter(); }
        }

        bool _IsScanning;
        public bool IsScanning
        {
            get => _IsScanning;
            private set
            {
                this.RaiseAndSetIfChanged(ref _IsScanning, value);
                this.RaisePropertyChanged(nameof(IsEmpty));
            }
        }

        bool _IsChecking;
        public bool IsChecking
        {
            get => _IsChecking;
            private set
            {
                this.RaiseAndSetIfChanged(ref _IsChecking, value);
                this.RaisePropertyChanged(nameof(CheckButtonText));
            }
        }

        public string CheckButtonText => IsChecking ? "Checking…" : "Check PS4";

        string _Summary = "";
        public string Summary
        {
            get => _Summary;
            private set => this.RaiseAndSetIfChanged(ref _Summary, value);
        }

        string _ConsoleSummary = "Not checked yet";
        public string ConsoleSummary
        {
            get => _ConsoleSummary;
            private set => this.RaiseAndSetIfChanged(ref _ConsoleSummary, value);
        }

        public bool HasSelection => Selected.Count > 0;
        public string SelectionText => Selected.Count == 0 ? "" :
            $"{Selected.Count} selected  ·  {TransferProgressInfo.FormatBytes(Selected.Sum(x => x.Entry.Size))}";

        void Load(IEnumerable<LibraryEntry> Entries)
        {
            // unchanged files come back as the same LibraryEntry objects from the cache:
            // keep their items (and decoded covers) instead of rebuilding everything
            var Previous = new Dictionary<LibraryEntry, LibraryItem>(ReferenceEqualityComparer.Instance);
            foreach (var Old in All)
                Previous[Old.Entry] = Old;

            All.Clear();

            // families stay together: game, then its updates (oldest first), then DLC
            static int Order(LibraryEntry e) => e.Kind switch { "Game" => 0, "Update" => 1, "DLC" => 2, _ => 3 };
            var FamilyTitle = Entries.Where(x => x.Kind == "Game" && x.TitleId != "")
                .GroupBy(x => x.TitleId).ToDictionary(x => x.Key, x => x.First().Title);
            var FamilyIcon = Entries.Where(x => x.IconFile != null && x.TitleId != "")
                .OrderBy(x => x.Kind == "Game" ? 0 : 1)
                .GroupBy(x => x.TitleId).ToDictionary(x => x.Key, x => x.First().IconFile);

            foreach (var Entry in Entries
                         .OrderBy(x => FamilyTitle.TryGetValue(x.TitleId, out var T) ? T : x.Title, StringComparer.CurrentCultureIgnoreCase)
                         .ThenBy(x => x.TitleId)
                         .ThenBy(Order)
                         .ThenBy(x => x.AppVersion))
            {
                var Icon = Entry.IconFile ?? FamilyIcon.GetValueOrDefault(Entry.TitleId);
                if (Previous.Remove(Entry, out var Existing) && Existing.IconFile == Icon)
                {
                    All.Add(Existing);
                    continue;
                }

                var Item = new LibraryItem(Entry, Icon);
                if (Snapshot != null)
                    Apply(Item, Snapshot);
                All.Add(Item);
            }

            // dropped items stop listening to queue rows
            foreach (var Gone in Previous.Values)
                Gone.Queued = null;

            // same package stored twice (content ID + type + version)
            var Copies = All.Where(x => x.Entry.ContentId.Length > 0)
                .GroupBy(x => (x.Entry.ContentId, x.Entry.Category, x.Entry.AppVersion))
                .Where(g => g.Count() > 1).SelectMany(g => g).ToHashSet();
            foreach (var Item in All)
                Item.IsDuplicate = Copies.Contains(Item);
            DuplicateCount = Copies.Count;

            RefreshFolders();

            LinkQueue();
            ApplyFilter();
            UpdateSummary();
        }

        static void Apply(LibraryItem Item, ConsoleSnapshot Snapshot)
        {
            Item.PatchOnConsole = !Snapshot.IsAppsOnly && Snapshot.Patches.Contains(Item.Entry.TitleId);
            Item.OnExtended = Snapshot.ExtendedApps.Contains(Item.Entry.TitleId);
            Item.InstallingOnConsole = Snapshot.InstallingApps.Contains(Item.Entry.TitleId);
            Item.UnknownText =
                Snapshot.IsAppsOnly && Snapshot.Apps.Contains(Item.Entry.TitleId) && Item.Entry.Kind is "Update" or "DLC" ? "Game on PS4"
                : !Snapshot.IsAppsOnly && Item.Entry.Kind == "Update" && Snapshot.Patches.Contains(Item.Entry.TitleId) ? "An update is on PS4"
                : "";
            Item.State = Snapshot.StateOf(Item.Entry.Category, Item.Entry.TitleId, Item.Entry.ContentId, Item.Entry.AppVersion);
        }

        /// <summary>Folder chips with availability and PKG counts.</summary>
        void RefreshFolders()
        {
            Folders.Clear();
            foreach (var Folder in LibraryService.Folders)
            {
                bool Available;
                try { Available = Directory.Exists(Folder); } catch { Available = false; }
                var Prefix = Folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                int Count = All.Count(x => x.Entry.Path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase));
                Folders.Add(new LibraryFolder(Folder, Available, Count));
            }
        }

        void LinkQueue()
        {
            var Active = SendQueue.Instance.Items
                .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);

            foreach (var Item in All)
                Item.Queued = Active.TryGetValue(Item.Entry.Path, out var Q) ? Q : null;
        }

        void ApplyFilter()
        {
            var Query = Search.Trim();
            var Visible = All.Where(x =>
            {
                if (Filter == LibraryFilter.Games && x.Entry.Kind != "Game") return false;
                if (Filter == LibraryFilter.Updates && x.Entry.Kind != "Update") return false;
                if (Filter == LibraryFilter.DLC && x.Entry.Kind != "DLC") return false;
                if (OnlyMissing && x.State is InstallState.Installed or InstallState.NewerInstalled) return false;
                if (OnlyDuplicates && !x.IsDuplicate) return false;
                if (Query.Length == 0) return true;
                return x.Entry.Title.Contains(Query, StringComparison.CurrentCultureIgnoreCase)
                       || x.Entry.TitleId.Contains(Query, StringComparison.OrdinalIgnoreCase)
                       || Path.GetFileName(x.Entry.Path).Contains(Query, StringComparison.CurrentCultureIgnoreCase);
            }).ToList();

            // All is already in family/title order
            if (Sort == LibrarySort.Newest)
                Visible = Visible.OrderByDescending(x => x.Entry.Modified).ToList();
            else if (Sort == LibrarySort.Largest)
                Visible = Visible.OrderByDescending(x => x.Entry.Size).ToList();

            // keep the selection to what is visible, so Send never includes hidden items
            foreach (var Hidden in Selected.Except(Visible).ToList())
                Selected.Remove(Hidden);

            // update in place: Clear() would reset the list and drop the selection on
            // every search keystroke; Move can still deselect, so restore it after
            var WasSelected = Selected.Where(Visible.Contains).ToList();
            var Keep = new HashSet<LibraryItem>(Visible);
            for (int i = Items.Count - 1; i >= 0; i--)
            {
                if (!Keep.Contains(Items[i]))
                    Items.RemoveAt(i);
            }
            for (int i = 0; i < Visible.Count; i++)
            {
                var Current = Items.IndexOf(Visible[i]);
                if (Current == i)
                    continue;
                if (Current < 0)
                    Items.Insert(i, Visible[i]);
                else
                    Items.Move(Current, i);
            }

            foreach (var Item in WasSelected)
            {
                if (!Selected.Contains(Item))
                    Selected.Add(Item);
            }

            this.RaisePropertyChanged(nameof(IsEmpty));
            this.RaisePropertyChanged(nameof(HasNoMatches));
        }

        void UpdateSummary()
        {
            int Games = All.Count(x => x.Entry.Kind == "Game");
            int Updates = All.Count(x => x.Entry.Kind == "Update");
            int Dlc = All.Count(x => x.Entry.Kind == "DLC");
            long Size = All.Sum(x => x.Entry.Size);
            Summary = All.Count == 0 ? "" :
                $"{Games} games  ·  {Updates} updates  ·  {Dlc} DLC  ·  {TransferProgressInfo.FormatBytes(Size)}"
                + (DuplicateCount > 0 ? $"  ·  {DuplicateCount} duplicates" : "");
        }

        public async Task AddFolderAsync(string Folder)
        {
            if (!Directory.Exists(Folder))
                return;

            if (LibraryService.AddFolder(Folder))
                RefreshFolders();

            this.RaisePropertyChanged(nameof(HasFolders));
            this.RaisePropertyChanged(nameof(IsEmpty));
            await ScanAsync();
        }

        public async Task RemoveFolderAsync(string Folder)
        {
            LibraryService.RemoveFolder(Folder);
            RefreshFolders();
            this.RaisePropertyChanged(nameof(HasFolders));
            Load(LibraryService.Cached);
            this.RaisePropertyChanged(nameof(IsEmpty));
            await Task.CompletedTask;
        }

        bool RescanPending;

        /// <summary>
        /// Scan the library folders. A request while a scan runs (e.g. a folder
        /// added during the startup scan) runs another pass right after.
        /// </summary>
        public async Task ScanAsync()
        {
            if (IsScanning)
            {
                RescanPending = true;
                return;
            }

            IsScanning = true;
            try
            {
                do
                {
                    RescanPending = false;
                    var Progress = new Progress<(int Done, int Total, string File)>(p =>
                        Summary = $"Reading {p.Done} of {p.Total}  ·  {Path.GetFileName(p.File)}");

                    try
                    {
                        var Entries = await LibraryService.ScanAsync(Progress);
                        Load(Entries);
                    }
                    catch (Exception ex)
                    {
                        UpdateSummary();
                        Summary = (Summary.Length > 0 ? Summary + "  ·  " : "") + "Scan failed: " + ex.Message;
                    }
                } while (RescanPending);
            }
            finally
            {
                IsScanning = false;
            }

            // (false until settings load; the startup pass runs from AutoCheckAsync)
            if (App.Config.CheckOfficialUpdates)
                _ = CheckOfficialUpdatesAsync();
        }

        /// <summary>
        /// Startup check (Settings > "Check the PS4 on startup"): waits for the
        /// first scan so every title ID is asked about in one go.
        /// </summary>
        public async Task AutoCheckAsync()
        {
            while (IsScanning)
                await Task.Delay(250);

            if (App.Config.CheckOfficialUpdates)
                _ = CheckOfficialUpdatesAsync();

            if (!App.Config.AutoCheckConsole || string.IsNullOrWhiteSpace(App.Config.PSIP))
                return;

            // even with an empty library: the summary shows what the console has
            await CheckConsoleAsync();
        }

        /// <summary>Ask the PS4 (GoldHEN FTP, else RPI) what is installed.</summary>
        public async Task CheckConsoleAsync()
        {
            var IP = App.Config.PSIP?.Trim();
            if (string.IsNullOrEmpty(IP) || IsChecking)
            {
                if (string.IsNullOrEmpty(IP))
                    ConsoleSummary = "Set the PS IP in Settings first";
                return;
            }

            IsChecking = true;
            ConsoleSummary = "Checking the PS4…";
            try
            {
                var TitleIds = All.Select(x => x.Entry.TitleId).Where(x => x != "").Distinct().ToList();
                var Result = await ConsoleInventory.QueryAsync(IP, TitleIds);

                if (Result == null)
                {
                    ConsoleSummary = "Couldn't read the PS4. Turn on GoldHEN's FTP server or open Remote Package Installer.";
                    return;
                }

                Snapshot = Result;
                foreach (var Item in All)
                    Apply(Item, Result);

                int OnConsole = All.Count(x => x.State is InstallState.Installed or InstallState.NewerInstalled);
                int Updates = All.Count(x => x.State == InstallState.UpdateAvailable);
                int OnExt = All.Where(x => x.Entry.Kind == "Game" && x.OnExtended).Select(x => x.Entry.TitleId).Distinct().Count();
                var When = Result.Taken.ToLocalTime().ToString("HH:mm");
                if (Result.IsAppsOnly)
                {
                    // RPI can only answer "is this game installed" for titles we ask about
                    ConsoleSummary = All.Count == 0
                        ? $"Remote Package Installer answered at {When}, but it can only check titles in your library. Add a PKG folder, or turn on GoldHEN's FTP server to see everything on the PS4."
                        : $"{OnConsole} of your library's games are on the PS4 (via RPI at {When}). Update and DLC status needs GoldHEN's FTP server on port 2121.";
                }
                else
                {
                    // what the console has, whether or not it's in the library
                    int Ext = Result.ExtendedApps.Count;
                    var ConsoleText = $"PS4: {Result.Apps.Count} apps, {Result.Patches.Count} updates{(Ext > 0 ? $" ({Ext} on extended storage)" : "")}";
                    ConsoleSummary = All.Count == 0
                        ? $"{ConsoleText}  ·  via {Result.Source} at {When}. Add a PKG folder to compare it with your files."
                        : $"{ConsoleText}  ·  {OnConsole} of your library's packages installed  ·  {Updates} updates to install  ·  via {Result.Source} at {When}";
                }
                ApplyFilter();
            }
            catch (Exception ex)
            {
                ConsoleSummary = "Couldn't read the PS4: " + ex.Message;
            }
            finally
            {
                IsChecking = false;
            }
        }

        /// <summary>
        /// Ask Sony's patch server for each title's latest update and flag the
        /// title's newest local game/update card when something newer exists.
        /// </summary>
        int OfficialCheckRunning;

        public async Task CheckOfficialUpdatesAsync()
        {
            // startup can ask twice (first scan + auto check): one run is enough
            if (System.Threading.Interlocked.Exchange(ref OfficialCheckRunning, 1) == 1)
                return;

            try
            {
                var Families = All.Where(x => x.Entry.Kind is "Game" or "Update" && x.Entry.TitleId.Length > 0)
                    .GroupBy(x => x.Entry.TitleId).ToList();
                if (Families.Count == 0)
                    return;

                var Latest = await PatchInfo.LatestAsync(Families.Select(x => x.Key));

                foreach (var Family in Families)
                {
                    var Newest = Family.OrderByDescending(x => x.Entry.AppVersion, Comparer<string>.Create(PatchInfo.Compare)).First();
                    foreach (var Item in Family)
                        Item.NewerOfficial = Item == Newest
                                             && Latest.TryGetValue(Family.Key, out var Version) && Version.Length > 0
                                             && PatchInfo.Compare(Version, Item.Entry.AppVersion) > 0
                            ? Version
                            : "";
                }
            }
            catch
            {
                // offline: nothing to show
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref OfficialCheckRunning, 0);
            }
        }

        /// <summary>Setting turned off: drop the "Update X is out" tags.</summary>
        public void ClearOfficialUpdates()
        {
            foreach (var Item in All)
                Item.NewerOfficial = "";
        }

        /// <summary>
        /// What a title still needs on the console: the game if it isn't installed,
        /// only the newest update (when newer than what's installed), and DLC not
        /// installed yet. Without a PS4 check everything counts as missing.
        /// </summary>
        public List<LibraryEntry> MissingFor(string TitleId)
        {
            var Family = All.Where(x => x.Entry.TitleId == TitleId && !x.HasError).ToList();
            static bool OnConsole(LibraryItem x) => x.State is InstallState.Installed or InstallState.NewerInstalled;

            var Result = new List<LibraryEntry>();

            var Game = Family.Where(x => x.Entry.Kind == "Game").OrderByDescending(x => x.Entry.Modified).FirstOrDefault();
            if (Game != null && !OnConsole(Game))
                Result.Add(Game.Entry);

            var NewestUpdate = Family.Where(x => x.Entry.Kind == "Update")
                .OrderByDescending(x => x.Entry.AppVersion, Comparer<string>.Create(PatchInfo.Compare)).FirstOrDefault();
            if (NewestUpdate != null && !OnConsole(NewestUpdate))
                Result.Add(NewestUpdate.Entry);

            Result.AddRange(Family.Where(x => x.Entry.Kind == "DLC" && !OnConsole(x)).Select(x => x.Entry));
            return Result;
        }

        /// <summary>Queue everything a title is missing (game, then newest update, then DLC).</summary>
        public int SendMissing(string TitleId)
        {
            var Missing = MissingFor(TitleId);
            if (Missing.Count > 0)
            {
                SendQueue.Instance.Enqueue(Missing);
                LinkQueue();
            }
            return Missing.Count;
        }

        public void SendSelected()
        {
            var Entries = Selected.Where(x => !x.HasError).Select(x => x.Entry).ToList();
            if (Entries.Count == 0)
                return;

            SendQueue.Instance.Enqueue(Entries);
            LinkQueue();
        }
    }
}
