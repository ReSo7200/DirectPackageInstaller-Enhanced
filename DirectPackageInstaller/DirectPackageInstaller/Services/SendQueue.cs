using System;
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

        SendQueue()
        {
            PS4Server.GlobalTransferProgressChanged += OnTransferProgress;
            Items.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(PendingCount));
        }

        public void Enqueue(System.Collections.Generic.IEnumerable<LibraryEntry> Entries)
        {
            foreach (var Entry in Entries)
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
                    await PushAsync(Item);
            }
            finally
            {
                IsRunning = false;
            }
        }

        async Task PushAsync(QueueItem Item)
        {
            Item.State = QueueState.Pushing;
            Item.Message = "";

            if (!File.Exists(Item.Path))
            {
                Fail(Item, "File not found. Was it moved or deleted?");
                return;
            }

            var PreviousPKG = Installer.CurrentPKG;
            try
            {
                using var Stream = new FileStream(Item.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var Info = Stream.GetPKGInfo();
                if (Info == null)
                {
                    Fail(Item, "Not a readable PS4 package.");
                    return;
                }

                Installer.CurrentPKG = Info.Value;
                Stream.Position = 0;

                string LastStatus = "";
                bool OK = await Installer.PushPackage(App.Config, Source.File, Stream, Item.Path, null, null,
                    Status =>
                    {
                        LastStatus = Status;
                        Dispatcher.UIThread.Post(() => Item.Message = Status);
                        return Task.CompletedTask;
                    },
                    () => LastStatus,
                    Silent: true);

                if (!OK)
                {
                    Fail(Item, "The console didn't accept the package. Check that GoldHEN's payload server, RPI or etaHEN is running.");
                    return;
                }

                // progress events may already have moved it on
                if (Item.State == QueueState.Pushing)
                {
                    Item.State = QueueState.Queued;
                    Item.Message = "The PS4 will start downloading shortly.";
                }
            }
            catch (Exception ex)
            {
                Fail(Item, ex.Message);
            }
            finally
            {
                Installer.CurrentPKG = PreviousPKG;
            }
        }

        static void Fail(QueueItem Item, string Message)
        {
            Item.State = QueueState.Failed;
            Item.Message = Message;
        }

        void OnTransferProgress(TransferProgressInfo Info)
        {
            var File = FileFromRequest(Info.RequestPath);
            if (File == null)
                return;

            Dispatcher.UIThread.Post(() =>
            {
                var Item = Items.LastOrDefault(x => string.Equals(x.Path, File, StringComparison.OrdinalIgnoreCase)
                                                   && x.State is QueueState.Pushing or QueueState.Queued or QueueState.Downloading or QueueState.Done);
                if (Item == null)
                    return;

                Item.Progress = Math.Round(Info.Percent * 100, 1);

                if (Info.Completed && Info.BytesSent >= Info.TotalBytes)
                {
                    Item.State = QueueState.Done;
                    Item.Message = $"Sent {TransferProgressInfo.FormatBytes(Info.TotalBytes)}";
                }
                else if (Item.State != QueueState.Pushing)
                {
                    Item.State = QueueState.Downloading;
                    Item.Message = $"{TransferProgressInfo.FormatBytes(Info.BytesSent)} of {TransferProgressInfo.FormatBytes(Info.TotalBytes)}  ·  {TransferProgressInfo.FormatBytes(Info.BytesPerSecond)}/s";
                }
            });
        }

        /// <summary>Local file behind a /file/?b64=... request, or null.</summary>
        static string? FileFromRequest(string RequestPath)
        {
            try
            {
                var Query = RequestPath.Contains('?') ? RequestPath.Substring(RequestPath.IndexOf('?') + 1) : "";
                // the console appends its own "?product=..." after ours
                if (Query.Contains('?'))
                    Query = Query.Substring(0, Query.IndexOf('?'));

                var B64 = HttpUtility.ParseQueryString(Query)["b64"];
                if (string.IsNullOrEmpty(B64))
                    return null;

                return Encoding.UTF8.GetString(Convert.FromBase64String(B64));
            }
            catch
            {
                return null;
            }
        }
    }
}
