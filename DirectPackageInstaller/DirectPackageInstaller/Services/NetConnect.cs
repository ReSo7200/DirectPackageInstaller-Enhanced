using System;
using System.Net;
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
            var Connect = Task.Run(() => Socket.Connect(Endpoint));
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
        }
    }
}
