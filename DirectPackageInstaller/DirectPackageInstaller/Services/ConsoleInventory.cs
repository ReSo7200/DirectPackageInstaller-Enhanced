using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LibOrbisPkg.SFO;

namespace DirectPackageInstaller.Services
{
    public enum InstallState { Unknown, NotInstalled, Installed, UpdateAvailable, NewerInstalled, BaseMissing, Staged }

    /// <summary>A point-in-time view of what is installed on the console.</summary>
    public sealed class ConsoleSnapshot
    {
        private static readonly IReadOnlySet<string> Empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public string Source { get; init; } = "";
        public DateTime Taken { get; init; } = DateTime.Now;

        /// <summary>Title IDs with /user/app/&lt;TID&gt;.</summary>
        public IReadOnlySet<string> Apps { get; }
        /// <summary>Title IDs with /user/patch/&lt;TID&gt;.</summary>
        public IReadOnlySet<string> Patches { get; }
        /// <summary>TID -> entitlement folder names under /user/addcont/&lt;TID&gt;/.</summary>
        public IReadOnlyDictionary<string, IReadOnlySet<string>> AddCont { get; }
        /// <summary>TID -> APP_VER (e.g. "01.06") from /system_data/priv/appmeta/&lt;TID&gt;/param.sfo.</summary>
        public IReadOnlyDictionary<string, string> AppVersions { get; }

        /// <summary>Title IDs installed on extended storage (/mnt/ext0/user/app); also in Apps.</summary>
        public IReadOnlySet<string> ExtendedApps { get; init; } = Empty;

        /// <summary>Registered on extended storage but not installed yet (still downloading).</summary>
        public IReadOnlySet<string> InstallingApps { get; init; } = Empty;

        /// <summary>
        /// Content IDs the console finished installing through a download task, from its
        /// notifications. Unlock-key DLC leaves no /user/addcont folder, only this.
        /// </summary>
        public IReadOnlySet<string> DownloadedContent { get; init; } = Empty;

        /// <summary>Content IDs of packages waiting in /data/pkg for the Package Installer.</summary>
        public IReadOnlySet<string> StagedContent { get; init; } = Empty;

        /// <summary>True when the snapshot only knows about installed base apps (RPI fallback).</summary>
        public bool IsAppsOnly => Source.Equals("RPI", StringComparison.OrdinalIgnoreCase);

