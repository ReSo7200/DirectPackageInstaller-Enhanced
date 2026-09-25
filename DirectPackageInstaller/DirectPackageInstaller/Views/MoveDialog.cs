using System.Threading.Tasks;
using DirectPackageInstaller.Services;

namespace DirectPackageInstaller.Views
{
    /// <summary>"Move to … storage" from the On PS4 and Library menus.</summary>
    public static class MoveDialog
    {
        public static async Task AskAndStartAsync(string TitleId, string Title, bool ToExtended)
        {
            if (ConsoleMove.WhyNot() is { } Why)
            {
                await MessageBox.ShowAsync(Why + ".", "Move", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var Where = ToExtended ? "extended storage" : "system storage";
            var Reply = await MessageBox.ShowAsync(
                $"Move {Title} ({TitleId}) to {Where}?\n\n" +
                "The console copies the game, its update and DLC to the other drive and removes the old copy. " +
                "Nothing is sent from this device, and saved data isn't touched.\n\n" +
                "Close the game on the console first. Big games take a while: keep DPI open until the Queue says it's done.",
                "Move", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (Reply != DialogResult.Yes)
                return;

            ConsoleMove.Start(TitleId, Title, ToExtended);
            AppShell.Current?.ShowQueue();
        }
    }
}
