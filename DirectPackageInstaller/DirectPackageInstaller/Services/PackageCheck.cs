using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LibOrbisPkg.PKG;

namespace DirectPackageInstaller.Services
{
    /// <summary>Outcome of a package check: digests that matched, didn't, and couldn't be checked.</summary>
    public sealed record PackageCheckResult(int Passed, List<string> Failed, int Skipped)
    {
        public bool Damaged => Failed.Count > 0;
    }

    /// <summary>
    /// Is a PKG damaged? Recomputes the digests stored in it (header, body, entries,
    /// PFS image) with LibOrbisPkg's validator. Signatures need Sony's keys and are
    /// skipped. Reads the whole file, so big packages take a while.
    /// </summary>
    public static class PackageCheck
    {
        /// <summary>
        /// Metadata digests the fake-PKG tools don't recompute: they "fail" on packages
        /// that install and run fine, so they say nothing about damage.
        /// </summary>
        static readonly HashSet<string> NotSetByFakePkgTools = new() { "Content Digest", "Major Param Digest" };

        public static Task<PackageCheckResult> CheckAsync(string Path, IProgress<string>? Progress = null) => Task.Run(() =>
        {
            // split packages read as one
            using var Stream = SplitPackages.Open(Path);
            var Pkg = new PkgReader(Stream).ReadPkg();
            var Validator = new PkgValidator(Pkg);

            // unlock keys (AL) have no PFS image: its digests can't match
            bool NoImage = Pkg.Header.content_type == ContentType.AL;
            var Steps = Validator.Validations(Stream)
                .Where(x => x.Type == PkgValidator.ValidationType.Hash && !NotSetByFakePkgTools.Contains(x.Name)
                            && !(NoImage && x.Name.StartsWith("PFS ", StringComparison.Ordinal)))
                .ToList();
            int Passed = 0, Skipped = 0, Index = 0;
            var Failed = new List<string>();

            foreach (var Step in Steps)
            {
                Progress?.Report($"Checking {++Index} of {Steps.Count}: {Step.Name}");
                PkgValidator.ValidationResult Result;
                try
                {
                    Result = Step.Validate();
                }
                catch (Exception ex)
                {
                    Failed.Add($"{Step.Name} ({ex.Message})");
                    continue;
                }

                switch (Result)
                {
                    case PkgValidator.ValidationResult.Ok: Passed++; break;
                    case PkgValidator.ValidationResult.Fail: Failed.Add(Step.Description.Length > 0 ? Step.Description : Step.Name); break;
                    default: Skipped++; break;
                }
            }

            return new PackageCheckResult(Passed, Failed, Skipped);
        });
    }
}
