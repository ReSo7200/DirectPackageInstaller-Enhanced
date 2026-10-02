using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LibOrbisPkg.GP4;
using LibOrbisPkg.PFS;
using LibOrbisPkg.PKG;
using LibOrbisPkg.SFO;
using LibOrbisPkg.Util;

namespace DirectPackageInstaller.Services
{
    /// <summary>
    /// Remarries a fake update PKG to a different fake base — the Modded Warfare workflow,
    /// but in-process using LibOrbisPkg instead of orbis-pub-cmd.
    ///
    /// A fake update whose <c>CONTENT_ID</c> doesn't match the fake base you have installed
    /// won't marry (CE-36441-8 at install). This takes the update apart, patches its
    /// <c>sce_sys/param.sfo</c> to point at the base's content id, drops files that are tied
    /// to the old signing (<c>playgo-chunk.sha</c>, <c>optfile.dat</c>), and rebuilds a fresh
    /// fake-signed <c>pkg_ps4_patch</c>.
    ///
    /// Scope and honesty — this only works on <b>fake</b> PKGs. Retail Sony PKGs are encrypted
    /// with content-specific keys we don't ship; <see cref="Pkg.GetEkpfs"/> returns null for
    /// them and <see cref="HarvestAsync"/> throws <see cref="FakeRemarryException"/>.
    /// </summary>
    public static class FakeRemarry
    {
        /// <summary>Zero passcode: the fake-PKG convention.</summary>
        public const string FakePasscode = "00000000000000000000000000000000";

        /// <summary>Files inside the extracted update's sce_sys that are tied to the original signing and must go before rebuild.</summary>
        static readonly string[] CrumbFiles = { "sce_sys/playgo-chunk.sha", "sce_sys/optfile.dat" };

        public static string DefaultOutputFolder => App.IsAndroid
            ? "/storage/emulated/0/Download/DPI Remarried Updates"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DPI Remarried Updates");

        /// <summary>What we learned reading the source update PKG.</summary>
        public sealed record SourceUpdate(string TitleId, string ContentId, string Title, string AppVersion);

