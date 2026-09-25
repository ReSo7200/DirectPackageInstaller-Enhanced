using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LibOrbisPkg.PKG;

namespace DirectPackageInstaller.Services
{
    /// <summary>Outcome of a package check: checksums that matched, parts that don't, and read errors.</summary>
    public sealed record PackageCheckResult(int Passed, List<string> Damaged, List<string> Unreadable)
    {
        public bool IsDamaged => Damaged.Count > 0;
        public bool CouldNotRead => Unreadable.Count > 0;
    }

    /// <summary>
    /// Is a PKG damaged? Recomputes the checksums stored in it (header, file table,
    /// each file, the game image) with LibOrbisPkg's validator. Signatures need Sony's
    /// keys and are skipped. Reads the whole file, so big packages take a while.
    /// </summary>
    public static class PackageCheck
    {
        /// <summary>
        /// Checksums that say nothing about damage: fake-PKG tools leave the first two
        /// stale on packages that work, and "Param Digest" is compared against a re-built
        /// param.sfo (the PARAM_SFO entry checksum covers the real bytes).
        /// </summary>
        static readonly HashSet<string> NotAboutDamage = new() { "Content Digest", "Major Param Digest", "Param Digest" };

        public static Task<PackageCheckResult> CheckAsync(string Path, IProgress<string>? Progress = null) => Task.Run(() =>
        {
            // split packages read as one
            using var Raw = SplitPackages.Open(Path);
            var Pkg = new PkgReader(Raw).ReadPkg();
            var Validator = new PkgValidator(Pkg);

            // unlock keys (AL) have no game image: its checksums can't match
            bool NoImage = Pkg.Header.content_type == ContentType.AL;

            int Passed = 0, Index = 0;
            var Damaged = new List<string>();
            var Unreadable = new List<string>();
            string Current = "";
            long Shown = -1;

            // the validators read from this stream: its position shows how far a long check is
            Raw.Position = 0;
            var Reading = new ProgressStream(Raw, Percent =>
            {
                if (Percent == Shown) return;
                Shown = Percent;
                Progress?.Report($"{Current} {Percent}%");
            });
            var Steps = Validator.Validations(Reading)
                .Where(x => x.Type == PkgValidator.ValidationType.Hash && !NotAboutDamage.Contains(x.Name)
                            && !(NoImage && x.Name.StartsWith("PFS ", StringComparison.Ordinal)))
                .ToList();

            foreach (var Step in Steps)
            {
                Current = $"Checking {++Index} of {Steps.Count}: {Friendly(Step.Name)}…";
                Shown = -1;
                Progress?.Report(Current);
                try
                {
                    switch (Step.Validate())
                    {
                        case PkgValidator.ValidationResult.Ok: Passed++; break;
                        case PkgValidator.ValidationResult.Fail: Damaged.Add(Friendly(Step.Name)); break;
                    }
                }
                catch (Exception ex)
                {
                    Unreadable.Add($"{Friendly(Step.Name)} ({ex.Message})");
                }
            }

            return new PackageCheckResult(Passed, Damaged, Unreadable);
        });

        /// <summary>What a checksum covers, in words.</summary>
        static string Friendly(string Name) => Name switch
        {
            "PFS Image Digest" => "the game data",
            "PFS Signed Digest" => "the start of the game data",
            "Body Digest" => "the package's file area",
            "Header Digest" or "PKG Header Digest" => "the package header",
            "Digest Table Hash" or "SC Entries Hash 1" or "SC Entries Hash 2" => "the package's file table",
            "Game Digest" => "the game data summary",
            _ when Name.EndsWith(" digest", StringComparison.Ordinal) => "the file " + Name[..^" digest".Length],
            _ => Name
        };

        /// <summary>A read-only stream that reports how far into the file reads have got.</summary>
        sealed class ProgressStream : Stream
        {
            readonly Stream Inner;
            readonly Action<long> Percent;

            public ProgressStream(Stream Inner, Action<long> Percent)
            {
                this.Inner = Inner;
                this.Percent = Percent;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int Read = Inner.Read(buffer, offset, count);
                if (Inner.Length > 0)
                    Percent(Inner.Position * 100 / Inner.Length);
                return Read;
            }

            public override bool CanRead => true;
            public override bool CanSeek => Inner.CanSeek;
            public override bool CanWrite => false;
            public override long Length => Inner.Length;
            public override long Position { get => Inner.Position; set => Inner.Position = value; }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => Inner.Seek(offset, origin);
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
