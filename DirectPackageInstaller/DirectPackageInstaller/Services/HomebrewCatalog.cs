using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;

namespace DirectPackageInstaller.Services
{
    /// <summary>One homebrew app: a GitHub project whose latest release has a .pkg.</summary>
    public sealed class HomebrewApp : ReactiveObject
    {
        public string Name { get; init; } = "";
        public string Repo { get; init; } = "";            // owner/name
        public string Description { get; init; } = "";
        /// <summary>Which release file is the PS4 package (several builds exist for some apps).</summary>
        public string AssetPattern { get; init; } = @"\.pkg$";
        public bool Custom { get; init; }

        public string PageUrl => $"https://github.com/{Repo}";

        string _Version = "";
        public string Version { get => _Version; set { this.RaiseAndSetIfChanged(ref _Version, value); this.RaisePropertyChanged(nameof(Detail)); } }

        string _Released = "";
        public string Released { get => _Released; set { this.RaiseAndSetIfChanged(ref _Released, value); this.RaisePropertyChanged(nameof(Detail)); } }

        long _Size;
        public long Size { get => _Size; set { this.RaiseAndSetIfChanged(ref _Size, value); this.RaisePropertyChanged(nameof(Detail)); } }

        public string DownloadUrl { get; set; } = "";
        public string FileName { get; set; } = "";

        string _Status = "";
        public string Status { get => _Status; set => this.RaiseAndSetIfChanged(ref _Status, value); }

        bool _Busy;
        public bool Busy { get => _Busy; set { this.RaiseAndSetIfChanged(ref _Busy, value); this.RaisePropertyChanged(nameof(CanGet)); this.RaisePropertyChanged(nameof(GetTip)); } }

        public bool CanGet => !Busy && DownloadUrl.Length > 0;

        public string Detail => Version.Length == 0 ? Repo
            : $"{Repo}  ·  {Version}  ·  {Released}  ·  {FileName}  ·  {Host.TransferProgressInfo.FormatBytes(Size)}";

        /// <summary>Why Download &amp; send is off, for its tooltip.</summary>
        public string GetTip => Busy ? "Downloading…"
            : DownloadUrl.Length == 0 ? "Its latest release has no PS4 package"
            : "Downloads the package from the project's release and adds it to the Queue";

        internal void Changed()
        {
            this.RaisePropertyChanged(nameof(CanGet));
            this.RaisePropertyChanged(nameof(GetTip));
        }
    }

    /// <summary>
    /// Homebrew from its authors' GitHub releases only: a short built-in list of well-known
    /// PS4 homebrew plus projects the user adds (owner/repo). Nothing is re-hosted; the
    /// package is downloaded from the release and sent like any other PKG. Release info
    /// is cached with ETags (GitHub allows 60 unauthenticated requests an hour).
    /// </summary>
    public static class HomebrewCatalog
    {
        static readonly HomebrewApp[] BuiltIn =
        {
            new() { Name = "Apollo Save Tool", Repo = "bucanero/apollo-ps4",
                    Description = "Save game manager: back up, restore, unlock and patch saves on the console." },
            new() { Name = "ezRemote Client", Repo = "cy33hc/ps4-ezremote-client",
                    Description = "File manager for FTP, SMB, WebDAV, HTTP servers and the console's own drives." },
            new() { Name = "Homebrew Store", Repo = "LightningMods/PS4-Store", AssetPattern = @"^Store-R2\.pkg$",
                    Description = "PKG-Zone's store app: browse and install homebrew on the console." },
            new() { Name = "GoldHEN Cheat Manager", Repo = "GoldHEN/GoldHEN_Cheat_Manager",
                    Description = "Download and toggle GoldHEN cheats for the games on the console." },
        };

        static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

        static HomebrewCatalog()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("DPI-Enhanced");
            Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        public static string Folder => Path.Combine(LibraryService.DataDir, "homebrew");
        static string CustomFile => Path.Combine(LibraryService.DataDir, "homebrew-projects.txt");
        static string CacheFile => Path.Combine(LibraryService.DataDir, "homebrew-releases.json");

        sealed class CachedRelease
        {
            public string ETag { get; set; } = "";
            public string Json { get; set; } = "";
            public DateTime Checked { get; set; }
        }

        /// <summary>The built-in apps plus the user's projects, without release info yet.</summary>
        public static List<HomebrewApp> Apps()
        {
            var List = BuiltIn.Select(x => new HomebrewApp { Name = x.Name, Repo = x.Repo, Description = x.Description, AssetPattern = x.AssetPattern }).ToList();
            foreach (var Repo in CustomRepos().Where(r => List.All(x => !x.Repo.Equals(r, StringComparison.OrdinalIgnoreCase))))
                List.Add(new HomebrewApp { Name = Repo.Split('/')[1], Repo = Repo, Custom = true, Description = "Added by you." });
            return List;
        }

