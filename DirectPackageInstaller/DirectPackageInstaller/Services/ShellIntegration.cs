using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace DirectPackageInstaller.Services
{
    /// <summary>What Explorer asked for: send a package, or add a library folder.</summary>
    public enum ShellCommandKind { Send, AddFolder }

    public readonly record struct ShellCommand(ShellCommandKind Kind, string Path);

    /// <summary>
    /// Per-user Windows Explorer context-menu verbs (HKCU, no admin needed):
    /// "Send to PS4 (DPI)" on .pkg files and "Add to DPI library" on folders.
    /// Explorer runs the command once per selected item; SingleInstance forwards
    /// each invocation to the running window.
    /// </summary>
    public static class ShellIntegration
    {
        public const string SendSwitch = "--send";
        public const string AddFolderSwitch = "--add-folder";

        const string SendVerbKey = @"SystemFileAssociations\.pkg\shell\DPIEnhanced.Send";
        const string FolderVerbKey = @"Directory\shell\DPIEnhanced.AddFolder";

        /// <summary>
        /// HKCU subkey the verbs live under. Only tests should change this
        /// (point it at a throwaway key, then restore it).
        /// </summary>
        public static string ClassesRoot { get; set; } = @"Software\Classes";

        static string SendKeyPath => ClassesRoot + "\\" + SendVerbKey;
        static string FolderKeyPath => ClassesRoot + "\\" + FolderVerbKey;

        public static bool IsSupported => OperatingSystem.IsWindows();

        /// <summary>Add both verbs, pointing at <paramref name="ExePath"/> (default: this executable).</summary>
        public static bool Register(string? ExePath = null)
        {
            if (!OperatingSystem.IsWindows())
                return false;

            ExePath ??= Environment.ProcessPath;
            if (string.IsNullOrEmpty(ExePath))
                return false;

            try
            {
                WriteVerb(SendKeyPath, "Send to PS4 (DPI)", ExePath, $"\"{ExePath}\" {SendSwitch} \"%1\"");
                WriteVerb(FolderKeyPath, "Add to DPI library", ExePath, $"\"{ExePath}\" {AddFolderSwitch} \"%1\"");
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool Unregister()
        {
            if (!OperatingSystem.IsWindows())
                return false;

            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(SendKeyPath, false);
                Registry.CurrentUser.DeleteSubKeyTree(FolderKeyPath, false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>True when both verbs exist and point at this executable (or <paramref name="ExePath"/>).</summary>
        public static bool IsRegistered(string? ExePath = null)
        {
            if (!OperatingSystem.IsWindows())
                return false;

            ExePath ??= Environment.ProcessPath;
            try
            {
                return VerbPointsTo(SendKeyPath, ExePath) && VerbPointsTo(FolderKeyPath, ExePath);
            }
            catch
            {
                return false;
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        static void WriteVerb(string KeyPath, string Label, string ExePath, string Command)
        {
            using (var Verb = Registry.CurrentUser.CreateSubKey(KeyPath, true))
            {
                Verb.SetValue("", Label);
                Verb.SetValue("MUIVerb", Label);
                Verb.SetValue("Icon", $"\"{ExePath}\",0");
            }

            using var Cmd = Registry.CurrentUser.CreateSubKey(KeyPath + "\\command", true);
            Cmd.SetValue("", Command);
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        static bool VerbPointsTo(string KeyPath, string? ExePath)
        {
            using var Cmd = Registry.CurrentUser.OpenSubKey(KeyPath + "\\command");
            if (Cmd?.GetValue("") is not string Command)
                return false;

            return string.IsNullOrEmpty(ExePath) || Command.StartsWith($"\"{ExePath}\"", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when the command line contains --send or --add-folder.</summary>
        public static bool IsShellInvocation(string[]? Args) =>
            Args != null && Args.Any(x => IsSwitch(x, SendSwitch) || IsSwitch(x, AddFolderSwitch));

        static bool IsSwitch(string Arg, string Switch) => string.Equals(Arg?.Trim(), Switch, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Parse "--send a.pkg --add-folder D:\Games ..." into commands, with
        /// absolute paths (the receiving instance may have another working directory).
        /// </summary>
        public static List<ShellCommand> Parse(string[]? Args)
        {
            var Result = new List<ShellCommand>();
            if (Args == null)
                return Result;

            for (int i = 0; i < Args.Length - 1; i++)
            {
                ShellCommandKind Kind;
                if (IsSwitch(Args[i], SendSwitch))
                    Kind = ShellCommandKind.Send;
                else if (IsSwitch(Args[i], AddFolderSwitch))
                    Kind = ShellCommandKind.AddFolder;
                else
                    continue;

                var Value = Args[++i].Trim().Trim('"');
                if (Value.Length == 0)
                    continue;

                try { Value = System.IO.Path.GetFullPath(Value); } catch { continue; }
                Result.Add(new ShellCommand(Kind, Value));
            }

            return Result;
        }

        /// <summary>Rebuild a normalized argument list (absolute paths) from the commands.</summary>
        public static string[] ToArgs(IEnumerable<ShellCommand> Commands) =>
            Commands.SelectMany(x => new[] { x.Kind == ShellCommandKind.Send ? SendSwitch : AddFolderSwitch, x.Path }).ToArray();
    }
}
