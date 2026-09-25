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

            VersionText.Text = $"DPI Enhanced {SelfUpdate.CurrentVersion}";

            DhcpRow.IsVisible = DhcpDivider.IsVisible = App.IsWindows;
            DhcpSwitch.Click += DhcpClick;
            BtnRestartServer.Click += (_, _) => Host?.RestartServer_OnClick(this, null);
            BtnOpenData.Click += (_, _) => OpenDataFolder();

            BtnRefreshAdapters.Click += (_, _) => FillAdapters();
            PcIpPicker.SelectionChanged += (_, _) =>
            {
                if (!Filling && PcIpPicker.SelectedItem is LocalAddress Choice && Model != null && Model.PCIP != Choice.Address)
                {
                    Model.PCIP = Choice.Address;
                    // the file server may be bound to the old address
                    Host?.RestartServer_OnClick(this, null);
                }
            };

            DataContextChanged += (_, _) =>
            {
                if (Model != null)
                    Model.PropertyChanged += (_, e) =>
                    {
                        // re-rank "same network" when the console address changes
                        if (e.PropertyName is nameof(ViewModels.MainViewModel.PS4IP) or nameof(ViewModels.MainViewModel.PCIP))
                            FillAdapters();
                    };
                FillAdapters();
            };
        }

        ViewModels.MainViewModel? Model => DataContext as ViewModels.MainViewModel;

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
