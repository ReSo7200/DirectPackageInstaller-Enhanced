using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using DirectPackageInstaller.Host;
using DirectPackageInstaller.Others;
using ReactiveUI;

namespace DirectPackageInstaller.Services
{
    /// <summary>The latest official update of a title, from Sony's title-patch XML.</summary>
    public sealed record OfficialPatch(string TitleId, string Version, long Size, string ManifestUrl, string ContentId, string SystemVersion);

    public enum DownloadState { Downloading, Verifying, Done, Failed, Cancelled }

    public sealed class UpdateDownload : ReactiveObject
    {
        public UpdateDownload(OfficialPatch Patch, string Title, string Folder)
        {
            this.Patch = Patch;
            this.Title = Title;
            this.Folder = Folder;
        }

        public OfficialPatch Patch { get; }
        public string Title { get; }
        public string Folder { get; }
        public string Detail => $"{Patch.TitleId}  ·  update v{Patch.Version}  ·  {TransferProgressInfo.FormatBytes(Patch.Size)}  ·  to {Folder}";

        internal readonly CancellationTokenSource Cancellation = new();

        DownloadState _State = DownloadState.Downloading;
        public DownloadState State
        {
            get => _State;
            set
            {
                this.RaiseAndSetIfChanged(ref _State, value);
                this.RaisePropertyChanged(nameof(StateText));
                this.RaisePropertyChanged(nameof(CanCancel));
                this.RaisePropertyChanged(nameof(IsFinished));
            }
        }

        public string StateText => State switch
        {
            DownloadState.Downloading => "Downloading",
            DownloadState.Verifying => "Checking",
            DownloadState.Done => "Downloaded",
            DownloadState.Failed => "Failed",
            _ => "Cancelled"
        };

        public bool CanCancel => State is DownloadState.Downloading or DownloadState.Verifying;
        public bool IsFinished => !CanCancel;

        double _Progress;
        public double Progress
        {
            get => _Progress;
            set => this.RaiseAndSetIfChanged(ref _Progress, value);
        }

        string _Message = "";
        public string Message
        {
            get => _Message;
            set => this.RaiseAndSetIfChanged(ref _Message, value);
        }
    }

    /// <summary>
    /// Downloads official updates from Sony's CDN (the manifest the console
    /// itself uses): every piece is resumable and checked against its SHA-1.
    /// Pieces keep Sony's names, so a multi-piece update lands as a split
    /// package (name_0.pkg, name_1.pkg, ...) that the library reads as one.
    /// </summary>
    public sealed class UpdateDownloads : ReactiveObject
    {
        public static UpdateDownloads Instance { get; } = new();

        public ObservableCollection<UpdateDownload> Items { get; } = new();

        /// <summary>Raised on the UI thread when a download finished (the library can rescan).</summary>
        public event Action<UpdateDownload>? Finished;

        static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

        /// <summary>How long a piece may go without a byte before we call it stalled.</summary>
        static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

        /// <summary>How long a piece's connect + response headers may take before we call it stalled.</summary>
        static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(20);

