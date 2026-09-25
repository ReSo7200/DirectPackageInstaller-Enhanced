using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;
using DirectPackageInstaller.Host;
using DirectPackageInstaller.Tasks;
using ReactiveUI;

namespace DirectPackageInstaller.Services
{
    /// <summary>One package of a title being moved (the game, its update or a DLC).</summary>
    public sealed class MovePart
    {
        public string Kind { get; init; } = "";        // "Game", "Update", "DLC"
        public string Category { get; init; } = "";    // SFO category for the BGFT type: gd, gp, ac
        public string Source { get; init; } = "";      // where it was installed
        public string Held { get; init; } = "";        // where it waits during the move (same drive)
        public string Target { get; init; } = "";      // where it ends up installed
        public long Size { get; init; }
        public string ContentId { get; set; } = "";
        public string HeaderType { get; set; } = "";
        public bool Installed { get; set; }
    }

    /// <summary>A move shown on the Queue page.</summary>
    public sealed class MoveJob : ReactiveObject
    {
        public string TitleId { get; init; } = "";
        public string Title { get; init; } = "";
        public bool ToExtended { get; init; }
        public string Destination => ToExtended ? "extended storage" : "system storage";
        public string Heading => $"{Title}  →  {Destination}";
        internal List<MovePart> Parts { get; } = new();
        internal byte[]? Icon;
        /// <summary>The old copy was uninstalled: from here on the held files are the only copy.</summary>
        internal bool Uninstalled;

        string _Status = "Starting…";
        public string Status { get => _Status; set => this.RaiseAndSetIfChanged(ref _Status, value); }

        bool _IsRunning = true;
        public bool IsRunning
        {
            get => _IsRunning;
            set { this.RaiseAndSetIfChanged(ref _IsRunning, value); this.RaisePropertyChanged(nameof(CanRetry)); }
        }

        bool _Failed;
        public bool Failed
        {
            get => _Failed;
            set { this.RaiseAndSetIfChanged(ref _Failed, value); this.RaisePropertyChanged(nameof(CanRetry)); }
        }

        /// <summary>Failed after the old copy was removed: installing the held files again finishes it.</summary>
        public bool CanRetry => Failed && !IsRunning && Uninstalled;
    }

    /// <summary>
    /// Moves an installed title between system and extended storage without sending it
    /// from this device, the way Itemzflow restores apps: the installed packages are
    /// renamed aside on the same drive over FTP (instant), the title is uninstalled with
    /// Remote Package Installer, and the experimental payload installs each package from
    /// that file (BGFT "ByStorage") with Application Install Location switched to the
    /// other drive. The console copies the data across drives once, like its own Move.
    /// </summary>
    public static class ConsoleMove
    {
        public const string HoldFolder = "/user/dpi_move";

        public static ObservableCollection<MoveJob> Jobs { get; } = new();

        public static bool IsBusy => Jobs.Any(x => x.IsRunning);

        static string Root(bool Extended) => Extended ? ConsoleInventory.ExtRoot : "";

        /// <summary>Why a move can't start now, or null.</summary>
        public static string? WhyNot()
        {
            var Console = ConsoleStatus.Instance;
            if (IsBusy)
                return "Another move is running (see Queue)";
            if (!Console.FtpOpen)
                return "Needs GoldHEN's FTP server running on the console";
            if (!Console.HasRpi)
                return "Needs Remote Package Installer open on the console (it removes the old copy)";
            if (!App.Config.ExperimentalPayload)
                return "Turn on the experimental GoldHEN payload in Settings";
            if (!Console.HasBinLoader)
                return "Needs GoldHEN's BinLoader (payload server) turned on";
            return null;
        }

        public static void Start(string TitleId, string Title, bool ToExtended)
        {
            var Job = new MoveJob { TitleId = TitleId, Title = Title, ToExtended = ToExtended };
            Jobs.Insert(0, Job);
            _ = RunAsync(Job);
        }

