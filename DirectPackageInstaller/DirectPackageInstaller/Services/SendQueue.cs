using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using Avalonia.Threading;
using DirectPackageInstaller.Host;
using DirectPackageInstaller.Tasks;
using ReactiveUI;

namespace DirectPackageInstaller.Services
{
    public enum QueueState
    {
        Waiting,
        Pushing,
        /// <summary>The console accepted it; waiting for it to start downloading.</summary>
        Queued,
        Downloading,
        Done,
        Failed
    }

    /// <summary>Union of byte ranges the console has fully received.</summary>
    sealed class ByteRanges
    {
        readonly List<(long Start, long End)> Ranges = new(); // [Start, End), sorted, merged

        public void Add(long Start, long End)
        {
            if (End <= Start)
                return;

            Ranges.Add((Start, End));
            Ranges.Sort((a, b) => a.Start.CompareTo(b.Start));

            var Merged = new List<(long Start, long End)>();
            foreach (var R in Ranges)
            {
                if (Merged.Count > 0 && R.Start <= Merged[^1].End)
                    Merged[^1] = (Merged[^1].Start, Math.Max(Merged[^1].End, R.End));
                else
                    Merged.Add(R);
            }

            Ranges.Clear();
            Ranges.AddRange(Merged);
        }

        public long Covered => Ranges.Sum(x => x.End - x.Start);

        public void Clear() => Ranges.Clear();
    }

    public sealed class QueueItem : ReactiveObject
    {
        public QueueItem(LibraryEntry Entry)
        {
            this.Entry = Entry;
        }

        public LibraryEntry Entry { get; }
        public string Path => Entry.Path;
        public string Title => Entry.Title;
        public string Kind => Entry.Kind;
        public string Detail => $"{Entry.TitleId}  ·  v{Entry.AppVersion}  ·  {TransferProgressInfo.FormatBytes(Entry.Size)}";

        long? _TaskId;
        /// <summary>RPI task on the console: progress then comes from the console itself.</summary>
        public long? TaskId
        {
            get => _TaskId;
            set { this.RaiseAndSetIfChanged(ref _TaskId, value); RaiseControls(); }
        }

        bool _IsPaused;
        public bool IsPaused
        {
            get => _IsPaused;
            set { this.RaiseAndSetIfChanged(ref _IsPaused, value); RaiseControls(); }
        }

        public bool CanPause => TaskId != null && !IsPaused && !IsFinished;
        public bool CanResume => TaskId != null && IsPaused && !IsFinished;
        public bool CanCancel => TaskId != null && !IsFinished;

        void RaiseControls()
        {
            this.RaisePropertyChanged(nameof(CanPause));
            this.RaisePropertyChanged(nameof(CanResume));
            this.RaisePropertyChanged(nameof(CanCancel));
        }

        /// <summary>Last time the console fetched part of this file.</summary>
        internal DateTime LastActivity = DateTime.UtcNow;

        /// <summary>Completed ranges of the file (the console may fetch it in pieces).</summary>
        internal readonly ByteRanges Received = new();

        QueueState _State = QueueState.Waiting;
        public QueueState State
        {
            get => _State;
            set
            {
                this.RaiseAndSetIfChanged(ref _State, value);
                this.RaisePropertyChanged(nameof(StateText));
                this.RaisePropertyChanged(nameof(IsFinished));
                this.RaisePropertyChanged(nameof(CanRetry));
                this.RaisePropertyChanged(nameof(StateBrush));
                this.RaisePropertyChanged(nameof(ShowProgress));
                RaiseControls();
            }
        }

        public string StateText => State switch
        {
            QueueState.Waiting => "Waiting",
            QueueState.Pushing => "Sending to PS4",
            QueueState.Queued => "Queued on PS4",
            QueueState.Downloading => "Downloading",
            QueueState.Done => "Downloaded",
            QueueState.Failed => "Failed",
            _ => ""
        };