        /// <summary>
        /// Peek at a fake update PKG without unpacking it, to confirm it IS fake and read its identity
        /// (title id, content id, app version, title) for the UI's confirm dialog.
        /// </summary>
        public static Task<SourceUpdate> InspectAsync(string PkgPath, CancellationToken Token = default)
            => Task.Run(() =>
            {
                if (!File.Exists(PkgPath))
                    throw new FakeRemarryException("Can't find the update PKG at " + PkgPath + ".");

                using var Stream = new FileStream(PkgPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var Pkg = new PkgReader(Stream).ReadPkg();

                if (!Pkg.CheckPasscode(FakePasscode))
                    throw new FakeRemarryException("This update is a retail (Sony-signed) PKG. Remarry only works on fake-signed updates.");

                // updates share the GD header content type with games; the SFO category tells them apart
                var Sfo = Pkg.ParamSfo.ParamSfo;
                string Val(string Key) => Sfo.GetValueByName(Key) is { } V ? Encoding.UTF8.GetString(V.ToByteArray()).Trim('\0') : "";
                var Category = Val("CATEGORY");
                if (!Category.StartsWith("gp", StringComparison.OrdinalIgnoreCase))
                    throw new FakeRemarryException($"This PKG isn't an update (category is '{Category}', not gp).");
                return new SourceUpdate(
                    TitleId: Val("TITLE_ID"),
                    ContentId: Val("CONTENT_ID") is { Length: > 0 } Cid ? Cid : Pkg.Header.content_id,
                    Title: Val("TITLE"),
                    AppVersion: Val("APP_VER"));
            }, Token);

        /// <summary>
        /// Extracts <paramref name="SourcePkg"/>, patches CONTENT_ID to <paramref name="TargetContentId"/>, and rebuilds
        /// a fake <c>pkg_ps4_patch</c> at <paramref name="OutputFolder"/>. Returns the new PKG's path.
        /// </summary>
        public static async Task<string> RemarryAsync(string SourcePkg, string TargetContentId, string OutputFolder,
            IProgress<string>? Progress = null, CancellationToken Token = default)
        {
            if (!IsContentId(TargetContentId))
                throw new FakeRemarryException("The base game's content id is missing or malformed. Press 'Check PS4' in the library so DPI can read it, then try again.");

            Directory.CreateDirectory(OutputFolder);
            var Work = Path.Combine(Path.GetTempPath(), "DPI-Remarry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Work);
            try
            {
                Progress?.Report("Reading the source update…");
                var Harvested = await Task.Run(() => HarvestAsync(SourcePkg, Work, Progress, Token), Token);

                Progress?.Report("Rewriting the identity to match your base…");
                PatchSfo(Harvested, TargetContentId);
                DropSigningCrumbs(Harvested.Root);

                Progress?.Report("Building the fake update PKG…");
                var Output = Path.Combine(OutputFolder, SafeNames.Of($"{Harvested.Source.Title} Update v{Harvested.Source.AppVersion}",
                    $"{BaseTitleId(TargetContentId)} Update") + ".pkg");

                await Task.Run(() => Build(Harvested, TargetContentId, Output, Progress, Token), Token);
                return Output;
            }
            finally
            {
                try { Directory.Delete(Work, true); } catch { }
            }
        }

        // ---------------------------------------------------------------- extraction

        /// <summary>What we extracted so the rest of the pipeline doesn't re-open the source PKG.</summary>
        sealed record Harvested(string Root, SourceUpdate Source);

        static Harvested HarvestAsync(string SourcePkg, string Work, IProgress<string>? Progress, CancellationToken Token)
        {
            using var Mapped = MemoryMappedFile.CreateFromFile(SourcePkg, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            Pkg Pkg;
            using (var Header = Mapped.CreateViewStream(0, 0, MemoryMappedFileAccess.Read))
                Pkg = new PkgReader(Header).ReadPkg();

            // mirror upstream Gp4Creator.CreateProjectFromPKG: derive EKPFS from the content id + passcode
            // when the passcode is valid (fake PKGs). GetEkpfs() is the fallback path and uses a
            // different key schedule — using it when the passcode works produces garbage and the inner
            // PFS reader reports "inode 0 is corrupt" on its first read.
            byte[]? Ekpfs = Pkg.CheckPasscode(FakePasscode)
                ? Crypto.ComputeKeys(Pkg.Header.content_id, FakePasscode, 1)
                : Pkg.GetEkpfs();
            if (Ekpfs == null)
                throw new FakeRemarryException("This update is retail (Sony-signed) or damaged — DPI can't derive the encryption key for it.");

            // sce_sys/param.sfo, icon0.png, changeinfo/*.xml, nptitle.dat … don't live in the PFS image:
            // a PKG stores them in its own entry table. Pull them out the way LibOrbisPkg's
            // Gp4Creator.CreateProjectFromPKG does, skipping entries PkgBuilder regenerates.
            var SysDir = Path.Combine(Work, "sce_sys");
            Directory.CreateDirectory(SysDir);
            foreach (var Meta in Pkg.Metas.Metas)
            {
                if (Gp4Creator.GeneratedEntries.Contains(Meta.id) || !EntryNames.IdToName.TryGetValue(Meta.id, out var EntryName))
                    continue;
                var Target = Path.Combine(SysDir, EntryName.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
                byte[] Data;
                using (var S = Mapped.CreateViewStream(Meta.DataOffset, Meta.DataSize, MemoryMappedFileAccess.Read))
                {
                    Data = new byte[Meta.DataSize];
                    S.ReadExactly(Data);
                }
                if (Meta.Encrypted)
                {
                    try { Data = Entry.Decrypt(Data, Pkg.Header.content_id, FakePasscode, Meta); }
                    catch { continue; } // keyed to Sony's keys; the builder doesn't need it
                }
                System.IO.File.WriteAllBytes(Target, Data);
            }

            using var View = Mapped.CreateViewAccessor((long)Pkg.Header.pfs_image_offset, (long)Pkg.Header.pfs_image_size, MemoryMappedFileAccess.Read);
            var OuterPfs = new PfsReader(View, Pkg.Header.pfs_flags, Ekpfs);
            var Inner = new PfsReader(new PFSCReader(OuterPfs.GetFile("pfs_image.dat").GetView()));
            var URoot = Inner.GetURoot();

            int Count = 0;
            void SaveRec(PfsReader.Dir Dir, string ParentPath)
            {
                foreach (var Child in Dir.children)
                {
                    Token.ThrowIfCancellationRequested();
                    if (Child is PfsReader.Dir SubDir)
                    {
                        var SubPath = Path.Combine(ParentPath, SubDir.name);
                        Directory.CreateDirectory(SubPath);
                        SaveRec(SubDir, SubPath);
                    }
                    else if (Child is PfsReader.File File)
                    {
                        var Target = Path.Combine(ParentPath, File.name);
                        Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
                        File.Save(Target);
                        if ((++Count & 15) == 0)
                            Progress?.Report($"Extracted {Count} files…");
                    }
                }
            }
            SaveRec(URoot, Work);

            // Find param.sfo wherever it ended up in the extraction tree (app PKGs put sce_sys at the
            // root, patch PKGs wrap files under /image0/, other layouts exist too). Accept any case
            // and any file name that normalises to "param.sfo".
            string? SfoPath = null;
            foreach (var P in Directory.EnumerateFiles(Work, "*.sfo", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFileName(P), "param.sfo", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.GetFileName(Path.GetDirectoryName(P)!), "sce_sys", StringComparison.OrdinalIgnoreCase))
                { SfoPath = P; break; }
            }
            if (SfoPath == null)
            {
                // Build the most useful diagnostic we can: specifically list what's inside each sce_sys
                // folder we found (that's where param.sfo belongs); fall back to a general listing.
                var Lines = new List<string>();
                var SceSysDirs = Directory.EnumerateDirectories(Work, "*", SearchOption.AllDirectories)
                    .Where(d => string.Equals(Path.GetFileName(d), "sce_sys", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (SceSysDirs.Count > 0)
                {
                    foreach (var D in SceSysDirs)
                    {
                        Lines.Add("Inside " + Path.GetRelativePath(Work, D).Replace('\\', '/') + "/:");
                        var Inside = Directory.EnumerateFileSystemEntries(D, "*", SearchOption.AllDirectories)
                            .Select(p => "  " + Path.GetRelativePath(D, p).Replace('\\', '/')).ToList();
                        if (Inside.Count == 0) Lines.Add("  (empty)");
                        else Lines.AddRange(Inside.Take(50));
                        if (Inside.Count > 50) Lines.Add($"  … and {Inside.Count - 50} more");
                    }
                }
                else
                {
                    Lines.Add("No sce_sys folder found anywhere. Top entries:");
                    Lines.AddRange(Directory.EnumerateFileSystemEntries(Work, "*", SearchOption.AllDirectories)
                        .Take(40).Select(p => "  " + Path.GetRelativePath(Work, p).Replace('\\', '/')));
                }
                throw new FakeRemarryException(
                    $"The extracted update has no sce_sys/param.sfo — the layout isn't what DPI expects.\n" +
                    $"Extracted {Count} files total.\n\n" + string.Join("\n", Lines));
            }
            var Root = Path.GetDirectoryName(Path.GetDirectoryName(SfoPath))!;

            var Sfo = ReadSfo(SfoPath)
                ?? throw new FakeRemarryException("sce_sys/param.sfo was found but DPI couldn't read it — it may be damaged.");

            string Val(string Key) => Sfo.GetValueByName(Key) is { } V ? Encoding.UTF8.GetString(V.ToByteArray()).Trim('\0') : "";
            var Source = new SourceUpdate(
                TitleId: Val("TITLE_ID"),
                ContentId: Val("CONTENT_ID") is { Length: > 0 } Cid ? Cid : Pkg.Header.content_id,
                Title: Val("TITLE"),
                AppVersion: Val("APP_VER"));

            Progress?.Report($"Extracted {Count} files.");
            return new Harvested(Root, Source);
        }

        // ---------------------------------------------------------------- SFO patch

        /// <summary>Swap CONTENT_ID (and TITLE_ID when the new id includes one) in the extracted param.sfo in place.</summary>
        static void PatchSfo(Harvested H, string TargetContentId)
        {
            var SfoPath = Path.Combine(H.Root, "sce_sys", "param.sfo");
            var Sfo = ReadSfo(SfoPath)!;

            // CONTENT_ID is the big one — the console checks this to decide the update belongs to the base
            SetString(Sfo, "CONTENT_ID", TargetContentId, SfoEntryType.Utf8Special, 0x30);

            // TITLE_ID (the "CUSAxxxxx" inside the content id). Keep whatever's there if the two match already.
            var TargetTid = BaseTitleId(TargetContentId);
            if (TargetTid.Length == 9)
                SetString(Sfo, "TITLE_ID", TargetTid, SfoEntryType.Utf8, 12);

            // the builder stamps its own tool info (Gp4Creator does the same before rebuilding)
            Sfo["PUBTOOLVER"] = null;
            Sfo["PUBTOOLINFO"] = null;

            using var Out = new FileStream(SfoPath, FileMode.Create, FileAccess.Write);
            Sfo.Write(Out);
        }

        /// <summary>Remove files that embed references to the original signing/content id (safe for the rebuild).</summary>
        static void DropSigningCrumbs(string Root)
        {
            foreach (var Rel in CrumbFiles)
            {
                var P = Path.Combine(Root, Rel.Replace('/', Path.DirectorySeparatorChar));
                try { if (File.Exists(P)) File.Delete(P); } catch { }
            }
        }

        // ---------------------------------------------------------------- rebuild

        static void Build(Harvested H, string TargetContentId, string OutputPkg, IProgress<string>? Progress, CancellationToken Token)
        {
            // Gp4Project.Create(pkg_ps4_patch) throws because SetType has no case for it; we build the project
            // directly with the same fields orbis-pub-cmd would write for a patch.
            var Project = new Gp4Project
            {
                Format = "gp4",
                version = 1000,
                files = new Files { Items = new List<Gp4File>() },
                RootDir = new List<Dir>(),
                volume = new Volume
                {
                    volume_ts = DateTime.UtcNow.ToString("s").Replace('T', ' '),
                    Package = new PackageInfo
                    {
                        ContentId = TargetContentId,
                        Passcode = FakePasscode,
                        StorageType = "digital50",
                        AppType = "full"
                    }
                }
            };
            Project.volume.Type = VolumeType.pkg_ps4_patch;

            // every extracted file becomes a gp4 entry, relative paths with forward slashes so BuildFSTree finds them
            foreach (var FullPath in Directory.EnumerateFiles(H.Root, "*", SearchOption.AllDirectories))
            {
                var Rel = Path.GetRelativePath(H.Root, FullPath).Replace('\\', '/');
                Project.files.Items.Add(new Gp4File { TargetPath = Rel, OrigPath = Rel });
            }

            // mirror the extracted folder tree into GP4 Dir entries so FindDir resolves nested files
            Project.RootDir = BuildDirTree(H.Root);

            Token.ThrowIfCancellationRequested();
            var Props = PkgProperties.FromGp4(Project, H.Root);
            var Partial = OutputPkg + ".part";
            try
            {
                new PkgBuilder(Props).Write(Partial, Line =>
                {
                    if (!string.IsNullOrEmpty(Line))
                        Progress?.Report(Line);
                });
                if (File.Exists(OutputPkg))
                    File.Delete(OutputPkg);
                File.Move(Partial, OutputPkg);
            }
            catch
            {
                try { File.Delete(Partial); } catch { }
                throw;
            }
        }

        /// <summary>Build the Gp4 RootDir tree (names only; files reference them via TargetPath).</summary>
        static List<Dir> BuildDirTree(string Root)
        {
            List<Dir> Read(string DirPath)
            {
                var List = new List<Dir>();
                foreach (var Sub in Directory.EnumerateDirectories(DirPath))
                    List.Add(new Dir { TargetName = Path.GetFileName(Sub), Children = Read(Sub) });
                return List;
            }
            return Read(Root);
        }

        // ---------------------------------------------------------------- helpers

        static readonly Regex ContentIdRegex = new(@"^[A-Z]{2}[0-9]{4}-[A-Z]{4}[0-9]{5}_00-[A-Z0-9]{16}$", RegexOptions.CultureInvariant);
        public static bool IsContentId(string Id) => !string.IsNullOrEmpty(Id) && ContentIdRegex.IsMatch(Id);

        public static string BaseTitleId(string ContentId)
        {
            // "XX0000-CUSA12345_00-ZZ..." → "CUSA12345"
            var Match = Regex.Match(ContentId ?? "", @"-([A-Z]{4}[0-9]{5})_");
            return Match.Success ? Match.Groups[1].Value : "";
        }

        static ParamSfo? ReadSfo(string Path)
        {
            try
            {
                using var S = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return ParamSfo.FromStream(S);
            }
            catch { return null; }
        }

        static void SetString(ParamSfo Sfo, string Key, string Value, SfoEntryType Type, int MinMaxLength)
        {
            var Needed = Encoding.UTF8.GetByteCount(Value) + (Type == SfoEntryType.Utf8 ? 1 : 0);
            var Existing = Sfo.GetValueByName(Key);
            if (Existing is Utf8Value U && Type == SfoEntryType.Utf8 && U.MaxLength >= Needed) { U.Value = Value; return; }
            if (Existing is Utf8SpecialValue S && Type == SfoEntryType.Utf8Special && S.MaxLength >= Encoding.UTF8.GetByteCount(Value)) { S.Value = Value; return; }

            var MaxLength = Math.Max(MinMaxLength, Needed);
            if (Existing != null)
                MaxLength = Math.Max(MaxLength, Existing.MaxLength);
            Sfo.SetValue(Key, Type, Value, MaxLength);
        }
    }

    /// <summary>A remarry step that failed for a reason worth showing the user verbatim.</summary>
    public sealed class FakeRemarryException : Exception
    {
        public FakeRemarryException(string message) : base(message) { }
    }
}