        public static void Retry(MoveJob Job)
        {
            if (!Job.CanRetry)
                return;
            Job.Failed = false;
            Job.IsRunning = true;
            _ = RunAsync(Job);
        }

        public static void Remove(MoveJob Job)
        {
            if (!Job.IsRunning)
                Jobs.Remove(Job);
        }

        static void Ui(Action Change) => Dispatcher.UIThread.Post(Change);

        static async Task RunAsync(MoveJob Job)
        {
            var IP = App.Config.PSIP.Trim();
            try
            {
                if (!Job.Uninstalled)
                {
                    await PrepareAsync(IP, Job);
                    await UninstallAsync(IP, Job);
                }

                await InstallAsync(IP, Job);

                Ui(() =>
                {
                    Job.Status = $"Moved to {Job.Destination}.";
                    Job.IsRunning = false;
                });
            }
            catch (Exception ex)
            {
                var Held = Job.Uninstalled
                    ? $" The packages are kept on the console in {Root(!Job.ToExtended)}{HoldFolder}/{Job.TitleId}: press Retry to install them again."
                    : " Nothing was changed on the console.";
                Ui(() =>
                {
                    Job.Status = ex.Message + Held;
                    Job.Failed = true;
                    Job.IsRunning = false;
                });
            }
            finally
            {
                ConsoleStatus.Instance.NotifyContentsChanged();
            }
        }

        /// <summary>Find the packages, check the space, and rename them aside (undone on failure).</summary>
        static async Task PrepareAsync(string IP, MoveJob Job)
        {
            var Tid = Job.TitleId;
            var From = Root(!Job.ToExtended);
            var To = Root(Job.ToExtended);
            var Hold = $"{From}{HoldFolder}/{Tid}";

            Ui(() => Job.Status = "Looking at the installed packages…");
            await WithFtp(async Ftp =>
            {
                Job.Parts.Clear();
                var AppSize = await Ftp.FileSizeAsync($"{From}/user/app/{Tid}/app.pkg");
                if (AppSize <= 0)
                    throw new InvalidOperationException($"{Tid} isn't installed on {(Job.ToExtended ? "system" : "extended")} storage as a package that can be moved.");
                Job.Parts.Add(new MovePart { Kind = "Game", Category = "gd", Source = $"{From}/user/app/{Tid}/app.pkg", Held = $"{Hold}/app.pkg", Target = $"{To}/user/app/{Tid}/app.pkg", Size = AppSize });

                var PatchSize = await Ftp.FileSizeAsync($"{From}/user/patch/{Tid}/patch.pkg");
                if (PatchSize > 0)
                    Job.Parts.Add(new MovePart { Kind = "Update", Category = "gp", Source = $"{From}/user/patch/{Tid}/patch.pkg", Held = $"{Hold}/patch.pkg", Target = $"{To}/user/patch/{Tid}/patch.pkg", Size = PatchSize });

                List<FtpEntry> Dlcs;
                try { Dlcs = await Ftp.ListAsync($"{From}/user/addcont/{Tid}"); }
                catch (FtpException) { Dlcs = new List<FtpEntry>(); }
                foreach (var Label in Dlcs.Where(x => x.IsDirectory && x.Name != "." && x.Name != ".."))
                {
                    var Size = await Ftp.FileSizeAsync($"{From}/user/addcont/{Tid}/{Label.Name}/ac.pkg");
                    if (Size > 0)
                        Job.Parts.Add(new MovePart { Kind = "DLC", Category = "ac", Source = $"{From}/user/addcont/{Tid}/{Label.Name}/ac.pkg", Held = $"{Hold}/dlc_{Label.Name}.pkg", Target = $"{To}/user/addcont/{Tid}/{Label.Name}/ac.pkg", Size = Size });
                }

                // content ID and package type from each header (BGFT needs them)
                foreach (var Part in Job.Parts)
                {
                    var Head = await Ftp.ReadHeadAsync(Part.Source, 0x100);
                    if (Head.Length < 0x78 || Head[0] != 0x7F || Head[1] != (byte)'C' || Head[2] != (byte)'N' || Head[3] != (byte)'T')
                        throw new InvalidOperationException($"{Part.Source} isn't a package this can move.");
                    Part.ContentId = Encoding.ASCII.GetString(Head, 0x40, 0x24).TrimEnd('\0');
                    Part.HeaderType = BinaryPrimitives.ReadUInt32BigEndian(Head.AsSpan(0x74)) switch
                    {
                        0x1A => "GD", 0x1B => "AC", 0x1C => "AL", 0x1E => "DP", _ => ""
                    };
                }

                foreach (var IconPath in Job.ToExtended
                             ? new[] { $"/user/appmeta/{Tid}/icon0.png" }
                             : new[] { $"/user/appmeta/external/{Tid}/icon0.png", $"/user/appmeta/{Tid}/icon0.png" })
                {
                    try { Job.Icon = await Ftp.DownloadAsync(IconPath, 1024 * 1024); break; }
                    catch (FtpException) { }
                }
                return true;
            });

            // the destination needs room for everything (the console copies once)
            Ui(() => Job.Status = "Checking free space…");
            long Needed = Job.Parts.Sum(x => x.Size) + 1024L * 1024 * 1024;
            var Space = await Installer.Payload.QueryFreeSpaceAsync(IP, App.Config.PCIP)
                        ?? throw new InvalidOperationException("Couldn't read the console's free space: " + (Installer.LastError ?? "the payload didn't answer."));
            if (Job.ToExtended && !Space.HasExtended)
                throw new InvalidOperationException("No extended storage is connected.");
            ulong Free = Job.ToExtended ? Space.ExtendedFree : Space.InternalFree;
            if (Free < (ulong)Needed)
                throw new InvalidOperationException($"Not enough space on {Job.Destination}: needs {TransferProgressInfo.FormatBytes(Needed)}, {TransferProgressInfo.FormatBytes((long)Free)} free.");

            // rename aside (same drive: instant); undo all of it if one fails
            Ui(() => Job.Status = "Setting the packages aside…");
            await WithFtp(async Ftp =>
            {
                await Ftp.MakeDirAsync($"{From}{HoldFolder}");
                await Ftp.MakeDirAsync(Hold);
                var Done = new List<MovePart>();
                try
                {
                    foreach (var Part in Job.Parts)
                    {
                        await Ftp.RenameAsync(Part.Source, Part.Held);
                        Done.Add(Part);
                    }
                }
                catch
                {
                    foreach (var Part in Done)
                        try { await Ftp.RenameAsync(Part.Held, Part.Source); } catch { }
                    await Ftp.RemoveDirAsync(Hold);
                    throw;
                }
                return true;
            }, Attempts: 1);
        }

