using System;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DirectPackageInstaller.Views
{
    public partial class SettingsPage : UserControl
    {
        /// <summary>Owns the DHCP and server lifecycles these settings drive.</summary>
        public MainView? Host { get; set; }

        public SettingsPage()
        {
            InitializeComponent();
            // phones / narrow windows: pages restyle through the "narrow" class
            PropertyChanged += (_, e) =>
            {
                if (e.Property == BoundsProperty)
                    Classes.Set("narrow", Bounds.Width > 0 && Bounds.Width < 700);
            };

            VersionText.Text = $"DPI Enhanced {SelfUpdate.CurrentVersion}";

            DhcpRow.IsVisible = DhcpDivider.IsVisible = App.IsWindows;
            DhcpSwitch.Click += DhcpClick;
            BtnRestartServer.Click += (_, _) => Host?.RestartServer_OnClick(this, null);
            BtnOpenData.Click += (_, _) => OpenDataFolder();
            BtnOpenData.IsVisible = !App.IsAndroid;
            ExitRow.IsVisible = App.IsAndroid;
            BtnExit.Click += (_, _) => Host?.btnExitOnClick(this, null);

            BtnRefreshAdapters.Click += (_, _) => FillAdapters();

            // experimental payload: stored straight in App.Config (not part of the main view model)
            // the page attaches before Settings.ini is read: sync again whenever it's shown
            void SyncExperimental()
            {
                ExperimentalSwitch.IsChecked = App.Config.ExperimentalPayload;
                StorageBox.SelectedIndex = App.Config.InstallStorage + 1;
                StorageBox.IsEnabled = App.Config.ExperimentalPayload;
            }
            AttachedToVisualTree += (_, _) => SyncExperimental();
            PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty && IsVisible)
                    SyncExperimental();
            };
            ExperimentalSwitch.IsCheckedChanged += async (_, _) =>
            {
                bool On = ExperimentalSwitch.IsChecked == true;
                if (On == App.Config.ExperimentalPayload)
                    return;
                App.Config.ExperimentalPayload = On;
                StorageBox.IsEnabled = On;
                App.SaveSettings();
                // the payload running on the console is the other build: let it exit
                await Tasks.Installer.Payload.ReleaseResidentAsync();
            };
            StorageBox.SelectionChanged += (_, _) =>
            {
                if (StorageBox.SelectedIndex < 0 || StorageBox.SelectedIndex - 1 == App.Config.InstallStorage)
                    return;
                App.Config.InstallStorage = StorageBox.SelectedIndex - 1;
                App.SaveSettings();
            };

            ShellRow.IsVisible = ShellDivider.IsVisible = Services.ShellIntegration.IsSupported;
            if (Services.ShellIntegration.IsSupported)
            {
                ShellSwitch.IsChecked = Services.ShellIntegration.IsRegistered();
                ShellSwitch.IsCheckedChanged += (_, _) =>
                {
                    bool Ok = ShellSwitch.IsChecked == true ? Services.ShellIntegration.Register() : Services.ShellIntegration.Unregister();
                    if (!Ok)
                        ShellSwitch.SetCurrentValue(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, Services.ShellIntegration.IsRegistered());
                };
            }
            BtnFindConsoles.Click += async (_, _) => await FindConsolesAsync();
            PcIpPicker.SelectionChanged += (_, _) =>
            {
                if (!Filling && PcIpPicker.SelectedItem is LocalAddress Choice && Model != null && Model.PCIP != Choice.Address)
                {
                    Model.PCIP = Choice.Address;
                    // the file server may be bound to the old address
                    Host?.RestartServer_OnClick(this, null);
                }
            };

            AdaptersChanged.Tick += (_, _) =>
            {
                AdaptersChanged.Stop();
                FillAdapters();
            };

            ViewModels.MainViewModel? Subscribed = null;
            DataContextChanged += (_, _) =>
            {
                if (Subscribed != null)
                    Subscribed.PropertyChanged -= OnModelChanged;
                Subscribed = Model;
                if (Subscribed != null)
                    Subscribed.PropertyChanged += OnModelChanged;
                FillAdapters();
            };
        }

        ViewModels.MainViewModel? Model => DataContext as ViewModels.MainViewModel;

        /// <summary>Scan the network and list consoles to pick from (there may be more than one).</summary>
        public async System.Threading.Tasks.Task FindConsolesAsync()
        {
            BtnFindConsoles.IsEnabled = false;
            BtnFindConsoles.Content = "Searching…";
            FoundPanel.IsVisible = true;
            FoundStatus.Text = "Looking for consoles on this PC's networks…";
            FoundList.ItemsSource = null;
            try
            {
                var Found = await Services.ConsoleScanner.ScanAsync();
                FoundList.ItemsSource = Found;
                FoundStatus.Text = Found.Count switch
                {
                    0 => "No consoles found. Make sure the console is on (not in rest mode), on the same network as this PC, and running GoldHEN or Remote Package Installer.",
                    1 => "Found one console. Click it to use it.",
                    _ => $"Found {Found.Count} consoles. Click the one to use."
                };
            }
            catch (Exception ex)
            {
                FoundStatus.Text = "Search failed: " + ex.Message;
            }
            finally
            {
                BtnFindConsoles.IsEnabled = true;
                BtnFindConsoles.Content = "Find consoles";
            }
        }

        void UseConsoleClick(object? sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.Tag is string Address && Model != null)
            {
                Model.PS4IP = Address;
                FoundStatus.Text = $"Using {Address}.";
                App.SaveSettings();
            }
        }

        readonly Avalonia.Threading.DispatcherTimer AdaptersChanged = new() { Interval = TimeSpan.FromMilliseconds(600) };

        /// <summary>Re-rank "same network" when the addresses change (debounced: fires per keystroke).</summary>
        void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ViewModels.MainViewModel.PS4IP) or nameof(ViewModels.MainViewModel.PCIP))
            {
                AdaptersChanged.Stop();
                AdaptersChanged.Start();
            }
        }

        bool Filling;

        /// <summary>
        /// List this PC's addresses and select the saved one. A saved address that
        /// is no longer on any adapter stays listed (flagged) instead of being
        /// silently replaced.
        /// </summary>
        void FillAdapters()
        {
            if (Model == null)
                return;

            Filling = true;
            try
            {
                var Current = Model.PCIP ?? "";
                var Choices = IPHelper.ListLocalAddresses(Model.PS4IP);

                if (Current.Length > 0 && Current != "0.0.0.0" && Choices.All(x => x.Address != Current))
                    Choices.Insert(0, new LocalAddress(Current, "not on any adapter right now", false));

                PcIpPicker.ItemsSource = Choices;
                PcIpPicker.SelectedItem = Choices.FirstOrDefault(x => x.Address == Current);
            }
            finally
            {
                Filling = false;
            }
        }

        void DhcpClick(object? sender, RoutedEventArgs e)
        {
            // the switch only reflects state: MainView asks for confirmation and may refuse
            // (SetCurrentValue keeps the OneWay binding alive)
            DhcpSwitch.SetCurrentValue(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, !DhcpSwitch.IsChecked);
            Host?.BtnDHCPServiceOnClick(this, e);
        }

        static void OpenDataFolder()
        {
            try
            {
                Process.Start(new ProcessStartInfo(App.WorkingDirectory) { UseShellExecute = true });
            }
            catch
            {
            }
        }
    }
}
