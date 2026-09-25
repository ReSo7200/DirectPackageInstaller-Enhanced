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
        public bool Busy { get => _Busy; set { this.RaiseAndSetIfChanged(ref _Busy, value); this.RaisePropertyChanged(nameof(CanGet)); } }

        public bool CanGet => !Busy && DownloadUrl.Length > 0;

        public string Detail => Version.Length == 0 ? Repo
            : $"{Repo}  ·  {Version}  ·  {Released}  ·  {Host.TransferProgressInfo.FormatBytes(Size)}";

        internal void Changed() => this.RaisePropertyChanged(nameof(CanGet));
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

        /// <summary>Fill in each app's latest release (version, date, size, download URL).</summary>
        public static async Task RefreshAsync(IEnumerable<HomebrewApp> Apps, CancellationToken Token = default)
        {
            var Cache = LoadCache();
            foreach (var App in Apps)
            {
                try
                {
                    var Json = await LatestReleaseAsync(App.Repo, Cache, Token);
                    Apply(App, Json);
                }
                catch (OperationCanceledException) when (Token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    App.Status = "Couldn't read its releases: " + ex.Message;
                }
            }
            SaveCache(Cache);
        }

        static async Task<string?> LatestReleaseAsync(string Repo, Dictionary<string, CachedRelease> Cache, CancellationToken Token)
        {
            Cache.TryGetValue(Repo, out var Cached);
            // recently checked: don't spend the hourly allowance
            if (Cached != null && DateTime.UtcNow - Cached.Checked < TimeSpan.FromHours(1))
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
                throw new InvalidOperationException("no releases (or the project doesn't exist)");
            if (!Response.IsSuccessStatusCode)
            {
                if (Cached != null)
                    return Cached.Json;   // rate-limited or offline: last known
                throw new InvalidOperationException($"GitHub answered {(int)Response.StatusCode}");
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
            Path.Combine(Folder, App.Repo.Replace('/', '_'), Sanitize(App.Version), Sanitize(App.FileName));

        static string Sanitize(string Name) => string.Concat(Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        /// <summary>Download the app's package (kept, so sending it again is instant).</summary>
        public static async Task<string> DownloadAsync(HomebrewApp App, IProgress<string>? Progress = null, CancellationToken Token = default)
        {
            var Target = LocalPath(App);
            if (File.Exists(Target) && new FileInfo(Target).Length == App.Size)
                return Target;

            Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
            var Partial = Target + ".part";
            using (var Response = await Http.GetAsync(App.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, Token))
            {
                Response.EnsureSuccessStatusCode();
                await using var Source = await Response.Content.ReadAsStreamAsync(Token);
                await using var File_ = File.Create(Partial);
                var Buffer = new byte[81920];
                long Done = 0;
                int Read;
                while ((Read = await Source.ReadAsync(Buffer, Token)) > 0)
                {
                    await File_.WriteAsync(Buffer.AsMemory(0, Read), Token);
                    Done += Read;
                    if (App.Size > 0)
                        Progress?.Report($"Downloading… {Done * 100 / App.Size}%");
                }
            }
            if (File.Exists(Target))
                File.Delete(Target);
            File.Move(Partial, Target);
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
