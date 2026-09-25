using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using ReactiveUI;

namespace DirectPackageInstaller.Services
{
    /// <summary>One patch for a game (60 FPS, resolution, …) from the patch database.</summary>
    public sealed class GamePatch : ReactiveObject
    {
        public string Title { get; init; } = "";
        public string Name { get; init; } = "";
        public string Author { get; init; } = "";
        public string Note { get; init; } = "";
        public string AppVer { get; init; } = "";
        public string AppElf { get; init; } = "";
        /// <summary>For the version installed on the console (or for any version).</summary>
        public bool Applies { get; init; }
        /// <summary>/data/GoldHEN/patches/settings/0x….txt: "1" on, "0" off.</summary>
        public string SettingsFile { get; init; } = "";

        public string Detail => $"for v{AppVer}" + (Author.Length > 0 ? $"  ·  by {Author}" : "") + (Applies ? "" : "  ·  not for the installed version");
        public bool HasNote => Note.Length > 0;

        bool _Enabled;
        public bool Enabled { get => _Enabled; set => this.RaiseAndSetIfChanged(ref _Enabled, value); }

        /// <summary>What the console has now (to know what changed).</summary>
        public bool WasEnabled { get; set; }
    }

    /// <summary>
    /// GoldHEN game patches: the database (illusionyy/PS-Game-Patch release patch1.zip,
    /// downloaded at run time, not bundled) and the console side of GoldHEN's game_patch
    /// plugin: /data/GoldHEN/patches/xml/&lt;TitleID&gt;.xml, and per patch a settings file
    /// "0x%016lx.txt" holding "1" or "0", named by the djb2 hash the plugin computes.
    /// Patches apply when the game starts, with the Game Patch plugin loaded.
    /// </summary>
    public static class GamePatches
    {
        const string DatabaseUrl = "https://github.com/illusionyy/PS-Game-Patch/releases/latest/download/patch1.zip";
        const string ConsoleRoot = "/data/GoldHEN/patches";

        static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
        static string Folder => Path.Combine(LibraryService.DataDir, "game-patches");
        static string ZipPath => Path.Combine(Folder, "patch1.zip");

