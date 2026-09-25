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

        /// <summary>Carry out planned moves; returns (moved, failed messages).</summary>
        public static (int Moved, List<string> Failed) Apply(IEnumerable<Move> Moves)
        {
            int Moved = 0;
            var Failed = new List<string>();
            foreach (var Move in Moves)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Move.To)!);
                    if (File.Exists(Move.To))
                        throw new IOException("a file with that name is already there");
                    File.Move(Move.From, Move.To);
                    Moved++;
                }
                catch (Exception ex)
                {
                    Failed.Add($"{Path.GetFileName(Move.From)}: {ex.Message}");
                }
            }
            return (Moved, Failed);
        }

        /// <summary>Move a package (all parts of a split one) to the Recycle Bin. Windows only.</summary>
        public static void Recycle(LibraryEntry Entry)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Moving to the Recycle Bin is only available on Windows.");

            foreach (var File in SplitPackages.PartsOf(Entry.Path) ?? new[] { Entry.Path })
            {
                var Operation = new SHFILEOPSTRUCT
                {
                    wFunc = FO_DELETE,
                    pFrom = File + "\0\0",
                    fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT
                };
                int Result = SHFileOperation(ref Operation);
                if (Result != 0 || Operation.fAnyOperationsAborted)
                    throw new IOException($"Windows couldn't move \"{Path.GetFileName(File)}\" to the Recycle Bin (code {Result}).");
            }
        }

        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400;

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
