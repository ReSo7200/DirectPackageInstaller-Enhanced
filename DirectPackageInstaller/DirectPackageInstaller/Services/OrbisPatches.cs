using System;
using System.Collections.Generic;
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

        /// <summary>The per-title token from the title page, needed for <see cref="LoadPatchesAsync"/>.</summary>
        static async Task<string?> TitleKeyAsync(string TitleId, CancellationToken Token)
        {
            try
            {
                var Html = await Http.GetStringAsync($"{Site}/{TitleId.ToUpperInvariant()}", Token);
                var Match = KeyRegex.Match(Html);
                return Match.Success ? Match.Groups["key"].Value : null;
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { throw; }
            catch { return null; }
        }

        /// <summary>Every official update version for a title, newest first; empty when none/failed.</summary>
        public static async Task<List<Patch>> LoadPatchesAsync(string TitleId, CancellationToken Token = default)
        {
            var Result = new List<Patch>();
            TitleId = TitleId.Trim().ToUpperInvariant();
            if (!IsTitleId(TitleId))
                return Result;

            var Key = await TitleKeyAsync(TitleId, Token);
            if (Key == null)
                return Result;

            try
            {
                var Body = new StringContent(JsonSerializer.Serialize(new { titleid = TitleId, key = Key }),
                    Encoding.UTF8, "application/json");
                using var Response = await Http.PostAsync($"{Api}/loadpatches", Body, Token);
                if (!Response.IsSuccessStatusCode)
                    return Result;

                var Json = await Response.Content.ReadAsStringAsync(Token);
                using var Doc = JsonDocument.Parse(Json);
                var Root = Doc.RootElement;
                if (!Root.TryGetProperty("success", out var Ok) || !Ok.GetBoolean()
                    || !Root.TryGetProperty("patches", out var Patches) || Patches.ValueKind != JsonValueKind.Array)
                    return Result;

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
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { throw; }
            catch { /* leave Result as-is */ }
            return Result;
        }

        /// <summary>The latest official update of a title, or null when it has none / the lookup failed.</summary>
        public static async Task<Patch?> LatestPatchAsync(string TitleId, CancellationToken Token = default)
        {
            var Patches = await LoadPatchesAsync(TitleId, Token);
            foreach (var Patch in Patches)
                if (Patch.IsLatest)
                    return Patch;
            return Patches.Count > 0 ? Patches[0] : null;
        }

        /// <summary>The public title page (where the browser can download any version through the reCAPTCHA gate).</summary>
        public static string PageFor(string TitleId) => $"{Site}/{TitleId.Trim().ToUpperInvariant()}";

        static string Str(JsonElement Element, string Name)
            => Element.TryGetProperty(Name, out var Value) && Value.ValueKind == JsonValueKind.String
                ? Value.GetString() ?? ""
                : "";
    }
}
