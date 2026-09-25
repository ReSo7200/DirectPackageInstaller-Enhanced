using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using DirectPackageInstaller.Tasks;
using ReactiveUI;

namespace DirectPackageInstaller.Services
{
    public enum ConsoleLink
    {
        NoAddress,
        Checking,
        Offline,
        Online
    }

    /// <summary>
    /// Which installer service answers on the console: the same probes and
    /// order Installer.PushPackage uses (RPI, etaHEN, GoldHEN payload server).
    /// </summary>
    public sealed class ConsoleStatus : ReactiveObject
    {
        public static ConsoleStatus Instance { get; } = new ConsoleStatus();

        readonly DispatcherTimer Timer;
        int Refreshing;

        ConsoleStatus()
        {
            Timer = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, async (_, _) => await RefreshAsync());
        }

        public void Start()
        {
            Timer.Start();
            _ = RefreshAsync();
        }

        ConsoleLink _Link = ConsoleLink.NoAddress;
        public ConsoleLink Link
        {
            get => _Link;
            private set
            {
                this.RaiseAndSetIfChanged(ref _Link, value);
                this.RaisePropertyChanged(nameof(IsOnline));
                this.RaisePropertyChanged(nameof(IsOffline));
                this.RaisePropertyChanged(nameof(IsChecking));
            }
        }

        public bool IsOnline => Link == ConsoleLink.Online;
        public bool IsOffline => Link is ConsoleLink.Offline or ConsoleLink.NoAddress;
        public bool IsChecking => Link == ConsoleLink.Checking;

        string _Address = "";
        public string Address
        {
            get => _Address;
            private set => this.RaiseAndSetIfChanged(ref _Address, value);
        }

        string _Mode = "Set the PS IP in Settings";
        /// <summary>What answered: "GoldHEN", "RPI", "etaHEN", ... or why nothing did.</summary>
        public string Mode
        {
            get => _Mode;
            private set => this.RaiseAndSetIfChanged(ref _Mode, value);
        }

        public async Task RefreshAsync()
        {
            if (Interlocked.Exchange(ref Refreshing, 1) == 1)
                return;

            try
            {
                var IP = App.Config.PSIP?.Trim() ?? "";
                Address = IP;

                if (string.IsNullOrEmpty(IP) || IP == "0.0.0.0")
                {
                    Link = ConsoleLink.NoAddress;
                    Mode = "Set the PS IP in Settings";
                    return;
                }

                if (Link != ConsoleLink.Online)
                    Link = ConsoleLink.Checking;

                string? Found = null;
                if (await IPHelper.IsRPIOnline(IP))
                    Found = "Remote Package Installer";
                else if (await IPHelper.IsEtaHenOnline(IP))
                    Found = "etaHEN";
                else if (await IPHelper.IsGoldHENOnline(IP))
                    Found = "GoldHEN";
                else if (Installer.Payload.ClientRunning)
                    Found = "GoldHEN (payload running)";

                // the IP may have changed while probing
                if (IP != (App.Config.PSIP?.Trim() ?? ""))
                    return;

                Link = Found != null ? ConsoleLink.Online : ConsoleLink.Offline;
                Mode = Found ?? "No installer answering";
            }
            finally
            {
                Interlocked.Exchange(ref Refreshing, 0);
            }
        }
    }
}
