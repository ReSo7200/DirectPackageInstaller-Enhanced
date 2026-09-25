using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DirectPackageInstaller.Services
{
    /// <summary>
    /// Unlock-key DLC (PKG header type AL: a license, no data). The console's
    /// download service refuses these (BGFT 0x80990006), so they can't be sent
    /// like other packages. Instead the file is copied over GoldHEN's FTP to
    /// /data/pkg, where the Package Installer (Settings › Debug Settings ›
    /// Game › Package Installer) lists it.
    /// </summary>
    public static class UnlockKeys
    {
        public const string ConsoleFolder = "/data/pkg";

        public static string HowToInstall =>
            $"On the PS4: Settings › Debug Settings › Game › Package Installer, then pick it from {ConsoleFolder}.";

        public static bool IsUnlockKey(PKGHelper.PKGInfo Info) =>
            string.Equals(Info.HeaderContentType, "AL", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Copies Package (read from its current position) to /data/pkg on the console.
        /// Returns the console path. Throws with a readable message on failure.
        /// </summary>
        public static async Task<string> CopyToConsoleAsync(string ConsoleIP, PKGHelper.PKGInfo Info, Stream Package,
            IProgress<string>? Status = null, CancellationToken Token = default)
        {
            var Name = SafeName(Info.ContentID) + ".pkg";
            var Target = $"{ConsoleFolder}/{Name}";
            long Length = Package.CanSeek ? Package.Length - Package.Position : -1;
            long Start = Package.CanSeek ? Package.Position : 0;

            Exception? Last = null;
            // GoldHEN's server often drops the first connection
            for (int Attempt = 1; Attempt <= ConsoleInventory.FtpAttempts; Attempt++)
            {
                FtpLite Ftp;
                try
                {
                    Status?.Report("Connecting to the console's FTP server…");
                    Ftp = await FtpLite.ConnectAsync(ConsoleIP, 2121, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15), Token);
                }
                catch (OperationCanceledException) when (Token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    Last = ex;
                    await Task.Delay(1000, Token);
                    continue;
                }

                await using (Ftp)
                {
                    try
                    {
                        // exists already on most consoles; the reply doesn't matter
                        await Ftp.CommandAsync("MKD " + ConsoleFolder, Token);

                        if (Package.CanSeek)
                            Package.Position = Start;
                        Status?.Report($"Copying to {Target}…");
                        // under a temporary name until complete: a cut-off copy must not look ready
                        var Partial = Target + ".part";
                        await Ftp.UploadAsync(Partial, Package, Sent =>
                        {
                            if (Length > 0)
                                Status?.Report($"Copying to {Target}… {Sent * 100 / Length}%");
                        }, Token);
                        try { await Ftp.DeleteAsync(Target, Token); } catch (FtpException) { } // an older copy
                        await Ftp.RenameAsync(Partial, Target, Token);
                        return Target;
                    }
                    catch (OperationCanceledException) when (Token.IsCancellationRequested) { throw; }
                    catch (FtpException ex) when (ex.Reply is { Code: >= 500 })
                    {
                        throw new IOException($"The console refused the copy to {Target} ({ex.Reply}).", ex);
                    }
                    catch (Exception ex)
                    {
                        Last = ex;
                    }
                }

                await Task.Delay(1000, Token);
            }

            throw new IOException("Couldn't reach GoldHEN's FTP server (port 2121). Turn it on in the GoldHEN settings, then send again." +
                                  (Last != null ? $" ({Last.Message})" : ""), Last);
        }

        static string SafeName(string? ContentID)
        {
            var Name = new string((ContentID ?? "").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
            return Name.Length > 0 ? Name : "unlock-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        }
    }
}
