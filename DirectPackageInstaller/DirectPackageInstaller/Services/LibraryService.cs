using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DirectPackageInstaller.Services
{
    /// <summary>One PKG file found in a library folder.</summary>
    public sealed class LibraryEntry
    {
        public string Path { get; set; } = "";
        public long Size { get; set; }
        public DateTime Modified { get; set; }

        public string Title { get; set; } = "";
        public string TitleId { get; set; } = "";
        public string ContentId { get; set; } = "";
        /// <summary>PARAM.SFO CATEGORY: gd = game, gp = update, ac = DLC.</summary>
        public string Category { get; set; } = "";
        public string AppVersion { get; set; } = "";
        public string SystemVersion { get; set; } = "";
        public bool Fake { get; set; }

        /// <summary>Cached ICON0.png on disk, or null.</summary>
        public string? IconFile { get; set; }

        /// <summary>Set when the file could not be read as a PKG.</summary>
        public string? Error { get; set; }

        public string Kind => Category.ToLowerInvariant() switch
        {
            "gd" => "Game",
            "gp" => "Update",
            "ac" => "DLC",
            "" => "Unknown",
            _ => "App"
        };
    }

    /// <summary>
    /// Local PKG library: a list of folders, scanned recursively. Parsed PKG
    /// details are cached by path + size + modified time so a rescan only
    /// reads new or changed files.
    /// </summary>
    public static class LibraryService
    {
        sealed class Store
        {
            public List<string> Folders { get; set; } = new();
            public List<LibraryEntry> Entries { get; set; } = new();
        }

        static readonly object Sync = new();
        static Store? Data;

        public static string DataDir => System.IO.Path.Combine(App.WorkingDirectory, "Library");
        static string StoreFile => System.IO.Path.Combine(DataDir, "library.json");
        static string IconDir => System.IO.Path.Combine(DataDir, "icons");

        static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        static Store Load()
        {
            lock (Sync)
            {
                if (Data != null)
                    return Data;

                try
                {
                    if (File.Exists(StoreFile))
                        Data = JsonSerializer.Deserialize<Store>(File.ReadAllText(StoreFile), JsonOptions);
                }
                catch
                {
                    // corrupt cache: start over, folders are re-added by the user
                }

                return Data ??= new Store();
            }
        }

        static void Save()
        {
            lock (Sync)
            {
                if (Data == null)
                    return;

                try
                {
                    Directory.CreateDirectory(DataDir);
                    var Temp = StoreFile + ".tmp";
                    File.WriteAllText(Temp, JsonSerializer.Serialize(Data, JsonOptions));
                    File.Move(Temp, StoreFile, true);
                }
                catch
                {
                }
            }
        }

        public static IReadOnlyList<string> Folders
        {
            get { lock (Sync) return Load().Folders.ToList(); }
        }

        /// <summary>Last scan result, without touching the disk.</summary>
        public static IReadOnlyList<LibraryEntry> Cached
        {
            get { lock (Sync) return Load().Entries.ToList(); }
        }

        public static bool AddFolder(string Folder)
        {
            Folder = System.IO.Path.GetFullPath(Folder.Trim().Trim('"'));
            lock (Sync)
            {
                var Store = Load();
                if (Store.Folders.Any(x => string.Equals(x, Folder, StringComparison.OrdinalIgnoreCase)))
                    return false;

                Store.Folders.Add(Folder);
            }
            Save();
            return true;
        }

        public static void RemoveFolder(string Folder)
        {
            lock (Sync)
            {
                var Store = Load();
                Store.Folders.RemoveAll(x => string.Equals(x, Folder, StringComparison.OrdinalIgnoreCase));
                Store.Entries.RemoveAll(x => IsUnder(x.Path, Folder));
            }
            Save();
        }

        static bool IsUnder(string File, string Folder)
        {
            var Prefix = Folder.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            return File.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Scan every library folder for *.pkg files. Unchanged files come from
        /// the cache; new or changed ones are parsed. Missing files are dropped.
        /// </summary>
        public static async Task<IReadOnlyList<LibraryEntry>> ScanAsync(IProgress<(int Done, int Total, string File)>? Progress = null, CancellationToken Token = default)
        {
            var Store = Load();
            List<string> Folders;
            Dictionary<string, LibraryEntry> Previous;
            lock (Sync)
            {
                Folders = Store.Folders.ToList();
                Previous = Store.Entries
                    .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            }

            var Files = await Task.Run(() => Folders.SelectMany(FindPackages).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), Token);

            var Results = new LibraryEntry?[Files.Count];
            int Done = 0;

            // Two readers: headers are small, but libraries often live on HDDs.
            await Parallel.ForEachAsync(Enumerable.Range(0, Files.Count), new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = Token }, async (i, ct) =>
            {
                var File = Files[i];
                Results[i] = await Task.Run(() => ReadEntry(File, Previous), ct);
                Progress?.Report((Interlocked.Increment(ref Done), Files.Count, File));
            });

            List<LibraryEntry> Entries;
            lock (Sync)
            {
                // a folder removed while scanning must not come back
                var Current = Store.Folders.ToList();
                Entries = Results.OfType<LibraryEntry>().Where(x => Current.Any(f => IsUnder(x.Path, f))).ToList();
                Store.Entries = Entries;
            }

            Save();
            return Entries;
        }

        static IEnumerable<string> FindPackages(string Folder)
        {
            if (!Directory.Exists(Folder))
                return Array.Empty<string>();

            try
            {
                return Directory.EnumerateFiles(Folder, "*.pkg", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    MatchCasing = MatchCasing.CaseInsensitive,
                    AttributesToSkip = FileAttributes.System
                }).ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Read one package outside any library folder (e.g. "Send to PS4" from
        /// Explorer). Returns null when the file does not exist; check Error for unreadable files.
        /// </summary>
        public static LibraryEntry? ReadPackage(string File) =>
            ReadEntry(System.IO.Path.GetFullPath(File), new Dictionary<string, LibraryEntry>(StringComparer.OrdinalIgnoreCase));

        static LibraryEntry? ReadEntry(string File, Dictionary<string, LibraryEntry> Previous)
        {
            FileInfo Info;
            try
            {
                Info = new FileInfo(File);
                if (!Info.Exists)
                    return null; // deleted since the folder was listed
                _ = Info.Length;
            }
            catch
            {
                return null;
            }

            // unreadable entries are retried every scan (the file may have been locked or still copying)
            if (Previous.TryGetValue(File, out var Cached) && Cached.Error == null && Cached.Size == Info.Length && Cached.Modified == Info.LastWriteTimeUtc
                && (Cached.IconFile == null || System.IO.File.Exists(Cached.IconFile)))
                return Cached;

            var Entry = new LibraryEntry
            {
                Path = File,
                Size = Info.Length,
                Modified = Info.LastWriteTimeUtc,
                Title = System.IO.Path.GetFileNameWithoutExtension(File)
            };

            try
            {
                using var Stream = new FileStream(File, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
                var Pkg = Stream.GetPKGInfo();
                if (Pkg == null)
                {
                    Entry.Error = "Not a PS4 package";
                    return Entry;
                }

                var Value = Pkg.Value;
                string Param(string Name) => Value.Params?.FirstOrDefault(x => x.Name == Name)?.Value ?? "";

                if (!string.IsNullOrWhiteSpace(Value.FriendlyName))
                    Entry.Title = Value.FriendlyName;

                Entry.TitleId = Value.TitleID ?? "";
                Entry.ContentId = Value.ContentID ?? "";
                Entry.Category = (Value.ContentType ?? "").Trim().ToLowerInvariant();
                Entry.AppVersion = Param("APP_VER");
                if (string.IsNullOrEmpty(Entry.AppVersion))
                    Entry.AppVersion = Param("VERSION");
                Entry.SystemVersion = SystemVersion(Param("SYSTEM_VER"));
                Entry.Fake = Value.FakePackage;

                if (Value.IconData is { Length: > 0 } Icon)
                    Entry.IconFile = SaveIcon(Icon);
            }
            catch (Exception ex)
            {
                Entry.Error = ex.Message;
            }

            return Entry;
        }

        /// <summary>SYSTEM_VER is shown as hex "05050000" → "5.05".</summary>
        static string SystemVersion(string Hex)
        {
            if (Hex.Length < 4 || !uint.TryParse(Hex, System.Globalization.NumberStyles.HexNumber, null, out var Raw) || Raw == 0)
                return "";

            var Major = (Raw >> 24) & 0xFF;
            var Minor = (Raw >> 16) & 0xFF;
            return $"{Major:X}.{Minor:X2}";
        }

        static string? SaveIcon(byte[] Png)
        {
            try
            {
                Directory.CreateDirectory(IconDir);
                var Name = Convert.ToHexString(SHA1.HashData(Png)) + ".png";
                var File = System.IO.Path.Combine(IconDir, Name);
                if (!System.IO.File.Exists(File))
                {
                    // a game and its update often share a cover and are read in parallel:
                    // write privately, then move into place (losing the race is fine)
                    var Temp = File + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    System.IO.File.WriteAllBytes(Temp, Png);
                    try { System.IO.File.Move(Temp, File, false); }
                    catch (IOException) { System.IO.File.Delete(Temp); }
                }
                return File;
            }
            catch
            {
                return null;
            }
        }
    }
}
