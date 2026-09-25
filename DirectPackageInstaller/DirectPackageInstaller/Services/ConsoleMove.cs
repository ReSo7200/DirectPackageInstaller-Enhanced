using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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
        public string Kind { get; set; } = "";        // "Game", "Update", "DLC"
        public string Category { get; set; } = "";    // SFO category for the BGFT type: gd, gp, ac
        public string Source { get; set; } = "";      // where it was installed
        public string Held { get; set; } = "";        // where it waits during the move (same drive)
        public string Target { get; set; } = "";      // where it ends up installed
        public long Size { get; set; }
        public string ContentId { get; set; } = "";
        public string HeaderType { get; set; } = "";
        public bool Installed { get; set; }
    }

    /// <summary>A move shown on the Queue page.</summary>
    public sealed class MoveJob : ReactiveObject
    {
        public string TitleId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Category { get; set; } = "gd";
        public bool ToExtended { get; set; }
        public List<MovePart> Parts { get; set; } = new();
        /// <summary>The old copy was uninstalled: from here on the held files are the only copy.</summary>
        public bool Uninstalled { get; set; }

        internal byte[]? Icon;

        public string Destination => ToExtended ? "extended storage" : "system storage";
        public string Heading => $"{Title}  →  {Destination}";

        string _Status = "Starting…";
        public string Status { get => _Status; set => this.RaiseAndSetIfChanged(ref _Status, value); }

        bool _IsRunning = true;
        public bool IsRunning
        {
            get => _IsRunning;
            set { this.RaiseAndSetIfChanged(ref _IsRunning, value); RaiseButtons(); }
        }

        bool _Failed;
        public bool Failed
        {
            get => _Failed;
            set { this.RaiseAndSetIfChanged(ref _Failed, value); RaiseButtons(); }
        }

        /// <summary>Failed after the old copy was removed: installing the held files again finishes it.</summary>
        public bool CanRetry => Failed && !IsRunning && Uninstalled;

        /// <summary>Only jobs that don't hold the title's only copy can be dismissed.</summary>
        public bool CanRemove => !IsRunning && !CanRetry;

        void RaiseButtons()
        {
            this.RaisePropertyChanged(nameof(CanRetry));
            this.RaisePropertyChanged(nameof(CanRemove));
        }
    }

    /// <summary>What job.json holds (plain data; MoveJob itself is a ReactiveObject).</summary>
    public sealed class MoveRecord
    {
        public string TitleId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Category { get; set; } = "gd";
        public bool ToExtended { get; set; }
        public bool Uninstalled { get; set; }
        public List<MovePart> Parts { get; set; } = new();

        public static MoveRecord Of(MoveJob Job) => new()
        {
            TitleId = Job.TitleId, Title = Job.Title, Category = Job.Category,
            ToExtended = Job.ToExtended, Uninstalled = Job.Uninstalled, Parts = Job.Parts
        };

        public MoveJob ToJob() => new()
        {
            TitleId = TitleId, Title = Title, Category = Category,
            ToExtended = ToExtended, Uninstalled = Uninstalled, Parts = Parts
        };
    }

    /// <summary>A failure explained for the user (not retried by WithFtp).</summary>
    sealed class MoveException : Exception
    {
        public MoveException(string Message) : base(Message) { }
    }

    /// <summary>
    /// Moves an installed title between system and extended storage without sending it
    /// from this device, the way Itemzflow restores apps: the installed packages are
    /// renamed aside on the same drive over FTP (instant), the title is uninstalled with
    /// Remote Package Installer, and the experimental payload installs each package from
    /// that file (BGFT "ByStorage") with Application Install Location switched to the
    /// other drive. The console copies the data across drives once, like its own Move.
    ///
    /// Safety: a held file is deleted only after the console's task for it finished
    /// without error. A job record (job.json) sits next to the held files, so a move
    /// interrupted by closing the app is found again (Recover) and can be retried.
    /// </summary>
    public static class ConsoleMove
    {
        public const string HoldFolder = "/user/dpi_move";
        const string JobFile = "job.json";

        public static ObservableCollection<MoveJob> Jobs { get; } = new();

        public static bool IsBusy => Jobs.Any(x => x.IsRunning);

        static string Root(bool Extended) => Extended ? ConsoleInventory.ExtRoot : "";
        static string HoldOf(MoveJob Job) => $"{Root(!Job.ToExtended)}{HoldFolder}/{Job.TitleId}";

        /// <summary>Why a move can't start now (for this title, when given), or null.</summary>
        public static string? WhyNot(string? TitleId = null)
        {
            var Console = ConsoleStatus.Instance;
            if (TitleId != null && string.Equals(Console.RunningTitleId, TitleId, StringComparison.OrdinalIgnoreCase))
                return "The game is running on the console: close it first";
            if (IsBusy)
                return "Another move is running (see Queue)";
            if (!Console.FtpOpen)
                return "Needs GoldHEN's FTP server running on the console";
            if (!Console.HasRpi)
                return "Needs Remote Package Installer open on the console (it removes the old copy and follows the install)";
            if (!App.Config.ExperimentalPayload)
                return "Turn on the experimental GoldHEN payload in Settings";
            if (!Console.HasBinLoader)
                return "Needs GoldHEN's BinLoader (payload server) turned on";
            return null;
        }

        public static void Start(string TitleId, string Title, string Category, bool ToExtended)
        {
            // the console reports a patched game as "gp": its app.pkg is still the game ("gd")
            var AppCategory = string.IsNullOrEmpty(Category) || Category == "gp" ? "gd" : Category;
            var Job = new MoveJob { TitleId = TitleId, Title = Title, Category = AppCategory, ToExtended = ToExtended };
            Jobs.Insert(0, Job);
            _ = RunAsync(Job);
        }

        public static void Retry(MoveJob Job)
        {
            if (!Job.CanRetry || WhyNot() is { })
                return;
            Job.Failed = false;
            Job.IsRunning = true;
            _ = RunAsync(Job);
        }

        public static void Remove(MoveJob Job)
        {
            if (Job.CanRemove)
                Jobs.Remove(Job);
        }

        static void Ui(Action Change) => Dispatcher.UIThread.Post(Change);

        static async Task RunAsync(MoveJob Job)
        {
            var IP = App.Config.PSIP.Trim();
            bool Held = false;
            try
            {
                if (!Job.Uninstalled)
                {
                    await PrepareAsync(IP, Job);
                    Held = true;
                    await UninstallAsync(IP, Job);
                }

                await InstallAsync(IP, Job);
                var Note = Job.Parts.Count == 0 ? "" : Job.Parts.Any(x => x.Kind == "DLC" && x.Target.StartsWith(Root(!Job.ToExtended) + "/"))
                    ? " (a DLC the console kept registered stayed where it was)" : "";
                Ui(() =>
                {
                    Job.Status = $"Moved to {Job.Destination}.{Note}";
                    Job.IsRunning = false;
                });
            }
            catch (Exception ex)
            {
                string Where = $" The packages are kept on the console in {HoldOf(Job)}: press Retry to install them again.";
                string Why = ex is MoveException ? ex.Message : "Unexpected error: " + ex.Message;
                string Outcome = Job.Uninstalled ? Where
                    : Held ? $" Some packages may still be in {HoldOf(Job)}; check with FTP before starting the game."
                    : " Nothing was changed on the console.";
                Ui(() =>
                {
                    Job.Status = Why + Outcome;
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
            var Hold = HoldOf(Job);

            Ui(() => Job.Status = "Looking at the installed packages…");
            var Found = await WithFtp(async Ftp =>
            {
                var Parts = new List<MovePart>();
                var AppSize = await Ftp.FileSizeAsync($"{From}/user/app/{Tid}/app.pkg");
                if (AppSize <= 0)
                    throw new MoveException($"{Tid} isn't installed on {(Job.ToExtended ? "system" : "extended")} storage as a package that can be moved.");
                Parts.Add(new MovePart { Kind = "Game", Category = Job.Category, Source = $"{From}/user/app/{Tid}/app.pkg", Held = $"{Hold}/app.pkg", Target = $"{To}/user/app/{Tid}/app.pkg", Size = AppSize });

                var PatchSize = await Ftp.FileSizeAsync($"{From}/user/patch/{Tid}/patch.pkg");
                if (PatchSize > 0)
                    Parts.Add(new MovePart { Kind = "Update", Category = "gp", Source = $"{From}/user/patch/{Tid}/patch.pkg", Held = $"{Hold}/patch.pkg", Target = $"{To}/user/patch/{Tid}/patch.pkg", Size = PatchSize });

                List<FtpEntry> Dlcs;
                try { Dlcs = await Ftp.ListAsync($"{From}/user/addcont/{Tid}"); }
                catch (FtpException ex) when (ex.Reply is { Code: >= 500 }) { Dlcs = new List<FtpEntry>(); }
                foreach (var Label in Dlcs.Where(x => x.IsDirectory && x.Name != "." && x.Name != ".."))
                {
                    var Size = await Ftp.FileSizeAsync($"{From}/user/addcont/{Tid}/{Label.Name}/ac.pkg");
                    if (Size > 0)
                        Parts.Add(new MovePart { Kind = "DLC", Category = "ac", Source = $"{From}/user/addcont/{Tid}/{Label.Name}/ac.pkg", Held = $"{Hold}/dlc_{Label.Name}.pkg", Target = $"{To}/user/addcont/{Tid}/{Label.Name}/ac.pkg", Size = Size });
                }
                return Parts;
            });

            // content ID and package type from each header (BGFT needs them); a cut-off
            // transfer can leave the connection unusable, so each read gets its own
            foreach (var Part in Found)
            {
                var Head = await WithFtp(Ftp => Ftp.ReadHeadAsync(Part.Source, 0x100));
                if (Head.Length < 0x78 || Head[0] != 0x7F || Head[1] != (byte)'C' || Head[2] != (byte)'N' || Head[3] != (byte)'T')
                    throw new MoveException($"{Part.Source} isn't a package this can move.");
                Part.ContentId = Encoding.ASCII.GetString(Head, 0x40, 0x24).TrimEnd('\0');
                Part.HeaderType = BinaryPrimitives.ReadUInt32BigEndian(Head.AsSpan(0x74)) switch
                {
                    0x1A => "GD", 0x1B => "AC", 0x1C => "AL", 0x1E => "DP", _ => ""
                };
            }
            // unlock keys (AL) can't be installed by BGFT: leave them where they are
            Job.Parts = Found.Where(x => x.HeaderType != "AL").ToList();

            Job.Icon = await WithFtp(async Ftp =>
            {
                foreach (var IconPath in Job.ToExtended
                             ? new[] { $"/user/appmeta/{Tid}/icon0.png" }
                             : new[] { $"/user/appmeta/external/{Tid}/icon0.png", $"/user/appmeta/{Tid}/icon0.png" })
                {
                    try { return await Ftp.DownloadAsync(IconPath, 1024 * 1024); }
                    catch (FtpException) { }
                }
                return null;
            });

            // the destination needs room for everything (the console copies once)
            Ui(() => Job.Status = "Checking free space…");
            long Needed = Job.Parts.Sum(x => x.Size) + 1024L * 1024 * 1024;
            var Space = await Installer.Payload.QueryFreeSpaceAsync(IP, App.Config.PCIP)
                        ?? throw new MoveException("Couldn't read the console's free space: " + (Installer.LastError ?? "the payload didn't answer."));
            if (Job.ToExtended && !Space.HasExtended)
                throw new MoveException("No extended storage is connected.");
            ulong Free = Job.ToExtended ? Space.ExtendedFree : Space.InternalFree;
            if (Free < (ulong)Needed)
                throw new MoveException($"Not enough space on {Job.Destination}: needs {TransferProgressInfo.FormatBytes(Needed)}, {TransferProgressInfo.FormatBytes((long)Free)} free.");

            // set aside (same drive: instant), checking each rename by listing
            Ui(() => Job.Status = "Setting the packages aside…");
            await WithFtp(async Ftp =>
            {
                await Ftp.MakeDirAsync($"{From}{HoldFolder}");
                await Ftp.MakeDirAsync(Hold);
                return true;
            });

            try
            {
                foreach (var Part in Job.Parts)
                {
                    await WithFtp(async Ftp =>
                    {
                        // a retried rename may already have happened
                        if (await Ftp.FileSizeAsync(Part.Held) != Part.Size)
                            await Ftp.RenameAsync(Part.Source, Part.Held);
                        return true;
                    });
                    bool Moved = await WithFtp(async Ftp => await Ftp.FileSizeAsync(Part.Held) == Part.Size && await Ftp.FileSizeAsync(Part.Source) < 0);
                    if (!Moved)
                        throw new MoveException($"Couldn't set {Part.Source} aside.");
                }

                // the record that makes an interrupted move recoverable
                await WithFtp(async Ftp =>
                {
                    await using var Record = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(MoveRecord.Of(Job)));
                    await Ftp.UploadAsync($"{Hold}/{JobFile}", Record);
                    return true;
                });
            }
            catch (Exception ex)
            {
                // put back whatever was set aside, each on a fresh connection, and check
                bool AllBack = true;
                foreach (var Part in Job.Parts)
                {
                    try
                    {
                        AllBack &= await WithFtp(async Ftp =>
                        {
                            if (await Ftp.FileSizeAsync(Part.Held) == Part.Size && await Ftp.FileSizeAsync(Part.Source) < 0)
                                await Ftp.RenameAsync(Part.Held, Part.Source);
                            return await Ftp.FileSizeAsync(Part.Source) == Part.Size;
                        });
                    }
                    catch
                    {
                        AllBack = false;
                    }
                }

                if (!AllBack)
                    throw new MoveException((ex is MoveException ? ex.Message : ex.Message) + $" Not every package could be put back: check {Hold} with FTP before starting the game.");

                try { await WithFtp(async Ftp => { await Ftp.DeleteAsync($"{Hold}/{JobFile}"); return true; }); } catch { }
                try { await WithFtp(Ftp => Ftp.RemoveDirAsync(Hold)); } catch { }
                throw new MoveException(ex.Message + " Everything was put back.");
            }
        }

        /// <summary>Remove the old installation (RPI) and wait until the console has deleted it.</summary>
        static async Task UninstallAsync(string IP, MoveJob Job)
        {
            var From = Root(!Job.ToExtended);
            Task<bool> AppStillThere() => WithFtp(async Ftp => (await Ftp.ListAsync($"{From}/user/app")).Any(x => x.Name == Job.TitleId));

            Ui(() => Job.Status = "Removing the old copy…");
            var Error = await ConsoleActions.UninstallAsync(IP, new LibraryEntry { Category = "gd", TitleId = Job.TitleId, Title = Job.Title });
            if (Error != null)
            {
                // RPI may time out on a big title while the console carries on deleting
                await Task.Delay(5000);
                if (await AppStillThere())
                {
                    bool AllBack = true;
                    foreach (var Part in Job.Parts)
                    {
                        try
                        {
                            AllBack &= await WithFtp(async Ftp =>
                            {
                                await Ftp.RenameAsync(Part.Held, Part.Source);
                                return await Ftp.FileSizeAsync(Part.Source) == Part.Size;
                            });
                        }
                        catch { AllBack = false; }
                    }
                    if (AllBack)
                    {
                        try { await WithFtp(async Ftp => { await Ftp.DeleteAsync($"{HoldOf(Job)}/{JobFile}"); await Ftp.RemoveDirAsync(HoldOf(Job)); return true; }); } catch { }
                        throw new MoveException("Couldn't remove the old copy: " + Error + " Everything was put back.");
                    }
                    throw new MoveException("Couldn't remove the old copy: " + Error + $" Not every package could be put back: check {HoldOf(Job)} with FTP.");
                }
            }

            Job.Uninstalled = true;
            await SaveRecordAsync(Job);

            Ui(() => Job.Status = "Waiting for the console to finish removing it…");
            for (int i = 0; i < 60 && await AppStillThere(); i++)
                await Task.Delay(2000);
            if (await AppStillThere())
                throw new MoveException("The console hasn't finished removing the old copy after two minutes.");

            // DLC: give the console time too; one still registered then was kept by the uninstall
            for (int i = 0; i < 15; i++)
            {
                bool AddcontGone = await WithFtp(async Ftp =>
                {
                    try { return (await Ftp.ListAsync($"{From}/user/addcont")).All(x => x.Name != Job.TitleId); }
                    catch (FtpException ex) when (ex.Reply is { Code: >= 500 }) { return true; }
                });
                if (AddcontGone || !Job.Parts.Any(x => x.Kind == "DLC"))
                    break;
                await Task.Delay(2000);
            }

            foreach (var Part in Job.Parts.Where(x => x.Kind == "DLC" && !x.Installed))
            {
                var Folder = Part.Source.Substring(0, Part.Source.LastIndexOf('/'));
                bool Kept = await WithFtp(async Ftp =>
                {
                    try
                    {
                        var Parent = Folder.Substring(0, Folder.LastIndexOf('/'));
                        var Label = Folder.Substring(Folder.LastIndexOf('/') + 1);
                        return (await Ftp.ListAsync(Parent)).Any(x => x.IsDirectory && x.Name == Label);
                    }
                    catch (FtpException ex) when (ex.Reply is { Code: >= 500 }) { return false; }
                });
                if (!Kept)
                    continue;

                // registered where it was: its file goes back there
                await WithFtp(async Ftp => { await Ftp.RenameAsync(Part.Held, Part.Source); return true; });
                Part.Target = Part.Source;
                Part.Installed = true;
            }
            await SaveRecordAsync(Job);
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
                var Result = await Installer.Payload.SendLocalPackageAsync(IP, App.Config.PCIP, Part.Held, Info, Storage)
                             ?? throw new MoveException($"The console didn't take the {Part.Kind.ToLowerInvariant()}: " + (Installer.LastError ?? "no answer from the payload."));
                if (Result.Task < 0 || Result.Result != 0)
                    throw new MoveException($"The console refused the {Part.Kind.ToLowerInvariant()}: " +
                                            (InstallErrors.Explain($"0x{Result.Result:X8}") ?? $"error 0x{Result.Result:X8}") + ".");

                await WaitForTaskAsync(IP, Job, Part, Result.Task);

                Part.Installed = true;
                await SaveRecordAsync(Job);
                await WithFtp(async Ftp => { await Ftp.DeleteAsync(Part.Held); return true; });
            }

            try
            {
                await WithFtp(async Ftp =>
                {
                    var Hold = HoldOf(Job);
                    try { await Ftp.DeleteAsync($"{Hold}/{JobFile}"); } catch (FtpException) { }
                    await Ftp.RemoveDirAsync(Hold);
                    await Ftp.RemoveDirAsync($"{Root(!Job.ToExtended)}{HoldFolder}");
                    return true;
                });
            }
            catch { /* empty folders left behind are harmless */ }
        }

        /// <summary>
        /// Follow the console's task (RPI knows every BGFT task) until it finished without
        /// error and the installed file is complete. Only then may the held copy go.
        /// </summary>
        static async Task WaitForTaskAsync(string IP, MoveJob Job, MovePart Part, int Task)
        {
            var What = Part.Kind.ToLowerInvariant();
            int Misses = 0;
            var LastMove = DateTime.Now;
            long Last = -1;

            while (true)
            {
                await System.Threading.Tasks.Task.Delay(4000);

                var Progress = await RpiTasks.ProgressAsync(IP, Task);
                if (Progress == null)
                {
                    // RPI must confirm the end: without it the held copy is never deleted
                    if (++Misses >= 45)
                        throw new MoveException($"Remote Package Installer stopped answering while the {What} was installing, so the move can't confirm it finished.");
                    continue;
                }
                Misses = 0;

                if (Progress.Error != 0)
                    throw new MoveException($"Installing the {What} failed: " + InstallErrors.Describe($"0x{Progress.Error:X8}"));

                if (Progress.Finished)
                {
                    bool Complete = await WithFtp(async Ftp => await Ftp.FileSizeAsync(Part.Target) == Part.Size);
                    if (Complete)
                        return;
                }

                if (Progress.Transferred != Last)
                {
                    Last = Progress.Transferred;
                    LastMove = DateTime.Now;
                }
                else if (DateTime.Now - LastMove > TimeSpan.FromMinutes(15))
                    throw new MoveException($"The {What} stopped copying (check the console's Notifications › Downloads).");

                var Done = Progress.Length > 0 ? $"{TransferProgressInfo.FormatBytes(Progress.Transferred)} of {TransferProgressInfo.FormatBytes(Progress.Length)}" : "starting";
                Ui(() => Job.Status = $"Copying the {What} to {Job.Destination}… {Done}");
            }
        }

        static async Task SaveRecordAsync(MoveJob Job)
        {
            try
            {
                await WithFtp(async Ftp =>
                {
                    await using var Record = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(MoveRecord.Of(Job)));
                    await Ftp.UploadAsync($"{HoldOf(Job)}/{JobFile}", Record);
                    return true;
                });
            }
            catch { /* the move itself doesn't depend on the record */ }
        }

        static bool Recovered;

        /// <summary>
        /// Look for moves interrupted by closing the app (job.json in the hold folders)
        /// and list them as failed, ready for Retry. Once per run, when FTP answers.
        /// </summary>
        public static async Task RecoverAsync()
        {
            if (Recovered || !ConsoleStatus.Instance.FtpOpen)
                return;
            Recovered = true;

            foreach (var Root in new[] { "", ConsoleInventory.ExtRoot })
            {
                try
                {
                    var Found = await WithFtp(async Ftp =>
                    {
                        var Records = new List<MoveJob>();
                        List<FtpEntry> Titles;
                        try { Titles = await Ftp.ListAsync(Root + HoldFolder); }
                        catch (FtpException) { return Records; }
                        foreach (var Tid in Titles.Where(x => x.IsDirectory && x.Name != "." && x.Name != ".."))
                        {
                            try
                            {
                                var Data = await Ftp.DownloadAsync($"{Root}{HoldFolder}/{Tid.Name}/{JobFile}", 1024 * 1024);
                                if (JsonSerializer.Deserialize<MoveRecord>(Data) is { } Record && Record.Parts.Count > 0)
                                    Records.Add(Record.ToJob());
                            }
                            catch (FtpException) { }
                        }
                        return Records;
                    });

                    foreach (var Job in Found.Where(j => Jobs.All(x => x.TitleId != j.TitleId)))
                    {
                        Job.IsRunning = false;
                        Job.Failed = true;
                        Job.Status = Job.Uninstalled
                            ? $"This move was interrupted. The packages are kept on the console in {HoldOf(Job)}: press Retry to finish installing them."
                            : $"This move was interrupted before the old copy was removed. Check {HoldOf(Job)} with FTP: its packages may need to go back.";
                        Ui(() => Jobs.Add(Job));
                    }
                }
                catch { }
            }
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
                catch (MoveException) { throw; }
                catch (Exception ex)
                {
                    Last = ex;
                    if (Attempt < Attempts)
                        await Task.Delay(1000);
                }
            }
            throw new MoveException("GoldHEN's FTP server stopped answering." + (Last != null ? $" ({Last.Message})" : ""));
        }
    }
}
