using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using DirectPackageInstaller.Host;
using DirectPackageInstaller.IO;
using DirectPackageInstaller.Tasks;

namespace DirectPackageInstaller.Desktop
{
    class Program
    {
        const uint SW_SHOW = 5;

        static IntPtr hConsole = IntPtr.Zero;

        [DllImport("kernel32.dll")]
        static extern bool AllocConsole();

        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, uint nCmdShow);


        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            bool UILaunch = args == null || args.Length == 0;

            if (!UILaunch)
            {
                UILaunch = args!.Where(x => x
                    .Trim(' ', '-', '\\', '/')
                    .Equals("ui", StringComparison.InvariantCultureIgnoreCase)
                ).Any();
            }

            if (UILaunch)
            {
                TempHelper.Clear();
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); 
                TempHelper.Clear();

                Environment.Exit(0);
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                AllocConsole();
                Console.OutputEncoding = Encoding.Unicode;
                hConsole = GetConsoleWindow();
                ShowWindow(hConsole, SW_SHOW);
            }

            Console.Title = "DirectPacakgeInstaller";
            Console.WriteLine($"DirectPacakgeInstaller v{SelfUpdate.CurrentVersion} - CLI");

            bool Proxy = false;
            bool ShowProgress = false;
            string Server = null;
            string PSIP = null;
            string URL = null;
            int Port = 0;
            int DPIPort = 0;

            for (int i = 0; i < args.Length; i++)
            {
                string Arg = args[i].Trim(' ', '-', '/', '\\').ToLowerInvariant();
                switch (Arg)
                {
                    case "dpiport":
                        if (i + 1 >= args.Length)
                            goto case "help";
                        int.TryParse(args[++i], out DPIPort);
                        break;
                    case "lan":
                    case "server":
                    case "serverip":
                    case "lanip":
                        if (i + 1 >= args.Length)
                            goto case "help";
                        
                        Server = args[++i];
                        break;
                    case "ps4":
                    case "ps4ip":
                        if (i + 1 >= args.Length)
                            goto case "help";
                        PSIP = args[++i];
                        break;
                    case "port":
                    case "binloader":
                        if (i + 1 >= args.Length)
                            goto case "help";
                        int.TryParse(args[++i], out Port);
                        break;
                    case "proxy":
                        Proxy = true;
                        break;
                    case "progress":
                        ShowProgress = true;
                        break;
                    case "selftest":
                        if (i + 1 >= args.Length)
                            goto case "help";
                        RunSelfTest(args[++i]);
                        Console.ReadKey();
                        return;
                    case "help":
                    case "h":
                    case "?":
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("DirectPackageInstaller – Basic Usage");
                        Console.ResetColor();
                        Console.WriteLine();

                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("Syntax:");
                        Console.ResetColor();
                        Console.WriteLine("  DirectPackageInstaller.Desktop -Server PKG_SENDER_PC_IP -PS4 PS4_IP -Port BIN_LOADER_PORT -DPIPort PKG_INFO_PORT [-Proxy] [-Progress] URL");
                        Console.WriteLine();

                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("Parameters:");
                        Console.ResetColor();
                        Console.WriteLine();

                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine("  PKG_SENDER_PC_IP");
                        Console.ResetColor();
                        Console.WriteLine("    The IP of the machine running DirectPackageInstaller.");
                        Console.WriteLine("    Automatically detected (optional), but not reliable if multiple network connections exist.");
                        Console.WriteLine();

                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine("  PS4_IP");
                        Console.ResetColor();
                        Console.WriteLine("    The IP address of your PS4.");
                        Console.WriteLine("    Supplying it makes the process faster. Optional, but autodetection may take time.");
                        Console.WriteLine();

                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine("  BIN_LOADER_PORT");
                        Console.ResetColor();
                        Console.WriteLine("    The port where the PS4 bin loader is running.");
                        Console.WriteLine("    By default, ports 9090, 9021, and 9020 are tried. Usually no need to specify.");
                        Console.WriteLine();

                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine("  PKG_INFO_PORT");
                        Console.ResetColor();
                        Console.WriteLine("    The port DPI listens on so the PS4 can send PKG metadata.");
                        Console.WriteLine("    By default, it is assigned by the OS.");
                        Console.WriteLine("    Can be saved in Settings.ini or overridden via this parameter.");
                        Console.WriteLine();

                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine("  -Proxy (optional)");
                        Console.ResetColor();
                        Console.WriteLine("    Makes the PS4 download the PKG via DirectPackageInstaller as a proxy.");
                        Console.WriteLine();

                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine("  -Progress (optional)");
                        Console.ResetColor();
                        Console.WriteLine("    Shows how much data the PS4 is reading from DirectPackageInstaller.");
                        Console.WriteLine();

                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine("  URL");
                        Console.ResetColor();
                        Console.WriteLine("    The PKG file location.");
                        Console.WriteLine("    Must be a direct .pkg file (RAR/7Z not supported yet).");
                        Console.WriteLine("    You may also use an absolute file path.");
                        Console.WriteLine("    TXT File list is also accepted, just ensure the file extension to be .txt");
                        Console.WriteLine();

                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("Example:");
                        Console.ResetColor();

                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("  DirectPackageInstaller.Desktop -Server 192.168.1.10 -PS4 192.168.1.20 -Port 9090 -DPIPort 8080 -Proxy http://example.com/game.pkg");
                        Console.ResetColor();
                        Console.WriteLine();

                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine("DirectPackageInstaller - By Marcussacana");
                        Console.ResetColor();

                        Console.ReadKey();
                        return;
                    default:
                        if (!Arg.StartsWith("http") && !File.Exists(args[i]))
                            goto case "help";
                        URL = args[i];
                        break;
                }
            }

            if (Server == null)
            {
                if (PSIP != null)
                {
                    Server = IPHelper.FindLocalIP(PSIP);
                }
            }

            if (PSIP == null)
            {
                _ = PS4Finder.StartFinder((NewPSIP, PCIP, VERSION) =>
                {
                    PSIP = NewPSIP.ToString();
                    
                    if (Server != null || PCIP == null)
                        return;
                    
                    Server = PCIP.ToString();
                });
                
                Console.WriteLine("Searching the PS4...");
                
                while (PSIP == null)
                {
                    Task.Delay(1000).ConfigureAwait(false).GetAwaiter().GetResult();
                }
            }


            if (Server == null)
            {
                Console.WriteLine("Failed to Detect your PC IP.");
                return;
            }
            
            Console.WriteLine($"LAN: {Server}");

            if (PSIP == null)
            {
                Console.WriteLine("Failed to Detect your PS4 IP");
                return;
            }
            
            Console.WriteLine($"PS4: {PSIP}");
            
            if (URL == null)
            {
                Console.WriteLine("Missing PKG URL");
                return;
            }
            
            if (Port != 0)
                Console.WriteLine($"Port: {Port}");

            PS4Server PSServer = new PS4Server(Server);
            DateTime LastProgressUpdate = DateTime.MinValue;
            if (ShowProgress)
            {
                PSServer.TransferProgressChanged += Info =>
                {
                    var Now = DateTime.Now;
                    if (!Info.Completed && (Now - LastProgressUpdate).TotalMilliseconds < 500)
                        return;

                    LastProgressUpdate = Now;
                    var Sent = TransferProgressInfo.FormatBytes(Info.BytesSent);
                    var Total = TransferProgressInfo.FormatBytes(Info.TotalBytes);
                    var Speed = TransferProgressInfo.FormatBytes(Info.BytesPerSecond);

                    if (Info.Completed)
                        Console.WriteLine($"\rSent {Sent} / {Total}                    ");
                    else
                        Console.Write($"\rSending {Sent} / {Total} ({Info.Percent:P1}) - {Speed}/s        ");
                };
            }
            
            try
            {
                PSServer.Start();
            }
            catch
            {
                Console.WriteLine("ERROR: Another Instance of the DPI is Running?");
                throw;
            }

            bool FileInput = !URL.StartsWith("http") && File.Exists(URL);

            bool ListInput = FileInput && Path.GetExtension(URL).Equals(".txt", StringComparison.InvariantCultureIgnoreCase);


            string[] Entries = new[] { URL };

            if (ListInput)
            {
                Entries = File.ReadAllLines(URL).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            }

            for (var i = 0; i < Entries.Length; i++) {

                var DirectURL = Entries[i];

                FileInput = !URL.StartsWith("http") && File.Exists(URL);

                if (Entries.Length == 1)
                {
                    Console.WriteLine($"Source: {DirectURL}");
                }
                else
                {
                    Console.WriteLine($"Source: {DirectURL} ({i}/{Entries.Length})");
                }


                Stream PKG = null;
                try
                {
                    DownloaderTask? DownTask = null;

                    bool LimitedFHost = false;

                    if (FileInput)
                    {
                        Proxy = true;
                        PKG = new FileStream(DirectURL, FileMode.Open);
                        URL = $"http://{Server}:{PSServer.Port}/file/?b64={Installer.B64Query(URL)}";
                    }
                    else
                    {
                        var HostStream = new FileHostStream(DirectURL);

                        if (HostStream.DirectLink && !HostStream.SingleConnection && !Proxy)
                            URL = HostStream.Url;

                        if ((!HostStream.DirectLink || Proxy) && !HostStream.SingleConnection)
                        {
                            Proxy = true;
                            URL = $"http://{Server}:{PSServer.Port}/proxy/?b64={Installer.B64Query(URL)}";
                        }

                        if (HostStream.SingleConnection)
                        {
                            HostStream.KeepAlive = true;

                            Console.WriteLine("WARNING: Limited File Hosting - The given url is supported but not recommended.");
                            DownTask = Downloader.CreateTask(URL, HostStream);

                            PKG = new VirtualStream(DownTask?.OpenRead() ?? throw new Exception(), 0, DownTask?.SafeLength ?? 0)
                            {
                                ForceAmount = true
                            };

                            LimitedFHost = true;
                        }
                        else
                            PKG = HostStream;
                    }

                    var Info = PKG.GetPKGInfo();

                    if (Info == null)
                    {
                        Console.WriteLine("Failed to get the PKG Info");
                        return;
                    }

                    if (LimitedFHost)
                    {
                        Proxy = true;

                        Console.WriteLine("Preloading PKG...");

                        while (DownTask?.SafeReadyLength < Info?.PreloadLength)
                            Task.Delay(1000).ConfigureAwait(false).GetAwaiter().GetResult();

                        URL = $"http://{Server}:{PSServer.Port}/cache/?b64={Installer.B64Query(URL)}";
                    }
                    else
                    {
                        PKG.Close();
                    }

                    Console.WriteLine($"Pushing: {Info?.FriendlyName}");
                    Console.WriteLine($"Title ID: {Info?.TitleID}");
                    Console.WriteLine($"Content ID: {Info?.ContentID}");
                    Console.WriteLine($"Type: {Info?.FirendlyContentType}");

                    Installer.Server = PSServer;
                    Installer.CurrentPKG = Info!.Value;

                    App.Config.PayloadPort = DPIPort;

                    bool Status = Installer.Payload.SendPKGPayload(PSIP, Server, URL, true, false).ConfigureAwait(false).GetAwaiter().GetResult();

                    if (!Status)
                    {
                        Console.WriteLine("Failed to Send the PKG");
                        return;
                    }

                    while (true)
                    {
                        Task.Delay(1000).ConfigureAwait(false).GetAwaiter().GetResult();

                        if (PSServer.LastRequest == null)
                            continue;

                        if (PSServer.Connections > 0)
                            continue;

                        if (DownTask is { Running: true })
                            continue;

                        var IDLESeconds = (DateTime.Now - PSServer.LastRequest!.Value).TotalSeconds;

                        if (Proxy && PSServer.LastRequestMode is "json" or null && IDLESeconds < 60)
                            continue;

                        if (IDLESeconds > 5)
                            break;
                    }

                    TempHelper.Clear();

                    Console.WriteLine("Sent!");
                }
                finally
                {
                    PKG?.Dispose();
                }
            }

            Environment.Exit(0);
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();

        static bool FillBuffer(Stream stream, byte[] buffer, int count, out int read)
        {
            read = 0;

            while (read < count)
            {
                int current = stream.Read(buffer, read, count - read);
                if (current == 0)
                    break;

                read += current;
            }

            return read > 0;
        }

        static void RunSelfTest(string url)
        {
            Console.WriteLine("Running SegmentedStream Self-Test on: " + url);
            
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            byte[] buf = new byte[65536];

            Console.WriteLine("\n--- Pass 1: Direct Download ---");
            var direct = new FileHostStream(url);
            long totalSize = direct.Length;
            Console.WriteLine("Total Size: " + totalSize);
            
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long pos = 0;
            while (pos < totalSize)
            {
                int r = direct.Read(buf, 0, buf.Length);
                if (r == 0) break;
                sha256.TransformBlock(buf, 0, r, null, 0);
                pos += r;
                Console.Write($"\rDownloaded: {pos} / {totalSize}");
            }
            sha256.TransformFinalBlock(buf, 0, 0);
            sw.Stop();
            var hash1 = BitConverter.ToString(sha256.Hash).Replace("-", "").ToLowerInvariant();
            var time1 = sw.Elapsed;
            Console.WriteLine($"\nDirect Time: {time1.TotalSeconds:F2}s, Hash: {hash1}");
            
            sha256.Initialize();
            
            Console.WriteLine("\n--- Pass 2: Segmented Download ---");
            var segStream = new SegmentedStream(() => new FileHostStream(url), null, 1024 * 1024, false, 4);
            
            sw.Restart();
            pos = 0;
            while (pos < totalSize)
            {
                int r = segStream.Read(buf, 0, buf.Length);
                if (r == 0) break;
                sha256.TransformBlock(buf, 0, r, null, 0);
                pos += r;
                Console.Write($"\rDownloaded: {pos} / {totalSize}");
            }
            sha256.TransformFinalBlock(buf, 0, 0);
            sw.Stop();
            
            var hash2 = BitConverter.ToString(sha256.Hash).Replace("-", "").ToLowerInvariant();
            var time2 = sw.Elapsed;
            Console.WriteLine($"\nSegmented Time: {time2.TotalSeconds:F2}s, Hash: {hash2}");

            Console.WriteLine("\n--- Pass 3: Read Verification ---");
            using var verificationDirect = new FileHostStream(url);
            using var verificationSegmented = new SegmentedStream(() => new FileHostStream(url), null, 1024 * 1024, false, 4);
            var verifyDirectBuffer = new byte[65536];
            var verifySegmentedBuffer = new byte[65536];
            long verified = 0;
            long mismatchOffset = -1;
            bool corruptionDetected = false;

            sw.Restart();
            while (verified < totalSize)
            {
                int target = (int)Math.Min(verifyDirectBuffer.Length, totalSize - verified);

                bool hasDirect = FillBuffer(verificationDirect, verifyDirectBuffer, target, out int directRead);
                bool hasSegmented = FillBuffer(verificationSegmented, verifySegmentedBuffer, target, out int segmentedRead);

                if (hasDirect != hasSegmented || directRead != segmentedRead)
                {
                    mismatchOffset = verified + Math.Min(directRead, segmentedRead);
                    corruptionDetected = true;
                    Console.WriteLine($"\nRead size mismatch at offset {mismatchOffset} (direct={directRead}, segmented={segmentedRead}).");
                    break;
                }

                if (!hasDirect)
                    break;

                for (int i = 0; i < directRead; i++)
                {
                    if (verifyDirectBuffer[i] == verifySegmentedBuffer[i])
                        continue;

                    mismatchOffset = verified + i;
                    corruptionDetected = true;
                    Console.WriteLine($"\nByte mismatch at offset {mismatchOffset} (direct=0x{verifyDirectBuffer[i]:X2}, segmented=0x{verifySegmentedBuffer[i]:X2}).");
                    break;
                }

                if (corruptionDetected)
                    break;

                verified += directRead;
                Console.Write($"\rVerified: {verified} / {totalSize}");
            }
            sw.Stop();

            if (!corruptionDetected && verified != totalSize)
            {
                mismatchOffset = verified;
                corruptionDetected = true;
                Console.WriteLine($"\nUnexpected EOF at offset {mismatchOffset}.");
            }

            Console.WriteLine($"\nVerification Time: {sw.Elapsed.TotalSeconds:F2}s");
            
            Console.WriteLine("\n--- Results ---");
            if (hash1 == hash2 && !corruptionDetected)
            {
                Console.WriteLine("Test Finished! No corruption detected.");
            }
            else
            {
                if (hash1 != hash2)
                    Console.WriteLine("CORRUPTION DETECTED: Hashes do not match.");

                if (corruptionDetected)
                    Console.WriteLine($"CORRUPTION DETECTED: Read verification failed at offset {mismatchOffset}.");
            }
            
            Console.WriteLine($"Speedup: {time1.TotalSeconds / time2.TotalSeconds:F2}x");
        }
    }
}
