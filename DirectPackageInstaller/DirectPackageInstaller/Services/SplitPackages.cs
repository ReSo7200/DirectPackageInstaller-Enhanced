using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DirectPackageInstaller.IO;
using LibOrbisPkg.PKG;

namespace DirectPackageInstaller.Services
{
    /// <summary>
    /// Packages stored as numbered parts (name_0.pkg, name_1.pkg, ... as the PSN
    /// CDN and some tools split them). Part 0 holds the PKG header; the parts
    /// concatenated are the real package. Treated as split only when part 0's
    /// header declares a package larger than the file and the parts add up to
    /// exactly that size, so ordinary files that happen to end in "_0" are left alone.
    /// </summary>
    public static class SplitPackages
    {
        static readonly Regex PartName = new(@"^(?<base>.+)_(?<n>\d+)\.pkg$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>A later part (_1, _2, ...) of some split package.</summary>
        public static bool IsLaterPart(string File)
        {
            var Match = PartName.Match(Path.GetFileName(File));
            if (!Match.Success || Match.Groups["n"].Value == "0")
                return false;
            var First = Path.Combine(Path.GetDirectoryName(File) ?? "", Match.Groups["base"].Value + "_0.pkg");
            return System.IO.File.Exists(First) && PartsOf(First) != null;
        }

        /// <summary>All parts in order when File is part 0 of a complete split package, else null.</summary>
        public static string[]? PartsOf(string File)
        {
            try
            {
                var Match = PartName.Match(Path.GetFileName(File));
                if (!Match.Success || Match.Groups["n"].Value != "0")
                    return null;

                var Folder = Path.GetDirectoryName(File) ?? "";
                var Base = Match.Groups["base"].Value;

                var Parts = new List<string> { File };
                for (int n = 1; ; n++)
                {
                    var Next = Path.Combine(Folder, $"{Base}_{n}.pkg");
                    if (!System.IO.File.Exists(Next))
                        break;
                    Parts.Add(Next);
                }
                if (Parts.Count < 2)
                    return null;

                ulong Declared;
                using (var Stream = new FileStream(File, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Declared = new PkgReader(Stream).ReadHeader().package_size;

                long Total = Parts.Sum(x => new FileInfo(x).Length);
                return Declared > (ulong)new FileInfo(File).Length && (ulong)Total == Declared ? Parts.ToArray() : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The whole package as one stream (all parts back to back), or null if File isn't split.</summary>
        public static Stream? OpenMerged(string File)
        {
            var Parts = PartsOf(File);
            if (Parts == null)
                return null;

            var Streams = Parts.Select(x => (Stream)new FileStream(x, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan)).ToArray();
            return new MergedStream(Streams, Streams.Select(x => x.Length).ToArray());
        }

        /// <summary>The package stream for a local file: merged parts when split, else the file.</summary>
        public static Stream Open(string File) =>
            OpenMerged(File) ?? new FileStream(File, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
}
