using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DirectPackageInstaller.Services
{
    /// <summary>
    /// Single-instance forwarding over a named pipe (per user). The first UI
    /// instance owns a named mutex and listens on the pipe; a later launch
    /// (e.g. Explorer's "Send to PS4") hands its arguments over and exits.
    /// </summary>
    public static class SingleInstance
    {
        static string Suffix => string.Concat(Environment.UserName.Select(c => char.IsLetterOrDigit(c) ? c : '_'));

        /// <summary>Pipe / mutex name; tests may override before calling anything else.</summary>
        // DPI_INSTANCE: a separate instance (testing a build next to the one in use)
        public static string Name { get; set; } = "DPIEnhanced-" + (Environment.GetEnvironmentVariable("DPI_INSTANCE") ?? Suffix);

        static string MutexName => "Local\\" + Name;

        static readonly object Sync = new();
        static readonly List<string[]> Pending = new();
        static Action<string[]>? Handlers;
        static Mutex? Owner;
        static Thread? Listener;

        /// <summary>
        /// Arguments forwarded from another launch (or queued at startup).
        /// Raised on a background thread: marshal to the UI thread. Arguments
        /// that arrived before anyone subscribed are delivered to the first
        /// subscriber synchronously, on the subscribing thread, when it subscribes.
        /// </summary>
        public static event Action<string[]> ArgumentsReceived
        {
            add
            {
                string[][] Backlog;
                lock (Sync)
                {
                    Handlers += value;
                    Backlog = Pending.ToArray();
                    Pending.Clear();
                }

                foreach (var Args in Backlog)
                    Invoke(value, Args);
            }
            remove
            {
                lock (Sync)
                    Handlers -= value;
            }
        }

        /// <summary>Queue arguments this process must handle itself (e.g. its own --send at startup).</summary>
        public static void Post(string[] Args)
        {
            Action<string[]>? Target;
            lock (Sync)
            {
                Target = Handlers;
                if (Target == null)
                {
                    Pending.Add(Args);
                    return;
                }
            }

            Invoke(Target, Args);
        }

        static void Invoke(Action<string[]> Target, string[] Args)
        {
            foreach (Action<string[]> Handler in Target.GetInvocationList())
            {
                try { Handler(Args); }
                catch { }
            }
        }

        /// <summary>True if another instance currently owns the pipe.</summary>
        public static bool OtherInstanceRunning()
        {
            if (Owner != null)
                return false;

            try
            {
                if (Mutex.TryOpenExisting(MutexName, out var Existing))
                {
                    Existing.Dispose();
                    return true;
                }
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
            catch
            {
            }

            return false;
        }

        /// <summary>
        /// Send <paramref name="Args"/> to the running instance. Returns false
        /// right away when none runs, or when it did not acknowledge in time.
        /// </summary>
        public static bool TryForward(string[] Args, int TimeoutMs = 5000)
        {
            if (!OtherInstanceRunning())
                return false;

            try
            {
                using var Client = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
                // the owner may have just started and not be listening yet: Connect waits
                Client.Connect(TimeoutMs);

                using (var Writer = new BinaryWriter(Client, Encoding.UTF8, true))
                {
                    Writer.Write(Args.Length);
                    foreach (var Arg in Args)
                        Writer.Write(Arg ?? "");
                    Writer.Flush();
                }

                using var Cts = new CancellationTokenSource(TimeoutMs);
                var Ack = new byte[1];
                int Read = Client.ReadAsync(Ack, 0, 1, Cts.Token).GetAwaiter().GetResult();
                return Read == 1 && Ack[0] == 1;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Become the primary instance and start listening. Returns false when
        /// another instance already is (nothing is started then). Safe to call twice.
        /// </summary>
        public static bool StartServer()
        {
            lock (Sync)
            {
                if (Owner != null)
                    return true;

                Mutex Created;
                try
                {
                    Created = new Mutex(false, MutexName, out bool CreatedNew);
                    if (!CreatedNew)
                    {
                        Created.Dispose();
                        return false;
                    }
                }
                catch
                {
                    return false;
                }

                Owner = Created;
                Listener = new Thread(Listen) { IsBackground = true, Name = "SingleInstance pipe" };
                Listener.Start();
                return true;
            }
        }

        static void Listen()
        {
            while (true)
            {
                try
                {
                    using var Server = new NamedPipeServerStream(Name, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    Server.WaitForConnection();

                    // a client that connects and never writes must not block later ones
                    using var Stuck = new Timer(_ => { try { Server.Disconnect(); } catch { } }, null, 5000, Timeout.Infinite);

                    string[] Args;
                    using (var Reader = new BinaryReader(Server, Encoding.UTF8, true))
                    {
                        int Count = Reader.ReadInt32();
                        if (Count < 0 || Count > 4096)
                            continue;

                        Args = new string[Count];
                        for (int i = 0; i < Count; i++)
                            Args[i] = Reader.ReadString();
                    }

                    Server.WriteByte(1);
                    Server.Flush();
                    if (OperatingSystem.IsWindows())
                        try { Server.WaitForPipeDrain(); } catch { }

                    // in arrival order (keeps multi-select order); handlers must just marshal to the UI
                    Post(Args);
                }
                catch
                {
                    // broken client: keep serving
                    Thread.Sleep(50);
                }
            }
        }
    }
}
