using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DirectPackageInstaller.Services;
using ReactiveUI;

namespace DirectPackageInstaller.ViewModels
{
    public enum UpdateStatus { Checking, UpToDate, UpdateAvailable, NoUpdates, Unknown }

    /// <summary>A game installed on the console, checked against orbispatches for a newer update.</summary>
    public sealed class InstalledUpdate : ReactiveObject
    {
        public InstalledUpdate(InstalledTitle Title) => this.Title = Title;

        public InstalledTitle Title { get; }
        public string TitleId => Title.TitleId;
        public string Name => Title.Title;
        /// <summary>The update level installed on the console (APP_VER); base games read "01.00".</summary>
        public string InstalledVersion => Title.Version.Length > 0 ? Title.Version : "01.00";

        bool _FakeBase;
        /// <summary>The base game in the local library is a fake PKG — Sony's retail update won't marry with it (CE-36441-8).</summary>
        public bool FakeBase
        {
            get => _FakeBase;
            set
            {
                this.RaiseAndSetIfChanged(ref _FakeBase, value);
                foreach (var Name in new[] { nameof(CanDownload), nameof(Detail), nameof(StatusText) })
                    this.RaisePropertyChanged(Name);
            }
        }

        /// <summary>A fake update PKG in the library that can be remarried to the fake base (highest version wins).</summary>
        public LibraryEntry? LibraryUpdate { get; set; }
        public bool HasLibraryUpdate => LibraryUpdate != null;

        bool _Busy;
        /// <summary>A long-running action (remarry, download) is in progress on this row.</summary>
        public bool Busy
        {
            get => _Busy;
            set => this.RaiseAndSetIfChanged(ref _Busy, value);
        }

        string _Message = "";
        /// <summary>Inline progress message under the row (remarry status, errors) — not the enum Status badge.</summary>
        public string Message
        {
            get => _Message;
            set { this.RaiseAndSetIfChanged(ref _Message, value); this.RaisePropertyChanged(nameof(HasMessage)); }
        }
        public bool HasMessage => Message.Length > 0;

        public string Monogram => new string(Name.Where(char.IsLetterOrDigit).Take(2).ToArray()).ToUpperInvariant() is { Length: > 0 } M ? M : "?";

        OrbisPatches.Patch? _Latest;
        public OrbisPatches.Patch? Latest => _Latest;

        UpdateStatus _Status = UpdateStatus.Checking;
        public UpdateStatus Status
        {
            get => _Status;
            private set
            {
                this.RaiseAndSetIfChanged(ref _Status, value);
                foreach (var Name in new[] { nameof(StatusText), nameof(StatusBrush), nameof(NeedsUpdate),
                             nameof(Detail), nameof(CanOpen), nameof(CanDownload) })
                    this.RaisePropertyChanged(Name);
            }
        }

        /// <summary>Record the orbispatches result (null = none found / not in the database).</summary>
        public void Resolve(OrbisPatches.Patch? Latest, bool Failed)
        {
            _Latest = Latest;
            Status = Failed ? UpdateStatus.Unknown
                : Latest == null ? UpdateStatus.NoUpdates
                : PatchInfo.Compare(Latest.Version, InstalledVersion) > 0 ? UpdateStatus.UpdateAvailable
                : UpdateStatus.UpToDate;
        }

        public bool NeedsUpdate => Status == UpdateStatus.UpdateAvailable;
        public bool CanOpen => Status is UpdateStatus.UpdateAvailable or UpdateStatus.UpToDate;
        /// <summary>Save/Send is possible when a Sony update applies (retail base) OR when we have a library update to remarry (fake base).</summary>
        public bool CanDownload => Status == UpdateStatus.UpdateAvailable && (!FakeBase || HasLibraryUpdate);

