using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace DirectPackageInstaller.Services
{
    /// <summary>
    /// TCP connect with a cancellation token. On Android the token overloads of
    /// Socket/TcpClient.ConnectAsync throw InvalidOperationException from inside the
    /// runtime (ManualResetValueTaskSourceCore.GetStatus), so there the connect runs
    /// blocking on a worker thread and a cancel closes the socket.
    /// </summary>
    public static class NetConnect
    {
        /// <summary>
        /// HttpClient for talking to the console (RPI, etaHEN, GoldHEN). The default on
        /// Android is the Java HTTP stack, whose requests differ from the desktop's and
        /// crashed Remote Package Installer; this sends the same requests everywhere.
        /// </summary>
        public static HttpClient ConsoleHttp(TimeSpan Timeout) => new(ConsoleHandler()) { Timeout = Timeout };

        public static HttpMessageHandler ConsoleHandler() => new SocketsHttpHandler
        {
            ConnectCallback = async (Context, Token) =>
            {
                var Address = IPAddress.TryParse(Context.DnsEndPoint.Host, out var Parsed) ? Parsed
                    : (await Dns.GetHostAddressesAsync(Context.DnsEndPoint.Host, Token).ConfigureAwait(false))[0];
                var Socket = new Socket(Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await ConnectAsync(Socket, new IPEndPoint(Address, Context.DnsEndPoint.Port), Token).ConfigureAwait(false);
                    Socket.NoDelay = true;
                    return new NetworkStream(Socket, ownsSocket: true);
                }
                catch
                {
                    Socket.Dispose();
                    throw;
                }
            }
        };

        public static Task ConnectAsync(TcpClient Client, IPAddress Address, int Port, CancellationToken Token) =>
            ConnectAsync(Client.Client, new IPEndPoint(Address, Port), Token);

        public static async Task ConnectAsync(Socket Socket, EndPoint Endpoint, CancellationToken Token)
        {
            if (!OperatingSystem.IsAndroid())
            {
                await Socket.ConnectAsync(Endpoint, Token).ConfigureAwait(false);
                return;
            }

            Token.ThrowIfCancellationRequested();
            // a thread of its own: blocking connects on pool threads starve everything else
            var Connect = Task.Factory.StartNew(() => Socket.Connect(Endpoint), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            using (Token.Register(() => { try { Socket.Close(); } catch { } }))
            {
                try
                {
                    await Connect.ConfigureAwait(false);
                }
                catch (Exception) when (Token.IsCancellationRequested)
                {
                    throw new OperationCanceledException(Token);
                }
            }
            // cancelled just after connecting: the socket was closed under the caller
            if (Token.IsCancellationRequested || !Socket.Connected)
                throw new OperationCanceledException(Token);
        }
    }
}
