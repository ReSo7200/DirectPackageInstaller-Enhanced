using System;
using System.Collections.Generic;
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
    public enum SavesFilter { All, OnConsole, NotBackedUp, BackupsOnly }

    /// <summary>One backup zip under a game on the Saves page.</summary>
    public sealed class SaveBackupItem : ReactiveObject
    {
        public SaveBackupItem(ConsoleSaves.SaveBackup Backup) => this.Backup = Backup;

        public ConsoleSaves.SaveBackup Backup { get; }
        public string UserText => Backup.UserName == Backup.UserId ? Backup.UserId : $"{Backup.UserName}";
        public string DateText => Backup.Created.ToString("d MMM yyyy, HH:mm");
        public string Detail => $"{Backup.Files} files  ·  {Host.TransferProgressInfo.FormatBytes(Backup.Size)}  ·  {Backup.UserId}";
        public bool BeforeRestore => Backup.BeforeRestore;

        bool _CanAct = true;
        public bool CanAct { get => _CanAct; set => this.RaiseAndSetIfChanged(ref _CanAct, value); }

        string _RestoreTip = "";
        public string RestoreTip { get => _RestoreTip; set => this.RaiseAndSetIfChanged(ref _RestoreTip, value); }
        bool _CanRestore;
        public bool CanRestore { get => _CanRestore; set => this.RaiseAndSetIfChanged(ref _CanRestore, value); }

        public void Sync(bool Busy, bool Restorable, string Tip)
        {
            CanAct = !Busy;
            CanRestore = !Busy && Restorable;
            RestoreTip = Tip;
        }
    }

    /// <summary>A game with saves on the console, backups, or both.</summary>
    public sealed class SaveGame : ReactiveObject
    {
        public SaveGame(string TitleId, string Name, string? IconFile)
        {
            this.TitleId = TitleId;
            this.Name = Name;
            this.IconFile = IconFile;
        }

        public string TitleId { get; }
        public string Name { get; }
        readonly string? IconFile;

        ConsoleSaves.TitleSaves? _OnConsole;
        public ConsoleSaves.TitleSaves? OnConsole
        {
            get => _OnConsole;
            set { this.RaiseAndSetIfChanged(ref _OnConsole, value); Changed(); }
        }

        List<SaveBackupItem> _Backups = new();
        public List<SaveBackupItem> Backups
        {
            get => _Backups;
            set { this.RaiseAndSetIfChanged(ref _Backups, value); Changed(); }
        }

        void Changed()
        {
            foreach (var Name in new[] { nameof(HasConsole), nameof(ConsoleText), nameof(UsersText), nameof(BackupText),
                         nameof(HasBackups), nameof(BackupBrush), nameof(StateText), nameof(StateBrush), nameof(BackupsHeader) })
                this.RaisePropertyChanged(Name);
        }

        public bool HasConsole => OnConsole != null;
        public bool HasBackups => Backups.Count > 0;

        public string UsersText => OnConsole == null ? "" : string.Join(", ", OnConsole.Users.Select(u => u.UserName));

        public string ConsoleText
        {
            get
            {
                if (OnConsole == null)
                    return "Not on the console";
                var Parts = new List<string>
                {
                    OnConsole.Slots == 1 ? "1 save slot" : $"{Math.Max(OnConsole.Slots, OnConsole.Users.Count)} save slots",
                    Host.TransferProgressInfo.FormatBytes(OnConsole.Size)
                };
                if (OnConsole.LastSaved is { } When)
                    Parts.Add("saved " + Ago(When));
                return string.Join("  ·  ", Parts);
            }
        }

        DateTime? NewestBackup => Backups.Where(b => !b.BeforeRestore).Select(b => (DateTime?)b.Backup.Created).Max()
                                  ?? Backups.Select(b => (DateTime?)b.Backup.Created).Max();

        public string BackupText => Backups.Count == 0
            ? "No backup yet"
            : $"{(Backups.Count == 1 ? "1 backup" : $"{Backups.Count} backups")}, newest {Ago(NewestBackup!.Value)}";

        /// <summary>Backed up after the console's last save?</summary>
        public bool UpToDate => OnConsole?.LastSaved is not { } Saved || NewestBackup is { } B && B >= Saved;

        public IBrush BackupBrush => LibraryItem.Brush(Backups.Count == 0 ? "Amber" : UpToDate ? "Go" : "Amber");

        public string StateText => OnConsole == null ? "BACKUP ONLY" : Backups.Count == 0 ? "NOT BACKED UP" : UpToDate ? "BACKED UP" : "CHANGED SINCE";
        public IBrush StateBrush => LibraryItem.Brush(OnConsole == null ? "Violet" : Backups.Count == 0 ? "Amber" : UpToDate ? "Go" : "Amber");

        public string BackupsHeader => Backups.Count == 0 ? "" : Expanded ? "Hide backups" : $"Backups ({Backups.Count})";

        bool _Expanded;
        public bool Expanded
        {
            get => _Expanded;
            set { this.RaiseAndSetIfChanged(ref _Expanded, value); this.RaisePropertyChanged(nameof(BackupsHeader)); }
        }

        bool _Selected;
        public bool Selected { get => _Selected; set => this.RaiseAndSetIfChanged(ref _Selected, value); }

        string _Status = "";
        public string Status
        {
            get => _Status;
            set { this.RaiseAndSetIfChanged(ref _Status, value); this.RaisePropertyChanged(nameof(HasStatus)); }
        }
        public bool HasStatus => Status.Length > 0;

        bool _CanBackup;
        public bool CanBackup { get => _CanBackup; set => this.RaiseAndSetIfChanged(ref _CanBackup, value); }
        string _BackupTip = "";
        public string BackupTip { get => _BackupTip; set => this.RaiseAndSetIfChanged(ref _BackupTip, value); }

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
                    var Icon = IconFile ?? ConsoleSaves.IconFor(TitleId);
                    if (Icon != null)
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
        public string? IconPath => IconFile;

        public static string Ago(DateTime When)
        {
            var Span = DateTime.Now - When;
            if (Span.TotalMinutes < 2) return "just now";
            if (Span.TotalHours < 1) return $"{(int)Span.TotalMinutes} min ago";
            if (Span.TotalHours < 24 && When.Date == DateTime.Today) return "today " + When.ToString("HH:mm");
            if (When.Date == DateTime.Today.AddDays(-1)) return "yesterday";
            if (Span.TotalDays < 7) return $"{(int)Math.Ceiling(Span.TotalDays)} days ago";
            return When.ToString(When.Year == DateTime.Now.Year ? "d MMM" : "d MMM yyyy");
        }
    }
}
