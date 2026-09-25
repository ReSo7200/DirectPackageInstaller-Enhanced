using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace DirectPackageInstaller.Services
{
    /// <summary>
    /// Changes on the console through Remote Package Installer's API
    /// (http://ps4:12800/api/...). Only RPI offers these; GoldHEN's payload and
    /// etaHEN don't.
    /// </summary>
    public static class ConsoleActions
    {
        static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

        /// <summary>
        /// Uninstall a game, its update, or one DLC, matching the library entry's kind.
        /// Returns null on success, else a plain-language reason.
        /// </summary>
        public static async Task<string?> UninstallAsync(string ConsoleIP, LibraryEntry Entry)
        {
            if (!await IPHelper.IsRPIOnline(ConsoleIP))
                return "Uninstalling needs Remote Package Installer open on the console.";

            string Endpoint, Body;
            switch (Entry.Kind)
            {
                case "Game":
                    Endpoint = "uninstall_game";
                    Body = $"{{\"title_id\":\"{Entry.TitleId}\"}}";
                    break;
                case "Update":
                    Endpoint = "uninstall_patch";
                    Body = $"{{\"title_id\":\"{Entry.TitleId}\"}}";
                    break;
                case "DLC":
                    Endpoint = "uninstall_ac";
                    Body = $"{{\"content_id\":\"{Entry.ContentId}\"}}";
                    break;
                default:
                    return "Only games, updates and DLC can be uninstalled.";
            }

            if (string.IsNullOrEmpty(Entry.TitleId) || (Entry.Kind == "DLC" && string.IsNullOrEmpty(Entry.ContentId)))
                return "This package has no title ID to uninstall by.";

            try
            {
                using var Content = new StringContent(Body, Encoding.UTF8, "application/json");
                using var Response = await Http.PostAsync($"http://{ConsoleIP}:12800/api/{Endpoint}", Content);
                var Reply = await Response.Content.ReadAsStringAsync();

                // RPI's replies aren't strict JSON (hex numbers), read the field directly
                if (ConsoleInventory.RpiField(Reply, "status").Equals("success", StringComparison.OrdinalIgnoreCase))
                    return null;

                return InstallErrors.Describe(Reply);
            }
            catch (Exception ex)
            {
                return "Couldn't reach Remote Package Installer: " + ex.Message;
            }
        }
    }
}
