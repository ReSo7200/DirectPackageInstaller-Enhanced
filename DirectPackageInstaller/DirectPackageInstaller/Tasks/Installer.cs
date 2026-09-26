using System;
using System.IO;
using System.Net;
using System.Web;
using System.Linq;
using System.Text;
using System.Threading;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Collections.Generic;
using DirectPackageInstaller.Views;
using DirectPackageInstaller.Host;
using DirectPackageInstaller.IO;
using DirectPackageInstaller.Others;
using DirectPackageInstaller.Services;
using SharpCompress.Archives;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Http.Headers;

namespace DirectPackageInstaller.Tasks
{
    public static class Installer
    {
        public const int ServerPort = 9898;
        public static PS4Server? Server;

        public static PKGHelper.PKGInfo CurrentPKG;
        public static string[]? CurrentFileList = null;

        public static string? EntryFileName;

        public static PayloadService Payload = new PayloadService();

        /// <summary>
        /// One push at a time. CurrentPKG is shared state read deep inside the push
        /// (preload length, GoldHEN PKG info), so callers set it and push while
        /// holding this lock: Direct link and the Library queue can't mix them up.
        /// </summary>
        public static readonly SemaphoreSlim PushLock = new SemaphoreSlim(1, 1);

        /// <summary>Why the last PushPackage failed, in plain words (queue rows show it).</summary>
        public static string? LastError;

        /// <summary>Set when the console reported the package as already installed.</summary>
        public static bool LastAlreadyInstalled;

        /// <summary>RPI task ID of the last push (for live progress), or null for other install methods.</summary>
        public static long? LastTaskId;

        /// <summary>
        /// Base64 value for a ?b64= query parameter. Escaped because
        /// ParseQueryString turns a raw '+' into a space, which breaks decoding.
        /// </summary>
        public static string B64Query(string Value) =>
            Uri.EscapeDataString(Convert.ToBase64String(Encoding.UTF8.GetBytes(Value)));

