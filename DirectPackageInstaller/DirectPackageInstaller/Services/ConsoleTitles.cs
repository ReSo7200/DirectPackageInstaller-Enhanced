using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LibOrbisPkg.SFO;

namespace DirectPackageInstaller.Services
{
    /// <summary>One title installed on the console.</summary>
    public sealed class InstalledTitle
    {
        public string TitleId { get; init; } = "";
        public string Title { get; set; } = "";
        /// <summary>APP_VER from the console's metadata: the installed update version when one is installed.</summary>
        public string Version { get; set; } = "";
        /// <summary>PARAM.SFO CATEGORY ("gd" game, "gde"/"gda" apps, ...; "gp" once an update is applied).</summary>
        public string Category { get; set; } = "";
        public bool OnExtended { get; set; }
        /// <summary>Registered on extended storage but not in its app folder yet (still downloading/installing).</summary>
        public bool Installing { get; set; }
        public bool HasUpdate { get; set; }
        public int DlcCount { get; set; }
        /// <summary>Cached ICON0.png on this PC, or null.</summary>
        public string? IconFile { get; set; }
    }

    /// <summary>
    /// Everything installed on the console, read over GoldHEN FTP:
    ///   internal  /user/app/TID, /user/patch/TID, /user/addcont/TID/*,
    ///             /system_data/priv/appmeta/TID/param.sfo, /user/appmeta/TID/icon0.png
    ///   extended  /mnt/ext0/user/app/TID, /mnt/ext0/user/patch, /mnt/ext0/user/addcont,
    ///             /system_data/priv/appmeta/external/TID/param.sfo, /user/appmeta/external/TID/icon0.png
    /// (layout verified on a PS4 with GoldHEN FTP v2.2 and a USB SSD as extended storage).
    /// </summary>
    public static class ConsoleTitles
    {
        static string IconCache => Path.Combine(LibraryService.DataDir, "console-icons");

        public static async Task<List<InstalledTitle>?> QueryAsync(string ConsoleIP, IProgress<string>? Progress = null, CancellationToken Token = default)
        {
            foreach (var Port in ConsoleInventory.FtpPorts)
            {
                // GoldHEN's server often drops the first connection
                for (int Attempt = 1; Attempt <= ConsoleInventory.FtpAttempts; Attempt++)
                {
                    FtpLite Ftp;
                    try
                    {
                        Ftp = await FtpLite.ConnectAsync(ConsoleIP, Port, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(8), Token);
                    }
                    catch (OperationCanceledException) when (Token.IsCancellationRequested) { throw; }
                    catch (System.Net.Sockets.SocketException) { break; }
                    catch
                    {
                        await Task.Delay(1000, Token);
                        continue;
                    }

                    await using (Ftp)
                    {
                        var Result = await ReadAsync(Ftp, Progress, Token);
                        if (Result != null)
                            return Result;
                    }

                    await Task.Delay(1000, Token);
                }
            }

            return null;
        }

        static async Task<List<string>> NamesAsync(FtpLite Ftp, string Path, CancellationToken Token)
        {
            try
            {
                return (await Ftp.ListAsync(Path, Token)).Where(x => x.Name != "." && x.Name != "..").Select(x => x.Name).ToList();
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new List<string>();
            }
        }

        static bool IsTitleId(string Name) => Regex.IsMatch(Name, "^[A-Z]{4}[0-9]{5}$");

