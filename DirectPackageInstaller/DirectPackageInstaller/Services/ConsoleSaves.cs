using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DirectPackageInstaller.Services
{
    /// <summary>
    /// Saved data on the console, over GoldHEN FTP. Each console user has
    /// /user/home/&lt;id&gt;/savedata/&lt;TitleID&gt;/ (sdimg_&lt;slot&gt; image + &lt;slot&gt;.bin key)
    /// and savedata_meta/user/&lt;TitleID&gt;/. A backup is a zip per user that keeps those
    /// paths, in Folder\&lt;Game&gt; (&lt;TitleID&gt;)\. Restoring writes the files back, to the
    /// same user or another one. Saves are sealed to the console: another console's
    /// backup needs Apollo Save Tool to resign it.
    /// </summary>
    public static class ConsoleSaves
    {
        /// <summary>The folder chosen on the Saves page, else Documents\DPI Save Backups (phone: Download).</summary>
        public static string Folder => !string.IsNullOrWhiteSpace(App.Config.SaveBackupFolder)
            ? App.Config.SaveBackupFolder
            : DefaultFolder;

        public static string DefaultFolder => App.IsAndroid
            ? "/storage/emulated/0/Download/DPI Save Backups"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DPI Save Backups");

        public const string BeforeRestoreTag = "before restore";

        public sealed record SaveFile(string Path, long Size, DateTime? Modified);

        public sealed record UserSaves(string UserId, string UserName, List<SaveFile> Files)
        {
            /// <summary>Save slots: one sdimg_ image each.</summary>
            public int Slots => Files.Count(f => System.IO.Path.GetFileName(f.Path).StartsWith("sdimg_", StringComparison.OrdinalIgnoreCase));
            public long Size => Files.Sum(f => Math.Max(0, f.Size));
            public DateTime? LastSaved => Files.Max(f => f.Modified);
        }

        /// <summary>One game's saves on the console: every user who has some.</summary>
        public sealed record TitleSaves(string TitleId, List<UserSaves> Users)
        {
            public long Size => Users.Sum(u => u.Size);
            public int Slots => Users.Sum(u => u.Slots);
            public DateTime? LastSaved => Users.Max(u => u.LastSaved);
        }

        public sealed record ConsoleUser(string Id, string Name);

        /// <summary>What the console has: saves per game, and every user (restore targets).</summary>
        public sealed record SaveScan(List<TitleSaves> Titles, List<ConsoleUser> Users);

        /// <summary>A backup zip in Folder (or an imported one).</summary>
        public sealed record SaveBackup(string Path, string TitleId, string GameName, string UserId, string UserName,
            DateTime Created, long Size, int Files, bool BeforeRestore);

        // user/home/<id>/savedata/<TID>/<file>  or  user/home/<id>/savedata_meta/user/<TID>/<file>
        static readonly Regex SavePath = new(@"^/?user/home/(?<user>[^/]+)/(?<kind>savedata|savedata_meta/user)/(?<tid>[^/]+)/(?<rest>[^/].*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // "<user name> <user id> 2026-09-26 071958[ before restore].zip"
        static readonly Regex ZipName = new(@"^(?<name>.*?)\s*(?<id>[0-9A-Fa-f]{8}) (?<date>\d{4}-\d{2}-\d{2} \d{6})(?<tag> " + BeforeRestoreTag + @")?$",
            RegexOptions.CultureInvariant);
        // "<game> (<TID>)"
        static readonly Regex GameFolder = new(@"^(?<name>.*) \((?<tid>[A-Z]{4}\d{5})\)$", RegexOptions.CultureInvariant);

        static bool IsEntry(FtpEntry x) => x.Name != "." && x.Name != "..";

        /// <summary>Every save on the console, per game (all users), and the console's users.</summary>
        public static async Task<SaveScan> ListAllAsync(string ConsoleIP, IProgress<string>? Progress = null, CancellationToken Token = default)
        {
            var ByTitle = new Dictionary<string, List<UserSaves>>(StringComparer.OrdinalIgnoreCase);
            var AllUsers = new List<ConsoleUser>();
            await using var Ftp = await Connect(ConsoleIP, Token);
            var Users = (await Ftp.ListAsync("/user/home", Token)).Where(x => x.IsDirectory && IsEntry(x)).ToList();
            int Index = 0;
            foreach (var User in Users)
            {
                Progress?.Report($"Reading user {++Index} of {Users.Count}…");
                var Home = $"/user/home/{User.Name}";
                string Name = await UserNameAsync(ConsoleIP, Home, User.Name, Token);
                AllUsers.Add(new ConsoleUser(User.Name, Name));

                List<FtpEntry> Titles;
                try { Titles = await Ftp.ListAsync($"{Home}/savedata", Token); }
                catch (FtpException ex) when (ex.Reply is { Code: >= 400 }) { continue; }

                foreach (var Title in Titles.Where(x => x.IsDirectory && IsEntry(x) && x.Name != "sce_backup"))
                {
                    var Files = await FilesOfAsync(Ftp, Home, Title.Name, Token);
                    if (Files.Count == 0)
                        continue;
                    if (!ByTitle.TryGetValue(Title.Name, out var List))
                        ByTitle[Title.Name] = List = new List<UserSaves>();
                    List.Add(new UserSaves(User.Name, Name, Files));
                }
            }
            return new SaveScan(ByTitle.Select(x => new TitleSaves(x.Key, x.Value)).OrderBy(x => x.TitleId).ToList(), AllUsers);
        }

        static async Task<List<SaveFile>> FilesOfAsync(FtpLite Ftp, string Home, string TitleId, CancellationToken Token)
        {
            var Files = new List<SaveFile>();
            foreach (var Dir in new[] { $"{Home}/savedata/{TitleId}", $"{Home}/savedata_meta/user/{TitleId}" })
            {
                try
                {
                    foreach (var File in await Ftp.ListAsync(Dir, Token))
                        if (!File.IsDirectory && IsEntry(File))
                            Files.Add(new SaveFile($"{Dir}/{File.Name}", File.Size, File.Modified));
                }
                catch (FtpException ex) when (ex.Reply is { Code: >= 400 }) { }
            }
            return Files;
        }

        static async Task<string> UserNameAsync(string ConsoleIP, string Home, string Fallback, CancellationToken Token)
        {
            try
            {
                // its own connection: a failed read can leave the listing one unusable
                await using var NameFtp = await Connect(ConsoleIP, Token);
                var Raw = await NameFtp.DownloadAsync($"{Home}/username.dat", 4096, Token);
                var Text = Encoding.UTF8.GetString(Raw).Split('\0')[0].Trim();
                return Text.Length > 0 ? Text : Fallback;
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { throw; }
            catch { return Fallback; }
        }

        /// <summary>The title's save files for every console user that has some (or only OnlyUser).</summary>
        public static async Task<List<UserSaves>> FindAsync(string ConsoleIP, string TitleId, CancellationToken Token = default, string? OnlyUser = null)
        {
            var Found = new List<UserSaves>();
            await using var Ftp = await Connect(ConsoleIP, Token);
            foreach (var User in (await Ftp.ListAsync("/user/home", Token)).Where(x => x.IsDirectory && IsEntry(x)))
            {
                if (OnlyUser != null && !string.Equals(User.Name, OnlyUser, StringComparison.OrdinalIgnoreCase))
                    continue;
                var Home = $"/user/home/{User.Name}";
                var Files = await FilesOfAsync(Ftp, Home, TitleId, Token);
                if (Files.Count > 0)
                    Found.Add(new UserSaves(User.Name, await UserNameAsync(ConsoleIP, Home, User.Name, Token), Files));
            }
            return Found;
        }

        /// <summary>This game's folder in Folder: "&lt;Game&gt; (&lt;TID&gt;)", or the one a backup already made.</summary>
        public static string GameFolderOf(string TitleId, string GameName)
        {
            try
            {
                // keep one folder per game even if its name was read differently another time
                if (Directory.Exists(Folder))
                    foreach (var Dir in Directory.EnumerateDirectories(Folder))
                        if (GameFolder.Match(System.IO.Path.GetFileName(Dir)) is { Success: true } M
                            && string.Equals(M.Groups["tid"].Value, TitleId, StringComparison.OrdinalIgnoreCase))
                            return Dir;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            var Name = string.Equals(GameName, TitleId, StringComparison.OrdinalIgnoreCase) ? TitleId : $"{GameName} ({TitleId})";
            return System.IO.Path.Combine(Folder, SafeNames.Of(Name, SafeNames.Of(TitleId)));
        }

        /// <summary>One zip per user in the game's folder; returns the folder. IconFile, when given, is kept as icon0.png.</summary>
        public static async Task<string> BackupAsync(string ConsoleIP, string TitleId, string GameName, List<UserSaves> Users,
            IProgress<string>? Progress = null, CancellationToken Token = default, string? Tag = null, string? IconFile = null)
        {
            var Target = GameFolderOf(TitleId, GameName);
            Directory.CreateDirectory(Target);
            var Stamp = DateTime.Now.ToString("yyyy-MM-dd HHmmss");

            // the cover, so the Saves page can show it when the game isn't installed
            try
            {
                var Icon = System.IO.Path.Combine(Target, "icon0.png");
                if (IconFile != null && File.Exists(IconFile) && !File.Exists(Icon))
                    File.Copy(IconFile, Icon);
            }
            catch { }

            foreach (var User in Users)
            {
                // the console's user id too: two users may share a name
                var Zip = System.IO.Path.Combine(Target, SafeNames.Of($"{User.UserName} {User.UserId} {Stamp}" + (Tag != null ? " " + Tag : "")) + ".zip");
                var Partial = Zip + ".part";
                try
                {
                    using (var Archive = ZipFile.Open(Partial, ZipArchiveMode.Create))
                    {
                        int Index = 0;
                        foreach (var File_ in User.Files)
                        {
                            Progress?.Report($"Backing up {User.UserName}'s saves: {++Index} of {User.Files.Count}");
                            // keep the console paths (user/home/<id>/...) so the zip shows where each file belongs
                            var Entry = Archive.CreateEntry(File_.Path.TrimStart('/'), CompressionLevel.Fastest);
                            if (File_.Modified is { } When)
                                Entry.LastWriteTime = When;
                            await using var Out = Entry.Open();
                            await using var Ftp = await Connect(ConsoleIP, Token);
                            await Ftp.DownloadToAsync(File_.Path, Out, null, Token);
                        }
                    }
                    if (File.Exists(Zip))
                        File.Delete(Zip);
                    File.Move(Partial, Zip);
                }
                catch
                {
                    try { File.Delete(Partial); } catch { }
                    throw;
                }
            }
            return Target;
        }

        /// <summary>Every backup zip in Folder (and one level of subfolders), newest first.</summary>
        public static List<SaveBackup> ListBackups()
        {
            var Found = new List<SaveBackup>();
            if (!Directory.Exists(Folder))
                return Found;

            IEnumerable<string> Zips;
            try
            {
                Zips = Directory.EnumerateFiles(Folder, "*.zip", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.EnumerateDirectories(Folder).SelectMany(d =>
                    {
                        try { return Directory.EnumerateFiles(d, "*.zip", SearchOption.TopDirectoryOnly).ToList(); }
                        catch { return new List<string>(); }
                    })).ToList();
            }
            catch (IOException) { return Found; }
            catch (UnauthorizedAccessException) { return Found; }

            foreach (var Zip in Zips)
                if (Read(Zip) is { } Backup)
                    Found.Add(Backup);
            return Found.OrderByDescending(x => x.Created).ToList();
        }

        /// <summary>A zip's game and user, from the save paths inside it; null when it isn't a save backup.</summary>
        public static SaveBackup? Read(string Zip)
        {
            try
            {
                using var Archive = ZipFile.OpenRead(Zip);
                string? TitleId = null, UserId = null;
                int Files = 0;
                foreach (var Entry in Archive.Entries)
                {
                    if (Entry.FullName.EndsWith('/') || SavePath.Match(Entry.FullName.Replace('\\', '/')) is not { Success: true } M)
                        continue;
                    TitleId ??= M.Groups["tid"].Value.ToUpperInvariant();
                    UserId ??= M.Groups["user"].Value;
                    if (string.Equals(TitleId, M.Groups["tid"].Value, StringComparison.OrdinalIgnoreCase))
                        Files++;
                }
                if (TitleId == null || UserId == null)
                    return null;

                var Info = new FileInfo(Zip);
                var Name = System.IO.Path.GetFileNameWithoutExtension(Zip);
                string UserName = UserId;
                DateTime Created = Info.LastWriteTime;
                bool Before = false;
                if (ZipName.Match(Name) is { Success: true } Z)
                {
                    if (Z.Groups["name"].Value.Trim() is { Length: > 0 } N)
                        UserName = N;
                    if (DateTime.TryParseExact(Z.Groups["date"].Value, "yyyy-MM-dd HHmmss", null, System.Globalization.DateTimeStyles.None, out var When))
                        Created = When;
                    Before = Z.Groups["tag"].Success;
                }

                var Dir = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Zip) ?? "");
                var Game = GameFolder.Match(Dir) is { Success: true } G && string.Equals(G.Groups["tid"].Value, TitleId, StringComparison.OrdinalIgnoreCase)
                    ? G.Groups["name"].Value
                    : TitleId;
                return new SaveBackup(Zip, TitleId, Game, UserId, UserName, Created, Info.Length, Files, Before);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The cover kept next to a game's backups, if any.</summary>
        public static string? IconFor(string TitleId)
        {
            var Icon = System.IO.Path.Combine(GameFolderOf(TitleId, TitleId), "icon0.png");
            return File.Exists(Icon) ? Icon : null;
        }

        /// <summary>
        /// Write a backup's files to TargetUser's savedata on the console. The game must not be
        /// running. Each file is checked by size afterwards. Other save slots the user has are kept.
        /// </summary>
        public static async Task RestoreAsync(string ConsoleIP, SaveBackup Backup, string TargetUser,
            IProgress<string>? Progress = null, CancellationToken Token = default)
        {
            using var Archive = ZipFile.OpenRead(Backup.Path);
            var Files = new List<(ZipArchiveEntry Entry, string Target)>();
            foreach (var Entry in Archive.Entries)
            {
                if (Entry.FullName.EndsWith('/') || SavePath.Match(Entry.FullName.Replace('\\', '/')) is not { Success: true } M
                    || !string.Equals(M.Groups["tid"].Value, Backup.TitleId, StringComparison.OrdinalIgnoreCase))
                    continue;
                var Rest = M.Groups["rest"].Value;
                if (Rest.Split('/').Any(x => x is "" or "." or ".."))
                    continue;
                Files.Add((Entry, $"/user/home/{TargetUser}/{M.Groups["kind"].Value.ToLowerInvariant()}/{M.Groups["tid"].Value}/{Rest}"));
            }
            if (Files.Count == 0)
                throw new IOException("This zip has no PS4 save files for " + Backup.TitleId + ".");

            await using (var Ftp = await Connect(ConsoleIP, Token))
            {
                var Home = $"/user/home/{TargetUser}";
                if (!(await Ftp.ListAsync("/user/home", Token)).Any(x => x.IsDirectory && string.Equals(x.Name, TargetUser, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException($"There's no user {TargetUser} on the console.");

                // the folders, one level at a time (MKD on an existing one is fine)
                foreach (var Dir in Files.Select(x => x.Target.Substring(0, x.Target.LastIndexOf('/'))).Distinct())
                {
                    var Path_ = Home;
                    foreach (var Part in Dir.Substring(Home.Length).Split('/', StringSplitOptions.RemoveEmptyEntries))
                    {
                        Path_ += "/" + Part;
                        await Ftp.MakeDirAsync(Path_, Token);
                    }
                }
            }

            int Index = 0;
            foreach (var (Entry, Target) in Files)
            {
                Progress?.Report($"Restoring {++Index} of {Files.Count}: {Entry.Name}");
                await using var Ftp = await Connect(ConsoleIP, Token);
                await using (var In = Entry.Open())
                    await Ftp.UploadAsync(Target, In, null, Token);
                var Written = await Ftp.FileSizeAsync(Target, Token);
                if (Written >= 0 && Written != Entry.Length)
                    throw new IOException($"{Entry.Name} arrived incomplete on the console ({Written} of {Entry.Length} bytes). Restore it again.");
            }
        }

        /// <summary>Copy a save zip from elsewhere into Folder, in its game's folder.</summary>
        public static SaveBackup Import(string Zip, Func<string, string> NameOf)
        {
            var Found = Read(Zip) ?? throw new IOException("It isn't a save backup: no user/home/<id>/savedata/<TitleID>/ files inside.");
            var Target = GameFolderOf(Found.TitleId, NameOf(Found.TitleId));
            Directory.CreateDirectory(Target);
            var Name = System.IO.Path.GetFileNameWithoutExtension(Zip);
            // keep the name when it's already ours; otherwise make one the list can read
            if (!ZipName.IsMatch(Name))
                Name = $"{Found.UserId} {Found.UserId} {File.GetLastWriteTime(Zip):yyyy-MM-dd HHmmss}";
            var Destination = System.IO.Path.Combine(Target, SafeNames.Of(Name) + ".zip");
            if (System.IO.Path.GetFullPath(Destination) != System.IO.Path.GetFullPath(Zip))
            {
                for (int i = 2; File.Exists(Destination); i++)
                    Destination = System.IO.Path.Combine(Target, SafeNames.Of(Name) + $" ({i}).zip");
                File.Copy(Zip, Destination);
            }
            return Read(Destination)!;
        }

        /// <summary>Move a backup to the Recycle Bin / Trash where there is one, else delete it.</summary>
        public static void Delete(SaveBackup Backup)
        {
            if (LibraryTidy.CanRecycle)
                LibraryTidy.Recycle(new LibraryEntry { Path = Backup.Path });
            else
                File.Delete(Backup.Path);
        }

        static async Task<FtpLite> Connect(string ConsoleIP, CancellationToken Token)
        {
            Exception? Last = null;
            // GoldHEN's server drops some first connections
            for (int Attempt = 0; Attempt < ConsoleInventory.FtpAttempts; Attempt++)
            {
                try { return await FtpLite.ConnectAsync(ConsoleIP, 2121, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30), Token); }
                catch (OperationCanceledException) when (Token.IsCancellationRequested) { throw; }
                catch (Exception ex) { Last = ex; await Task.Delay(1000, Token); }
            }
            throw new IOException("GoldHEN's FTP server didn't answer." + (Last != null ? $" ({Last.Message})" : ""));
        }
    }
}
