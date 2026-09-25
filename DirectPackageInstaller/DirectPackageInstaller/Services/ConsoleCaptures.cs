using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DirectPackageInstaller.Services
{
    /// <summary>
    /// Screenshots and video clips on the console, copied to this device over GoldHEN FTP
    /// (read-only: nothing on the console is changed). The console keeps them in
    /// /user/av_contents/{photo,video}/NPXS20001/&lt;TitleID&gt;/&lt;folder&gt;/&lt;timestamp&gt;.jpg|.mp4.
    /// </summary>
    public static class ConsoleCaptures
    {
        const string Root = "/user/av_contents";
        static readonly string[] Extensions = { ".jpg", ".png", ".mp4" };

        public sealed record Capture(string TitleId, string ConsolePath, string Name, long Size, bool IsVideo);

        /// <summary>Where copies go: Pictures\DPI Captures on a PC, Download/DPI Captures on a phone.</summary>
        public static string Folder => App.IsAndroid
            ? "/storage/emulated/0/Download/DPI Captures"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "DPI Captures");

        public static async Task<List<Capture>> ListAsync(string ConsoleIP, CancellationToken Token = default)
        {
            var Found = new List<Capture>();
            await using var Ftp = await Connect(ConsoleIP, Token);
            foreach (var Kind in new[] { "photo", "video" })
            {
                foreach (var Owner in await Dirs(Ftp, $"{Root}/{Kind}", Token))              // NPXS20001
                foreach (var Tid in await Dirs(Ftp, $"{Root}/{Kind}/{Owner}", Token))
                foreach (var Sub in await Dirs(Ftp, $"{Root}/{Kind}/{Owner}/{Tid}", Token))
                {
                    var Path_ = $"{Root}/{Kind}/{Owner}/{Tid}/{Sub}";
                    foreach (var File in await Ftp.ListAsync(Path_, Token))
                        if (!File.IsDirectory && Extensions.Any(x => File.Name.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
                            Found.Add(new Capture(Tid, $"{Path_}/{File.Name}", File.Name, File.Size, Kind == "video"));
                }
            }
            return Found;
        }

        /// <summary>
        /// Copy the captures not copied yet (same name and size) into a folder per game.
        /// Returns how many were copied.
        /// </summary>
        public static async Task<int> CopyAsync(string ConsoleIP, IEnumerable<Capture> Captures, Func<string, string> GameName,
            IProgress<string>? Progress = null, CancellationToken Token = default)
        {
            var Todo = Captures.Where(x => !Exists(Target(x, GameName), x.Size)).ToList();
            int Done = 0;
            foreach (var Item in Todo)
            {
                Token.ThrowIfCancellationRequested();
                Progress?.Report($"Copying {Done + 1} of {Todo.Count}: {Item.Name}");
                var To = Target(Item, GameName);
                Directory.CreateDirectory(Path.GetDirectoryName(To)!);

                // videos can be large: stream to a .part file, one connection per file
                await using var Ftp = await Connect(ConsoleIP, Token);
                var Partial = To + ".part";
                try
                {
                    long Shown = -1;
                    await using (var Out = File.Create(Partial))
                        await Ftp.DownloadToAsync(Item.ConsolePath, Out, Got =>
                        {
                            // only when the percentage changes: every block would flood the UI
                            long Percent = Item.Size > 0 ? Got * 100 / Item.Size : -1;
                            if (Percent == Shown) return;
                            Shown = Percent;
                            Progress?.Report($"Copying {Done + 1} of {Todo.Count}: {Item.Name} ({Percent}%)");
                        }, Token);
                    if (File.Exists(To))
                        File.Delete(To);
                    File.Move(Partial, To);
                }
                catch
                {
                    try { File.Delete(Partial); } catch { }
                    throw;
                }
                Done++;
            }
            return Done;
        }

        static string Target(Capture Item, Func<string, string> GameName) =>
            Path.Combine(Folder, SafeNames.Of(GameName(Item.TitleId), SafeNames.Of(Item.TitleId)), SafeNames.Of(Item.Name));

        static bool Exists(string Path_, long Size) => File.Exists(Path_) && (Size < 0 || new FileInfo(Path_).Length == Size);

        static async Task<List<string>> Dirs(FtpLite Ftp, string Path_, CancellationToken Token)
        {
            try
            {
                return (await Ftp.ListAsync(Path_, Token)).Where(x => x.IsDirectory && x.Name != "." && x.Name != "..").Select(x => x.Name).ToList();
            }
            catch (FtpException ex) when (ex.Reply is { Code: >= 500 })
            {
                return new List<string>();
            }
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
