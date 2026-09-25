using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DirectPackageInstaller.Services
{
    /// <summary>
    /// Backs up a game's saved data from the console over GoldHEN FTP (read-only there).
    /// Each console user has /user/home/&lt;id&gt;/savedata/&lt;TitleID&gt;/ (sdimg_* image + .bin key)
    /// and savedata_meta/user/&lt;TitleID&gt;/. A zip per user keeps those paths. Saves are
    /// tied to the console and account: restoring is left to Apollo Save Tool on the console.
    /// </summary>
    public static class ConsoleSaves
    {
        public static string Folder => App.IsAndroid
            ? "/storage/emulated/0/Download/DPI Save Backups"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DPI Save Backups");

        public sealed record UserSaves(string UserId, string UserName, List<(string Path, long Size)> Files);

        /// <summary>The title's save files for every console user that has some.</summary>
        public static async Task<List<UserSaves>> FindAsync(string ConsoleIP, string TitleId, CancellationToken Token = default)
        {
            var Found = new List<UserSaves>();
            await using var Ftp = await Connect(ConsoleIP, Token);
            foreach (var User in (await Ftp.ListAsync("/user/home", Token)).Where(x => x.IsDirectory && x.Name != "." && x.Name != ".."))
            {
                var Home = $"/user/home/{User.Name}";
                var Files = new List<(string, long)>();
                foreach (var Folder_ in new[] { $"{Home}/savedata/{TitleId}", $"{Home}/savedata_meta/user/{TitleId}" })
                {
                    try
                    {
                        foreach (var File in await Ftp.ListAsync(Folder_, Token))
                            if (!File.IsDirectory)
                                Files.Add(($"{Folder_}/{File.Name}", File.Size));
                    }
                    catch (FtpException ex) when (ex.Reply is { Code: >= 400 }) { }
                }
                if (Files.Count == 0)
                    continue;

                string Name = User.Name;
                try
                {
                    // its own connection: a failed read can leave the listing one unusable
                    await using var NameFtp = await Connect(ConsoleIP, Token);
                    var Raw = await NameFtp.DownloadAsync($"{Home}/username.dat", 4096, Token);
                    var Text = Encoding.UTF8.GetString(Raw).Split('\0')[0].Trim();
                    if (Text.Length > 0)
                        Name = Text;
                }
                catch (OperationCanceledException) when (Token.IsCancellationRequested) { throw; }
                catch { /* the id will do */ }

                Found.Add(new UserSaves(User.Name, Name, Files));
            }
            return Found;
        }

        /// <summary>One zip per user in Folder\&lt;Game&gt;\; returns the folder.</summary>
        public static async Task<string> BackupAsync(string ConsoleIP, string TitleId, string GameName, List<UserSaves> Users,
            IProgress<string>? Progress = null, CancellationToken Token = default)
        {
            var Target = Path.Combine(Folder, SafeNames.Of($"{GameName} ({TitleId})", SafeNames.Of(TitleId)));
            Directory.CreateDirectory(Target);
            var Stamp = DateTime.Now.ToString("yyyy-MM-dd HHmmss");

            foreach (var User in Users)
            {
                // the console's user id too: two users may share a name
                var Zip = Path.Combine(Target, SafeNames.Of($"{User.UserName} {User.UserId} {Stamp}") + ".zip");
                var Partial = Zip + ".part";
                try
                {
                using (var Archive = ZipFile.Open(Partial, ZipArchiveMode.Create))
                {
                    int Index = 0;
                    foreach (var (FilePath, Size) in User.Files)
                    {
                        Progress?.Report($"Backing up {User.UserName}'s saves: {++Index} of {User.Files.Count}");
                        // keep the console paths (user/home/<id>/...) so the zip shows where each file belongs
                        var Entry = Archive.CreateEntry(FilePath.TrimStart('/'), CompressionLevel.Fastest);
                        await using var Out = Entry.Open();
                        await using var Ftp = await Connect(ConsoleIP, Token);
                        await Ftp.DownloadToAsync(FilePath, Out, null, Token);
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
