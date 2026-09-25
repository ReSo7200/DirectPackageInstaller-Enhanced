using DirectPackageInstaller.Views;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DirectPackageInstaller.Tasks;

namespace DirectPackageInstaller.Host
{
    /// <summary>
    /// GoldHEN install path. The PS4 payload (Payload/main.c) is resident: every
    /// loop iteration it opens a NEW connection to ServiceSocket, reads one
    /// package (cmd 1) or an exit request (cmd 0), then closes it. If it can't
    /// connect it shows "DPI: GET INFO ERROR" and exits. So ServiceSocket must
    /// stay open for the whole session, each queued connection is used exactly
    /// once, and the payload is injected only when no connection is waiting.
    /// </summary>
    public class PayloadService
    {

        ~PayloadService()
        {
            StopServer().ConfigureAwait(false).GetAwaiter().GetResult();
        }

        private Socket? ServiceSocket = null;
        private Socket? PayloadSocket;

        /// <summary>A resident payload is waiting for a package.</summary>
        public bool ClientRunning => !Queue.IsEmpty;

        private bool ServerRunning = false;

        private readonly ConcurrentQueue<Socket> Queue = new ConcurrentQueue<Socket>();

        private readonly SemaphoreSlim SendLock = new SemaphoreSlim(1, 1);

        /// <summary>How long a freshly injected payload gets to connect back.</summary>
        const int CallbackTimeoutMs = 15000;

        // Payload/info.c buffer sizes (the payload rejects longer strings).
        const int MaxUrl = 0x800, MaxName = 0x259, MaxId = 0x30, MaxType = 0x10;