        public Avalonia.Media.IBrush StateBrush => ViewModels.LibraryItem.Brush(State switch
        {
            QueueState.Done => "Go",
            QueueState.Failed => "Alarm",
            QueueState.Waiting => "Muted",
            _ => "Signal"
        });

        public bool ShowProgress => State is QueueState.Downloading or QueueState.Done;

        Avalonia.Media.Imaging.Bitmap? _Cover;
        bool CoverLoaded;
        public Avalonia.Media.Imaging.Bitmap? Cover
        {
            get
            {
                if (!CoverLoaded)
                {
                    CoverLoaded = true;
                    try
                    {
                        if (Entry.IconFile != null && File.Exists(Entry.IconFile))
                        {
                            using var Stream = File.OpenRead(Entry.IconFile);
                            _Cover = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(Stream, 112);
                        }
                    }
                    catch { }
                }
                return _Cover;
            }
        }

        public bool IsFinished => State is QueueState.Done or QueueState.Failed;
        public bool CanRetry => State == QueueState.Failed;

        string _Message = "";
        public string Message
        {
            get => _Message;
            set => this.RaiseAndSetIfChanged(ref _Message, value);
        }

        double _Progress;
        /// <summary>0..100</summary>
        public double Progress
        {
            get => _Progress;
            set => this.RaiseAndSetIfChanged(ref _Progress, value);
        }
    }

    /// <summary>
    /// Sends local PKGs one after another. Each push only has to be accepted
    /// by the console (it queues downloads itself), so the next item goes out
    /// right after; download progress is then tracked from the file server.
    /// </summary>
    public sealed class SendQueue : ReactiveObject
    {
        public static SendQueue Instance { get; } = new SendQueue();

        public ObservableCollection<QueueItem> Items { get; } = new();

        bool _IsRunning;
        public bool IsRunning
        {
            get => _IsRunning;
            private set => this.RaiseAndSetIfChanged(ref _IsRunning, value);
        }

        public int PendingCount => Items.Count(x => !x.IsFinished);

        readonly DispatcherTimer Watchdog;

        /// <summary>No requests for this long with (nearly) everything sent means downloaded.</summary>
        public static TimeSpan IdleDone { get; set; } = TimeSpan.FromSeconds(45);

