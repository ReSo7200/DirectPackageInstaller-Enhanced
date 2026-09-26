using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using DirectPackageInstaller.Views;

namespace DirectPackageInstaller
{
    /// <summary>A local IPv4 address and the adapter it belongs to.</summary>
    public sealed record LocalAddress(string Address, string Adapter, bool SameNetworkAsConsole)
    {
        public string Label => SameNetworkAsConsole ? $"{Address}  ·  {Adapter}  (same network as the console)" : $"{Address}  ·  {Adapter}";
    }

    public static class IPHelper
    {
        public static string[] EnumLocalIPs()
        {
            if (App.IsAndroid && App.GetIPAddresses != null)
            {
                var Ips = App.GetIPAddresses() ?? null;
                if (Ips != null && Ips.Length > 0)
                    return Ips;
            }

            return SearchInterfaces();
        }
        /// <summary>
        /// The local IPv4 address on the same subnet as RemoteIP (the one the
        /// console can call back). Uses real subnet masks, so 192.168.1.x no
        /// longer matches 192.168.10.x, and VPN/virtual adapters on other
        /// subnets are ignored. With several candidates, the one the OS routes
        /// through wins.
        /// </summary>
        public static string? FindLocalIP(string RemoteIP)
        {
            if (!IPAddress.TryParse(RemoteIP, out var Remote) || Remote.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return null;

            try
            {
                var Candidates = new List<string>();

                if (App.IsAndroid && App.GetIPAddresses != null)
                {
                    // no masks available: same /24
                    string RemotePrefix = RemoteIP.Substring(0, RemoteIP.LastIndexOf('.') + 1);
                    Candidates.AddRange((App.GetIPAddresses() ?? Array.Empty<string>()).Where(x => x.StartsWith(RemotePrefix)));
                }
                else
                {
                    foreach (var Interface in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (Interface.OperationalStatus != OperationalStatus.Up || Interface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                            continue;

                        foreach (var Info in Interface.GetIPProperties().UnicastAddresses)
                        {
                            if (Info.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || Info.IPv4Mask == null)
                                continue;

                            if (SameSubnet(Info.Address, Remote, Info.IPv4Mask) && !Candidates.Contains(Info.Address.ToString()))
                                Candidates.Add(Info.Address.ToString());
                        }
                    }
                }

                if (Candidates.Count == 1)
                    return Candidates[0];

                var Routed = RoutedLocalIP(Remote);
                if (Candidates.Count > 1)
                    return Candidates.Contains(Routed) ? Routed : Candidates[0];

                // console on another subnet (routed LAN): trust the OS route
                return Routed;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Every IPv4 address on an adapter that is up (loopback and
        /// link-local 169.254.x excluded), same-network-as-console first.
        /// </summary>
        public static List<LocalAddress> ListLocalAddresses(string? ConsoleIP)
        {
            IPAddress.TryParse(ConsoleIP ?? "", out var Console);
            var Result = new List<LocalAddress>();

            try
            {
                foreach (var Interface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (Interface.OperationalStatus != OperationalStatus.Up || Interface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    foreach (var Info in Interface.GetIPProperties().UnicastAddresses)
                    {
                        if (Info.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                            continue;

                        var Text = Info.Address.ToString();
                        if (Text.StartsWith("169.254.") || Result.Any(x => x.Address == Text))
                            continue;

                        bool Same = Console != null && Info.IPv4Mask != null && SameSubnet(Info.Address, Console, Info.IPv4Mask);
                        Result.Add(new LocalAddress(Text, Interface.Name, Same));
                    }
                }
            }
            catch
            {
            }

            return Result.OrderByDescending(x => x.SameNetworkAsConsole).ThenBy(x => x.Adapter).ToList();
        }

        static bool SameSubnet(IPAddress A, IPAddress B, IPAddress Mask)
        {
            var a = A.GetAddressBytes();
            var b = B.GetAddressBytes();
            var m = Mask.GetAddressBytes();
            for (int i = 0; i < 4; i++)
            {
                if ((a[i] & m[i]) != (b[i] & m[i]))
                    return false;
            }
            return true;
        }

        /// <summary>Local address the OS would use to reach Remote (no packet is sent).</summary>
        static string? RoutedLocalIP(IPAddress Remote)
        {
            try
            {
                using var Probe = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
                Probe.Connect(new IPEndPoint(Remote, 9));
                return (Probe.LocalEndPoint as IPEndPoint)?.Address.ToString();
            }
            catch
            {
                return null;
            }
        }

        static string[] SearchInterfaces()
        {
            List<string> IPs = new List<string>();
            foreach (NetworkInterface Interface in NetworkInterface.GetAllNetworkInterfaces().Where(x => x.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || x.NetworkInterfaceType == NetworkInterfaceType.Ethernet))
            {
                try
                {
                    foreach (UnicastIPAddressInformation IpInfo in Interface.GetIPProperties().UnicastAddresses.Where(x => x.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
                    {
                        try
                        {
                            if (Interface.OperationalStatus != OperationalStatus.Up)
                                continue;

                            if (!IPs.Contains(IpInfo.Address.ToString()))
                                IPs.Add(IpInfo.Address.ToString());
                        }
                        catch { }
                    }
                }
                catch { }
            }

            return IPs.ToArray();
        }

        static HttpClient Client = Services.NetConnect.ConsoleHttp(TimeSpan.FromMilliseconds(1000));
        
        
        public static async Task<bool> IsGoldHENOnline(string IP)
        {
            if (!IPAddress.TryParse(IP, out _))
                return false;
            
            var APIUrl = $"http://{IP}:9090/status";
            try
            {
                using var Response = await Client.GetAsync(APIUrl);
                var Resp = await Response.Content.ReadAsStringAsync();
                if (Resp.Replace(" ", "").Contains("\"status\":\"ready\""))
                    return true;
            }
            catch  { }
            return false;
        } 

        public static async Task<bool> IsRPIOnline(string IP)
        {
            if (!IPAddress.TryParse(IP, out _))
                return false;
            
            var APIUrl = $"http://{IP}:12800/api";
            try
            {
                using var Response = await Client.GetAsync(APIUrl);
                var Resp = await Response.Content.ReadAsStringAsync();
                if (Resp.Contains("Unsupported method") && Resp.Contains("fail"))
                    return true;
            }
            catch  { }
            return false;
        }

        public static async Task<bool> IsEtaHenOnline(string IP)
        {
            if (!IPAddress.TryParse(IP, out _))
                return false;

            var APIUrl = $"http://{IP}:12800/";
            try
            {
                using var Response = await Client.GetAsync(APIUrl);
                var Resp = await Response.Content.ReadAsStringAsync();
                if (Resp.Contains("etaHEN"))
                    return true;
            }
            catch { }
            return false;
        }
    }
}
