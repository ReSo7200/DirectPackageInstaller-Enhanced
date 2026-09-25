using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DirectPackageInstaller.Services;

namespace DirectPackageInstaller.Views
{
    /// <summary>Homebrew from its authors' GitHub releases (see HomebrewCatalog).</summary>
    public partial class HomebrewPage : UserControl
    {
        readonly ObservableCollection<HomebrewApp> Apps = new();
        bool Loaded, Refreshing;

        public HomebrewPage()
        {
            InitializeComponent();
            // phones / narrow windows: pages restyle through the "narrow" class
            NarrowLayout.Watch(this);

            AppList.ItemsSource = Apps;
            BtnRefresh.Click += async (_, _) => await RefreshAsync(Force: true);
            RepoBox.TextChanged += (_, _) => SyncButtons();
            BtnAdd.Click += async (_, _) =>
            {
                if (Refreshing || HomebrewCatalog.NormalizeRepo(RepoBox.Text ?? "") is not { } Repo)
                    return;
                HomebrewCatalog.AddRepo(Repo);
                RepoBox.Text = "";
                await RefreshAsync(Force: false);
            };
            SyncButtons();
        }

        /// <summary>Read the releases the first time the page is shown.</summary>
        public async void OnShown()
        {
            if (Loaded)
                return;
            Loaded = true;
            await RefreshAsync(Force: false);
        }

        void SyncButtons()
        {
            bool ValidRepo = HomebrewCatalog.NormalizeRepo(RepoBox.Text ?? "") != null;
            BtnAdd.IsEnabled = !Refreshing && ValidRepo;
            ToolTip.SetTip(BtnAdd, Refreshing ? "Wait for the check to finish"
                : ValidRepo ? "Adds the project; its latest release must have a .pkg file"
                : "Type a GitHub project as owner/name (e.g. bucanero/apollo-ps4) or paste its github.com link");
            BtnRefresh.IsEnabled = !Refreshing;
            ToolTip.SetTip(BtnRefresh, Refreshing ? "Checking…" : "Check each project for a newer release");
        }

        async Task RefreshAsync(bool Force)
        {
            if (Refreshing)
                return;
            Refreshing = true;
            SyncButtons();
            try
            {
                Apps.Clear();
                foreach (var App in HomebrewCatalog.Apps())
                {
                    App.Status = "Checking its latest release…";
                    Apps.Add(App);
                }
                await HomebrewCatalog.RefreshAsync(Apps, Force);
                foreach (var App in Apps.Where(x => x.Status == "Checking its latest release…"))
                    App.Status = "";
            }
            catch (Exception ex)
            {
                foreach (var App in Apps.Where(x => x.Status == "Checking its latest release…"))
                    App.Status = "Couldn't check: " + ex.Message;
            }
            finally
            {
                Refreshing = false;
                SyncButtons();
            }
        }

        async void GetClick(object? sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.Tag is not HomebrewApp App || !App.CanGet)
                return;

            App.Busy = true;
            try
            {
                var Path = await HomebrewCatalog.DownloadAsync(App, new Progress<string>(Text => App.Status = Text));
                var Entry = LibraryService.ReadPackage(Path);
                if (Entry == null || Entry.Error != null)
                {
                    App.Status = "The download isn't a readable PS4 package" + (Entry?.Error is { } Why ? $": {Why}" : ".");
                    return;
                }

                SendQueue.Instance.Enqueue(new[] { Entry });
                App.Status = ConsoleStatus.Instance.CanInstall
                    ? "Added to the Queue."
                    : "Added to the Queue; it's sent once the console can install (" + ConsoleStatus.Instance.Mode + ").";
            }
            catch (Exception ex)
            {
                App.Status = "Download failed: " + ex.Message;
            }
            finally
            {
                App.Busy = false;
            }
        }

        async void PageClick(object? sender, RoutedEventArgs e)
        {
            try
            {
                if ((sender as Control)?.Tag is HomebrewApp App && TopLevel.GetTopLevel(this) is { } Top)
                    await Top.Launcher.LaunchUriAsync(new Uri(App.PageUrl));
            }
            catch { /* no browser: nothing to do */ }
        }

        void RemoveClick(object? sender, RoutedEventArgs e)
        {
            // not while a check walks the list
            if (Refreshing || (sender as Control)?.Tag is not HomebrewApp App || !App.Custom)
                return;
            HomebrewCatalog.RemoveRepo(App.Repo);
            Apps.Remove(App);
        }
    }
}
