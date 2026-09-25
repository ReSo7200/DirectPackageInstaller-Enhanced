using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace DirectPackageInstaller.Services
{
    /// <summary>
    /// Sends a payload file (.bin / .elf) chosen by the user to GoldHEN's BinLoader, the
    /// way separate "payload sender" tools do. The payload runs on the console as soon
    /// as it arrives, so this is only done on an explicit request.
    /// </summary>
    public static class PayloadSender
    {
        public const int BinLoaderPort = 9090;
        public const long MaxSize = 64L * 1024 * 1024;

        public static async Task SendAsync(string ConsoleIP, string File, CancellationToken Token = default)
        {
            var Info = new FileInfo(File);
            if (!Info.Exists)
                throw new FileNotFoundException("The payload file isn't there any more.", File);
            if (Info.Length == 0 || Info.Length > MaxSize)
                throw new InvalidDataException("That doesn't look like a payload (empty, or bigger than 64 MB).");

            var Data = await System.IO.File.ReadAllBytesAsync(File, Token);
            using var Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using (var Timeout = CancellationTokenSource.CreateLinkedTokenSource(Token))
            {
                Timeout.CancelAfter(5000);
                try
                {
                    await NetConnect.ConnectAsync(Socket, new IPEndPoint(IPAddress.Parse(ConsoleIP), BinLoaderPort), Timeout.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException)
                {
                    throw new IOException("GoldHEN's BinLoader isn't answering on port 9090. Turn it on in GoldHEN's settings.", ex);
                }
            }

            int Sent = 0;
            while (Sent < Data.Length)
                Sent += await Socket.SendAsync(new ArraySegment<byte>(Data, Sent, Data.Length - Sent), SocketFlags.None, Token);
            Socket.Shutdown(SocketShutdown.Send);
        }
    }
}
