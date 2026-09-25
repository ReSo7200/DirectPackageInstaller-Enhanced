using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace DirectPackageInstaller.Services
{
    /// <summary>
    /// Library file housekeeping. Nothing here deletes permanently: removal goes
    /// to the Recycle Bin, and renames/moves stay on the same drive.
    /// </summary>
    public static class LibraryTidy
    {
        /// <summary>"Minecraft [CUSA00265] [Update v03.36].pkg" style name for a package.</summary>
        public static string StandardName(LibraryEntry Entry)
        {
            var Title = Clean(Entry.Title.Length > 0 ? Entry.Title : Path.GetFileNameWithoutExtension(Entry.Path));
            var Parts = new List<string> { Title };
            if (Entry.TitleId.Length > 0)
                Parts.Add($"[{Entry.TitleId}]");

            var Kind = Entry.Kind is "Game" or "Update" or "DLC" ? Entry.Kind : "";
            var Version = Entry.AppVersion.Length > 0 ? "v" + Entry.AppVersion : "";
            var Tag = string.Join(" ", new[] { Kind, Version }.Where(x => x.Length > 0));
            if (Tag.Length > 0)
                Parts.Add($"[{Tag}]");

            // DLC share a title ID: keep them apart with their entitlement label
            if (Entry.Kind == "DLC" && Entry.ContentId.Contains('-'))
                Parts.Add($"[{Entry.ContentId[(Entry.ContentId.LastIndexOf('-') + 1)..]}]");

            return string.Join(" ", Parts) + ".pkg";
        }

        /// <summary>Per-game folder name: "Minecraft [CUSA00265]".</summary>
        public static string FolderName(string Title, string TitleId) =>
            Clean(Title) + (TitleId.Length > 0 ? $" [{TitleId}]" : "");

        static string Clean(string Name)
        {
            var Invalid = Path.GetInvalidFileNameChars();
            var Result = new string(Name.Select(c => Invalid.Contains(c) || c is '™' or '®' or '©' ? ' ' : c).ToArray());
            while (Result.Contains("  "))
                Result = Result.Replace("  ", " ");
            Result = Result.Trim().TrimEnd('.');
            return Result.Length > 100 ? Result[..100].Trim() : Result;
        }

        /// <summary>Rename in place; returns the new path, or throws with a plain message.</summary>
        public static string Rename(LibraryEntry Entry, string NewName)
        {
            if (Entry.Parts > 1)
                throw new InvalidOperationException("Split packages keep their part names (name_0.pkg, name_1.pkg, ...).");

            var Target = Path.Combine(Path.GetDirectoryName(Entry.Path)!, NewName);
            if (string.Equals(Target, Entry.Path, StringComparison.OrdinalIgnoreCase))
                return Entry.Path;
            if (File.Exists(Target))
                throw new IOException($"\"{NewName}\" already exists in that folder.");
            if (IsBusy(Entry.Path))
                throw new IOException("It's being sent to the console right now. Try again when that's done.");

            File.Move(Entry.Path, Target);
            return Target;
        }

        /// <summary>A planned move for "organize into game folders".</summary>
        public sealed record Move(string From, string To);

        /// <summary>
        /// Moves that put each package directly inside Root into a "Title [CUSA...]"
        /// subfolder (families share their game's folder). Files already in
        /// subfolders are left alone. Split packages move with all their parts.
        /// </summary>
        public static List<Move> PlanOrganize(string Root, IEnumerable<LibraryEntry> Entries)
        {
            var InRoot = Entries.Where(x => string.Equals(Path.GetDirectoryName(x.Path), Root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)).ToList();

            // folder named after the game, else whatever title the family has
            var FamilyTitle = Entries.Where(x => x.TitleId.Length > 0)
                .GroupBy(x => x.TitleId)
                .ToDictionary(g => g.Key, g => (g.FirstOrDefault(x => x.Kind == "Game") ?? g.First()).Title);

            var Moves = new List<Move>();
            foreach (var Entry in InRoot)
            {
                var Title = Entry.TitleId.Length > 0 && FamilyTitle.TryGetValue(Entry.TitleId, out var T) ? T : Entry.Title;
                var Folder = Path.Combine(Root, FolderName(Title, Entry.TitleId));

                var Files = SplitPackages.PartsOf(Entry.Path) ?? new[] { Entry.Path };
                foreach (var File in Files)
                    Moves.Add(new Move(File, Path.Combine(Folder, Path.GetFileName(File))));
            }
            return Moves;
        }

        /// <summary>
        /// A package the console or a download is using right now: renaming or
        /// moving it would break the transfer.
        /// </summary>
        public static bool IsBusy(string File)
        {
            bool Same(string A) => string.Equals(A, File, StringComparison.OrdinalIgnoreCase)
                                   || (SplitPackages.PartsOf(A)?.Any(p => string.Equals(p, File, StringComparison.OrdinalIgnoreCase)) ?? false);

            if (SendQueue.Instance.Items.Any(x => !x.IsFinished && Same(x.Path)))
                return true;

            var Folder = Path.GetDirectoryName(File) ?? "";
            return UpdateDownloads.Instance.Items.Any(x => !x.IsFinished && string.Equals(x.Folder.TrimEnd('\\', '/'), Folder, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Carry out planned moves package by package (split parts together): if
        /// one part fails, the parts already moved go back. Returns (moved, failed).
        /// </summary>
        public static (int Moved, List<string> Failed) Apply(IEnumerable<Move> Moves)
        {
            int Moved = 0;
            var Failed = new List<string>();

            // group the parts of one package: they share the part-0 base name
            var Packages = Moves.GroupBy(m =>
            {
                var Name = Path.GetFileName(m.From);
                var Match = System.Text.RegularExpressions.Regex.Match(Name, @"^(.+)_\d+\.pkg$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                return Path.Combine(Path.GetDirectoryName(m.From)!, Match.Success ? Match.Groups[1].Value : Name);
            });

            foreach (var Package in Packages)
            {
                var Done = new List<Move>();
                try
                {
                    foreach (var Move in Package)
                    {
                        if (IsBusy(Move.From))
                            throw new IOException("it's being sent to the console or downloaded right now");
                        Directory.CreateDirectory(Path.GetDirectoryName(Move.To)!);
                        if (File.Exists(Move.To))
                            throw new IOException("a file with that name is already there");
                        File.Move(Move.From, Move.To);
                        Done.Add(Move);
                    }
                    Moved += Done.Count;
                }
                catch (Exception ex)
                {
                    foreach (var Back in Done)
                        try { File.Move(Back.To, Back.From); } catch { }
                    Failed.Add($"{Path.GetFileName(Package.First().From)}: {ex.Message}");
                }
            }
            return (Moved, Failed);
        }

        /// <summary>Move a package (all parts of a split one) to the Recycle Bin. Windows only.</summary>
        public static void Recycle(LibraryEntry Entry)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Moving to the Recycle Bin is only available on Windows.");

            // all parts in one operation (a split package goes as a whole). FOF_WANTNUKEWARNING:
            // when a file can't be recycled (bigger than the bin, network/USB drive) Windows
            // asks before deleting it permanently instead of silently doing it.
            var Files = SplitPackages.PartsOf(Entry.Path) ?? new[] { Entry.Path };
            var Operation = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = string.Join("\0", Files) + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING | FOF_SILENT
            };
            int Result = SHFileOperation(ref Operation);
            if (Result != 0 || Operation.fAnyOperationsAborted)
                throw new IOException(Operation.fAnyOperationsAborted
                    ? "Cancelled: nothing was deleted."
                    : $"Windows couldn't move \"{Path.GetFileName(Entry.Path)}\" to the Recycle Bin (code {Result}).");
        }

        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_WANTNUKEWARNING = 0x4000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string? pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string? lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHFileOperation(ref SHFILEOPSTRUCT FileOp);
    }
}
