using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using DirectPackageInstaller.Services;
using DirectPackageInstaller.Tasks;
using DirectPackageInstaller.ViewModels;

namespace DirectPackageInstaller.Views
{
    public partial class MainWindow : Window, IAppShell
    {
        public static MainWindow Instance;

        public MainWindow()
        {
            Instance = this;
            AppShell.Current = this;

            InitializeComponent();
            Notices.Attach(this);

            // one view model for Direct link and Settings (they drive the same options)
            View.DataContext = new MainViewModel();
            SettingsPage.DataContext = View.DataContext;
            SettingsPage.Host = View;

            ConsolePill.DataContext = ConsoleStatus.Instance;
            ConsolePill.Click += async (_, _) => await ConsoleStatus.Instance.RefreshAsync();

            // a finished official update download shows up in the library
            UpdateDownloads.Instance.Finished += Download =>
            {
                if (LibraryPage.DataContext is LibraryViewModel Library)
                    _ = Library.ScanAsync();
            };

            ConsolePage.Attach(() => (LibraryPage.DataContext as LibraryViewModel)?.Entries ?? Array.Empty<LibraryEntry>());
            SavesPage.Attach(() => ConsolePage.ViewModel);
            UpdatesPage.Attach(() => ConsolePage.ViewModel);

            NavLibrary.IsCheckedChanged += (_, _) => ShowPage();
            NavConsole.IsCheckedChanged += (_, _) => ShowPage();
            NavLink.IsCheckedChanged += (_, _) => ShowPage();
            NavSaves.IsCheckedChanged += (_, _) => ShowPage();
            NavUpdates.IsCheckedChanged += (_, _) => ShowPage();
            NavQueue.IsCheckedChanged += (_, _) => ShowPage();
            NavHomebrew.IsCheckedChanged += (_, _) => ShowPage();
            NavSettings.IsCheckedChanged += (_, _) => ShowPage();

            SendQueue.Instance.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SendQueue.PendingCount))
                    NavQueue.Tag = SendQueue.Instance.PendingCount > 0 ? SendQueue.Instance.PendingCount.ToString() : null;
            };

            ((MainViewModel)View.DataContext).PropertyChanged += (_, e) =>
            {
                // debounced (typing an IP fires per keystroke); by the time it runs
                // MainView has copied PS4IP into App.Config
                if (e.PropertyName == nameof(MainViewModel.CheckOfficialUpdates) && LibraryPage.DataContext is LibraryViewModel Library)
                {
                    if (((MainViewModel)View.DataContext!).CheckOfficialUpdates)
                        _ = Library.CheckOfficialUpdatesAsync();
                    else
                        Library.ClearOfficialUpdates();
                }

                if (e.PropertyName == nameof(MainViewModel.PS4IP))
                {
                    IpChanged.Stop();
                    IpChanged.Start();
                }
            };

            IpChanged.Tick += (_, _) =>
            {
                IpChanged.Stop();
                _ = ConsoleStatus.Instance.RefreshAsync();
            };

            KeyDown += OnKeyDown;
            Opened += MainWindowOpened;
            Closing += MainWindowClosing;
        }

        readonly Avalonia.Threading.DispatcherTimer IpChanged = new() { Interval = TimeSpan.FromMilliseconds(700) };

        void ShowPage()
        {
            bool LeavingSettings = SettingsPage.IsVisible && NavSettings.IsChecked != true;

            LibraryPage.IsVisible = NavLibrary.IsChecked == true;
            ConsolePage.IsVisible = NavConsole.IsChecked == true;
            View.IsVisible = NavLink.IsChecked == true;
            SavesPage.IsVisible = NavSaves.IsChecked == true;
            UpdatesPage.IsVisible = NavUpdates.IsChecked == true;
            QueuePage.IsVisible = NavQueue.IsChecked == true;
            HomebrewPage.IsVisible = NavHomebrew.IsChecked == true;
            SettingsPage.IsVisible = NavSettings.IsChecked == true;

            // settings used to be saved only on a clean exit
            if (LeavingSettings)
            {
                App.SaveSettings();
                _ = ConsoleStatus.Instance.RefreshAsync();
            }

            if (LibraryPage.IsVisible)
                LibraryPage.OnShown();
            if (ConsolePage.IsVisible)
                ConsolePage.OnShown();
            if (HomebrewPage.IsVisible)
                HomebrewPage.OnShown();
            if (SavesPage.IsVisible)
                SavesPage.OnShown();
            if (UpdatesPage.IsVisible)
                UpdatesPage.OnShown();
        }

        void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
                return;

            RadioButton? Target = e.Key switch
            {
                Key.D1 or Key.NumPad1 => NavLibrary,
                Key.D2 or Key.NumPad2 => NavConsole,
                Key.D3 or Key.NumPad3 => NavLink,
                Key.D4 or Key.NumPad4 => NavQueue,
                Key.D5 or Key.NumPad5 => NavSettings,
                Key.D6 or Key.NumPad6 => NavHomebrew,
                Key.D7 or Key.NumPad7 => NavSaves,
                Key.D8 or Key.NumPad8 => NavUpdates,
                _ => null
            };

            if (Target != null)
            {
                Target.IsChecked = true;
                e.Handled = true;
            }
        }

        public void ShowDirectLink() => NavLink.IsChecked = true;

        public void ShowQueue() => NavQueue.IsChecked = true;

        public void ShowSaves(string? TitleId = null)
        {
            if (TitleId != null)
                SavesPage.ShowTitle(TitleId);
            NavSaves.IsChecked = true;
        }

        /// <summary>Files sent from Explorer go to the queue; folders join the library.</summary>
        async System.Threading.Tasks.Task HandleShellArgsAsync(string[] Args)
        {
            var Library = LibraryPage.DataContext as LibraryViewModel;
            var Failed = new System.Collections.Generic.List<string>();
            bool Sent = false;

            foreach (var Command in ShellIntegration.Parse(Args))
            {
                if (Command.Kind == ShellCommandKind.AddFolder)
                {
                    if (Library != null)
                        await Library.AddFolderAsync(Command.Path);
                    NavLibrary.IsChecked = true;
                    continue;
                }

                var Entry = await System.Threading.Tasks.Task.Run(() => LibraryService.ReadPackage(Command.Path));
                if (Entry == null || Entry.Error != null)
                {
                    Failed.Add($"{System.IO.Path.GetFileName(Command.Path)}: {Entry?.Error ?? "file not found"}");
                    continue;
                }

                SendQueue.Instance.Enqueue(new[] { Entry });
                Sent = true;
            }

            if (Sent)
                NavQueue.IsChecked = true;

            // bring the window forward: the click happened in Explorer
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();

            if (Failed.Count > 0)
                await MessageBox.ShowAsync("Couldn't send:\n" + string.Join("\n", Failed), "Send to PS4", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        /// <summary>Library filtered to one title ID (from "On PS4").</summary>
        public void ShowInLibrary(string TitleId)
        {
            NavLibrary.IsChecked = true;
            if (LibraryPage.DataContext is LibraryViewModel Library)
                Library.Search = TitleId;
        }

        /// <summary>Show a file's full package details on the Direct link page.</summary>
        public void OpenInDirectLink(string Source)
        {
            ShowDirectLink();
            View.OpenSource(Source);
        }

        private async void MainWindowOpened(object? sender, EventArgs e)
        {
#if DEBUG
            this.AttachDevTools();
#endif
            // the scan needs no settings; don't make it wait for update checks
            LibraryPage.OnShown();

            // Explorer "Send to PS4 (DPI)" / "Add to DPI library", from this launch or later ones
            SingleInstance.ArgumentsReceived += Args => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = HandleShellArgsAsync(Args));

            await View.OnShown(this);

            // settings are loaded by OnShown
            ConsoleStatus.Instance.Start();
            LibraryPage.AutoCheck();
        }

        private bool ShutdownDone;
        private bool ShuttingDown;

        /// <summary>
        /// Cancel the first close, finish the shutdown work (servers, DHCP network
        /// restore), then close for real. Previously the process could exit
        /// before the network adapter was restored.
        /// </summary>
        private async void MainWindowClosing(object? sender, CancelEventArgs e)
        {
            if (ShutdownDone)
                return;

            e.Cancel = true;
            if (ShuttingDown)
                return;

            ShuttingDown = true;
            try
            {
                bool PS4Downloading = Installer.Server?.Connections > 0;
                PS4Downloading |= (DateTime.Now - Installer.Server?.LastRequest)?.TotalSeconds < 5;
                PS4Downloading |= SendQueue.Instance.PendingCount > 0;

                if (PS4Downloading &&
                    await MessageBox.ShowAsync("The console is still downloading from this PC, or packages are waiting in the queue.\nClose anyway? Unfinished downloads will stop.",
                        "DirectPackageInstaller", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;

                await View.SetStatus("Shutting down servers…");

                App.SaveSettings();
                await Installer.Payload.StopServer();

                if (View.DHCP != null)
                {
                    View.DHCP.Stop();
                    await App.SetupDHCPNetwork();
                }
            }
            catch
            {
            }
            finally
            {
                ShuttingDown = false;
            }

            ShutdownDone = true;
            Close();
        }
    }
}