        SendQueue()
        {
            PS4Server.GlobalTransferProgressChanged += OnTransferProgress;

            // Safety net for the PC-side estimate: if the console stopped asking for
            // a file that was sent except for the last few MB, it has it.
            Watchdog = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) =>
            {
                foreach (var Item in Items.Where(x => x.State == QueueState.Downloading && x.TaskId == null))
                {
                    long Size = Item.Entry.Size;
                    // a few MB of slack only: a console that stopped at 99% of 100 GB is not done
                    if (Size > 0 && DateTime.UtcNow - Item.LastActivity > IdleDone && Item.Received.Covered >= Size - 4L * 1024 * 1024)
                    {
                        Item.State = QueueState.Done;
                        Item.Progress = 100;
                        Item.Message = $"Sent {TransferProgressInfo.FormatBytes(Size)}";
                    }
                }
            });
            Watchdog.Start();
            Items.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(PendingCount));
        }

        static int KindOrder(LibraryEntry Entry) => Entry.Kind switch { "Game" => 0, "Update" => 1, "DLC" => 2, _ => 3 };

        /// <summary>
        /// Queue packages. Within one title the base game goes first, then
        /// updates (oldest first), then DLC: the order the console needs them.
        /// </summary>
        public void Enqueue(IEnumerable<LibraryEntry> Entries)
        {
            var Ordered = Entries
                .Select((Entry, Index) => (Entry, Index))
                .OrderBy(x => x.Index) // keep the user's title order...
                .GroupBy(x => string.IsNullOrEmpty(x.Entry.TitleId) ? x.Entry.Path : x.Entry.TitleId)
                .SelectMany(g => g.OrderBy(x => KindOrder(x.Entry)).ThenBy(x => x.Entry.AppVersion).Select(x => x.Entry)); // ...but base game first

            foreach (var Entry in Ordered)
            {
                // don't queue the same file twice while it is still pending
                if (Items.Any(x => !x.IsFinished && string.Equals(x.Path, Entry.Path, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var Item = new QueueItem(Entry);
                Item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(QueueItem.State))
                        this.RaisePropertyChanged(nameof(PendingCount));
                };
                Items.Add(Item);
            }

            _ = RunAsync();
        }

        public void Retry(QueueItem Item)
        {
            Item.State = QueueState.Waiting;
            Item.Message = "";
            Item.Progress = 0;
            Item.Received.Clear();
            // a new push gets a new console task (or none): never control the old one
            Item.TaskId = null;
            Item.IsPaused = false;
            _ = RunAsync();
        }

        public void Remove(QueueItem Item)
        {
            if (Item.State == QueueState.Pushing)
                return;
            Items.Remove(Item);
        }

        public void ClearFinished()
        {
            foreach (var Item in Items.Where(x => x.IsFinished).ToList())
                Items.Remove(Item);
        }

        async Task RunAsync()
        {
            if (IsRunning)
                return;

            IsRunning = true;
            try
            {
                while (Items.FirstOrDefault(x => x.State == QueueState.Waiting) is { } Item)
                {
                    // nothing can be sent without both addresses: fail the rest at once
                    // instead of one dialog/attempt per item
                    var Missing = MissingAddress();
                    if (Missing != null)
                    {
                        foreach (var Waiting in Items.Where(x => x.State == QueueState.Waiting).ToList())
                            Fail(Waiting, Missing);
                        break;
                    }

                    await PushAsync(Item);
                }
            }
            finally
            {
                IsRunning = false;
            }
        }

        static string? MissingAddress()
        {
            static bool Unset(string? IP) => string.IsNullOrWhiteSpace(IP) || IP == "0.0.0.0";

            if (Unset(App.Config.PSIP))
                return "The console address isn't set. Enter it in Settings, then press Retry.";
            if (Unset(App.Config.PCIP))
                return "This PC's address isn't set. Pick it in Settings, then press Retry.";
            return null;
        }

        async Task PushAsync(QueueItem Item)
        {
            Item.State = QueueState.Pushing;
            Item.Message = "";
            Item.Received.Clear();
            Item.TaskId = null;
            Item.IsPaused = false;

            if (!File.Exists(Item.Path))
            {
                Fail(Item, "File not found. Was it moved or deleted?");
                return;
            }

            try
            {
                // split packages read as one (the PS4 must be told the full size)
                using var Stream = SplitPackages.Open(Item.Path);
                var Info = Stream.GetPKGInfo();
                if (Info == null)
                {
                    Fail(Item, "Not a readable PS4 package.");
                    return;
                }

                Stream.Position = 0;

                string LastStatus = "";
                bool OK;
                bool AlreadyInstalled;
                string? Error;
                long? TaskId;

                // CurrentPKG is shared with Direct link: set it and push under the lock
                await Installer.PushLock.WaitAsync();
                try
                {
                    Installer.CurrentPKG = Info.Value;
                    OK = await Installer.PushPackage(App.Config, Source.File, Stream, Item.Path, null, null,
                        Status =>
                        {
                            LastStatus = Status;
                            Dispatcher.UIThread.Post(() => Item.Message = Status);
                            return Task.CompletedTask;
                        },
                        () => LastStatus,
                        Silent: true);

                    AlreadyInstalled = Installer.LastAlreadyInstalled;
                    Error = Installer.LastError;
                    TaskId = Installer.LastTaskId;
                }
                finally
                {
                    Installer.PushLock.Release();
                }

                if (!OK)
                {
                    Fail(Item, Error ?? "The console didn't accept the package. Check that GoldHEN's payload server, RPI or etaHEN is running.");
                    return;
                }

                if (AlreadyInstalled)
                {
                    Item.State = QueueState.Done;
                    Item.Progress = 100;
                    Item.Message = "Already installed on the console.";
                    return;
                }

                // progress events may already have moved it on
                if (Item.State == QueueState.Pushing)
                {
                    Item.State = QueueState.Queued;
                    Item.Message = "The PS4 will start downloading shortly.";
                }

                // RPI: follow the console's own task (progress, stalls, pause/resume)
                if (TaskId != null)
                {
                    Item.TaskId = TaskId;
                    _ = MonitorAsync(Item, App.Config.PSIP);
                }
            }
            catch (Exception ex)
            {
                Fail(Item, ex.Message);
            }
        }

        /// <summary>Restart a task after this long without progress.</summary>
        public static TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(90);
        public static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(3);
        const int MaxAutoResumes = 3;

        /// <summary>
        /// Poll the console's task until it's downloaded or fails. When the byte
        /// count stops moving for StallTimeout, pause and resume the task (a
        /// common fix for stuck downloads), at most MaxAutoResumes times.
        /// </summary>
        internal async Task MonitorAsync(QueueItem Item, string ConsoleIP)
        {
            long Last = -1;
            var LastMove = DateTime.UtcNow;
            int Resumes = 0, Misses = 0;
            string Note = "";

            void Ui(Action Change) => Dispatcher.UIThread.Post(Change);

            while (Item.TaskId is long Id && !Item.IsFinished)
            {
                await Task.Delay(PollInterval);

                if (Item.IsPaused)
                {
                    LastMove = DateTime.UtcNow;
                    continue;
                }

                var Progress = await RpiTasks.ProgressAsync(ConsoleIP, Id);
                if (Progress == null)
                {
                    // RPI closed or busy: keep the PC-side estimate meanwhile
                    if (++Misses == 10)
                        Ui(() => Item.Message = "Remote Package Installer stopped answering; the download may still be running.");
                    continue;
                }
                Misses = 0;

                if (Progress.Error != 0)
                {
                    var Reason = InstallErrors.Describe($"0x{Progress.Error:X8}");
                    Ui(() => Fail(Item, Reason));
                    return;
                }

                if (Progress.Finished)
                {
                    Ui(() =>
                    {
                        Item.State = QueueState.Done;
                        Item.Progress = 100;
                        Item.Message = "Downloaded. The console is installing it.";
                    });
                    return;
                }

                if (Progress.Transferred != Last)
                {
                    Last = Progress.Transferred;
                    LastMove = DateTime.UtcNow;
                }
                else if (DateTime.UtcNow - LastMove > StallTimeout && Resumes < MaxAutoResumes)
                {
                    Resumes++;
                    LastMove = DateTime.UtcNow;
                    await RpiTasks.ControlAsync(ConsoleIP, Id, "pause_task");
                    await Task.Delay(2000);
                    await RpiTasks.ControlAsync(ConsoleIP, Id, "resume_task");
                    Note = $"  ·  stalled, restarted {Resumes}x";
                }

                var Text = $"{TransferProgressInfo.FormatBytes(Progress.Transferred)} of {TransferProgressInfo.FormatBytes(Progress.Length)}"
                           + (Progress.RestSeconds > 0 ? $"  ·  {FormatRest(Progress.RestSeconds)} left" : "")
                           + Note;
                Ui(() =>
                {
                    if (Item.IsFinished)
                        return;
                    Item.State = QueueState.Downloading;
                    Item.Progress = Math.Round(Progress.Percent, 1);
                    Item.Message = Text;
                });
            }
        }

        static string FormatRest(long Seconds) =>
            Seconds >= 3600 ? $"{Seconds / 3600} h {Seconds % 3600 / 60} min" : Seconds >= 60 ? $"{Seconds / 60} min" : $"{Seconds} s";

        public async Task PauseAsync(QueueItem Item)
        {
            if (Item.TaskId is long Id && await RpiTasks.ControlAsync(App.Config.PSIP, Id, "pause_task"))
            {
                Item.IsPaused = true;
                Item.Message = "Paused on the console.";
            }
        }

        public async Task ResumeAsync(QueueItem Item)
        {
            if (Item.TaskId is long Id && await RpiTasks.ControlAsync(App.Config.PSIP, Id, "resume_task"))
            {
                Item.IsPaused = false;
                Item.Message = "Resuming...";
            }
        }

        public async Task CancelAsync(QueueItem Item)
        {
            if (Item.TaskId is long Id && await RpiTasks.ControlAsync(App.Config.PSIP, Id, "stop_task"))
                Fail(Item, "Cancelled. Delete the partial download from Notifications > Downloads on the console.");
        }

        static void Fail(QueueItem Item, string Message)
        {
            Item.State = QueueState.Failed;
            Item.Message = Message;
        }

        void OnTransferProgress(TransferProgressInfo Info)
        {
            var Request = FileFromRequest(Info.RequestPath);
            if (Request == null)
                return;

            var (File, PieceOffset) = Request.Value;

            Dispatcher.UIThread.Post(() =>
            {
                var Item = Items.LastOrDefault(x => string.Equals(x.Path, File, StringComparison.OrdinalIgnoreCase)
                                                   && x.State is QueueState.Pushing or QueueState.Queued or QueueState.Downloading or QueueState.Done);
                if (Item == null)
                    return;

                // the console reports this one itself (RPI task)
                if (Item.TaskId != null && Item.State != QueueState.Pushing)
                    return;

                // Positions are relative to the served piece; AutoSplit pieces start at PieceOffset.
                long FileSize = Item.Entry.Size > 0 ? Item.Entry.Size : Info.TotalBytes;
                long ResponseStart = PieceOffset + Info.BytesSent - Info.ResponseBytesSent;

                // every event says how far this response got: count what was
                // actually sent (a dropped connection never reports "complete")
                Item.Received.Add(ResponseStart, ResponseStart + Info.ResponseBytesSent);
                Item.LastActivity = DateTime.UtcNow;

                // done only when every byte of the file arrived, whatever order the ranges came in
                long Have = Math.Min(FileSize, Item.Received.Covered);
                Item.Progress = FileSize <= 0 ? 0 : Math.Round(Have * 100.0 / FileSize, 1);

                if (Item.Received.Covered >= FileSize && FileSize > 0)
                {
                    Item.State = QueueState.Done;
                    Item.Progress = 100;
                    Item.Message = $"Sent {TransferProgressInfo.FormatBytes(FileSize)}";
                }
                else if (Item.State != QueueState.Pushing && Item.State != QueueState.Done)
                {
                    Item.State = QueueState.Downloading;
                    Item.Message = $"{TransferProgressInfo.FormatBytes(Have)} of {TransferProgressInfo.FormatBytes(FileSize)}  ·  {TransferProgressInfo.FormatBytes(Info.BytesPerSecond)}/s";
                }
            });
        }

        /// <summary>
        /// Local file behind a request, with the piece offset: /file/?b64=PATH,
        /// or /split/?b64=URL&amp;offset=N where URL is itself a /file/?b64=PATH link.
        /// </summary>
        static (string File, long Offset)? FileFromRequest(string RequestPath)
        {
            try
            {
                long Offset = 0;
                var Current = RequestPath;

                for (int Depth = 0; Depth < 3; Depth++)
                {
                    var Query = Current.Contains('?') ? Current.Substring(Current.IndexOf('?') + 1) : "";
                    // the console appends its own "?product=..." after ours
                    if (Query.Contains('?'))
                        Query = Query.Substring(0, Query.IndexOf('?'));

                    var Values = HttpUtility.ParseQueryString(Query);
                    var B64 = Values["b64"];
                    if (string.IsNullOrEmpty(B64))
                        return null;

                    if (long.TryParse(Values["offset"], out var PieceOffset))
                        Offset += PieceOffset;

                    var Decoded = Encoding.UTF8.GetString(Convert.FromBase64String(B64));
                    if (!Decoded.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        return (Decoded, Offset);

                    Current = Decoded;
                }
            }
            catch
            {
            }

            return null;
        }
    }
}