        public async Task<bool> SendPKGPayload(string PS4IP, string PCIP, string URL, bool Silent, bool AutoSplit)
        {
            if (Installer.Server == null)
                return false;

            URL = Installer.Server.RegisterJSON(URL, PCIP, Installer.CurrentPKG, AutoSplit);

            byte[] PKGInfo;
            try
            {
                PKGInfo = BuildPkgInfo(URL);
                // experimental payload + a chosen storage: "package v2" carries it
                if (App.Config.ExperimentalPayload && App.Config.InstallStorage != ExperimentalPayloadProtocol.StorageDefault)
                    PKGInfoV2 = ExperimentalPayloadProtocol.BuildPackageV2(URL, Installer.CurrentPKG.FriendlyName, Installer.CurrentPKG.ContentID,
                        Installer.CurrentPKG.BGFTContentType, Installer.CurrentPKG.PackageSize, Installer.CurrentPKG.IconData, App.Config.InstallStorage);
                else
                    PKGInfoV2 = null;
            }
            catch (ArgumentException ex)
            {
                Installer.LastError = ex.Message;
                if (!Silent)
                    await MessageBox.ShowAsync("Failed:\n" + ex.Message, "DirectPackageInstaller", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            await SendLock.WaitAsync();
            try
            {
                // a dead queued connection fails the send: try the next one / inject
                for (int Attempt = 0; Attempt < 3; Attempt++)
                {
                    var Connection = await GetConnectionAsync(PS4IP, PCIP);
                    if (Connection == null)
                        return false;

                    // only the experimental payload understands v2
                    var Data = ResidentExperimental && PKGInfoV2 != null ? PKGInfoV2 : PKGInfo;
                    if (await TrySend(Connection, Data))
                        return await Sent(Silent);
                }

                Installer.LastError = "The PS4 payload connection kept dropping. Try again.";
                return false;
            }
            finally
            {
                SendLock.Release();
            }
        }

        byte[]? PKGInfoV2;

        /// <summary>The payload running on the console is the experimental build (commands 2 and 3).</summary>
        public bool ResidentExperimental { get; private set; }

        /// <summary>
        /// A connection from the payload on the console: a waiting resident one, or
        /// inject the payload and wait for it to call back. Sets LastError on failure.
        /// </summary>
        async Task<Socket?> GetConnectionAsync(string PS4IP, string PCIP)
        {
            if (!EnsureListener(PCIP))
            {
                Installer.LastError = "Couldn't open the port the PS4 payload connects back to.";
                return null;
            }

            // 1) a resident payload is already waiting
            var Connection = TakeLiveConnection();
            if (Connection != null)
                return Connection;

            // 2) none (or it died): inject once, wait for its callback
            if (!await TryConnectSocket(PS4IP))
            {
                Installer.LastError = "GoldHEN's payload server isn't answering (ports 9090/9021/9020). Turn on BinLoader in GoldHEN settings, or open Remote Package Installer.";
                return null;
            }

            bool Experimental = App.Config.ExperimentalPayload;
            if (!InjectPayload(PCIP, Experimental))
            {
                Installer.LastError = "Sending the installer payload to GoldHEN failed. Try again.";
                return null;
            }
            ResidentExperimental = Experimental;

            DateTime WaitBegin = DateTime.Now;
            while ((DateTime.Now - WaitBegin).TotalMilliseconds < CallbackTimeoutMs)
            {
                Connection = TakeLiveConnection();
                if (Connection != null)
                    return Connection;
                await Task.Delay(100);
            }

            Installer.LastError = "The PS4 payload didn't connect back to this PC. Check the PC address in Settings and that Windows Firewall allows DirectPackageInstaller.";
            return null;
        }

        /// <summary>
        /// Free space on system and extended storage, from the experimental payload
        /// (injected if needed). Null when it can't be read; see Installer.LastError.
        /// </summary>
        public async Task<PayloadFreeSpace?> QueryFreeSpaceAsync(string PS4IP, string PCIP)
        {
            if (!App.Config.ExperimentalPayload)
                return null;

            await SendLock.WaitAsync();
            try
            {
                // a resident default payload doesn't know command 3: replace it
                if (!ResidentExperimental)
                    await ReleaseResidentAsync();

                for (int Attempt = 0; Attempt < 3; Attempt++)
                {
                    var Connection = await GetConnectionAsync(PS4IP, PCIP);
                    if (Connection == null)
                        return null;
                    if (!ResidentExperimental)
                        return null;

                    try
                    {
                        var Query = ExperimentalPayloadProtocol.BuildFreeSpaceQuery();
                        await Connection.SendAsync(new ArraySegment<byte>(Query), SocketFlags.None);

                        var Reply = new byte[32];
                        int Got = 0;
                        using var Timeout = new CancellationTokenSource(5000);
                        while (Got < Reply.Length)
                        {
                            int Count = await Connection.ReceiveAsync(new ArraySegment<byte>(Reply, Got, Reply.Length - Got), SocketFlags.None, Timeout.Token);
                            if (Count <= 0)
                                break;
                            Got += Count;
                        }

                        if (Got == Reply.Length)
                            return ExperimentalPayloadProtocol.ParseFreeSpaceReply(Reply);
                    }
                    catch
                    {
                    }
                    finally
                    {
                        Connection.Close();
                    }
                }

                Installer.LastError = "The payload didn't report free space.";
                return null;
            }
            finally
            {
                SendLock.Release();
            }
        }

        /// <summary>Tell waiting payloads to exit (cmd 0) so the next push injects a fresh one.</summary>
        public async Task ReleaseResidentAsync()
        {
            while (TakeLiveConnection() is { } Connection)
            {
                try { await Connection.SendAsync(new ArraySegment<byte>(new byte[4]), SocketFlags.None); } catch { }
                Connection.Close();
            }
            ResidentExperimental = false;
        }

        private static async Task<bool> Sent(bool Silent)
        {
            if (!Silent)
                await MessageBox.ShowAsync("Package Sent!", "DirectPackageInstaller", MessageBoxButtons.OK, MessageBoxIcon.Information);

            return true;
        }

        /// <summary>
        /// Wire format read by Payload/info.c get_pkg_info:
        /// u32 cmd(1), [u32 len + bytes] URL, Name, ID, Type, u64 size, [u32 len + bytes] icon.
        /// </summary>
        private static byte[] BuildPkgInfo(string URL)
        {
            var UrlData = Encoding.UTF8.GetBytes(URL);
            var NameData = Encoding.UTF8.GetBytes(Installer.CurrentPKG.FriendlyName ?? "");
            var IDData = Encoding.UTF8.GetBytes(Installer.CurrentPKG.ContentID ?? "");
            var PKGType = Encoding.UTF8.GetBytes(Installer.CurrentPKG.BGFTContentType);
            var PackageSize = BitConverter.GetBytes(Installer.CurrentPKG.PackageSize);
            var IconData = Installer.CurrentPKG.IconData ?? new byte[0];

            // The payload drops fields that don't fit its buffers (plus NUL),
            // shows GET INFO ERROR and exits; fail here instead.
            if (UrlData.Length >= MaxUrl)
                throw new ArgumentException($"Package URL is too long for the PS4 payload ({UrlData.Length} bytes, max {MaxUrl - 1}).");
            if (IDData.Length >= MaxId)
                throw new ArgumentException("Content ID is too long for the PS4 payload.");
            if (PKGType.Length >= MaxType)
                throw new ArgumentException("Package type is too long for the PS4 payload.");
            if (NameData.Length >= MaxName)
                NameData = TruncateUtf8(Installer.CurrentPKG.FriendlyName!, MaxName - 1);

            List<byte> PKGInfoBuffer = new List<byte>();

            //1 = New Package, 0 = Service Exit
            PKGInfoBuffer.AddRange(BitConverter.GetBytes(1u));

            PKGInfoBuffer.AddRange(BitConverter.GetBytes(UrlData.Length));
            PKGInfoBuffer.AddRange(UrlData);
            PKGInfoBuffer.AddRange(BitConverter.GetBytes(NameData.Length));
            PKGInfoBuffer.AddRange(NameData);
            PKGInfoBuffer.AddRange(BitConverter.GetBytes(IDData.Length));
            PKGInfoBuffer.AddRange(IDData);
            PKGInfoBuffer.AddRange(BitConverter.GetBytes(PKGType.Length));
            PKGInfoBuffer.AddRange(PKGType);

            PKGInfoBuffer.AddRange(PackageSize);

            PKGInfoBuffer.AddRange(BitConverter.GetBytes(IconData.Length));
            PKGInfoBuffer.AddRange(IconData);

            return PKGInfoBuffer.ToArray();
        }

        private static byte[] TruncateUtf8(string Value, int MaxBytes)
        {
            var Result = Encoding.UTF8.GetBytes(Value);
            while (Result.Length > MaxBytes && Value.Length > 0)
            {
                Value = Value.Substring(0, Value.Length - 1);
                Result = Encoding.UTF8.GetBytes(Value);
            }
            return Result;
        }

        /// <summary>
        /// Send one package over a payload connection, then close it: the payload
        /// reads a single package per connection and reconnects for the next.
        /// </summary>
        private static async Task<bool> TrySend(Socket Connection, byte[] Data)
        {
            try
            {
                int Sent = 0;
                while (Sent < Data.Length)
                {
                    int Count = await Connection.SendAsync(new ArraySegment<byte>(Data, Sent, Data.Length - Sent), SocketFlags.None);
                    if (Count <= 0)
                        return false;
                    Sent += Count;
                }

                try { Connection.Shutdown(SocketShutdown.Send); } catch { }
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                Connection.Close();
            }
        }

        /// <summary>Oldest queued payload connection that is still open, or null.</summary>
        private Socket? TakeLiveConnection()
        {
            while (Queue.TryDequeue(out var Connection))
            {
                bool Alive;
                try
                {
                    // readable with nothing to read = the payload side closed it
                    Alive = !(Connection.Poll(0, SelectMode.SelectRead) && Connection.Available == 0);
                }
                catch
                {
                    Alive = false;
                }

                if (Alive)
                    return Connection;

                Connection.Close();
            }

            return null;
        }

        /// <summary>Payload/payload_experimental.bin, embedded at build time (a fresh copy to patch).</summary>
        static byte[] ExperimentalPayloadBytes()
        {
            using var Stream = typeof(PayloadService).Assembly.GetManifestResourceStream("payload_experimental.bin");
            if (Stream == null)
                return Array.Empty<byte>();
            using var Buffer = new System.IO.MemoryStream();
            Stream.CopyTo(Buffer);
            return Buffer.ToArray();
        }

        public async Task StopServer()
        {
            if (!ServerRunning)
                return;

            ServerRunning = false;

            try { ServiceSocket?.Close(); } catch { }

            while (ServiceSocket != null)
                await Task.Delay(100);

            // cmd 0: the payload shows "DirectPackageInstaller Exited" and quits cleanly
            while (Queue.TryDequeue(out var Connection))
            {
                try
                {
                    await Connection.SendAsync(new ArraySegment<byte>(new byte[4]), SocketFlags.None);
                    Connection.Shutdown(SocketShutdown.Both);
                }
                catch { }

                Connection.Close();
            }
        }

        /// <summary>
        /// Tries to connect the PayloadSocket at the GoldHEN/MiraLoader payload port
        /// </summary>
        /// <param name="IP">The PS4 IP</param>
        /// <param name="Retry">For internal usage, don't set this parameter</param>
        /// <returns>When true the PayloadSocket holds a valid connection</returns>
        public async Task<bool> TryConnectSocket(string IP, bool Retry = true)
        {
            int[] Ports = new int[] { 9090, 9021, 9020 };
            foreach (var Port in Ports)
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.ReceiveTimeout = 3000;
                socket.SendTimeout = 3000;
                socket.NoDelay = true;

                using CancellationTokenSource CToken = new CancellationTokenSource();
                CToken.CancelAfter(3000);

                try
                {
                    var Endpoint = new IPEndPoint(IPAddress.Parse(IP), Port);

                    if (App.IsAndroid)
                        socket.Connect(Endpoint);
                    else
                        await socket.ConnectAsync(Endpoint, CToken.Token);

                    if (socket.Connected)
                    {
                        PayloadSocket = socket;
                        return true;
                    }
                }
                catch (Exception ex)
                {
#if DEBUG
                    await MessageBox.ShowAsync("try conn err \n" + ex.ToString());
#endif
                }

                socket.Dispose();
            }

            if (Retry)
            {
                await Task.Delay(3000);
                return await TryConnectSocket(IP, false);
            }

            return false;
        }

        /// <summary>
        /// Ensure the PKG Info Server is listening. It stays open for the whole
        /// session so a resident payload can always reconnect.
        /// </summary>
        private bool EnsureListener(string PCIP)
        {
            if (ServerRunning && ServiceSocket != null)
                return true;

            try
            {
                var Listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                Listener.NoDelay = true;

                if (App.IsAndroid)
                    Listener.Bind(new IPEndPoint(IPAddress.Parse(PCIP), App.Config.PayloadPort ?? 0));
                else
                    Listener.Bind(new IPEndPoint(IPAddress.Any, App.Config.PayloadPort ?? 0));

                Listener.Listen();

                ServiceSocket = Listener;
                ServerRunning = true;

                _ = ServerLoop(Listener);
                return true;
            }
            catch (Exception ex)
            {
#if DEBUG
                MessageBox.ShowSync("EnsureListener Failed\n" + ex.ToString());
#endif
                return false;
            }
        }

        /// <summary>
        /// Runs the PKG Info Socket connection accept loop.
        /// </summary>
        private async Task ServerLoop(Socket Listener)
        {
            while (ServerRunning)
            {
                Socket ClientSocket;
                try
                {
                    // AcceptAsync is cancelled by closing the listener (StopServer);
                    // unlike Thread.Interrupt it really unblocks, also on Android.
                    ClientSocket = await Listener.AcceptAsync();
                }
                catch
                {
                    break;
                }

                try
                {
                    ClientSocket.NoDelay = true;
                    // detect a payload lost to rest mode / reboot
                    ClientSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                }
                catch { }

                Queue.Enqueue(ClientSocket);
            }

            try { Listener.Close(); } catch { }

            if (ServiceSocket == Listener)
            {
                ServiceSocket = null;
                ServerRunning = false;
            }
        }

        /// <summary>
        /// Send the installer payload to the GoldHEN/MiraLoader bin loader,
        /// patched with this PC's IP and the listener port.
        /// </summary>
        /// <param name="PCIP">The PC IP</param>
        /// <returns>True when the payload was sent</returns>
        private bool InjectPayload(string PCIP, bool Experimental = false)
        {
            if (ServiceSocket == null || PayloadSocket == null)
                return false;

            // patch a copy: never mutate the shared resource bytes
            var Payload = Experimental ? ExperimentalPayloadBytes() : (byte[])Resources.Payload.Clone();
            if (Payload.Length == 0)
                return false;

            var Offset = Payload.IndexOf(new byte[] { 0xB4, 0xB4, 0xB4, 0xB4, 0xB4, 0xB4 });
            if (Offset == -1)
                return false;

            try
            {
                ushort LocalPort = (ushort)((IPEndPoint)ServiceSocket.LocalEndPoint!).Port;

                var IP = IPAddress.Parse(PCIP).GetAddressBytes();
                var Port = BitConverter.GetBytes(LocalPort).Reverse().ToArray();

                IP.CopyTo(Payload, Offset);
                Port.CopyTo(Payload, Offset + 4);

                PayloadSocket.SendBufferSize = Payload.Length;

                int Sent = 0;
                while (Sent < Payload.Length)
                {
                    int Count = PayloadSocket.Send(Payload, Sent, Payload.Length - Sent, SocketFlags.None);
                    if (Count <= 0)
                        return false;
                    Sent += Count;
                }
            }
            catch
            {
                return false;
            }
            finally
            {
                try { PayloadSocket.Shutdown(SocketShutdown.Both); } catch { }
                PayloadSocket.Close();
                PayloadSocket = null;
            }

            return true;
        }
    }
}
