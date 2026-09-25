using System;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

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
            NarrowLayout.Watch(this, ApplyNarrow);

            VersionText.Text = $"DPI Enhanced {SelfUpdate.CurrentVersion}";

            DhcpRow.IsVisible = DhcpDivider.IsVisible = App.IsWindows;
            DhcpSwitch.Click += DhcpClick;
            BtnRestartServer.Click += (_, _) => Host?.RestartServer_OnClick(this, null);

            // payload sender: only while BinLoader answers
            void SyncPayloadButton()
            {
                BtnSendPayload.IsEnabled = Services.ConsoleStatus.Instance.HasBinLoader;
                ToolTip.SetTip(BtnSendPayload, BtnSendPayload.IsEnabled ? "Pick the payload file to send" : "Needs GoldHEN's BinLoader turned on");
            }
            SyncPayloadButton();
            Services.ConsoleStatus.Instance.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Services.ConsoleStatus.HasBinLoader))
                    SyncPayloadButton();
            };
            BtnSendPayload.Click += async (_, _) => await SendPayloadAsync();
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

        /// <summary>Narrow: address and key fields go under their label, full width.</summary>
        void ApplyNarrow(bool Narrow)
        {
            NarrowLayout.Reflow(PsIpRow, Narrow);
            foreach (var Row in this.GetLogicalDescendants().OfType<Grid>().Where(x => x.Classes.Contains("debrid")))
                NarrowLayout.Reflow(Row, Narrow);

            // the adapter picker already has its own line: let it fill it
            NarrowLayout.Set(PcIpLine.ColumnDefinitions[0], ColumnDefinition.WidthProperty, GridLength.Star, Narrow);
            NarrowLayout.Set(PcIpLine, HorizontalAlignmentProperty, Avalonia.Layout.HorizontalAlignment.Stretch, Narrow);
            NarrowLayout.Set(PcIpPicker, MinWidthProperty, 0d, Narrow);
            NarrowLayout.Set(PcIpPicker, HorizontalAlignmentProperty, Avalonia.Layout.HorizontalAlignment.Stretch, Narrow);
        }

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

        async System.Threading.Tasks.Task SendPayloadAsync()
        {
            string? File = null;
            if (App.IsSingleView)
            {
                var Picker = new FilePicker();
                await Picker.OpenDir(App.RootDir);
                await SingleView.CallView(Picker, false);
                File = Picker.SelectedFiles.FirstOrDefault();
            }
            else if (TopLevel.GetTopLevel(this) is { } Top)
            {
                var Picked = await Top.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Choose a payload (.bin / .elf)",
                    FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Payloads") { Patterns = new[] { "*.bin", "*.elf" } } }
                });
                File = Picked.FirstOrDefault()?.TryGetLocalPath();
            }
            if (string.IsNullOrEmpty(File))
                return;

            var Name = System.IO.Path.GetFileName(File);
            if (await MessageBox.ShowAsync($"Send {Name} to the console? It runs right away.", "Send a payload",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            BtnSendPayload.IsEnabled = false;
            try
            {
                await Services.PayloadSender.SendAsync(App.Config.PSIP.Trim(), File);
                PayloadHint.Text = $"Sent {Name}.";
            }
            catch (Exception ex)
            {
                PayloadHint.Text = $"Couldn't send {Name}: {ex.Message}";
            }
            finally
            {
                BtnSendPayload.IsEnabled = Services.ConsoleStatus.Instance.HasBinLoader;
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
