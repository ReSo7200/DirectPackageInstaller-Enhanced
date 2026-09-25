namespace DirectPackageInstaller.Views
{
    /// <summary>
    /// Navigation the pages need, implemented by the desktop window and the
    /// phone shell, so Library / On PS4 / Queue work the same in both.
    /// </summary>
    public interface IAppShell
    {
        void ShowInLibrary(string TitleId);
        void OpenInDirectLink(string Source);
        void ShowQueue();
    }

    public static class AppShell
    {
        /// <summary>The window or phone shell that's showing the pages.</summary>
        public static IAppShell? Current { get; set; }
    }
}
