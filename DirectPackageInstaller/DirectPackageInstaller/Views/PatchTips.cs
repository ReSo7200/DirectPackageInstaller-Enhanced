using Avalonia.Data.Converters;

namespace DirectPackageInstaller.Views
{
    /// <summary>Tooltip of a patch's checkbox: why it can't be switched on.</summary>
    public static class PatchTips
    {
        public static readonly IValueConverter Tip = new FuncValueConverter<bool, string>(Applies =>
            Applies ? "Switch this patch on or off" : "This patch is for another version of the game");
    }
}
