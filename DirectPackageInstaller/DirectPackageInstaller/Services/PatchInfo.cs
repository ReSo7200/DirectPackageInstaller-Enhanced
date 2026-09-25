using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DirectPackageInstaller.Services
{
    /// <summary>
    /// Latest official PS4 update version per title ID, from Sony's public
    /// title-patch XML (the same lookup the console does):
    /// http://gs-sec.ww.np.dl.playstation.net/plo/np/{TID}/{HMAC-SHA256("np_"+TID)}/{TID}-ver.xml
    /// A 404 means the title has no updates. Results are cached on disk for a while.
    /// </summary>
    public static class PatchInfo
    {
        static readonly byte[] Key = Convert.FromHexString("AD62E37F905E06BC19593142281C112CEC0E7EC3E97EFDCAEFCDBAAFA6378D84");

        static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

        static readonly TimeSpan CacheLife = TimeSpan.FromHours(12);

        sealed class CacheEntry
        {
            public string Version { get; set; } = ""; // "" = no updates published
            public DateTime Checked { get; set; }
        }

        static readonly object Sync = new();
        static Dictionary<string, CacheEntry>? Cache;
        static string CacheFile => Path.Combine(LibraryService.DataDir, "patches.json");

        public static string UrlFor(string TitleId)
        {
            TitleId = TitleId.Trim().ToUpperInvariant();
            var Hash = Convert.ToHexString(HMACSHA256.HashData(Key, Encoding.ASCII.GetBytes("np_" + TitleId))).ToLowerInvariant();
            return $"http://gs-sec.ww.np.dl.playstation.net/plo/np/{TitleId}/{Hash}/{TitleId}-ver.xml";
        }

        /// <summary>
        /// Latest update version ("03.49") per title ID; titles with no published
        /// updates map to "". Titles that couldn't be checked are left out.
        /// </summary>
        public static async Task<Dictionary<string, string>> LatestAsync(IEnumerable<string> TitleIds, CancellationToken Token = default)
        {
            var Wanted = TitleIds.Where(x => !string.IsNullOrWhiteSpace(x) && Regex.IsMatch(x, "^[A-Z]{4}[0-9]{5}$"))
                .Select(x => x.ToUpperInvariant()).Distinct().ToList();

            var Result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var Stale = new List<string>();

            lock (Sync)
            {
                LoadCache();
                foreach (var Tid in Wanted)
                {
                    if (Cache!.TryGetValue(Tid, out var Hit) && DateTime.UtcNow - Hit.Checked < CacheLife)
                        Result[Tid] = Hit.Version;
                    else
                        Stale.Add(Tid);
                }
            }

            var Fetched = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            await Parallel.ForEachAsync(Stale, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = Token }, async (Tid, ct) =>
            {
                var Version = await FetchAsync(Tid, ct);
                if (Version != null)
                    Fetched[Tid] = Version;
            });

            lock (Sync)
            {
                foreach (var (Tid, Version) in Fetched)
                {
                    Result[Tid] = Version;
                    Cache![Tid] = new CacheEntry { Version = Version, Checked = DateTime.UtcNow };
                }
                SaveCache();
            }

            return Result;
        }

        /// <summary>Version string, "" when no updates exist, null when the lookup failed.</summary>
        static async Task<string?> FetchAsync(string TitleId, CancellationToken Token)
        {
            try
            {
                using var Response = await Http.GetAsync(UrlFor(TitleId), Token);
                if (Response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return "";
                if (!Response.IsSuccessStatusCode)
                    return null;

                var Xml = await Response.Content.ReadAsStringAsync(Token);
                // <titlepatch><tag ...><package version="03.49" .../>
                var Match = Regex.Match(Xml, "<package\\b[^>]*\\bversion=\"([0-9]+\\.[0-9]+)\"");
                return Match.Success ? Match.Groups[1].Value : "";
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

        static void LoadCache()
        {
            if (Cache != null)
                return;
            try
            {
                if (File.Exists(CacheFile))
                    Cache = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(CacheFile));
            }
            catch
            {
            }
            Cache = Cache != null
                ? new Dictionary<string, CacheEntry>(Cache, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        }

        static void SaveCache()
        {
            try
            {
                Directory.CreateDirectory(LibraryService.DataDir);
                File.WriteAllText(CacheFile, JsonSerializer.Serialize(Cache));
            }
            catch
            {
            }
        }

        /// <summary>Compare "01.06" style versions numerically. Unparseable sorts as equal.</summary>
        public static int Compare(string? A, string? B)
        {
            static (int, int) Parse(string? V)
            {
                var Parts = (V ?? "").Split('.');
                int.TryParse(Parts.ElementAtOrDefault(0), out var Major);
                int.TryParse(Parts.ElementAtOrDefault(1), out var Minor);
                return (Major, Minor);
            }
            return Parse(A).CompareTo(Parse(B));
        }
    }
}