        public ConsoleSnapshot(IEnumerable<string>? apps = null, IEnumerable<string>? patches = null,
            IDictionary<string, IEnumerable<string>>? addCont = null, IDictionary<string, string>? appVersions = null)
        {
            Apps = new HashSet<string>(apps ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            Patches = new HashSet<string>(patches ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var ac = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
            if (addCont != null)
                foreach (var kv in addCont)
                    ac[kv.Key] = new HashSet<string>(kv.Value, StringComparer.OrdinalIgnoreCase);
            AddCont = ac;
            AppVersions = appVersions == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(appVersions, StringComparer.OrdinalIgnoreCase);
        }

        /// <param name="category">PKG CATEGORY param: "gd" game, "gp" patch, "ac" DLC.</param>
        /// <param name="titleId">e.g. CUSA00000</param>
        /// <param name="contentId">e.g. UP0001-CUSA00000_00-ABCDEFGHIJKLMNOP</param>
        /// <param name="pkgAppVersion">APP_VER of the PKG (e.g. "01.10"), if known.</param>
        /// <param name="unlockKey">Unlock-key DLC: only the download notifications show it installed.</param>
        public InstallState StateOf(string category, string titleId, string contentId, string? pkgAppVersion, bool unlockKey = false)
        {
            if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(titleId))
                return InstallState.Unknown;
            var cat = category.Trim().ToLowerInvariant();
            var tid = titleId.Trim();
            bool hasApp = Apps.Contains(tid);

            if (cat == "gd")
                return hasApp ? InstallState.Installed : InstallState.NotInstalled;

            // RPI only answers "is this game installed": an update/DLC without its
            // game can still be flagged; with the game present its version is unknown.
            if (IsAppsOnly)
                return (cat is "gp" or "ac") && !hasApp ? InstallState.BaseMissing : InstallState.Unknown;

            switch (cat)
            {
                case "gp":
                {
                    if (!hasApp) return InstallState.BaseMissing;
                    if (AppVersions.TryGetValue(tid, out var installed)
                        && TryParseVersion(installed, out var iv)
                        && pkgAppVersion != null
                        && TryParseVersion(pkgAppVersion, out var pv))
                    {
                        int c = iv.CompareTo(pv);
                        return c == 0 ? InstallState.Installed
                             : c < 0 ? InstallState.UpdateAvailable
                             : InstallState.NewerInstalled;
                    }
                    // no installed version to compare: some update is there, but maybe not this one
                    return Patches.Contains(tid) ? InstallState.Unknown : InstallState.UpdateAvailable;
                }
                case "ac":
                {
                    if (!hasApp) return InstallState.BaseMissing;
                    var id = contentId?.Trim().TrimEnd('\0') ?? "";
                    // a deleted data DLC keeps its notification: trust it for unlock keys only
                    if (unlockKey && DownloadedContent.Contains(id)) return InstallState.Installed;
                    var label = EntitlementLabel(contentId);
                    if (label == null) return InstallState.Unknown;
                    if (AddCont.TryGetValue(tid, out var set) && set.Contains(label))
                        return InstallState.Installed;
                    return StagedContent.Contains(id) ? InstallState.Staged : InstallState.NotInstalled;
                }
                default:
                    return InstallState.Unknown;
            }
        }

        /// <summary>"UP0001-CUSA00000_00-ABCDEFGHIJKLMNOP" -> "ABCDEFGHIJKLMNOP".</summary>
        public static string? EntitlementLabel(string? contentId)
        {
            if (string.IsNullOrWhiteSpace(contentId)) return null;
            var id = contentId.Trim().TrimEnd('\0');
            int dash = id.LastIndexOf('-');
            var label = dash >= 0 ? id.Substring(dash + 1) : id;
            return label.Length == 0 ? null : label;
        }

        /// <summary>Parses "01.06" / "1.6" / "01.100" into a (major, minor) pair compared numerically.</summary>
        public static bool TryParseVersion(string? s, out Version version)
        {
            version = new Version(0, 0);
            if (string.IsNullOrWhiteSpace(s)) return false;
            var parts = s.Trim().TrimEnd('\0').Split('.');
            var nums = new int[Math.Max(2, parts.Length)];
            for (int i = 0; i < parts.Length; i++)
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i]))
                    return false;
            version = parts.Length >= 3 ? new Version(nums[0], nums[1], nums[2]) : new Version(nums[0], nums[1]);
            return true;
        }

        public override string ToString() =>
            $"{Source} @ {Taken:T}: {Apps.Count} apps, {Patches.Count} patches, {AddCont.Count} addcont, {AppVersions.Count} versions";
    }

    public static class ConsoleInventory
    {
        public static readonly int[] FtpPorts = { 2121, 1337, 21 };
        public const int RpiPort = 12800;

        /// <summary>Where the PS4 mounts extended storage.</summary>
        public const string ExtRoot = "/mnt/ext0";

        /// <summary>Connection attempts per FTP port (GoldHEN's server drops some).</summary>
        public const int FtpAttempts = 3;
        public const int MaxAppMetaDownloads = 60;
        public static TimeSpan OverallTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Queries what is installed on the console. Tries FTP (GoldHEN 2121, ps4-ftp 1337, 21), then
        /// the Remote Package Installer API. Returns null if nothing answers.
        /// </summary>
        public static Task<ConsoleSnapshot?> QueryAsync(string ps4Ip, IEnumerable<string> titleIdsOfInterest, CancellationToken ct = default)
            => QueryAsync(ps4Ip, titleIdsOfInterest, FtpPorts, RpiPort, ct);

        /// <summary>Overload with explicit ports (used for testing / custom setups).</summary>
        public static async Task<ConsoleSnapshot?> QueryAsync(string ps4Ip, IEnumerable<string> titleIdsOfInterest,
            IEnumerable<int> ftpPorts, int rpiPort, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(ps4Ip)) return null;
            var tids = (titleIdsOfInterest ?? Array.Empty<string>())
                .Where(IsTitleId).Select(t => t.Trim().ToUpperInvariant()).Distinct().ToList();

            using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            overall.CancelAfter(OverallTimeout);
            var token = overall.Token;

            try
            {
                foreach (var port in ftpPorts)
                {
                    // GoldHEN's FTP server often accepts a connection and closes it
                    // without a greeting, then works on the next try: retry before
                    // giving up on the port. A refused port fails fast and is skipped.
                    for (int attempt = 1; attempt <= FtpAttempts; attempt++)
                    {
                        token.ThrowIfCancellationRequested();
                        FtpLite ftp;
                        try
                        {
                            ftp = await FtpLite.ConnectAsync(ps4Ip, port, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (OperationCanceledException) { return await RpiOrNull(); } // overall timeout
                        catch (System.Net.Sockets.SocketException) { break; } // nothing listening
                        catch
                        {
                            await Task.Delay(1000, token).ConfigureAwait(false);
                            continue;
                        }

                        await using (ftp)
                        {
                            var label = port == 2121 ? "GoldHEN FTP" : $"FTP port {port}";
                            var snap = await QueryFtpAsync(ftp, label, tids, token).ConfigureAwait(false);
                            if (snap != null) return snap;
                        }

                        await Task.Delay(1000, token).ConfigureAwait(false);
                    }
                }

                return await RpiOrNull();

                async Task<ConsoleSnapshot?> RpiOrNull() =>
                    tids.Count > 0 && !token.IsCancellationRequested
                        ? await QueryRpiAsync(ps4Ip, rpiPort, tids, token).ConfigureAwait(false)
                        : null;

            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // overall timeout
            }
            return null;
        }

        private static async Task<ConsoleSnapshot?> QueryFtpAsync(FtpLite ftp, string source, List<string> tids, CancellationToken ct)
        {
            // /user/app is mandatory: if we cannot list it, this server is not useful (e.g. jailed root).
            List<FtpEntry> apps;
            try { apps = await ftp.ListAsync("/user/app", ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return null; }

            var appSet = DirNames(apps).Where(IsTitleId).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Extended storage (USB drive formatted as extended storage) mirrors the
            // layout under /mnt/ext0/user. Missing when no drive is attached: ignore.
            var extSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ftp.IsConnected)
                try { extSet.UnionWith(DirNames(await ftp.ListAsync(ExtRoot + "/user/app", ct).ConfigureAwait(false)).Where(IsTitleId)); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { }
            appSet.UnionWith(extSet);

            // an install to extended storage registers its metadata here first
            var installingSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ftp.IsConnected)
                try { installingSet.UnionWith(DirNames(await ftp.ListAsync("/system_data/priv/appmeta/external", ct).ConfigureAwait(false)).Where(IsTitleId)); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { }
            installingSet.ExceptWith(appSet);

            var patchSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var addContTids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var addContRoots = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var addCont = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);
            var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var Root in new[] { "", ExtRoot })
            {
                if (ftp.IsConnected)
                    try { patchSet.UnionWith(DirNames(await ftp.ListAsync(Root + "/user/patch", ct).ConfigureAwait(false)).Where(IsTitleId)); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch { }

                if (ftp.IsConnected)
                    try
                    {
                        foreach (var Tid in DirNames(await ftp.ListAsync(Root + "/user/addcont", ct).ConfigureAwait(false)))
                            addContRoots[Tid] = addContRoots.TryGetValue(Tid, out var Existing) ? Existing.Append(Root).ToList() : new List<string> { Root };
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch { }
            }
            addContTids.UnionWith(addContRoots.Keys);

            foreach (var tid in tids)
            {
                if (!ftp.IsConnected) break;
                if (!addContTids.Contains(tid)) continue;
                try
                {
                    // Entitlement folders; accept any entry type in case the server misreports it.
                    var names = new List<string>();
                    foreach (var Root in addContRoots.TryGetValue(tid, out var Roots) ? Roots : new List<string> { "" })
                        names.AddRange((await ftp.ListAsync($"{Root}/user/addcont/{tid}", ct).ConfigureAwait(false)).Select(e => e.Name));
                    addCont[tid] = names;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { }
            }

            // versions are the slow part (one download per title): stop reading them
            // before the overall limit so the apps/patches/DLC found so far are kept
            var versionDeadline = DateTime.UtcNow + TimeSpan.FromTicks(OverallTimeout.Ticks * 2 / 3);
            int downloads = 0;
            foreach (var tid in tids)
            {
                if (!ftp.IsConnected || downloads >= MaxAppMetaDownloads || DateTime.UtcNow > versionDeadline) break;
                if (!appSet.Contains(tid)) continue;
                downloads++;
                try
                {
                    // games on extended storage keep their metadata there; fall back to internal
                    var paths = extSet.Contains(tid)
                        ? new[] { $"{ExtRoot}/user/appmeta/{tid}/param.sfo", $"/system_data/priv/appmeta/{tid}/param.sfo" }
                        : new[] { $"/system_data/priv/appmeta/{tid}/param.sfo" };
                    foreach (var path in paths)
                    {
                        try
                        {
                            var ver = ReadAppVer(await ftp.DownloadAsync(path, 1024 * 1024, ct).ConfigureAwait(false));
                            if (ver != null) { versions[tid] = ver; break; }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch when (ftp.IsConnected) { }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { }
            }

            // unlock-key DLC: installed ones only show in the download notifications,
            // copied-but-not-installed ones wait in /data/pkg
            var downloaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ftp.IsConnected)
                try { downloaded.UnionWith(FinishedDownloads(await ftp.DownloadAsync(NotificationDb, 8 * 1024 * 1024, ct).ConfigureAwait(false))); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { }

            var staged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ftp.IsConnected)
                try
                {
                    foreach (var e in await ftp.ListAsync(UnlockKeys.ConsoleFolder, ct).ConfigureAwait(false))
                        if (!e.IsDirectory && ContentIdPattern.Match(e.Name) is { Success: true } m)
                            staged.Add(m.Value);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { }

            return new ConsoleSnapshot(appSet, patchSet, addCont, versions)
            {
                ExtendedApps = extSet,
                InstallingApps = installingSet,
                DownloadedContent = downloaded,
                StagedContent = staged,
                Source = source,
                Taken = DateTime.Now
            };
        }

        const string NotificationDb = "/system_data/priv/mms/notification.db";

        static readonly Regex ContentIdPattern = new(@"[A-Z]{2}\d{4}-[A-Z]{4}\d{5}_\d{2}-[A-Z0-9]{16}", RegexOptions.Compiled);

        // "psdownload:play?taskid=10000006&type=1&subtype=7&errorcode=0&state=3&...&contentid=UP0006-CUSA57220_00-FULLGAMEUNLOCK00"
        static readonly Regex FinishedDownload = new(@"psdownload:[^\x00'""]*?errorcode=0&[^\x00'""]*?contentid=([A-Z]{2}\d{4}-[A-Z]{4}\d{5}_\d{2}-[A-Z0-9]{16})", RegexOptions.Compiled);

        /// <summary>
        /// Content IDs of downloads the console finished without an error, read straight
        /// from the notification database's bytes (the URIs are stored as plain text).
        /// </summary>
        public static IEnumerable<string> FinishedDownloads(byte[] NotificationDbBytes)
        {
            var Text = Encoding.Latin1.GetString(NotificationDbBytes);
            foreach (Match m in FinishedDownload.Matches(Text))
                yield return m.Groups[1].Value;
        }

        private static async Task<ConsoleSnapshot?> QueryRpiAsync(string ip, int port, List<string> tids, CancellationToken ct)
        {
            using var http = NetConnect.ConsoleHttp(TimeSpan.FromSeconds(4));
            var url = $"http://{ip}:{port}/api/is_exists";
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool anyAnswer = false;

            foreach (var tid in tids.Take(MaxAppMetaDownloads))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var body = new StringContent(JsonSerializer.Serialize(new { title_id = tid }), Encoding.UTF8, "application/json");
                    using var resp = await http.PostAsync(url, body, ct).ConfigureAwait(false);
                    var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    anyAnswer = true;
                    // Not parsed as JSON: RPI writes the size as a hex literal
                    // ({ "status": "success", "exists": "true", "size": 0x2A589E000 }),
                    // which strict parsers reject.
                    if (!RpiField(json, "status").Equals("success", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (RpiField(json, "exists").Equals("true", StringComparison.OrdinalIgnoreCase))
                        installed.Add(tid);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (HttpRequestException) when (!anyAnswer) { return null; } // RPI not running
                catch (TaskCanceledException) when (!anyAnswer) { return null; } // HttpClient timeout
                catch { }
            }

            return anyAnswer ? new ConsoleSnapshot(installed) { Source = "RPI", Taken = DateTime.Now } : null;
        }

        /// <summary>Value of "name": "value" / "name": value in RPI's almost-JSON reply, or "".</summary>
        internal static string RpiField(string Reply, string Name)
        {
            var Match = System.Text.RegularExpressions.Regex.Match(Reply,
                "\"" + System.Text.RegularExpressions.Regex.Escape(Name) + "\"\\s*:\\s*\"?([^\",}\\s]*)");
            return Match.Success ? Match.Groups[1].Value : "";
        }

        private static bool IsTrue(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => string.Equals(e.GetString(), "true", StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Number => e.TryGetInt32(out var n) && n != 0,
            _ => false
        };

        internal static string? ReadAppVer(byte[] data)
        {
            try
            {
                using var ms = new MemoryStream(data);
                var sfo = ParamSfo.FromStream(ms);
                var v = sfo?.GetValueByName("APP_VER");
                if (v == null) return null;
                var s = Encoding.UTF8.GetString(v.ToByteArray()).Trim('\0').Trim();
                return s.Length == 0 ? null : s;
            }
            catch { return null; }
        }

        private static IEnumerable<string> DirNames(IEnumerable<FtpEntry> entries) =>
            entries.Where(e => e.IsDirectory).Select(e => e.Name);

        /// <summary>PS4 title IDs look like CUSA12345 (4 letters + 5 digits).</summary>
        private static bool IsTitleId(string? s)
        {
            if (s == null) return false;
            s = s.Trim();
            if (s.Length != 9) return false;
            for (int i = 0; i < 4; i++) if (!char.IsLetter(s[i])) return false;
            for (int i = 4; i < 9; i++) if (!char.IsDigit(s[i])) return false;
            return true;
        }
    }
}