        /// <summary>Latest official update with its download manifest, or null (no updates / offline).</summary>
        public static async Task<OfficialPatch?> LatestAsync(string TitleId, CancellationToken Token = default)
        {
            try
            {
                using var Short = CancellationTokenSource.CreateLinkedTokenSource(Token);
                Short.CancelAfter(TimeSpan.FromSeconds(15));
                var Xml = await Http.GetStringAsync(PatchInfo.UrlFor(TitleId), Short.Token);

                var Package = Regex.Match(Xml, "<package\\b[^>]*>");
                if (!Package.Success)
                    return null;

                string Attr(string Name) => Regex.Match(Package.Value, "\\b" + Name + "=\"([^\"]*)\"").Groups[1].Value;

                long.TryParse(Attr("size"), out var Size);
                uint.TryParse(Attr("system_ver"), out var SystemVer);
                var Firmware = SystemVer == 0 ? "" : $"{(SystemVer >> 24) & 0xFF:X}.{(SystemVer >> 16) & 0xFF:X2}";

                var Manifest = Attr("manifest_url");
                return Manifest.Length == 0 ? null : new OfficialPatch(TitleId, Attr("version"), Size, Manifest, Attr("content_id"), Firmware);
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        public UpdateDownload Start(OfficialPatch Patch, string Title, string Folder)
        {
            var Download = new UpdateDownload(Patch, Title, Folder);
            Items.Insert(0, Download);
            _ = RunAsync(Download);
            return Download;
        }

        public void Cancel(UpdateDownload Download) => Download.Cancellation.Cancel();

        public void Remove(UpdateDownload Download)
        {
            if (Download.IsFinished)
                Items.Remove(Download);
        }

        static void Ui(Action Change) => Dispatcher.UIThread.Post(Change);

        async Task RunAsync(UpdateDownload Download)
        {
            var Token = Download.Cancellation.Token;
            try
            {
                Directory.CreateDirectory(Download.Folder);

                var ManifestJson = await Http.GetStringAsync(Download.Patch.ManifestUrl, Token);
                var Manifest = JsonSerializer.Deserialize<PKGManifest>(ManifestJson, JSONContext.Default.Options);
                var Pieces = (Manifest.pieces ?? Array.Empty<PkgPiece>()).OrderBy(x => x.fileOffset).ToArray();
                if (Pieces.Length == 0)
                    throw new InvalidDataException("The update manifest lists no files.");

                long Total = Pieces.Sum(x => x.fileSize);
                long DoneBefore = 0;

                foreach (var Piece in Pieces)
                {
                    var Name = Path.GetFileName(new Uri(Piece.url).LocalPath);
                    var Target = Path.Combine(Download.Folder, Name);

                    if (!(File.Exists(Target) && new FileInfo(Target).Length == Piece.fileSize))
                        await DownloadPieceAsync(Download, Piece, Target, DoneBefore, Total, Token);

                    DoneBefore += Piece.fileSize;
                }

                Ui(() =>
                {
                    Download.State = DownloadState.Done;
                    Download.Progress = 100;
                    Download.Message = $"Saved {TransferProgressInfo.FormatBytes(Total)}. It's in your library now.";
                    Finished?.Invoke(Download);
                });
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested)
            {
                Ui(() =>
                {
                    Download.State = DownloadState.Cancelled;
                    Download.Message = "Cancelled. Start it again to continue where it stopped.";
                });
            }
            catch (Exception ex)
            {
                Ui(() =>
                {
                    Download.State = DownloadState.Failed;
                    Download.Message = ex.Message;
                });
            }
        }

        /// <summary>Download one piece to Target (resuming a partial ".download" file), then verify its SHA-1.</summary>
        static async Task DownloadPieceAsync(UpdateDownload Download, PkgPiece Piece, string Target, long DoneBefore, long Total, CancellationToken Token)
        {
            var Partial = Target + ".download";
            long Have = File.Exists(Partial) ? new FileInfo(Partial).Length : 0;
            if (Have > Piece.fileSize)
            {
                File.Delete(Partial);
                Have = 0;
            }

            if (Have < Piece.fileSize)
            {
                using var Request = new HttpRequestMessage(HttpMethod.Get, Piece.url);
                if (Have > 0)
                    Request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(Have, null);

                HttpResponseMessage Response;
                using (var HdrCts = CancellationTokenSource.CreateLinkedTokenSource(Token))
                {
                    HdrCts.CancelAfter(HeaderTimeout);
                    try
                    {
                        Response = await Http.SendAsync(Request, HttpCompletionOption.ResponseHeadersRead, HdrCts.Token);
                    }
                    catch (OperationCanceledException) when (!Token.IsCancellationRequested)
                    {
                        throw new IOException($"PlayStation Network didn't answer in {(int)HeaderTimeout.TotalSeconds}s. Start it again to retry.");
                    }
                }
                using (Response)
                {
                    Response.EnsureSuccessStatusCode();

                // server ignored the range: start over
                if (Have > 0 && Response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                    Have = 0;

                await using var Input = await Response.Content.ReadAsStreamAsync(Token);
                await using var Output = new FileStream(Partial, Have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024);

                var Buffer = new byte[1024 * 1024];
                var Started = DateTime.UtcNow;
                long SessionStart = Have;
                var LastUi = DateTime.MinValue;
                int Read;
                while (true)
                {
                    // per-read stall guard: if no bytes arrive within ReadTimeout the socket has stalled;
                    // throw a timeout so the download can be resumed instead of hanging forever
                    using var ReadCts = CancellationTokenSource.CreateLinkedTokenSource(Token);
                    ReadCts.CancelAfter(ReadTimeout);
                    try
                    {
                        Read = await Input.ReadAsync(Buffer, ReadCts.Token);
                    }
                    catch (OperationCanceledException) when (!Token.IsCancellationRequested)
                    {
                        throw new IOException($"The download stalled (no data for {(int)ReadTimeout.TotalSeconds}s). Start it again to continue where it stopped.");
                    }
                    if (Read <= 0)
                        break;

                    await Output.WriteAsync(Buffer.AsMemory(0, Read), Token);
                    Have += Read;

                    if ((DateTime.UtcNow - LastUi).TotalMilliseconds > 400)
                    {
                        LastUi = DateTime.UtcNow;
                        double Speed = (Have - SessionStart) / Math.Max(0.5, (DateTime.UtcNow - Started).TotalSeconds);
                        long Overall = DoneBefore + Have;
                        Ui(() =>
                        {
                            Download.Progress = Math.Round(Overall * 100.0 / Math.Max(1, Total), 1);
                            Download.Message = $"{TransferProgressInfo.FormatBytes(Overall)} of {TransferProgressInfo.FormatBytes(Total)}  ·  {TransferProgressInfo.FormatBytes(Speed)}/s";
                        });
                    }
                }
                }
            }

            if (new FileInfo(Partial).Length != Piece.fileSize)
                throw new IOException("The download ended early. Start it again to continue.");

            // integrity check against the manifest
            if (!string.IsNullOrWhiteSpace(Piece.hashValue) && Piece.hashValue.Trim('0').Length > 0)
            {
                Ui(() =>
                {
                    Download.State = DownloadState.Verifying;
                    Download.Message = "Checking the download…";
                });

                string Hash;
                await using (var Check = new FileStream(Partial, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
                    Hash = Convert.ToHexString(await SHA1.HashDataAsync(Check, Token));

                if (!Hash.Equals(Piece.hashValue, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(Partial);
                    throw new InvalidDataException("The downloaded file is damaged (checksum mismatch) and was deleted. Start it again.");
                }

                Ui(() => Download.State = DownloadState.Downloading);
            }

            File.Move(Partial, Target, true);
        }
    }
}
