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

        int RefreshAgain;

        public async Task RefreshAsync()
        {
            if (Interlocked.Exchange(ref Refreshing, 1) == 1)
            {
                // a probe is running (maybe for an old address): run again after it
                Interlocked.Exchange(ref RefreshAgain, 1);
                return;
            }

            try
            {
                do
                {
                    Interlocked.Exchange(ref RefreshAgain, 0);
                    await ProbeAsync();
                } while (Interlocked.Exchange(ref RefreshAgain, 0) == 1);
            }
            finally
            {
                Interlocked.Exchange(ref Refreshing, 0);
            }

            // a request that arrived after the loop's last check but before the
            // flag cleared would otherwise be lost
            if (Volatile.Read(ref RefreshAgain) == 1)
                _ = RefreshAsync();
        }

        /// <summary>
        /// Nothing that installs answered: say what does (so "offline" isn't
        /// misleading when the console is on and only BinLoader is off).
        /// </summary>
        static async Task<string> WhyNotAsync(string IP)
        {
            static async Task<bool> Opens(string Host, int Port)
            {
                try
                {
                    using var Client = new System.Net.Sockets.TcpClient();
                    using var Timeout = new CancellationTokenSource(800);
                    await NetConnect.ConnectAsync(Client, System.Net.IPAddress.Parse(Host), Port, Timeout.Token);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            if (await Opens(IP, 2121))
                return "Only FTP answers: turn on BinLoader in GoldHEN";
            if (await Opens(IP, 12800))
                return "RPI isn't responding: open it on the console";
            return "No installer answering";
        }

        async Task ProbeAsync()
        {
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

                // the IP changed while probing: probe the new one
                if (IP != (App.Config.PSIP?.Trim() ?? ""))
                {
                    Interlocked.Exchange(ref RefreshAgain, 1);
                    return;
                }

                Link = Found != null ? ConsoleLink.Online : ConsoleLink.Offline;
                Mode = Found ?? await WhyNotAsync(IP);
            }
        }
    }
}