        /// <summary>Remove the old installation (RPI); on failure put the packages back.</summary>
        static async Task UninstallAsync(string IP, MoveJob Job)
        {
            Ui(() => Job.Status = "Removing the old copy…");
            var Error = await ConsoleActions.UninstallAsync(IP, new LibraryEntry { Category = "gd", TitleId = Job.TitleId, Title = Job.Title });
            if (Error != null)
            {
                await WithFtp(async Ftp =>
                {
                    foreach (var Part in Job.Parts)
                        try { await Ftp.RenameAsync(Part.Held, Part.Source); } catch { }
                    return true;
                });
                throw new InvalidOperationException("Couldn't remove the old copy: " + Error);
            }

            Job.Uninstalled = true;

            // wait for the console to finish deleting it
            var From = Root(!Job.ToExtended);
            for (int i = 0; i < 30; i++)
            {
                bool Gone = await WithFtp(async Ftp => (await Ftp.ListAsync($"{From}/user/app")).All(x => x.Name != Job.TitleId));
                if (Gone)
                    break;
                await Task.Delay(2000);
            }

            // a DLC the uninstall left registered can't be installed again: put it back in place
            await WithFtp(async Ftp =>
            {
                foreach (var Part in Job.Parts.Where(x => x.Kind == "DLC" && !x.Installed))
                {
                    var Folder = Part.Source.Substring(0, Part.Source.LastIndexOf('/'));
                    var Parent = Folder.Substring(0, Folder.LastIndexOf('/'));
                    var Label = Folder.Substring(Folder.LastIndexOf('/') + 1);
                    List<FtpEntry> Left;
                    try { Left = await Ftp.ListAsync(Parent); }
                    catch (FtpException) { continue; }
                    if (Left.Any(x => x.IsDirectory && x.Name == Label))
                    {
                        await Ftp.RenameAsync(Part.Held, Part.Source);
                        Part.Installed = true;
                    }
                }
                return true;
            });
        }