        public static List<string> CustomRepos()
        {
            try { return File.Exists(CustomFile) ? File.ReadAllLines(CustomFile).Select(x => x.Trim()).Where(IsRepo).ToList() : new(); }
            catch { return new(); }
        }

        /// <summary>"owner/name", also taken from a github.com URL.</summary>
        public static string? NormalizeRepo(string Text)
        {
            var Match = Regex.Match(Text.Trim(), @"^(?:https?://github\.com/)?([\w.-]+/[\w.-]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase);
            return Match.Success ? Match.Groups[1].Value : null;
        }

        static bool IsRepo(string Text) => NormalizeRepo(Text) != null;

        public static void AddRepo(string Repo)
        {
            var List = CustomRepos();
            if (!List.Contains(Repo, StringComparer.OrdinalIgnoreCase))
                List.Add(Repo);
            Directory.CreateDirectory(LibraryService.DataDir);
            File.WriteAllLines(CustomFile, List);
        }

        public static void RemoveRepo(string Repo)
        {
            var List = CustomRepos().Where(x => !x.Equals(Repo, StringComparison.OrdinalIgnoreCase)).ToList();
            File.WriteAllLines(CustomFile, List);
        }

        /// <summary>GitHub's hourly allowance ran out: when it comes back.</summary>
        sealed class RateLimited : Exception
        {
            public RateLimited(DateTime Until) : base($"GitHub's hourly limit for this network is used up; try again after {Until.ToLocalTime():HH:mm}.") { }
        }

        /// <summary>
        /// Fill in each app's latest release (version, date, size, download URL).
        /// Force: ask GitHub even if checked within the hour (an unchanged answer is free).
        /// </summary>
        public static async Task RefreshAsync(IEnumerable<HomebrewApp> Apps, bool Force = false, CancellationToken Token = default)
        {
            var Cache = LoadCache();
            // a copy: the page's list may change while this runs
            var List = Apps.ToList();
            for (int i = 0; i < List.Count; i++)
            {
                try
                {
                    Apply(List[i], await LatestReleaseAsync(List[i].Repo, Cache, Force, Token));
                }
                catch (OperationCanceledException) when (Token.IsCancellationRequested) { throw; }
                catch (RateLimited ex)
                {
                    // the rest would fail the same way: show what's cached
                    foreach (var App in List.Skip(i))
                    {
                        if (Cache.TryGetValue(App.Repo, out var Known))
                            Apply(App, Known.Json);
                        else
                            App.Status = ex.Message;
                    }
                    break;
                }
                catch (Exception ex)
                {
                    List[i].Status = "Couldn't read its releases: " + Plain(ex);
                }
            }
            SaveCache(Cache);
        }

        static string Plain(Exception ex) => ex switch
        {
            HttpRequestException => "GitHub couldn't be reached (check the internet connection).",
            TaskCanceledException or TimeoutException => "GitHub didn't answer in time.",
            _ => ex.Message
        };

        static async Task<string?> LatestReleaseAsync(string Repo, Dictionary<string, CachedRelease> Cache, bool Force, CancellationToken Token)
        {
            Cache.TryGetValue(Repo, out var Cached);
            // recently checked: don't spend the hourly allowance
            if (!Force && Cached != null && DateTime.UtcNow - Cached.Checked < TimeSpan.FromHours(1))
                return Cached.Json;

            using var Request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
            if (Cached is { ETag.Length: > 0 })
                Request.Headers.TryAddWithoutValidation("If-None-Match", Cached.ETag);

            using var Response = await Http.SendAsync(Request, Token);
            if (Response.StatusCode == System.Net.HttpStatusCode.NotModified && Cached != null)
            {
                Cached.Checked = DateTime.UtcNow;
                return Cached.Json;
            }
            if (Response.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new InvalidOperationException("it has no releases, or the project doesn't exist.");
            if ((int)Response.StatusCode is 403 or 429
                && Response.Headers.TryGetValues("X-RateLimit-Remaining", out var Left) && Left.FirstOrDefault() == "0")
            {
                var Reset = Response.Headers.TryGetValues("X-RateLimit-Reset", out var R) && long.TryParse(R.FirstOrDefault(), out var Epoch)
                    ? DateTimeOffset.FromUnixTimeSeconds(Epoch).UtcDateTime : DateTime.UtcNow.AddHours(1);
                throw new RateLimited(Reset);
            }
            if (!Response.IsSuccessStatusCode)
            {
                if (Cached != null)
                    return Cached.Json;   // offline: last known
                throw new InvalidOperationException($"GitHub couldn't answer right now (error {(int)Response.StatusCode}).");
            }

            var Json = await Response.Content.ReadAsStringAsync(Token);
            Cache[Repo] = new CachedRelease { ETag = Response.Headers.ETag?.Tag ?? "", Json = Json, Checked = DateTime.UtcNow };
            return Json;
        }

        static void Apply(HomebrewApp App, string? Json)
        {
            if (string.IsNullOrEmpty(Json))
                return;
            using var Doc = JsonDocument.Parse(Json);
            var Root = Doc.RootElement;
            App.Version = Root.TryGetProperty("tag_name", out var Tag) ? Tag.GetString() ?? "" : "";
            App.Released = Root.TryGetProperty("published_at", out var Date) && Date.GetString() is { Length: >= 10 } D ? D.Substring(0, 10) : "";

            var Pattern = new Regex(App.AssetPattern, RegexOptions.IgnoreCase);
            var Assets = Root.TryGetProperty("assets", out var A) ? A.EnumerateArray().ToList() : new List<JsonElement>();
            var Pkg = Assets.FirstOrDefault(x => Pattern.IsMatch(x.GetProperty("name").GetString() ?? ""));
            if (Pkg.ValueKind == JsonValueKind.Undefined)
            {
                App.DownloadUrl = "";
                App.Status = "Its latest release has no PS4 package.";
            }
            else
            {
                App.FileName = Pkg.GetProperty("name").GetString() ?? "app.pkg";
                App.DownloadUrl = Pkg.GetProperty("browser_download_url").GetString() ?? "";
                App.Size = Pkg.GetProperty("size").GetInt64();
                if (File.Exists(LocalPath(App)))
                    App.Status = "Downloaded.";
            }
            App.Changed();
        }

        /// <summary>Where an app's package goes (one folder per project and version).</summary>
        public static string LocalPath(HomebrewApp App) =>
            Path.Combine(ProjectFolder(App), SafeNames.Of(App.Version), SafeNames.Of(App.FileName));

        static string ProjectFolder(HomebrewApp App) => Path.Combine(Folder, SafeNames.Of(App.Repo.Replace('/', '_')));

        /// <summary>Download the app's package (kept, so sending it again is instant).</summary>
        public static async Task<string> DownloadAsync(HomebrewApp App, IProgress<string>? Progress = null, CancellationToken Token = default)
        {
            var Target = LocalPath(App);
            if (File.Exists(Target) && new FileInfo(Target).Length == App.Size)
                return Target;

            Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
            var Partial = Target + ".part";
            try
            {
                using (var Response = await Http.GetAsync(App.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, Token))
                {
                    if (!Response.IsSuccessStatusCode)
                        throw new IOException($"GitHub didn't send the file (error {(int)Response.StatusCode}).");
                    await using var Source = await Response.Content.ReadAsStreamAsync(Token);
                    await using var Out = File.Create(Partial);
                    var Buffer = new byte[81920];
                    long Done = 0, Shown = -1;
                    while (true)
                    {
                        // HttpClient's timeout stops at the headers: each read gets its own
                        using var Stall = CancellationTokenSource.CreateLinkedTokenSource(Token);
                        Stall.CancelAfter(TimeSpan.FromSeconds(30));
                        int Read;
                        try { Read = await Source.ReadAsync(Buffer, Stall.Token); }
                        catch (OperationCanceledException) when (!Token.IsCancellationRequested)
                        {
                            throw new IOException("The download stalled for 30 seconds.");
                        }
                        if (Read <= 0)
                            break;
                        await Out.WriteAsync(Buffer.AsMemory(0, Read), Token);
                        Done += Read;
                        long Percent = App.Size > 0 ? Done * 100 / App.Size : -1;
                        if (Percent != Shown)
                        {
                            Shown = Percent;
                            Progress?.Report($"Downloading… {Percent}%");
                        }
                    }
                }
                if (App.Size > 0 && new FileInfo(Partial).Length != App.Size)
                    throw new IOException("The download ended early; press Download & send again.");
                if (File.Exists(Target))
                    File.Delete(Target);
                File.Move(Partial, Target);
            }
            catch
            {
                try { File.Delete(Partial); } catch { }
                throw;
            }

            // older versions of this app aren't needed any more
            foreach (var Old in Directory.GetDirectories(ProjectFolder(App)).Where(x => !Target.StartsWith(x + Path.DirectorySeparatorChar)))
                try { Directory.Delete(Old, true); } catch { }
            return Target;
        }

        static Dictionary<string, CachedRelease> LoadCache()
        {
            try
            {
                return File.Exists(CacheFile)
                    ? JsonSerializer.Deserialize<Dictionary<string, CachedRelease>>(File.ReadAllText(CacheFile)) ?? new()
                    : new();
            }
            catch { return new(); }
        }

        static void SaveCache(Dictionary<string, CachedRelease> Cache)
        {
            try
            {
                Directory.CreateDirectory(LibraryService.DataDir);
                File.WriteAllText(CacheFile, JsonSerializer.Serialize(Cache));
            }
            catch { }
        }
    }
}
