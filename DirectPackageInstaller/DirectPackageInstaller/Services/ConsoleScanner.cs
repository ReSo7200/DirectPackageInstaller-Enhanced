using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DirectPackageInstaller.Services
{
    /// <summary>A console found on the network.</summary>
    public sealed class FoundConsole
    {
        public string Address { get; init; } = "";
        /// <summary>Name set on the console (from PlayStation discovery), if it answered.</summary>
        public string Name { get; set; } = "";
        /// <summary>"PS4", "PS5" or "" when only homebrew ports answered.</summary>
        public string Kind { get; set; } = "";
        /// <summary>Discovery reply: "200" on, "620" rest mode.</summary>
        public bool InRestMode { get; set; }
        public List<string> Services { get; } = new();

        public string Label
        {
            get
            {
                var Parts = new List<string> { Address };
                var What = string.Join(" ", new[] { Kind, Name }.Where(x => x.Length > 0));
                if (What.Length > 0)
                    Parts.Add(What);
                if (InRestMode)
                    Parts.Add("rest mode");
                Parts.Add(Services.Count > 0 ? string.Join(", ", Services) : "no installer running");
                return string.Join("  ·  ", Parts);
            }
        }
    }

    /// <summary>
    /// Finds consoles on the local network two ways, merged per address:
    /// the PlayStation discovery broadcast (name, PS4/PS5, rest mode) and a
    /// quick sweep of the local /24 networks for the homebrew install
    /// services (RPI 12800, GoldHEN BinLoader 9090, GoldHEN FTP 2121).
    /// </summary>
    public static class ConsoleScanner
    {
        static readonly (int Port, string Name)[] ServicePorts =
        {
            (12800, "RPI / etaHEN"),
            (9090, "GoldHEN BinLoader"),
            (2121, "GoldHEN FTP"),
        };

        static readonly string[] VirtualAdapterWords =
        {
            "virtual", "vmware", "virtualbox", "hyper-v", "vethernet", "wsl", "vpn", "tap-", "tap ", "tun",
            "wintun", "wireguard", "tailscale", "zerotier", "hamachi", "radmin", "npcap", "loopback", "bluetooth"
        };

        public static async Task<List<FoundConsole>> ScanAsync(CancellationToken Token = default)
        {
            var Found = new ConcurrentDictionary<string, FoundConsole>();
            FoundConsole Get(string Address) => Found.GetOrAdd(Address, a => new FoundConsole { Address = a });

            var Networks = LocalNetworks();

            await Task.WhenAll(
                DiscoveryAsync(Networks, Get, Token),
                SweepAsync(Networks, Get, Token));

            // gateways/firewalls that accept connections on every port aren't consoles
            var Candidates = Found.Values.Where(x => x.Kind.Length == 0).ToList();
            var AcceptsAnything = await Task.WhenAll(Candidates.Select(x => OpensAsync(IPAddress.Parse(x.Address), 47911, Token)));
            for (int i = 0; i < Candidates.Count; i++)
                if (AcceptsAnything[i])
                    Found.TryRemove(Candidates[i].Address, out _);

            // a PC with a web server on 12800 isn't a console: keep answers to
            // discovery, or hosts with more than the RPI port
            return Found.Values
                .Where(x => x.Kind.Length > 0 || x.Services.Any(s => s != "RPI / etaHEN"))
                .OrderByDescending(x => x.Services.Count)
                .ThenBy(x => x.Address, Comparer<string>.Create(CompareIP))
                .ToList();
        }

        static int CompareIP(string A, string B)
        {
            var a = IPAddress.Parse(A).GetAddressBytes();
            var b = IPAddress.Parse(B).GetAddressBytes();
            for (int i = 0; i < 4; i++)
                if (a[i] != b[i])
                    return a[i].CompareTo(b[i]);
            return 0;
        }

        /// <summary>This PC's IPv4 addresses and masks on real, connected adapters.</summary>
        static List<(IPAddress Local, IPAddress Mask)> LocalNetworks()
        {
            var Result = new List<(IPAddress, IPAddress)>();
            foreach (var Interface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (Interface.OperationalStatus != OperationalStatus.Up || Interface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                // VPN and virtual-machine adapters never lead to a console, and VPN
                // gateways accept every port (false hits). Only real Ethernet / Wi-Fi.
                if (Interface.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT))
                    continue;
                var Description = (Interface.Description + " " + Interface.Name).ToLowerInvariant();
                if (VirtualAdapterWords.Any(Description.Contains))
                    continue;

                foreach (var Info in Interface.GetIPProperties().UnicastAddresses)
                {
                    if (Info.Address.AddressFamily != AddressFamily.InterNetwork || Info.IPv4Mask == null)
                        continue;

                    var Bytes = Info.Address.GetAddressBytes();
                    // link-local and CGNAT/VPN ranges (Tailscale 100.64/10) have no consoles
                    if (Bytes[0] == 169 && Bytes[1] == 254) continue;
                    if (Bytes[0] == 100 && (Bytes[1] & 0xC0) == 64) continue;

                    Result.Add((Info.Address, Info.IPv4Mask));
                }
            }
            return Result;
        }

        static async Task DiscoveryAsync(List<(IPAddress Local, IPAddress Mask)> Networks, Func<string, FoundConsole> Get, CancellationToken Token)
        {
            var Tasks = Networks.Select(async Network =>
            {
                try
                {
                    using var Socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
                    Socket.Bind(new IPEndPoint(Network.Local, 0));

                    // directed broadcast of this network, plus the global one
                    var Local = Network.Local.GetAddressBytes();
                    var Mask = Network.Mask.GetAddressBytes();
                    var Broadcast = new IPAddress(Local.Select((b, i) => (byte)(b | ~Mask[i])).ToArray());

                    foreach (var Version in new[] { "00020020", "00030010" })
                    {
                        var Message = Encoding.ASCII.GetBytes($"SRCH * HTTP/1.1\ndevice-discovery-protocol-version:{Version}\n");
                        foreach (var Target in new[] { Broadcast, IPAddress.Broadcast }.Distinct())
                        {
                            try { await Socket.SendToAsync(Message, SocketFlags.None, new IPEndPoint(Target, 987)); } catch { }  // PS4
                            try { await Socket.SendToAsync(Message, SocketFlags.None, new IPEndPoint(Target, 9302)); } catch { } // PS5
                        }
                    }

                    var Buffer = new byte[2048];
                    var Until = DateTime.UtcNow.AddMilliseconds(2500);
                    while (DateTime.UtcNow < Until && !Token.IsCancellationRequested)
                    {
                        using var Wait = CancellationTokenSource.CreateLinkedTokenSource(Token);
                        Wait.CancelAfter(Until - DateTime.UtcNow);
                        SocketReceiveFromResult Reply;
                        try
                        {
                            Reply = await Socket.ReceiveFromAsync(Buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), Wait.Token);
                        }
                        catch
                        {
                            break;
                        }

                        var Text = Encoding.UTF8.GetString(Buffer, 0, Reply.ReceivedBytes);
                        if (!Text.StartsWith("HTTP/1.1", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var Console = Get(((IPEndPoint)Reply.RemoteEndPoint).Address.ToString());
                        Console.InRestMode = Text.Contains(" 620 ");
                        foreach (var Line in Text.Split('\n'))
                        {
                            var Colon = Line.IndexOf(':');
                            if (Colon < 0) continue;
                            var Key = Line.Substring(0, Colon).Trim().ToLowerInvariant();
                            var Value = Line.Substring(Colon + 1).Trim();
                            if (Key == "host-name") Console.Name = Value;
                            if (Key == "host-type") Console.Kind = Value.ToUpperInvariant().Contains("PS5") ? "PS5" : "PS4";
                        }
                        if (Console.Kind.Length == 0)
                            Console.Kind = "PS4";
                    }
                }
                catch
                {
                }
            });

            await Task.WhenAll(Tasks);
        }

        /// <summary>Probe every host of each local network (up to a /22) for the install ports.</summary>
        static async Task SweepAsync(List<(IPAddress Local, IPAddress Mask)> Networks, Func<string, FoundConsole> Get, CancellationToken Token)
        {
            var Hosts = new List<IPAddress>();
            foreach (var (Local, Mask) in Networks)
            {
                uint L = ToUInt(Local), M = ToUInt(Mask);
                // big networks (e.g. a /16 office LAN): only this PC's /24
                if (~M > 1023)
                    M = 0xFFFFFF00;
                uint First = (L & M) + 1, Last = (L | ~M) - 1;
                for (uint Host = First; Host <= Last; Host++)
                    if (Host != L)
                        Hosts.Add(FromUInt(Host));
            }

            var Throttle = new SemaphoreSlim(256);
            var Tasks = Hosts.Distinct().SelectMany(Host => ServicePorts.Select(async Service =>
            {
                await Throttle.WaitAsync(Token);
                try
                {
                    if (!await OpensAsync(Host, Service.Port, Token))
                        return;
                    var Console = Get(Host.ToString());
                    lock (Console.Services)
                        if (!Console.Services.Contains(Service.Name))
                            Console.Services.Add(Service.Name);
                }
                finally
                {
                    Throttle.Release();
                }
            }));

            await Task.WhenAll(Tasks);
        }

        static async Task<bool> OpensAsync(IPAddress Host, int Port, CancellationToken Token)
        {
            try
            {
                using var Client = new TcpClient();
                using var Timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
                Timeout.CancelAfter(400);
                await NetConnect.ConnectAsync(Client, Host, Port, Timeout.Token);
                return true;
            }
            catch
            {
                return false;
            }
        }

        static uint ToUInt(IPAddress Address)
        {
            var b = Address.GetAddressBytes();
            return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        }

        static IPAddress FromUInt(uint Value) =>
            new IPAddress(new[] { (byte)(Value >> 24), (byte)(Value >> 16), (byte)(Value >> 8), (byte)Value });
    }
}
