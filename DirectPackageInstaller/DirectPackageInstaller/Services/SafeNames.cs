using System;
using System.IO;
using System.Linq;

namespace DirectPackageInstaller.Services
{
    /// <summary>
    /// File and folder names built from outside text (console file names, game titles,
    /// GitHub release names): no separators, no reserved characters, never "." or "..",
    /// so a name can't point outside the folder it's meant for on any platform.
    /// </summary>
    public static class SafeNames
    {
        static readonly char[] Bad = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).Distinct().ToArray();

        public static string Of(string? Name, string Fallback = "unnamed")
        {
            var Clean = string.Concat((Name ?? "").Select(c => char.IsControl(c) || Bad.Contains(c) ? '_' : c)).Trim().TrimEnd('.');
            return Clean.Length == 0 || Clean == "." || Clean == ".." ? Fallback : Clean;
        }
    }
}