        /// <summary>Install each held package on the other drive, one at a time, then clean up.</summary>
        static async Task InstallAsync(string IP, MoveJob Job)
        {
            int Storage = Job.ToExtended ? ExperimentalPayloadProtocol.StorageExtended : ExperimentalPayloadProtocol.StorageInternal;

            foreach (var Part in Job.Parts.Where(x => !x.Installed))
            {
                var Info = new PKGHelper.PKGInfo
                {
                    FriendlyName = Part.Kind == "Game" ? Job.Title : $"{Job.Title} ({Part.Kind})",
                    ContentID = Part.ContentId,
                    TitleID = Job.TitleId,
                    ContentType = Part.Category,
                    HeaderContentType = Part.HeaderType,
                    PackageSize = Part.Size,
                    IconData = Job.Icon!
                };

                Ui(() => Job.Status = $"Installing the {Part.Kind.ToLowerInvariant()} on {Job.Destination}…");
                if (!await Installer.Payload.SendLocalPackageAsync(IP, App.Config.PCIP, Part.Held, Info, Storage))
                    throw new InvalidOperationException($"The console didn't take the {Part.Kind.ToLowerInvariant()}: " + (Installer.LastError ?? "no answer from the payload."));

                // done when the installed file is complete on the other drive
                var Deadline = DateTime.Now + TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(Part.Size / (15L * 1024 * 1024));
                while (true)
                {
                    await Task.Delay(5000);
                    long Now = await WithFtp(Ftp => Ftp.FileSizeAsync(Part.Target));
                    if (Now == Part.Size)
                        break;
                    var Copied = Now > 0 ? $"{TransferProgressInfo.FormatBytes(Now)} of {TransferProgressInfo.FormatBytes(Part.Size)}" : "starting";
                    Ui(() => Job.Status = $"Copying the {Part.Kind.ToLowerInvariant()} to {Job.Destination}… {Copied}");
                    if (DateTime.Now > Deadline)
                        throw new TimeoutException($"The {Part.Kind.ToLowerInvariant()} didn't finish installing in time (check the console's Notifications › Downloads).");
                }

                Part.Installed = true;
                await WithFtp(async Ftp => { await Ftp.DeleteAsync(Part.Held); return true; });
            }

            await WithFtp(async Ftp =>
            {
                var From = Root(!Job.ToExtended);
                await Ftp.RemoveDirAsync($"{From}{HoldFolder}/{Job.TitleId}");
                await Ftp.RemoveDirAsync($"{From}{HoldFolder}");
                return true;
            });
        }

        /// <summary>Run with a fresh GoldHEN FTP connection (it drops some first connections).</summary>
        static async Task<T> WithFtp<T>(Func<FtpLite, Task<T>> Action, int Attempts = ConsoleInventory.FtpAttempts)
        {
            Exception? Last = null;
            for (int Attempt = 1; Attempt <= Attempts; Attempt++)
            {
                try
                {
                    await using var Ftp = await FtpLite.ConnectAsync(App.Config.PSIP.Trim(), 2121, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15));
                    return await Action(Ftp);
                }
                catch (InvalidOperationException) { throw; }
                catch (Exception ex) when (Attempt < Attempts)
                {
                    Last = ex;
                    await Task.Delay(1000);
                }
            }
            throw new InvalidOperationException("GoldHEN's FTP server stopped answering." + (Last != null ? $" ({Last.Message})" : ""));
        }
    }
}