        public string StatusText => Status switch
        {
            UpdateStatus.Checking => "Checking…",
            UpdateStatus.UpToDate => "Up to date",
            UpdateStatus.UpdateAvailable => FakeBase
                ? HasLibraryUpdate
                    ? $"Update to v{_Latest!.Version} (fake v{LibraryUpdate!.AppVersion} from your library)"
                    : $"Update to v{_Latest!.Version} (fake base — needs a fake/fPKG update; retail ones fail CE-36441-8)"
                : $"Update to v{_Latest!.Version}",
            UpdateStatus.NoUpdates => "No updates",
            _ => "Couldn't check"
        };

        public IBrush StatusBrush => LibraryItem.Brush(Status switch
        {
            UpdateStatus.UpdateAvailable => "Amber",
            UpdateStatus.UpToDate => "Go",
            UpdateStatus.Unknown => "Signal",
            _ => "Violet"
        });

        public string Detail
        {
            get
            {
                var Parts = new System.Collections.Generic.List<string> { "Installed v" + InstalledVersion };
                if (Status == UpdateStatus.UpdateAvailable && _Latest != null)
                {
                    Parts.Add("latest v" + _Latest.Version + (_Latest.SizeText.Length > 0 ? " · " + _Latest.SizeText : ""));
                    if (_Latest.FirmwareText.Length > 0)
                        Parts.Add("needs " + _Latest.FirmwareText);
                }
                return string.Join("  ·  ", Parts);
            }
        }

        Bitmap? _Cover;
        bool CoverRequested;
        public Bitmap? Cover
        {
            get
            {
                if (!CoverRequested)
                {
                    CoverRequested = true;
                    var Icon = Title.IconFile;
                    if (Icon != null && File.Exists(Icon))
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                using var Stream = File.OpenRead(Icon);
                                var Decoded = Bitmap.DecodeToWidth(Stream, 160);
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

    /// <summary>A game in an orbispatches search result.</summary>
    public sealed class UpdateGame : ReactiveObject
    {
        static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

        public UpdateGame(OrbisPatches.Game Game) => this.Game = Game;

        public OrbisPatches.Game Game { get; }
        public string TitleId => Game.TitleId;
        public string Name => Game.Name;
        public string Meta => string.Join("  ·  ", new[] { Game.TitleId, Game.RegionText }.Where(x => x.Length > 0));

        public string Monogram => new string(Name.Where(char.IsLetterOrDigit).Take(2).ToArray()).ToUpperInvariant() is { Length: > 0 } M ? M : "?";

        Bitmap? _Cover;
        bool CoverRequested;
        public Bitmap? Cover
        {
            get
            {
                if (!CoverRequested)
                {
                    CoverRequested = true;
                    if (!string.IsNullOrWhiteSpace(Game.IconUrl))
                        _ = LoadCoverAsync(Game.IconUrl!);
                }
                return _Cover;
            }
        }
        public bool HasCover => _Cover != null;

        async Task LoadCoverAsync(string Url)
        {
            try
            {
                var Data = await Http.GetByteArrayAsync(Url);
                using var Stream = new MemoryStream(Data);
                var Decoded = Bitmap.DecodeToWidth(Stream, 160);
                Dispatcher.UIThread.Post(() =>
                {
                    _Cover = Decoded;
                    this.RaisePropertyChanged(nameof(Cover));
                    this.RaisePropertyChanged(nameof(HasCover));
                });
            }
            catch { /* keep the monogram */ }
        }
    }

    /// <summary>One update version of a title on the Updates page.</summary>
    public sealed class UpdatePatch : ReactiveObject
    {
        public UpdatePatch(OrbisPatches.Patch Patch) => this.Patch = Patch;

        public OrbisPatches.Patch Patch { get; }
        public string Version => "Patch " + Patch.Version;
        public bool IsLatest => Patch.IsLatest;
        public string Meta => string.Join("  ·  ",
            new[] { Patch.SizeText, Patch.FirmwareText, Patch.Date }.Where(x => !string.IsNullOrWhiteSpace(x)));
        public string Changelog => Patch.Changelog;
        public bool HasChangelog => !string.IsNullOrWhiteSpace(Patch.Changelog);

        bool _Busy;
        public bool Busy { get => _Busy; set => this.RaiseAndSetIfChanged(ref _Busy, value); }
    }
}
