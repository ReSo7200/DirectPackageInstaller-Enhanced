using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DirectPackageInstaller.Services
{
    /// <summary>
    /// Plain-language explanations for the console's download/install
    /// (BGFT / AppInstUtil) error codes, instead of raw hex.
    /// </summary>
    public static class InstallErrors
    {
        static readonly Dictionary<uint, string> Known = new()
        {
            [0x80990006] = "The console refused this package type. Unlock-key DLC can't be downloaded by the PS4; this version of DPI copies it to /data/pkg for the Package Installer instead.",
            [0x80990015] = "The console already has a download for this package. Delete it from Notifications › Downloads on the console, then send again.",
            [0x8099002C] = "The console lost its connection to this PC while downloading. Check the PC address in Settings and that nothing (firewall, VPN) blocks port 9898.",
            [0x80990033] = "The console couldn't download from this PC. Check the PC address in Settings and allow port 9898 in Windows Firewall.",
            [0x80990039] = "Not enough free space on the console.",
            [0x80A30026] = "Not enough free space on the console.",
            [0x80990085] = "Not enough free space on the console (it needs one continuous free area; deleting other games or rebuilding the database can help).",
            [0x80990086] = "An old download is blocking this one. Delete finished or failed downloads from Notifications › Downloads on the console.",
            [0x80990088] = "Already installed: the console has this exact package.",
        };

        static readonly Regex Code = new(@"0x([0-9A-Fa-f]{8})", RegexOptions.Compiled);

        /// <summary>Explanation for the first known error code in Text, or null.</summary>
        public static string? Explain(string? Text)
        {
            if (string.IsNullOrEmpty(Text))
                return null;

            foreach (Match Match in Code.Matches(Text))
            {
                if (uint.TryParse(Match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var Value) && Known.TryGetValue(Value, out var Message))
                    return $"{Message} (0x{Value:X8})";
            }

            return null;
        }

        /// <summary>Explanation when known, else the trimmed raw reply.</summary>
        public static string Describe(string? Raw)
        {
            var Clean = (Raw ?? "").Trim();
            if (Clean.Length > 300)
                Clean = Clean.Substring(0, 300) + "…";
            return Explain(Clean) ?? (Clean.Length > 0 ? Clean : "The console rejected the package.");
        }
    }
}
