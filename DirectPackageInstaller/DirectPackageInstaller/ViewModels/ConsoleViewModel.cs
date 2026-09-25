using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DirectPackageInstaller.Services;
using ReactiveUI;

namespace DirectPackageInstaller.ViewModels
{
    public enum ConsoleFilter { All, Games, Apps, Extended }

    /// <summary>One title on the "On PS4" page.</summary>
    public sealed class ConsoleTitleItem : ReactiveObject
    {
        public ConsoleTitleItem(InstalledTitle Title, int LibraryPackages)
        {
            this.Title = Title;
            this.LibraryPackages = LibraryPackages;
        }

        public InstalledTitle Title { get; }
        public int LibraryPackages { get; }

        public string Name => Title.Title;
        public string TitleId => Title.TitleId;
        static readonly string[] GamePrefixes = { "CUSA", "PLAS", "SLUS", "SLES", "SCUS", "SCES" };

        /// <summary>Games (incl. patched and PS2 classics) vs homebrew/system apps.</summary>
        public bool IsGame => Title.Category.ToLowerInvariant() is "gd" or "gp" or "gdo"
                              || GamePrefixes.Any(p => Title.TitleId.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        public string KindText => IsGame ? "GAME" : "APP";
        public IBrush KindBrush => LibraryItem.Brush(IsGame ? "Text" : "Muted");

        public string VersionText => Title.Version.Length == 0 ? "" : $"v{Title.Version}";

        public bool OnExtended => Title.OnExtended;
        public bool OnSystem => !Title.OnExtended;

        public string StorageText => Title.Installing ? "INSTALLING · EXT" : Title.OnExtended ? "EXT STORAGE" : "";
        public bool ShowStorage => StorageText.Length > 0;
        public IBrush StorageBrush => LibraryItem.Brush(Title.Installing ? "Signal" : "Violet");

        public string DetailText
        {
            get
            {
                var Parts = new List<string>();
                if (Title.HasUpdate)
                    Parts.Add("updated");
                if (Title.DlcCount > 0)
                    Parts.Add(Title.DlcCount == 1 ? "1 DLC" : $"{Title.DlcCount} DLC");
                Parts.Add(LibraryPackages switch
                {
                    0 => "not in library",
                    1 => "1 PKG in library",
                    _ => $"{LibraryPackages} PKGs in library"
                });
                return string.Join("  ·  ", Parts);
            }
        }

        public IBrush RailBrush => LibraryItem.Brush(Title.Installing ? "Signal" : "Go");
        public bool CanUninstallUpdate => Title.HasUpdate;
        public string Tooltip => $"{Title.Title}\n{Title.TitleId}  ·  {(Title.OnExtended ? "extended storage" : "system storage")}"
                                 + (Title.Installing ? "\nStill downloading/installing on the console" : "");

        Bitmap? _Cover;
        bool CoverRequested;
        public Bitmap? Cover
        {
            get
            {
                if (!CoverRequested)
                {
                    CoverRequested = true;
                    if (Title.IconFile != null)
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                using var Stream = File.OpenRead(Title.IconFile);
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
    }

    /// <summary>"On PS4": what's installed on the console, read over GoldHEN FTP.</summary>
    /// <summary>One drive in the "On PS4" storage boxes.</summary>
    public sealed class StorageBox
    {
        public string Name { get; init; } = "";
        public IBrush Accent { get; init; } = LibraryItem.Brush("Signal");
        public string FreeText { get; init; } = "";
        public string TotalText { get; init; } = "";
        /// <summary>0..1 of the drive in use.</summary>
        public double Used { get; init; }
        public string UsedText => $"{Used * 100:0}% used";
        public string TitlesText { get; init; } = "";
        public IBrush BarBrush => LibraryItem.Brush(Used >= 0.9 ? "Alarm" : Used >= 0.75 ? "Amber" : "Go");

        public static StorageBox Of(string Name, string Accent, ulong Free, ulong Total, int Titles) => new()
        {
            Name = Name,
            Accent = LibraryItem.Brush(Accent),
            FreeText = Host.TransferProgressInfo.FormatBytes(Free),
            TotalText = "free of " + Host.TransferProgressInfo.FormatBytes(Total),
            Used = Total == 0 ? 0 : Math.Clamp(1 - (double)Free / Total, 0, 1),
            TitlesText = Titles == 1 ? "1 title" : $"{Titles} titles"
        };
    }

    public sealed class ConsoleViewModel : ReactiveObject
    {
        readonly Func<IReadOnlyList<LibraryEntry>> LibraryEntries;
        List<ConsoleTitleItem> All = new();

        public ConsoleViewModel(Func<IReadOnlyList<LibraryEntry>> LibraryEntries)
        {
            this.LibraryEntries = LibraryEntries;

            // installs and uninstalls: read again once things settle (only if already shown)
            var Reread = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            Reread.Tick += async (_, _) =>
            {
                Reread.Stop();
                await RefreshAsync();
            };
            ConsoleStatus.Instance.ContentsChanged += () =>
            {
                if (!Loaded)
                    return;
                Reread.Stop();
                Reread.Start();
            };

            ConsoleStatus.Instance.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ConsoleStatus.FtpOpen))
                {
                    this.RaisePropertyChanged(nameof(CanCopyCaptures));
                    this.RaisePropertyChanged(nameof(CapturesTip));
                }
                if (e.PropertyName == nameof(ConsoleStatus.CanRead))
                {
                    this.RaisePropertyChanged(nameof(CanRefresh));
                    this.RaisePropertyChanged(nameof(RefreshTip));
                }
            };
        }

        // ----- screenshots and video clips (copied to this device, never deleted on the console)

        bool CopyingCaptures;
        public bool CanCopyCaptures => !CopyingCaptures && ConsoleStatus.Instance.FtpOpen;
        public string CapturesTip => ConsoleStatus.Instance.FtpOpen
            ? $"Copy the console's screenshots and video clips to {ConsoleCaptures.Folder} (a folder per game; ones already copied are skipped)"
            : "Needs GoldHEN's FTP server running on the console";

        string _CapturesStatus = "";
        public string CapturesStatus
        {
            get => _CapturesStatus;
            private set { this.RaiseAndSetIfChanged(ref _CapturesStatus, value); this.RaisePropertyChanged(nameof(HasCapturesStatus)); }
        }
        public bool HasCapturesStatus => CapturesStatus.Length > 0;

        public async Task CopyCapturesAsync()
        {
            var IP = App.Config.PSIP?.Trim();
            if (!CanCopyCaptures || string.IsNullOrEmpty(IP))
                return;

            CopyingCaptures = true;
            this.RaisePropertyChanged(nameof(CanCopyCaptures));
            try
            {
                CapturesStatus = "Looking for screenshots and videos…";
                var Found = await ConsoleCaptures.ListAsync(IP);
                if (Found.Count == 0)
                {
                    CapturesStatus = "No screenshots or videos on the console.";
                    return;
                }

                // folders named after the game when the console (or library) knows it
                var Names = All.ToDictionary(x => x.TitleId, x => x.Name, StringComparer.OrdinalIgnoreCase);
                foreach (var Entry in LibraryEntries())
                    Names.TryAdd(Entry.TitleId, Entry.Title);

                int Copied = await ConsoleCaptures.CopyAsync(IP, Found, Tid => Names.TryGetValue(Tid, out var Name) ? Name : Tid,
                    new Progress<string>(Text => CapturesStatus = Text));
                int Photos = Found.Count(x => !x.IsVideo), Videos = Found.Count - Photos;
                CapturesStatus = Copied == 0
                    ? $"All {Photos} screenshots and {Videos} videos were already copied to {ConsoleCaptures.Folder}."
                    : $"Copied {Copied} new ({Photos} screenshots and {Videos} videos on the console) to {ConsoleCaptures.Folder}.";
            }
            catch (Exception ex)
            {
                CapturesStatus = "Couldn't copy the captures: " + ex.Message;
            }
            finally
            {
                CopyingCaptures = false;
                this.RaisePropertyChanged(nameof(CanCopyCaptures));
            }
        }

        /// <summary>Zip the title's saves (every console user) to this device; read-only on the console.</summary>
        public async Task BackupSavesAsync(ConsoleTitleItem Item)
        {
            var IP = App.Config.PSIP?.Trim();
            if (!CanCopyCaptures || string.IsNullOrEmpty(IP))
                return;

            CopyingCaptures = true;
            this.RaisePropertyChanged(nameof(CanCopyCaptures));
            try
            {
                CapturesStatus = $"Looking for {Item.Name}'s saves…";
                var Users = await ConsoleSaves.FindAsync(IP, Item.TitleId);
                if (Users.Count == 0)
                {
                    CapturesStatus = $"{Item.Name} has no saved data on the console.";
                    return;
                }

                var Folder = await ConsoleSaves.BackupAsync(IP, Item.TitleId, Item.Name, Users, new Progress<string>(Text => CapturesStatus = Text));
                CapturesStatus = $"Backed up {Item.Name}'s saves ({string.Join(", ", Users.Select(x => x.UserName))}) to {Folder}. " +
                                 "Saves only load on this console and account; restore them with Apollo Save Tool (Homebrew page).";
            }
            catch (Exception ex)
            {
                CapturesStatus = "Couldn't back up the saves: " + ex.Message;
            }
            finally
            {
                CopyingCaptures = false;
                this.RaisePropertyChanged(nameof(CanCopyCaptures));
            }
        }

        /// <summary>Reading needs GoldHEN's FTP (or RPI) answering.</summary>
        public bool CanRefresh => !IsLoading && ConsoleStatus.Instance.CanRead;
        public string RefreshTip => ConsoleStatus.Instance.CanRead || IsLoading
            ? "Read what's installed on the console (GoldHEN FTP)"
            : "Needs GoldHEN's FTP server running on the console (GoldHEN › Server Settings)";

        public ObservableCollection<ConsoleTitleItem> Items { get; } = new();

        bool _IsLoading;
        public bool IsLoading
        {
            get => _IsLoading;
            private set
            {
                this.RaiseAndSetIfChanged(ref _IsLoading, value);
                this.RaisePropertyChanged(nameof(RefreshText));
                this.RaisePropertyChanged(nameof(CanRefresh));
                this.RaisePropertyChanged(nameof(RefreshTip));
            }
        }
        public string RefreshText => IsLoading ? "Reading…" : "Refresh";

        bool _Loaded;
        public bool Loaded
        {
            get => _Loaded;
            private set
            {
                this.RaiseAndSetIfChanged(ref _Loaded, value);
                this.RaisePropertyChanged(nameof(ShowIntro));
            }
        }
        public bool ShowIntro => !Loaded && !IsLoading;

        string _Summary = "";
        public string Summary
        {
            get => _Summary;
            private set => this.RaiseAndSetIfChanged(ref _Summary, value);
        }

        string _FreeSpace = "";
        /// <summary>Free space line (experimental payload only).</summary>
        public string FreeSpace
        {
            get => _FreeSpace;
            private set
            {
                this.RaiseAndSetIfChanged(ref _FreeSpace, value);
                this.RaisePropertyChanged(nameof(HasFreeSpace));
            }
        }
        public bool HasFreeSpace => FreeSpace.Length > 0;

        IReadOnlyList<StorageBox> _Storage = Array.Empty<StorageBox>();
        /// <summary>System and extended storage boxes (experimental payload's free space).</summary>
        public IReadOnlyList<StorageBox> Storage
        {
            get => _Storage;
            private set
            {
                this.RaiseAndSetIfChanged(ref _Storage, value);
                this.RaisePropertyChanged(nameof(HasStorage));
            }
        }
        public bool HasStorage => Storage.Count > 0;

        async Task ReadFreeSpaceAsync(string IP)
        {
            if (!App.Config.ExperimentalPayload)
            {
                FreeSpace = "";
                Storage = Array.Empty<StorageBox>();
                return;
            }

            // no BinLoader and no payload running: nothing can answer (and don't hold up sends trying)
            if (!ConsoleStatus.Instance.HasBinLoader && !Tasks.Installer.Payload.ClientRunning)
            {
                if (!HasStorage)
                    FreeSpace = "Free space: needs GoldHEN's BinLoader (payload server) turned on";
                return;
            }

            // keep the last boxes while reading; the text only shows without them
            FreeSpace = HasStorage ? "" : "Reading free space…";
            var Space_ = await Tasks.Installer.Payload.QueryFreeSpaceAsync(IP, App.Config.PCIP);
            if (Space_ is not { } S)
            {
                Storage = Array.Empty<StorageBox>();
                FreeSpace = "Free space: " + (Tasks.Installer.LastError ?? "not available");
                return;
            }

            int OnExt = All.Count(x => x.Title.OnExtended);
            var Boxes = new List<StorageBox>();
            if (S.HasInternal)
                Boxes.Add(StorageBox.Of("SYSTEM", "Signal", S.InternalFree, S.InternalTotal, All.Count - OnExt));
            if (S.HasExtended)
                Boxes.Add(StorageBox.Of("EXTENDED", "Violet", S.ExtendedFree, S.ExtendedTotal, OnExt));
            Storage = Boxes;
            FreeSpace = Boxes.Count > 0 ? "" : "Free space: not reported by the console";
        }

        string _Search = "";
        public string Search
        {
            get => _Search;
            set { this.RaiseAndSetIfChanged(ref _Search, value); ApplyFilter(); }
        }

        ConsoleFilter _Filter = ConsoleFilter.All;
        public ConsoleFilter Filter
        {
            get => _Filter;
            set { this.RaiseAndSetIfChanged(ref _Filter, value); ApplyFilter(); }
        }

        public async Task RefreshAsync()
        {
            var IP = App.Config.PSIP?.Trim();
            if (IsLoading)
                return;
            if (string.IsNullOrEmpty(IP))
            {
                Summary = "Set the console address in Settings first.";
                return;
            }

            IsLoading = true;
            Summary = "Reading the console…";
            this.RaisePropertyChanged(nameof(ShowIntro));
            try
            {
                var Progress = new Progress<string>(p => Summary = p);
                var Titles = await ConsoleTitles.QueryAsync(IP, Progress);
                if (Titles == null)
                {
                    Summary = "Couldn't read the console. Turn on GoldHEN's FTP server (Settings › Server Settings on the PS4), then press Refresh."
                              + (ConsoleTitles.LastError is { } Why ? $"\n({Why})" : "");
                    return;
                }

                var ByTitle = LibraryEntries().GroupBy(x => x.TitleId).ToDictionary(x => x.Key, x => x.Count());
                All = Titles.Select(t => new ConsoleTitleItem(t, ByTitle.GetValueOrDefault(t.TitleId))).ToList();

                int Games = All.Count(x => x.IsGame), Ext = All.Count(x => x.Title.OnExtended), Updates = All.Count(x => x.Title.HasUpdate);
                int Installing = All.Count(x => x.Title.Installing);
                Summary = $"{Games} games  ·  {All.Count - Games} apps  ·  {Updates} with updates"
                          + (Ext > 0 ? $"  ·  {Ext} on extended storage" : "")
                          + (Installing > 0 ? $" ({Installing} still installing)" : "")
                          + $"  ·  read at {DateTime.Now:HH:mm}";
                Loaded = true;
                ApplyFilter();
                _ = ReadFreeSpaceAsync(IP);
            }
            catch (Exception ex)
            {
                Summary = "Couldn't read the console: " + ex.Message;
            }
            finally
            {
                IsLoading = false;
                this.RaisePropertyChanged(nameof(ShowIntro));
            }
        }

        void ApplyFilter()
        {
            var Query = Search.Trim();
            var Visible = All.Where(x =>
            {
                if (Filter == ConsoleFilter.Games && !x.IsGame) return false;
                if (Filter == ConsoleFilter.Apps && x.IsGame) return false;
                if (Filter == ConsoleFilter.Extended && !x.Title.OnExtended) return false;
                return Query.Length == 0
                       || x.Name.Contains(Query, StringComparison.CurrentCultureIgnoreCase)
                       || x.TitleId.Contains(Query, StringComparison.OrdinalIgnoreCase);
            }).OrderBy(x => x.IsGame ? 0 : 1).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase);

            Items.Clear();
            foreach (var Item in Visible)
                Items.Add(Item);
        }
    }
}
