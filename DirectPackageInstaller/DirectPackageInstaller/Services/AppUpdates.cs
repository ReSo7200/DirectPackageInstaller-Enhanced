using System;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DirectPackageInstaller.Services
{
    /// <summary>A newer DPI Enhanced on GitHub: its version, where to read about it and what to download.</summary>
    public sealed record AppRelease(Version Version, string Tag, string PageUrl, string? DownloadUrl, string? FileName, DateTime Published);

    /// <summary>
    /// New versions of this app, as InstaEclipse does it: version.json on the repo's
    /// default branch ({"latest_version": "8.6.0", "update_url": "…"}), bumped when a
    /// version is published. Without it, the latest GitHub release (tag v8.6.0 with the
    /// builds attached). The answer is kept for 6 hours; nothing is installed by itself.
    /// </summary>
    public static class AppUpdates
    {
        public const string Repo = "ReSo7200/DirectPackageInstaller-Enhanced";
        public static string ReleasesPage => $"https://github.com/{Repo}/releases";

        static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
        static AppRelease? Cached;
        static DateTime CheckedAt;

        static AppUpdates()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("DPI-Enhanced/" + SelfUpdate.CurrentVersion);
            Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        public static Version Current => Version.TryParse(SelfUpdate.CurrentVersion, out var V) ? V : new Version(0, 0);

        /// <summary>The newest release when it's newer than this app, else null. Throws with a plain message when GitHub can't be asked.</summary>
        public static async Task<AppRelease?> NewerAsync(bool Force = false, CancellationToken Token = default)
        {
            var Latest = await LatestAsync(Force, Token);
            return Latest != null && Latest.Version > Current ? Latest : null;
        }

        public static async Task<AppRelease?> LatestAsync(bool Force = false, CancellationToken Token = default)
        {
            if (!Force && Cached != null && DateTime.UtcNow - CheckedAt < TimeSpan.FromHours(6))
                return Cached;

            // 1) version.json: the version and where to get it
            try
            {
                using var Json = await Http.GetAsync($"https://raw.githubusercontent.com/{Repo}/HEAD/version.json", Token);
                if (Json.IsSuccessStatusCode)
                {
                    using var Doc = JsonDocument.Parse(await Json.Content.ReadAsStringAsync(Token));
                    var Root = Doc.RootElement;
                    var Latest = Root.TryGetProperty("latest_version", out var L) ? L.GetString() ?? "" : "";
                    var Url = Root.TryGetProperty("update_url", out var U) ? U.GetString() : null;
                    if (System.Version.TryParse(Latest.TrimStart('v', 'V'), out var FromJson))
                    {
                        // the release, when there is one, has this device's file
                        AppRelease? Release = null;
                        try { Release = await FromReleasesAsync(Token); } catch { }
                        Cached = new AppRelease(FromJson, Latest, Url ?? Release?.PageUrl ?? ReleasesPage,
                            Release != null && Release.Version == FromJson ? Release.DownloadUrl : null,
                            Release != null && Release.Version == FromJson ? Release.FileName : null,
                            Release?.Published ?? DateTime.MinValue);
                        CheckedAt = DateTime.UtcNow;
                        return Cached;
                    }
                }
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { throw; }
            catch { /* no version.json yet: ask the releases */ }

            // 2) the latest release
            Cached = await FromReleasesAsync(Token);
            CheckedAt = DateTime.UtcNow;
            return Cached;
        }

        static async Task<AppRelease?> FromReleasesAsync(CancellationToken Token)
        {
            HttpResponseMessage Response;
            try
            {
                Response = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw new InvalidOperationException("GitHub couldn't be reached (check the internet connection).");
            }

            using (Response)
            {
                // no release published yet
                if (Response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return null;
                if (!Response.IsSuccessStatusCode)
                    throw new InvalidOperationException((int)Response.StatusCode is 403 or 429
                        ? "GitHub's hourly limit for this network is used up; try again later."
                        : $"GitHub couldn't answer right now (error {(int)Response.StatusCode}).");

                using var Doc = JsonDocument.Parse(await Response.Content.ReadAsStringAsync(Token));
                var Root = Doc.RootElement;
                var Tag = Root.GetProperty("tag_name").GetString() ?? "";
                if (!System.Version.TryParse(Tag.TrimStart('v', 'V').Split('-')[0], out var Parsed))
                    return null;

                var Assets = Root.TryGetProperty("assets", out var A) ? A.EnumerateArray().ToList() : new();
                var Pattern = AssetHint();
                var Asset = Assets.FirstOrDefault(x => (x.GetProperty("name").GetString() ?? "").Contains(Pattern, StringComparison.OrdinalIgnoreCase));

                return new AppRelease(Parsed, Tag,
                    Root.TryGetProperty("html_url", out var Html) ? Html.GetString() ?? ReleasesPage : ReleasesPage,
                    Asset.ValueKind == JsonValueKind.Undefined ? null : Asset.GetProperty("browser_download_url").GetString(),
                    Asset.ValueKind == JsonValueKind.Undefined ? null : Asset.GetProperty("name").GetString(),
                    Root.TryGetProperty("published_at", out var Date) && Date.TryGetDateTime(out var When) ? When : DateTime.MinValue);
            }
        }

        /// <summary>Which release file is for this device (the names build-windows.ps1 and CI use).</summary>
        static string AssetHint()
        {
            if (OperatingSystem.IsAndroid())
                return ".apk";
            var Arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "ARM64",
                Architecture.Arm => "ARM",
                Architecture.X86 => "X86",
                _ => "X64"
            };
            if (OperatingSystem.IsWindows())
                return $"Windows-{Arch}";
            if (OperatingSystem.IsMacOS())
                return $"OSX-{Arch}";
            return $"Linux-{Arch}";
        }
    }
}