        static async Task<List<InstalledTitle>?> ReadAsync(FtpLite Ftp, IProgress<string>? Progress, CancellationToken Token)
        {
            // /user/app must list, else this server can't see the console's files
            List<FtpEntry> Internal;
            try { Internal = await Ftp.ListAsync("/user/app", Token); }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { throw; }
            catch { return null; }

            var InternalApps = Internal.Select(x => x.Name).Where(IsTitleId).ToHashSet();
            var ExtApps = (await NamesAsync(Ftp, "/mnt/ext0/user/app", Token)).Where(IsTitleId).ToHashSet();
            var ExtRegistered = (await NamesAsync(Ftp, "/system_data/priv/appmeta/external", Token)).Where(IsTitleId).ToHashSet();
            var Patches = (await NamesAsync(Ftp, "/user/patch", Token))
                .Concat(await NamesAsync(Ftp, "/mnt/ext0/user/patch", Token)).Where(IsTitleId).ToHashSet();

            var DlcRoots = new Dictionary<string, List<string>>();
            foreach (var Root in new[] { "", "/mnt/ext0" })
                foreach (var Tid in (await NamesAsync(Ftp, Root + "/user/addcont", Token)).Where(IsTitleId))
                    (DlcRoots.TryGetValue(Tid, out var L) ? L : DlcRoots[Tid] = new List<string>()).Add(Root);

            var All = InternalApps.Union(ExtApps).Union(ExtRegistered).OrderBy(x => x).ToList();
            var Result = new List<InstalledTitle>();
            Directory.CreateDirectory(IconCache);

            int Index = 0;
            foreach (var Tid in All)
            {
                Token.ThrowIfCancellationRequested();
                Progress?.Report($"Reading {++Index} of {All.Count}  ·  {Tid}");

                bool External = ExtApps.Contains(Tid) || (ExtRegistered.Contains(Tid) && !InternalApps.Contains(Tid));
                var Item = new InstalledTitle
                {
                    TitleId = Tid,
                    Title = Tid,
                    OnExtended = External,
                    Installing = External && !ExtApps.Contains(Tid),
                    HasUpdate = Patches.Contains(Tid)
                };

                // name / version / category
                var SfoPaths = External
                    ? new[] { $"/system_data/priv/appmeta/external/{Tid}/param.sfo", $"/system_data/priv/appmeta/{Tid}/param.sfo" }
                    : new[] { $"/system_data/priv/appmeta/{Tid}/param.sfo" };
                foreach (var SfoPath in SfoPaths)
                {
                    var Values = await SfoAsync(Ftp, SfoPath, Token);
                    if (Values == null)
                        continue;
                    if (Values.TryGetValue("TITLE", out var Title) && Title.Length > 0) Item.Title = Title;
                    if (Values.TryGetValue("APP_VER", out var Version)) Item.Version = Version;
                    if (Values.TryGetValue("CATEGORY", out var Category)) Item.Category = Category;
                    break;
                }

                // DLC entitlement folders
                if (DlcRoots.TryGetValue(Tid, out var Roots))
                    foreach (var Root in Roots)
                        Item.DlcCount += (await NamesAsync(Ftp, $"{Root}/user/addcont/{Tid}", Token)).Count;

                // cover, downloaded once per title
                var Cached = Path.Combine(IconCache, Tid + ".png");
                if (File.Exists(Cached) && new FileInfo(Cached).Length > 0)
                    Item.IconFile = Cached;
                else
                {
                    var IconPaths = External
                        ? new[] { $"/user/appmeta/external/{Tid}/icon0.png", $"/user/appmeta/{Tid}/icon0.png" }
                        : new[] { $"/user/appmeta/{Tid}/icon0.png" };
                    foreach (var IconPath in IconPaths)
                    {
                        try
                        {
                            var Png = await Ftp.DownloadAsync(IconPath, 4 * 1024 * 1024, Token);
                            if (Png.Length > 0)
                            {
                                await File.WriteAllBytesAsync(Cached, Png, Token);
                                Item.IconFile = Cached;
                                break;
                            }
                        }
                        catch (OperationCanceledException) when (Token.IsCancellationRequested) { throw; }
                        catch when (Ftp.IsConnected) { }
                    }
                }

                Result.Add(Item);

                if (!Ftp.IsConnected)
                    return Result; // keep what we have; the caller can refresh
            }

            return Result;
        }

        static async Task<Dictionary<string, string>?> SfoAsync(FtpLite Ftp, string SfoPath, CancellationToken Token)
        {
            try
            {
                var Data = await Ftp.DownloadAsync(SfoPath, 1024 * 1024, Token);
                using var Stream = new MemoryStream(Data);
                var Sfo = ParamSfo.FromStream(Stream);
                var Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var Name in new[] { "TITLE", "APP_VER", "CATEGORY", "VERSION" })
                {
                    var Value = Sfo?.GetValueByName(Name);
                    if (Value != null)
                        Values[Name] = Encoding.UTF8.GetString(Value.ToByteArray()).Trim('\0').Trim();
                }
                return Values;
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
    }
}
