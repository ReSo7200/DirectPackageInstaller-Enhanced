using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DirectPackageInstaller.Services
{
    /// <summary>The console's own view of a download task (RPI /api/get_task_progress).</summary>
    public sealed record RpiTaskProgress(long Transferred, long Length, long RestSeconds, long Error, long Bits)
    {
        public double Percent => Length > 0 ? Math.Min(100, Transferred * 100.0 / Length) : 0;
        public bool Finished => Length > 0 && Transferred >= Length;
    }

    /// <summary>
    /// Remote Package Installer task API (flatz/ps4_remote_pkg_installer):
    /// POST http://ps4:12800/api/{get_task_progress|pause_task|resume_task|stop_task|start_task|unregister_task}
    /// with {"task_id":N}. Replies aren't strict JSON (numbers as 0x hex).
    /// </summary>
    public static class RpiTasks
    {
        static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

        /// <summary>For tests: the RPI port.</summary>
        public static int Port { get; set; } = 12800;

        static async Task<string?> PostAsync(string ConsoleIP, string Api, long TaskId, CancellationToken Token)
        {
            try
            {
                using var Body = new StringContent($"{{\"task_id\":{TaskId}}}", Encoding.UTF8, "application/json");
                using var Response = await Http.PostAsync($"http://{ConsoleIP}:{Port}/api/{Api}", Body, Token);
                return await Response.Content.ReadAsStringAsync(Token);
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Numeric field of an RPI reply, decimal or 0x hex; null when absent.</summary>
        public static long? Number(string Reply, string Name)
        {
            var Match = Regex.Match(Reply, "\"" + Regex.Escape(Name) + "\"\\s*:\\s*\"?(-?0x[0-9A-Fa-f]+|-?[0-9]+)");
            if (!Match.Success)
                return null;

            var Text = Match.Groups[1].Value;
            bool Negative = Text.StartsWith("-");
            if (Negative)
                Text = Text.Substring(1);

            long Value = Text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? long.Parse(Text.Substring(2), NumberStyles.HexNumber)
                : long.Parse(Text, CultureInfo.InvariantCulture);
            return Negative ? -Value : Value;
        }

        public static async Task<RpiTaskProgress?> ProgressAsync(string ConsoleIP, long TaskId, CancellationToken Token = default)
        {
            var Reply = await PostAsync(ConsoleIP, "get_task_progress", TaskId, Token);
            if (Reply == null || !ConsoleInventory.RpiField(Reply, "status").Equals("success", StringComparison.OrdinalIgnoreCase))
                return null;

            // totals cover every piece of a split package; fall back to the current piece
            long Transferred = Number(Reply, "transferred_total") ?? Number(Reply, "transferred") ?? 0;
            long Length = Number(Reply, "length_total") ?? Number(Reply, "length") ?? 0;
            return new RpiTaskProgress(Transferred, Length,
                Number(Reply, "rest_sec_total") ?? Number(Reply, "rest_sec") ?? 0,
                Number(Reply, "error") ?? 0,
                Number(Reply, "bits") ?? 0);
        }

        /// <summary>pause_task, resume_task, stop_task, start_task, unregister_task. True on success.</summary>
        public static async Task<bool> ControlAsync(string ConsoleIP, long TaskId, string Verb, CancellationToken Token = default)
        {
            var Reply = await PostAsync(ConsoleIP, Verb, TaskId, Token);
            return Reply != null && ConsoleInventory.RpiField(Reply, "status").Equals("success", StringComparison.OrdinalIgnoreCase);
        }
    }
}