        public static async Task<bool> PushPackage(Settings Config, Source InputType, Stream? PKGStream, string URL, IArchive? Decompressor, DecompressorHelperStream[]? DecompressorStreams, Func<string, Task> SetStatus, Func<string> GetStatus, bool Silent)
        {
            LastError = null;
            LastAlreadyInstalled = false;
            LastTaskId = null;

            if (string.IsNullOrEmpty(Config.PSIP) || Config.PSIP == "0.0.0.0")
            {
                LastError = "The console address isn't set. Enter it in Settings.";
                if (!Silent)
                    await MessageBox.ShowAsync("PS IP not defined, please, type the PS IP in Settings", "PS IP Not Found", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            if (string.IsNullOrEmpty(Config.PCIP) || Config.PCIP == "0.0.0.0")
            {
                LastError = "This PC's address isn't set. Pick it in Settings.";
                if (!Silent)
                    await MessageBox.ShowAsync("PC IP not defined, please, pick your PC's LAN address in Settings", "PC IP Not Found", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            if (!await StartServer(Config.PCIP, Silent))
            {
                LastError = $"Couldn't start the file server on port {ServerPort}. Is another installer (or DPI) already running?";
                return false;
            }

            // per push: a non-direct link must not force proxy mode on every later install
            bool ForceProxy = false;

            if (PKGStream is FileHostStream)
            {
                var HostStream = ((FileHostStream)PKGStream);
                URL = HostStream.Url;

                if (!HostStream.DirectLink && !ForceProxy)
                {
                    if (!Config.ProxyDownload)
                    {
                        var Reply = await MessageBox.ShowAsync("The given URL can't be direct downloaded.\nDo you want to the DirectPackageInstaller act as a server?", "DirectPackageInstaller", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                        if (Reply != DialogResult.Yes)
                            return false;
                    }

                    ForceProxy = true;
                }
            }

            if ((Config.ProxyDownload || ForceProxy) && !InputType.HasFlag(Source.DiskCache))
                InputType |= Source.Proxy;

            if (Config.SegmentedDownload && !InputType.HasFlag(Source.DiskCache))
                InputType |= Source.Segmented | Source.Proxy;

            
            
            //InputType is DiskCache when the file hosting is limited
            //Then segmented option must be ignored to works
            if (InputType.HasFlag(Source.DiskCache))
                InputType &= ~(Source.Segmented | Source.Proxy);


            //Just to reduce the switch cases
            if (InputType.HasFlag(Source.SevenZip) || InputType.HasFlag(Source.RAR))
                InputType &= ~(Source.Proxy | Source.Segmented | Source.DiskCache);

            if (InputType.HasFlag(Source.JSON))
                InputType &= ~(Source.Proxy | Source.DiskCache | Source.URL | Source.File);

            if (InputType.HasFlag(Source.File))
                InputType &= ~(Source.Proxy | Source.Segmented | Source.DiskCache);

            if (InputType.HasFlag(Source.DiskCache) || InputType.HasFlag(Source.Segmented))
                InputType &= ~Source.Proxy;
            
            
            if (!await MemoryInfo.EnsureFreeSpace(PKGStream, DecompressorStreams, InputType))
                return false;

            bool CanSplit = true;

            uint LastResource = CurrentPKG.PreloadLength;

            switch (InputType)
            {
                case Source.URL | Source.SevenZip:
                case Source.URL | Source.RAR:
                    CanSplit = false;

                    bool Retry = false;

                    var ID = DecompressService.TaskCache.Count.ToString();
                    foreach (var Task in DecompressService.TaskCache)
                    {
                        if (Task.Value.Entry == EntryFileName && Task.Value.Url == URL)
                        {
                            if (DecompressService.EntryMap.ContainsKey(URL) && Server.Decompress.Tasks.ContainsKey(DecompressService.EntryMap[URL]))
                            {
                                if (Server.Decompress.Tasks[DecompressService.EntryMap[URL]].Failed)
                                    continue;

                                ID = Task.Key;
                                Retry = true;
                            }
                            break;
                        }
                    }

                    var OriStatus = GetStatus();
                    await SetStatus("Initializing Decompressor...");

                    if (!Retry)
                    {
                        DecompressService.TaskCache[ID] = (EntryFileName, URL);
                        
                        string Entry = null;

                        if (await App.RunInNewThread(() => Entry = Server!.Decompress.Decompressor.CreateDecompressor(Decompressor, DecompressorStreams, EntryFileName)))
                            return false;
                        
                        EntryFileName = Entry;

                        if (Entry == null)
                            throw new AbortException("Failed to decompress");

                        DecompressService.EntryMap[URL] = Entry;
                    }

                    var DecompressTask = Server.Decompress.Tasks[EntryFileName!];

                    while (DecompressTask.SafeTotalDecompressed < LastResource)
                    {
                        if (DecompressTask.Failed || DecompressTask.Error != null)
                        {
                            await SetStatus(OriStatus);
                            await MessageBox.ShowAsync("Failed to decompress the package:\n" + (DecompressTask.Error?.Message ?? "unknown error"), "DirectPackageInstaller", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return false;
                        }
                        await SetStatus($"Preloading Compressed PKG... ({(double)DecompressTask.SafeTotalDecompressed / LastResource:P})");
                        await Task.Delay(100);
                    }
                    await SetStatus(OriStatus);

                    URL = $"http://{Config.PCIP}:{ServerPort}/{(InputType.HasFlag(Source.SevenZip) ? "un7z" : "unrar")}/?id={ID}";
                    break;

                case Source.JSON | Source.Segmented:
                case Source.URL | Source.DiskCache:
                case Source.URL | Source.Segmented:
                    CanSplit = !InputType.HasFlag(Source.DiskCache);

                    var CacheTask = Downloader.CreateTask(URL);
                    
                    OriStatus = GetStatus();
                    while (CacheTask.SafeReadyLength < LastResource)
                    {
                        if (CacheTask.Failed || CacheTask.Error != null)
                        {
                            await SetStatus(OriStatus);
                            await MessageBox.ShowAsync("Failed to download the package:\n" + (CacheTask.Error?.Message ?? "unknown error"), "DirectPackageInstaller", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return false;
                        }
                        await SetStatus($"Preloading PKG... ({(double)(CacheTask.SafeReadyLength) / LastResource:P})");
                        await Task.Delay(100);
                    }
                    await SetStatus(OriStatus);

                    URL = $"http://{Config.PCIP}:{ServerPort}/cache/?b64={Installer.B64Query(URL)}";
                    break;

                case Source.URL | Source.Proxy:
                    URL = $"http://{Config.PCIP}:{ServerPort}/proxy/?b64={Installer.B64Query(URL)}";
                    break;

                case Source.JSON:
                    URL = $"http://{Config.PCIP}:{ServerPort}/merge/?b64={Installer.B64Query(URL)}";
                    break;
                
                case Source.File:
                    URL = $"http://{Config.PCIP}:{ServerPort}/file/?b64={Installer.B64Query(URL)}";
                    break;

                case Source.URL:
                    CanSplit = false;
                    break;

                default:
                    MessageBox.ShowSync("Unexpected Install Method: \n" + InputType.ToString());
                    return false;
            }

            if (!Config.AutoSplitPKG)
                CanSplit = false;

            // a chosen install storage only works through the experimental payload:
            // prefer it over RPI/etaHEN when GoldHEN can take it
            bool WantsStorage = Config.ExperimentalPayload && ExperimentalPayloadProtocol.StorageChoiceWorks
                                && Config.InstallStorage != ExperimentalPayloadProtocol.StorageDefault;
            bool PayloadFirst = WantsStorage && (Payload.ClientRunning || await IPHelper.IsGoldHENOnline(Config.PSIP));

            bool OK;
            if (!PayloadFirst && await IPHelper.IsRPIOnline(Config.PSIP))
                OK = await PushRPI(URL, Config, Silent);
            else if (!PayloadFirst && await IPHelper.IsEtaHenOnline(Config.PSIP))
                OK = await PushEtaHen(URL, Config, Silent);
            else
            {
                OK = await Payload.SendPKGPayload(Config.PSIP, Config.PCIP, URL, Silent, CanSplit);
                if (!OK)
                    LastError ??= "GoldHEN didn't take the package. Turn on the payload server (BinLoader, port 9090) in GoldHEN settings, or open Remote Package Installer.";
            }
            
            return OK;
        }

        #region etaHEN
        public static async Task<bool> PushEtaHen(string URL, Settings Config, bool Silent)
        {
            try
            {
                string Boundary = GetBoundary();

                using var client = Services.NetConnect.ConsoleHttp(TimeSpan.FromSeconds(100));
                var requestUri = $"http://{Config.PSIP}:12800/upload";

                var content = new MultipartFormDataContent(Boundary);
                content.Add(new StringContent("", null, "application/octet-stream"), "\"file\"", "\"\"");
                using (var buffer = new MemoryStream(Encoding.UTF8.GetBytes(URL)))
                {
                    content.Add(new StreamContent(buffer), "\"url\"");

                    // DPI v2 also takes the name/ID shown in the console's download list
                    // (otherwise it reads "etaHEN DPI"); older etaHEN ignores extra fields.
                    if (!string.IsNullOrWhiteSpace(CurrentPKG.FriendlyName))
                        content.Add(new StringContent(CurrentPKG.FriendlyName, Encoding.UTF8), "\"content_name\"");
                    if (!string.IsNullOrWhiteSpace(CurrentPKG.ContentID))
                        content.Add(new StringContent(CurrentPKG.ContentID, Encoding.UTF8), "\"content_id\"");

                    var Response = await client.PostAsync(requestUri, content);

                    using var Buffer = new MemoryStream();
                    await Response.Content.CopyToAsync(Buffer);

                    var Result = Encoding.UTF8.GetString(Buffer.ToArray());

                    if (Result.Contains("SUCCESS:"))
                    {
                        if (!Silent)
                            await MessageBox.ShowAsync("Package Sent!", "DirectPackageInstaller", MessageBoxButtons.OK, MessageBoxIcon.Information);

                        return true;
                    }
                    else
                    {
                        LastError = InstallErrors.Describe(Result);
                        if (!Silent)
                            await MessageBox.ShowAsync("Failed:\n" + LastError, "DirectPackageInstaller", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                string Result = null;
                if (ex is WebException)
                {
                    try
                    {
                        using (var Resp = ((WebException)ex).Response.GetResponseStream())
                        using (MemoryStream Stream = new MemoryStream())
                        {
                            Resp.CopyTo(Stream);
                            Result = Encoding.UTF8.GetString(Stream.ToArray());
                        }
                    }
                    catch { }
                }

                await File.WriteAllTextAsync(Path.Combine(App.WorkingDirectory, "DPI-ERROR.log"), ex.ToString());
                LastError = Result != null ? InstallErrors.Describe(Result) : "Couldn't reach the console: " + ex.Message;
                if (!Silent)
                    await MessageBox.ShowAsync("Failed:\n" + (Result == null ? ex.ToString() : LastError), "DirectPackageInstaller", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private static string GetBoundary()
        {
            var rndData = new byte[16];
            Random rnd = new Random();
            rnd.NextBytes(rndData);
            var rndStr = string.Join("", rndData.Select(x => x.ToString("x2")));
            return "------DirectPackageInstaller_" + rndStr;
        }
        #endregion

        #region RemotePackageInstaller
        public static async Task<bool> PushRPI(string URL, Settings Config, bool Silent)
        {
            try
            {
                using var client = Services.NetConnect.ConsoleHttp(TimeSpan.FromSeconds(100));
                var requestUri = $"http://{Config.PSIP}:12800/api/install";


                var EscapedURL = HttpUtility.UrlEncode(URL.Replace("https://", "http://"));
                var JSON = $"{{\"type\":\"direct\",\"packages\":[\"{EscapedURL}\"]}}";

                var content = new StringContent(JSON, Encoding.UTF8);
                
                var Response = await client.PostAsync(requestUri, content);

                using var Buffer = new MemoryStream();
                await Response.Content.CopyToAsync(Buffer);

                var Result = Encoding.UTF8.GetString(Buffer.ToArray());

                if (Result.Contains("\"success\""))
                {
                    // RPI reports "same version already installed" as success with task_id -1
                    LastAlreadyInstalled = System.Text.RegularExpressions.Regex.IsMatch(Result, "\"task_id\"\\s*:\\s*-1");
                    var TaskId = RpiTasks.Number(Result, "task_id");
                    LastTaskId = TaskId is >= 0 ? TaskId : null;

                    if (!Silent)
                        await MessageBox.ShowAsync(LastAlreadyInstalled ? "Already installed on the console." : "Package Sent!", "DirectPackageInstaller", MessageBoxButtons.OK, MessageBoxIcon.Information);

                    return true;
                }
                else
                {
                    LastError = InstallErrors.Describe(Result);
                    if (!Silent)
                        await MessageBox.ShowAsync("Failed:\n" + LastError, "DirectPackageInstaller", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }
            }
            catch (Exception ex)
            {
                string Result = null;
                if (ex is WebException)
                {
                    try
                    {
                        using (var Resp = ((WebException)ex).Response.GetResponseStream())
                        using (MemoryStream Stream = new MemoryStream())
                        {
                            Resp.CopyTo(Stream);
                            Result = Encoding.UTF8.GetString(Stream.ToArray());
                        }
                    }
                    catch { }
                }

                await File.WriteAllTextAsync(Path.Combine(App.WorkingDirectory, "DPI-ERROR.log"), ex.ToString());
                LastError = Result != null ? InstallErrors.Describe(Result) : "Couldn't reach the console: " + ex.Message;
                if (!Silent)
                    await MessageBox.ShowAsync("Failed:\n" + (Result == null ? ex.ToString() : LastError), "DirectPackageInstaller", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }
        #endregion

        #region DirectPackageInstaller
        /// <summary>
        /// Start the PKG HTTP server once. Server is only set after Start()
        /// succeeds, so a busy port 9898 is retried on the next push instead of
        /// handing the console URLs that nobody serves.
        /// </summary>
        public static async Task<bool> StartServer(string LocalIP, bool Silent = false)
        {
            if (Server != null)
                return true;

            if (string.IsNullOrEmpty(LocalIP))
                LocalIP = "0.0.0.0";

            Exception? StartError = null;
            foreach (var BindIP in new[] { LocalIP, "0.0.0.0" }.Distinct())
            {
                PS4Server? NewServer = null;
                try
                {
                    NewServer = new PS4Server(BindIP, ServerPort);
                    NewServer.Start();
                    Server = NewServer;
                    return true;
                }
                catch (Exception ex)
                {
                    StartError = ex;
                    NewServer?.Stop();
                }
            }

            if (!Silent)
                await MessageBox.ShowAsync($"Failed to open the HTTP server on port {ServerPort}.\nIs another DirectPackageInstaller or PKG sender already running?\n\n{StartError?.Message}", "DirectPackageInstaller", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        #endregion

    }
}
