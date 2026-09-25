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

    /// <summary>One cover in the library grid.</summary>
    public sealed class LibraryItem : ReactiveObject
    {
        public LibraryItem(LibraryEntry Entry, string? FamilyIcon = null)
        {
            this.Entry = Entry;
            IconFile = Entry.IconFile ?? FamilyIcon;
        }

        /// <summary>Own ICON0, else the base game's (updates often ship without one).</summary>
        readonly string? IconFile;

        public LibraryEntry Entry { get; }

        public string Title => Entry.Title;
        public string Kind => Entry.Kind.ToUpperInvariant();
        public string TitleId => string.IsNullOrEmpty(Entry.TitleId) ? "—" : Entry.TitleId;
        public string VersionText => string.IsNullOrEmpty(Entry.AppVersion) ? "" : "v" + Entry.AppVersion;
        public string SizeText => TransferProgressInfo.FormatBytes(Entry.Size);
        public string Tooltip => $"{Entry.Title}\n{Entry.ContentId}\n{Entry.Path}"
                                 + (string.IsNullOrEmpty(Entry.SystemVersion) ? "" : $"\nRequires firmware {Entry.SystemVersion}")
                                 + (Entry.Error != null ? $"\n{Entry.Error}" : "");
        public bool HasError => Entry.Error != null;

        public IBrush KindBrush => Entry.Kind switch
        {
            "Game" => Brush("Text"),
            "Update" => Brush("Amber"),
            "DLC" => Brush("Violet"),
            _ => Brush("Muted")
        };

        Bitmap? _Cover;
        bool CoverLoaded;
        /// <summary>ICON0, decoded small and only when first shown.</summary>
        public Bitmap? Cover
        {
            get
            {
                if (!CoverLoaded)
                {
                    CoverLoaded = true;
                    if (IconFile != null && File.Exists(IconFile))
                    {
                        try
                        {
                            using var Stream = File.OpenRead(IconFile);
                            _Cover = Bitmap.DecodeToWidth(Stream, 256);
                        }
                        catch { }
                    }
                }
                return _Cover;
            }
        }
        public bool HasCover => Cover != null;

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
                    InstallState.Installed => "On PS4",
                    InstallState.NewerInstalled => "Newer on PS4",
                    InstallState.UpdateAvailable => Entry.Kind == "Update" ? "Update not installed" : "Update available",
                    InstallState.BaseMissing => "Needs base game",
                    InstallState.NotInstalled => "Not on PS4",
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

        void RaiseRail()
        {
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
        public ObservableCollection<LibraryItem> Selected { get; } = new();
        public ObservableCollection<string> Folders { get; } = new();

        ConsoleSnapshot? Snapshot;

        public LibraryViewModel()
        {
            foreach (var Folder in LibraryService.Folders)
                Folders.Add(Folder);

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
                var Item = new LibraryItem(Entry, FamilyIcon.GetValueOrDefault(Entry.TitleId));
                if (Snapshot != null)
                    Apply(Item, Snapshot);
                All.Add(Item);
            }

            LinkQueue();
            ApplyFilter();
            UpdateSummary();
        }

        static void Apply(LibraryItem Item, ConsoleSnapshot Snapshot)
        {
            Item.UnknownText = Snapshot.IsAppsOnly && Snapshot.Apps.Contains(Item.Entry.TitleId) && Item.Entry.Kind is "Update" or "DLC"
                ? "Game on PS4"
                : "";
            Item.State = Snapshot.StateOf(Item.Entry.Category, Item.Entry.TitleId, Item.Entry.ContentId, Item.Entry.AppVersion);
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
                if (Query.Length == 0) return true;
                return x.Entry.Title.Contains(Query, StringComparison.CurrentCultureIgnoreCase)
                       || x.Entry.TitleId.Contains(Query, StringComparison.OrdinalIgnoreCase)
                       || Path.GetFileName(x.Entry.Path).Contains(Query, StringComparison.CurrentCultureIgnoreCase);
            }).ToList();

            Items.Clear();
            foreach (var Item in Visible)
                Items.Add(Item);

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
                $"{Games} games  ·  {Updates} updates  ·  {Dlc} DLC  ·  {TransferProgressInfo.FormatBytes(Size)}";
        }

        public async Task AddFolderAsync(string Folder)
        {
            if (!Directory.Exists(Folder))
                return;

            if (LibraryService.AddFolder(Folder))
                Folders.Add(Path.GetFullPath(Folder));

            this.RaisePropertyChanged(nameof(HasFolders));
            this.RaisePropertyChanged(nameof(IsEmpty));
            await ScanAsync();
        }

        public async Task RemoveFolderAsync(string Folder)
        {
            LibraryService.RemoveFolder(Folder);
            Folders.Remove(Folder);
            this.RaisePropertyChanged(nameof(HasFolders));
            Load(LibraryService.Cached);
            this.RaisePropertyChanged(nameof(IsEmpty));
            await Task.CompletedTask;
        }

        public async Task ScanAsync()
        {
            if (IsScanning)
                return;

            IsScanning = true;
            try
            {
                var Progress = new Progress<(int Done, int Total, string File)>(p =>
                    Summary = $"Reading {p.Done} of {p.Total}  ·  {Path.GetFileName(p.File)}");

                var Entries = await LibraryService.ScanAsync(Progress);
                Load(Entries);
            }
            finally
            {
                IsScanning = false;
            }
        }

        /// <summary>
        /// Startup check (Settings > "Check the PS4 on startup"): waits for the
        /// first scan so every title ID is asked about in one go.
        /// </summary>
        public async Task AutoCheckAsync()
        {
            if (!App.Config.AutoCheckConsole || string.IsNullOrWhiteSpace(App.Config.PSIP))
                return;

            while (IsScanning)
                await Task.Delay(250);

            if (All.Count > 0)
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
                    ConsoleSummary = "Couldn't read the PS4. Turn on GoldHEN's FTP server (port 2121), or open Remote Package Installer.";
                    return;
                }

                Snapshot = Result;
                foreach (var Item in All)
                    Apply(Item, Result);

                int OnConsole = All.Count(x => x.State is InstallState.Installed or InstallState.NewerInstalled);
                int Updates = All.Count(x => x.State == InstallState.UpdateAvailable);
                ConsoleSummary = Result.IsAppsOnly
                    // RPI can only say which games are installed
                    ? $"{OnConsole} games on PS4 (via RPI at {Result.Taken.ToLocalTime():HH:mm}). Update and DLC status needs GoldHEN's FTP server on port 2121: turn it off and on in GoldHEN settings, then Check PS4."
                    : $"{OnConsole} on PS4  ·  {Updates} updates to install  ·  via {Result.Source} at {Result.Taken.ToLocalTime():HH:mm}";
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
