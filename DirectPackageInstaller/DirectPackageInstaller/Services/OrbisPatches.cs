using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DirectPackageInstaller.Services
{
    /// <summary>
    /// Read-only client for orbispatches.com — a database of every PS4 game's official
    /// update history. Two of its endpoints are open (no reCAPTCHA):
    ///   GET  /api/internal/search?term=…            → games matching a name or title id
    ///   POST /api/internal/loadpatches {titleid,key}→ every patch version for a title
    /// The per-title <c>key</c> is a token embedded in the title page (data-loadparams).
    /// The actual PKG-download endpoint (/patch) is reCAPTCHA-gated and can't be called
    /// from here, so downloads of the latest version go through Sony's CDN (<see cref="UpdateDownloads"/>)
    /// and older ones open on the site in the browser.
    /// </summary>
    public static class OrbisPatches
    {
        public const string Site = "https://orbispatches.com";
        const string Api = Site + "/api/internal";

        static readonly HttpClient Http = CreateClient();

        // Per-session caches: orbispatches rate-limits after a parallel burst (five titles at once
        // during a console scan), and clicking Details on a game would otherwise refetch the same
        // two endpoints and come back empty. Hold on to successful results so the detail view just
        // reuses what we already have, and skip the title-page round trip on repeat lookups.
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> KeyCache
            = new(StringComparer.OrdinalIgnoreCase);
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<Patch>> PatchCache
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Why the last lookup of a title came back empty (null when it worked), for the UI and diagnostics.</summary>
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> LastFailure
            = new(StringComparer.OrdinalIgnoreCase);
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> NotListed
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>True when orbispatches answered and simply doesn't know this title (homebrew, system apps).</summary>
        public static bool IsNotListed(string TitleId) => NotListed.ContainsKey(TitleId.Trim());

        static HttpClient CreateClient()
        {
            var Client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            Client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36");
            Client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", Site + "/");
            Client.DefaultRequestHeaders.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            return Client;
        }

        /// <summary>A game in orbispatches' database.</summary>
        public sealed record Game(string TitleId, string Name, string Region, string? IconUrl)
        {
            public string RegionText => string.IsNullOrWhiteSpace(Region) ? "" : Region.ToUpperInvariant();
        }

        /// <summary>One official update of a title.</summary>
        public sealed record Patch(string Version, string SizeText, string RequiredFirmware, string Date,
            string Changelog, bool IsLatest)
        {
            public string FirmwareText => string.IsNullOrWhiteSpace(RequiredFirmware) ? "" : "FW " + RequiredFirmware;
        }

        static readonly Regex TitleIdRegex = new("^[A-Za-z]{4}[0-9]{5}$", RegexOptions.CultureInvariant);
        // data-loadparams="{ 'titleid': 'CUSA00411', 'key': '…hex…' }"
        static readonly Regex KeyRegex = new(@"data-loadparams=""[^""]*'key'\s*:\s*'(?<key>[0-9a-fA-F]{16,})'",
            RegexOptions.CultureInvariant);

        public static bool IsTitleId(string Text) => TitleIdRegex.IsMatch(Text.Trim());

        /// <summary>Games whose name or title id matches Term (newest/most-relevant first, as the site returns them).</summary>
        public static async Task<List<Game>> SearchAsync(string Term, CancellationToken Token = default)
        {
            var Result = new List<Game>();
            Term = Term.Trim();
            if (Term.Length < 2)
                return Result;
            try
            {
                var Json = await Http.GetStringAsync($"{Api}/search?term={Uri.EscapeDataString(Term)}", Token);
                using var Doc = JsonDocument.Parse(Json);
                if (!Doc.RootElement.TryGetProperty("results", out var Results) || Results.ValueKind != JsonValueKind.Array)
                    return Result;
                foreach (var Item in Results.EnumerateArray())
                {
                    var TitleId = Str(Item, "titleid");
                    if (TitleId.Length == 0)
                        continue;
                    Result.Add(new Game(TitleId, Str(Item, "name"), Str(Item, "region"),
                        Str(Item, "icon") is { Length: > 0 } Icon ? Icon : null));
                }
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { throw; }
            catch { /* offline / shape change: return what we have */ }
            return Result;
        }

        /// <summary>The per-title token from the title page, needed for <see cref="LoadPatchesAsync"/>. Cached per session.</summary>
        static async Task<string?> TitleKeyAsync(string TitleId, CancellationToken Token)
        {
            if (KeyCache.TryGetValue(TitleId, out var Cached))
                return Cached;
            try
            {
                var Html = await Http.GetStringAsync($"{Site}/{TitleId.ToUpperInvariant()}", Token);
                var Match = KeyRegex.Match(Html);
                if (!Match.Success)
                {
                    // a real page without load params = the title isn't in their database (homebrew,
                    // system apps). That's a definitive answer, not a failure worth retrying.
                    if (Html.Contains("<html", StringComparison.OrdinalIgnoreCase) && Html.Length > 5000)
                        NotListed[TitleId] = true;
                    LastFailure[TitleId] = NotListed.ContainsKey(TitleId)
                        ? "not in orbispatches"
                        : "title page without a key (" + Html.Length + " bytes — blocked or rate-limited?)";
                    return null;
                }
                var Key = Match.Groups["key"].Value;
                KeyCache[TitleId] = Key;
                return Key;
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { throw; }
            catch (Exception ex) { LastFailure[TitleId] = "title page: " + ex.GetType().Name + ": " + ex.Message; return null; }
        }

        /// <summary>Every official update version for a title, newest first; empty when none/failed. Cached per session.</summary>
        public static async Task<List<Patch>> LoadPatchesAsync(string TitleId, CancellationToken Token = default)
        {
            TitleId = TitleId.Trim().ToUpperInvariant();
            if (!IsTitleId(TitleId))
                return new List<Patch>();

            if (PatchCache.TryGetValue(TitleId, out var CachedPatches))
                return CachedPatches;

            // orbispatches intermittently rejects the opening burst of a console check (measured: the
            // first ~11 titles of a 29-title scan came back empty, the rest fine). Back off and retry
            // instead of letting those titles read as "no updates" for the whole session.
            var Result = await LoadPatchesOnceAsync(TitleId, Token);
            foreach (var Delay in new[] { 1500, 4000 })
            {
                if (Result.Count > 0 || NotListed.ContainsKey(TitleId))
                    break;
                await Task.Delay(Delay, Token);
                Result = await LoadPatchesOnceAsync(TitleId, Token);
            }
            return Result;
        }

        static async Task<List<Patch>> LoadPatchesOnceAsync(string TitleId, CancellationToken Token)
        {
            var Result = new List<Patch>();
            var Key = await TitleKeyAsync(TitleId, Token);
            if (Key == null)
                return Result;

            bool Complete = false;
            try
            {
                var Body = new StringContent(JsonSerializer.Serialize(new { titleid = TitleId, key = Key }),
                    Encoding.UTF8, "application/json");
                using var Response = await Http.PostAsync($"{Api}/loadpatches", Body, Token);
                if (!Response.IsSuccessStatusCode)
                {
                    LastFailure[TitleId] = "loadpatches HTTP " + (int)Response.StatusCode;
                    return Result;
                }

                var Json = await Response.Content.ReadAsStringAsync(Token);
                using var Doc = JsonDocument.Parse(Json);
                var Root = Doc.RootElement;
                if (!Root.TryGetProperty("success", out var Ok) || !Ok.GetBoolean()
                    || !Root.TryGetProperty("patches", out var Patches) || Patches.ValueKind != JsonValueKind.Array)
                {
                    LastFailure[TitleId] = "loadpatches: " + (Json.Length > 120 ? Json[..120] : Json);
                    return Result;
                }

                foreach (var Item in Patches.EnumerateArray())
                {
                    var Version = Str(Item, "version");
                    if (Version.Length == 0)
                        continue;
                    Result.Add(new Patch(
                        Version,
                        Str(Item, "filesize"),
                        Str(Item, "required_firmware"),
                        Str(Item, "creation_date"),
                        Str(Item, "changelog_preview"),
                        Item.TryGetProperty("is_latest", out var Latest) && Latest.ValueKind == JsonValueKind.True));
                }
                Complete = true;
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { throw; }
            catch (Exception ex) { LastFailure[TitleId] = "loadpatches: " + ex.GetType().Name + ": " + ex.Message; }
            // Only cache a fully-parsed, non-empty result. A truncated list from a mid-loop parse
            // error (or an empty one from a rate-limit) must not stick — the next lookup should
            // retry and get the real list, not keep returning the stub forever.
            if (Complete && Result.Count > 0)
            {
                PatchCache[TitleId] = Result;
                LastFailure.TryRemove(TitleId, out _);
            }
            return Result;
        }

        /// <summary>The latest official update of a title, or null when it has none / the lookup failed.</summary>
        public static async Task<Patch?> LatestPatchAsync(string TitleId, CancellationToken Token = default)
        {
            var Patches = await LoadPatchesAsync(TitleId, Token);
            // prefer the server's is_latest flag (it's an explicit choice), but if orbispatches ever
            // ships back is_latest in a form JsonValueKind.True doesn't catch (e.g. "1" instead of
            // true), fall back to the highest version by numeric compare — not Patches[0], which
            // trusts an API ordering that isn't documented.
            foreach (var Patch in Patches)
                if (Patch.IsLatest)
                    return Patch;
            return Patches.Count == 0 ? null
                : Patches.OrderByDescending(p => p.Version, Comparer<string>.Create(PatchInfo.Compare)).First();
        }

        /// <summary>The public title page (where the browser can download any version through the reCAPTCHA gate).</summary>
        public static string PageFor(string TitleId) => $"{Site}/{TitleId.Trim().ToUpperInvariant()}";

        static string Str(JsonElement Element, string Name)
            => Element.TryGetProperty(Name, out var Value) && Value.ValueKind == JsonValueKind.String
                ? Value.GetString() ?? ""
                : "";
    }
}