        /// <summary>The local copy of the database, fetched when missing or a day old.</summary>
        public static async Task<string> DatabaseAsync(CancellationToken Token = default)
        {
            if (File.Exists(ZipPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(ZipPath) < TimeSpan.FromDays(1))
                return ZipPath;

            Directory.CreateDirectory(Folder);
            var Partial = ZipPath + ".part";
            try
            {
                using var Response = await Http.GetAsync(DatabaseUrl, Token);
                if (!Response.IsSuccessStatusCode)
                    throw new IOException($"The patch database couldn't be downloaded (error {(int)Response.StatusCode}).");
                await File.WriteAllBytesAsync(Partial, await Response.Content.ReadAsByteArrayAsync(Token), Token);
                using (ZipFile.OpenRead(Partial)) { }   // a whole zip, not an error page
                File.Move(Partial, ZipPath, true);
            }
            catch when (File.Exists(ZipPath))
            {
                // offline: yesterday's database will do
                try { File.Delete(Partial); } catch { }
            }
            catch
            {
                try { File.Delete(Partial); } catch { }
                throw;
            }
            return ZipPath;
        }

        /// <summary>"Patch database version: …" from the database, for the footer.</summary>
        public static string DatabaseVersion()
        {
            try
            {
                using var Zip = ZipFile.OpenRead(ZipPath);
                var Entry = Zip.GetEntry("patches/misc/patch_ver.txt");
                using var Reader = new StreamReader(Entry!.Open());
                return Reader.ReadLine()?.Replace("Patch database version:", "").Trim() ?? "";
            }
            catch { return ""; }
        }

        /// <summary>The title's patch file from the database (null when there are no patches for it).</summary>
        public static byte[]? XmlFor(string TitleId)
        {
            using var Zip = ZipFile.OpenRead(ZipPath);
            var Entry = Zip.GetEntry($"patches/xml/{TitleId}.xml");
            if (Entry == null)
                return null;
            using var Stream = Entry.Open();
            using var Copy = new MemoryStream();
            Stream.CopyTo(Copy);
            return Copy.ToArray();
        }

        public static List<GamePatch> Parse(string TitleId, byte[] Xml, string InstalledVersion)
        {
            var Doc = XDocument.Load(new MemoryStream(Xml));
            var XmlPath = $"{ConsoleRoot}/xml/{TitleId}.xml";
            var Result = new List<GamePatch>();
            foreach (var Meta in Doc.Descendants("Metadata"))
            {
                string Attr(string Name) => (string?)Meta.Attribute(Name) ?? "";
                var AppVer = Attr("AppVer");
                bool AnyVersion = AppVer.StartsWith("mask", StringComparison.Ordinal) || AppVer.StartsWith("all", StringComparison.Ordinal);
                Result.Add(new GamePatch
                {
                    Title = Attr("Title"),
                    Name = Attr("Name"),
                    Author = Attr("Author"),
                    Note = Attr("Note"),
                    AppVer = AppVer,
                    AppElf = Attr("AppElf"),
                    Applies = AnyVersion || AppVer == InstalledVersion,
                    SettingsFile = $"{ConsoleRoot}/settings/0x{Hash(Attr("Title"), Attr("Name"), AppVer, XmlPath, Attr("AppElf")):x16}.txt"
                });
            }
            return Result;
        }

        /// <summary>
        /// The plugin's patch_hash_calc: djb2 over snprintf("%s%s%s%s%s") into a 256-byte
        /// buffer (so the first 255 bytes), with the console's signed char: bytes from
        /// 0x80 up are sign-extended before the xor, as the C code does.
        /// </summary>
        public static ulong Hash(string Title, string Name, string AppVer, string XmlPath, string Elf)
        {
            var Bytes = Encoding.UTF8.GetBytes(Title + Name + AppVer + XmlPath + Elf);
            ulong Value = 5381;
            foreach (var B in Bytes.Take(255))
            {
                uint C = B >= 0x80 ? 0xFFFFFF00u | B : B;
                Value = unchecked(Value * 33) ^ C;
            }
            return Value;
        }

        /// <summary>Which patches the console has switched on.</summary>
        public static async Task ReadStateAsync(string ConsoleIP, List<GamePatch> Patches, CancellationToken Token = default)
        {
            await using var Ftp = await Connect(ConsoleIP, Token);
            foreach (var Patch in Patches)
            {
                bool On = false;
                try { On = (await Ftp.DownloadAsync(Patch.SettingsFile, 64, Token)).FirstOrDefault() == (byte)'1'; }
                catch (FtpException) { }
                Patch.Enabled = Patch.WasEnabled = On;
            }
        }

        /// <summary>A warning when GoldHEN's Game Patch plugin isn't set to load (null when it is, or unknown).</summary>
        public static async Task<string?> PluginWarningAsync(string ConsoleIP, CancellationToken Token = default)
        {
            try
            {
                await using var Ftp = await Connect(ConsoleIP, Token);
                var Ini = Encoding.UTF8.GetString(await Ftp.DownloadAsync("/data/GoldHEN/plugins.ini", 256 * 1024, Token));
                return Ini.Contains("game_patch.prx", StringComparison.OrdinalIgnoreCase) ? null
                    : "GoldHEN's Game Patch plugin isn't in plugins.ini, so patches won't apply. Add it with GoldHEN's plugin settings (or its plugins package).";
            }
            catch (FtpException)
            {
                return "No plugins.ini on the console: patches only apply with GoldHEN's Game Patch plugin installed and turned on.";
            }
        }

        /// <summary>Put the title's patch file on the console and switch each patch on or off.</summary>
        public static async Task SaveAsync(string ConsoleIP, string TitleId, byte[] Xml, IEnumerable<GamePatch> Patches, CancellationToken Token = default)
        {
            await using var Ftp = await Connect(ConsoleIP, Token);
            foreach (var Dir in new[] { "/data/GoldHEN", ConsoleRoot, $"{ConsoleRoot}/xml", $"{ConsoleRoot}/settings" })
                await Ftp.MakeDirAsync(Dir, Token);

            await using (var Data = new MemoryStream(Xml))
                await Ftp.UploadAsync($"{ConsoleRoot}/xml/{TitleId}.xml", Data, null, Token);

            foreach (var Patch in Patches)
            {
                await using var Flag = new MemoryStream(Encoding.ASCII.GetBytes(Patch.Enabled ? "1\n" : "0\n"));
                await Ftp.UploadAsync(Patch.SettingsFile, Flag, null, Token);
                Patch.WasEnabled = Patch.Enabled;
            }
        }

        static async Task<FtpLite> Connect(string ConsoleIP, CancellationToken Token)
        {
            Exception? Last = null;
            // GoldHEN's server drops some first connections
            for (int Attempt = 0; Attempt < ConsoleInventory.FtpAttempts; Attempt++)
            {
                try { return await FtpLite.ConnectAsync(ConsoleIP, 2121, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15), Token); }
                catch (OperationCanceledException) when (Token.IsCancellationRequested) { throw; }
                catch (Exception ex) { Last = ex; await Task.Delay(1000, Token); }
            }
            throw new IOException("GoldHEN's FTP server didn't answer." + (Last != null ? $" ({Last.Message})" : ""));
        }
    }
}
