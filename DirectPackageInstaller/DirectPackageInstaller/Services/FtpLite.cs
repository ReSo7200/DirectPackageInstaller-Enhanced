using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DirectPackageInstaller.Services
{
    /// <summary>A single FTP server reply (final code + all text lines).</summary>
    public readonly record struct FtpReply(int Code, string Text)
    {
        public bool IsPositive => Code >= 100 && Code < 400;
        public override string ToString() => $"{Code} {Text}";
    }

    /// <summary>A directory entry parsed from a LIST response (Size -1 when the listing doesn't give it).</summary>
    public readonly record struct FtpEntry(string Name, bool IsDirectory, long Size = -1, DateTime? Modified = null);

    public class FtpException : Exception
    {
        public FtpReply? Reply { get; }
        public FtpException(string message, FtpReply? reply = null) : base(message) { Reply = reply; }
    }

    /// <summary>A transfer cut off at its size limit; Head holds the bytes up to it.</summary>
    public sealed class FtpHeadException : FtpException
    {
        public byte[] Head { get; }
        public FtpHeadException(string message, byte[] head) : base(message) { Head = head; }
    }

    /// <summary>
    /// Minimal async FTP client over raw sockets (passive mode only, binary type).
    /// Every operation is bounded by <see cref="OperationTimeout"/> and honours the CancellationToken.
    /// Not thread-safe: issue one command at a time.
    /// </summary>
    public sealed class FtpLite : IAsyncDisposable
    {
        public const int DefaultMaxDownload = 1024 * 1024;

        private readonly string _host;
        private readonly int _port;
        private TcpClient? _control;
        private NetworkStream? _stream;
        private readonly byte[] _buf = new byte[4096];
        private int _bufPos, _bufLen;
        private IPAddress? _remoteIp;

        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(3);
        public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(5);
        public string Host => _host;
        public int Port => _port;
        public FtpReply? Welcome { get; private set; }
        public bool IsConnected => _control?.Connected == true;

        public FtpLite(string host, int port = 21)
        {
            _host = host;
            _port = port;
        }

        /// <summary>Connects, reads the greeting, logs in anonymously and switches to TYPE I.</summary>
        public static async Task<FtpLite> ConnectAsync(string host, int port, TimeSpan? connectTimeout = null,
            TimeSpan? operationTimeout = null, CancellationToken ct = default)
        {
            var ftp = new FtpLite(host, port);
            if (connectTimeout.HasValue) ftp.ConnectTimeout = connectTimeout.Value;
            if (operationTimeout.HasValue) ftp.OperationTimeout = operationTimeout.Value;
            try
            {
                await ftp.ConnectAsync(ct).ConfigureAwait(false);
                await ftp.LoginAsync("anonymous", "anonymous", ct).ConfigureAwait(false);
                await ftp.SetBinaryAsync(ct).ConfigureAwait(false);
                return ftp;
            }
            catch
            {
                await ftp.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            if (_control != null) throw new InvalidOperationException("Already connected");
            // IPv4 socket for an IPv4 address; NetConnect works around Android's ConnectAsync
            var address = IPAddress.TryParse(_host, out var parsed) ? parsed
                : (await Dns.GetHostAddressesAsync(_host, ct).ConfigureAwait(false))[0];
            _control = new TcpClient(address.AddressFamily);
            using (var cts = Linked(ct, ConnectTimeout))
            {
                try
                {
                    await NetConnect.ConnectAsync(_control, address, _port, cts.Token).ConfigureAwait(false);
                    _control.NoDelay = true;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"FTP connect to {_host}:{_port} timed out");
                }
            }
            _stream = _control.GetStream();
            _remoteIp = (_control.Client.RemoteEndPoint as IPEndPoint)?.Address;
            if (_remoteIp != null && _remoteIp.IsIPv4MappedToIPv6) _remoteIp = _remoteIp.MapToIPv4();

            var greet = await ReadReplyAsync(ct).ConfigureAwait(false);
            // Some servers send 120 "ready in n minutes" first.
            if (greet.Code == 120) greet = await ReadReplyAsync(ct).ConfigureAwait(false);
            if (greet.Code != 220) throw new FtpException("Unexpected FTP greeting", greet);
            Welcome = greet;
        }

        public async Task LoginAsync(string user = "anonymous", string pass = "anonymous", CancellationToken ct = default)
        {
            var r = await CommandAsync("USER " + user, ct).ConfigureAwait(false);
            if (r.Code == 230 || r.Code == 202) return;        // logged in immediately / no auth
            if (r.Code == 331 || r.Code == 332)
            {
                r = await CommandAsync("PASS " + pass, ct).ConfigureAwait(false);
                if (r.Code == 230 || r.Code == 202) return;
                throw new FtpException("FTP login rejected", r);
            }
            // Some homebrew servers answer USER with 500/502 because they have no auth at all; continue.
            if (r.Code >= 500 && r.Code < 510) return;
            throw new FtpException("FTP USER rejected", r);
        }

        public async Task SetBinaryAsync(CancellationToken ct = default)
        {
            // Tolerate servers that don't implement TYPE: they are binary anyway.
            await CommandAsync("TYPE I", ct).ConfigureAwait(false);
        }

        /// <summary>Sends a command and returns the (final) reply.</summary>
        public async Task<FtpReply> CommandAsync(string command, CancellationToken ct = default)
        {
            await SendAsync(command, ct).ConfigureAwait(false);
            return await ReadReplyAsync(ct).ConfigureAwait(false);
        }

        /// <summary>LIST path; returns entry names (excluding "." and "..").</summary>
        public async Task<List<FtpEntry>> ListAsync(string path, CancellationToken ct = default)
        {
            var data = await TransferAsync("LIST " + path, 4 * 1024 * 1024, ct).ConfigureAwait(false);
            var text = Encoding.UTF8.GetString(data);
            var result = new List<FtpEntry>();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (TryParseListLine(line, out var entry) && entry.Name != "." && entry.Name != "..")
                    result.Add(entry);
            }
            return result;
        }

        /// <summary>RETR path into memory; throws if the file exceeds maxBytes.</summary>
        public Task<byte[]> DownloadAsync(string path, int maxBytes = DefaultMaxDownload, CancellationToken ct = default)
            => TransferAsync("RETR " + path, maxBytes, ct);

        /// <summary>RNFR/RNTO. Only within one drive on the PS4 (no copy across drives).</summary>
        public async Task RenameAsync(string from, string to, CancellationToken ct = default)
        {
            // PS4 servers (ftps4-based) answer 226 where others say 250: any 2xx is done
            var r = await CommandAsync("RNFR " + from, ct).ConfigureAwait(false);
            if (r.Code != 350) throw new FtpException($"Can't rename '{from}'", r);
            r = await CommandAsync("RNTO " + to, ct).ConfigureAwait(false);
            if (r.Code / 100 != 2) throw new FtpException($"Can't rename '{from}' to '{to}'", r);
        }

        public async Task DeleteAsync(string path, CancellationToken ct = default)
        {
            var r = await CommandAsync("DELE " + path, ct).ConfigureAwait(false);
            if (r.Code / 100 != 2) throw new FtpException($"Can't delete '{path}'", r);
        }

        /// <summary>MKD; an existing folder is fine.</summary>
        public async Task MakeDirAsync(string path, CancellationToken ct = default)
            => await CommandAsync("MKD " + path, ct).ConfigureAwait(false);

        public async Task<bool> RemoveDirAsync(string path, CancellationToken ct = default)
            => (await CommandAsync("RMD " + path, ct).ConfigureAwait(false)).Code / 100 == 2;

        /// <summary>Size of a file (SIZE, else its folder's listing), or -1 when it isn't there.</summary>
        public async Task<long> FileSizeAsync(string path, CancellationToken ct = default)
        {
            var sized = await CommandAsync("SIZE " + path, ct).ConfigureAwait(false);
            if (sized.Code == 213 && long.TryParse(sized.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                return n;
            if (sized.Code == 550)
                return -1;

            int slash = path.LastIndexOf('/');
            var dir = slash <= 0 ? "/" : path.Substring(0, slash);
            var name = path.Substring(slash + 1);
            try
            {
                foreach (var e in await ListAsync(dir, ct).ConfigureAwait(false))
                    if (!e.IsDirectory && e.Name == name)
                        return e.Size;
            }
            catch (FtpException ex) when (ex.Reply is { Code: >= 500 })
            {
                // the folder doesn't exist
            }
            return -1;
        }

        /// <summary>The first Count bytes of a file (e.g. a PKG header); the transfer is cut off after them.</summary>
        public async Task<byte[]> ReadHeadAsync(string path, int Count, CancellationToken ct = default)
        {
            try
            {
                return await DownloadAsync(path, Count, ct).ConfigureAwait(false);
            }
            catch (FtpHeadException ex)
            {
                return ex.Head;
            }
        }

        /// <summary>RETR path into Target as it arrives (for files too big to hold in memory).</summary>
        public async Task DownloadToAsync(string path, Stream Target, Action<long>? Progress = null, CancellationToken ct = default)
        {
            var ep = await EnterPassiveAsync(ct).ConfigureAwait(false);
            using var data = new TcpClient(ep.AddressFamily);
            using (var cts = Linked(ct, ConnectTimeout))
            {
                try
                {
                    await NetConnect.ConnectAsync(data, ep.Address, ep.Port, cts.Token).ConfigureAwait(false);
                    data.NoDelay = true;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Abort();
                    throw new TimeoutException($"FTP data connection to {ep} timed out");
                }
            }

            await SendAsync("RETR " + path, ct).ConfigureAwait(false);
            var prelim = await ReadReplyAsync(ct).ConfigureAwait(false);
            if (prelim.Code != 125 && prelim.Code != 150)
            {
                // some servers answer an empty file with 226 straight away
                if (prelim.Code == 226 || prelim.Code == 250) return;
                throw new FtpException($"Download of '{path}' was refused", prelim);
            }

            var ds = data.GetStream();
            var chunk = new byte[65536];
            long got = 0;
            while (true)
            {
                int n;
                using (var cts = Linked(ct, OperationTimeout))
                {
                    try
                    {
                        n = await ds.ReadAsync(chunk.AsMemory(), cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        Abort();
                        throw new TimeoutException($"FTP download of '{path}' timed out");
                    }
                }
                if (n <= 0) break;
                try
                {
                    await Target.WriteAsync(chunk.AsMemory(0, n), ct).ConfigureAwait(false);
                }
                catch
                {
                    // the transfer is abandoned mid-way: the control connection is out of step
                    Abort();
                    throw;
                }
                got += n;
                Progress?.Invoke(got);
            }

            var done = await ReadReplyAsync(ct).ConfigureAwait(false);
            if (done.Code / 100 != 2)
                throw new FtpException($"Download of '{path}' did not complete", done);
        }

        /// <summary>STOR the rest of Source to path. Progress gets the bytes sent so far.</summary>
        public async Task UploadAsync(string path, Stream Source, Action<long>? Progress = null, CancellationToken ct = default)
        {
            var ep = await EnterPassiveAsync(ct).ConfigureAwait(false);
            using var data = new TcpClient(ep.AddressFamily);
            using (var cts = Linked(ct, ConnectTimeout))
            {
                try
                {
                    await NetConnect.ConnectAsync(data, ep.Address, ep.Port, cts.Token).ConfigureAwait(false);
                    data.NoDelay = true;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Abort();
                    throw new TimeoutException($"FTP data connection to {ep} timed out");
                }
            }

            await SendAsync("STOR " + path, ct).ConfigureAwait(false);
            var prelim = await ReadReplyAsync(ct).ConfigureAwait(false);
            if (prelim.Code != 125 && prelim.Code != 150)
                throw new FtpException($"Upload of '{path}' was refused", prelim);

            var ds = data.GetStream();
            var chunk = new byte[65536];
            long sent = 0;
            while (true)
            {
                int n = await Source.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false);
                if (n <= 0) break;
                using (var cts = Linked(ct, OperationTimeout))
                {
                    try
                    {
                        await ds.WriteAsync(chunk.AsMemory(0, n), cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        Abort();
                        throw new TimeoutException($"FTP upload of '{path}' timed out");
                    }
                }
                sent += n;
                Progress?.Invoke(sent);
            }
            try { data.Client.Shutdown(SocketShutdown.Both); } catch { }
            data.Close();

            var done = await ReadReplyAsync(ct).ConfigureAwait(false);
            if (done.Code != 226 && done.Code != 250)
                throw new FtpException($"Upload of '{path}' did not complete", done);
        }

        public async Task QuitAsync(CancellationToken ct = default)
        {
            if (_stream == null) return;
            try
            {
                using var cts = Linked(ct, TimeSpan.FromSeconds(1));
                await SendAsync("QUIT", cts.Token).ConfigureAwait(false);
                await ReadReplyAsync(cts.Token).ConfigureAwait(false);
            }
            catch { /* best effort */ }
        }

        public async ValueTask DisposeAsync()
        {
            if (_control != null && _control.Connected)
                await QuitAsync().ConfigureAwait(false);
            try { _stream?.Dispose(); } catch { }
            try { _control?.Dispose(); } catch { }
            _stream = null;
            _control = null;
        }

        // ---------------------------------------------------------------- internals

        private async Task<byte[]> TransferAsync(string command, int maxBytes, CancellationToken ct)
        {
            var ep = await EnterPassiveAsync(ct).ConfigureAwait(false);
            using var data = new TcpClient(ep.AddressFamily);
            using (var cts = Linked(ct, ConnectTimeout))
            {
                try
                {
                    await NetConnect.ConnectAsync(data, ep.Address, ep.Port, cts.Token).ConfigureAwait(false);
                    data.NoDelay = true;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Abort();
                    throw new TimeoutException($"FTP data connection to {ep} timed out");
                }
            }

            await SendAsync(command, ct).ConfigureAwait(false);
            var prelim = await ReadReplyAsync(ct).ConfigureAwait(false);
            if (prelim.Code != 125 && prelim.Code != 150)
            {
                // Some servers reply 226 directly for empty listings.
                if (prelim.Code == 226 || prelim.Code == 250) return Array.Empty<byte>();
                throw new FtpException($"'{command}' failed", prelim);
            }

            var ms = new MemoryStream();
            var ds = data.GetStream();
            var chunk = new byte[16384];
            while (true)
            {
                int n;
                using (var cts = Linked(ct, OperationTimeout))
                {
                    try
                    {
                        n = await ds.ReadAsync(chunk.AsMemory(), cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        Abort();
                        throw new TimeoutException($"FTP data transfer for '{command}' timed out");
                    }
                }
                if (n <= 0) break;
                if (ms.Length + n > maxBytes)
                {
                    // keep what fits: ReadHeadAsync wants exactly the first maxBytes
                    ms.Write(chunk, 0, (int)(maxBytes - ms.Length));
                    // Abort: drop the data connection and resync the control channel.
                    try { data.Client.Close(); } catch { }
                    try
                    {
                        using var rc = Linked(ct, TimeSpan.FromSeconds(2));
                        await ReadReplyAsync(rc.Token).ConfigureAwait(false);
                    }
                    catch { Abort(); }
                    throw new FtpHeadException($"'{command}' exceeds size limit of {maxBytes} bytes", ms.ToArray());
                }
                ms.Write(chunk, 0, n);
            }
            try { data.Client.Shutdown(SocketShutdown.Both); } catch { }

            var done = await ReadReplyAsync(ct).ConfigureAwait(false);
            if (done.Code != 226 && done.Code != 250)
                throw new FtpException($"'{command}' transfer did not complete", done);
            return ms.ToArray();
        }

        private async Task<IPEndPoint> EnterPassiveAsync(CancellationToken ct)
        {
            var r = await CommandAsync("PASV", ct).ConfigureAwait(false);
            if (r.Code != 227) throw new FtpException("PASV failed", r);
            int open = r.Text.IndexOf('(');
            int close = open >= 0 ? r.Text.IndexOf(')', open) : -1;
            string tuple;
            if (open >= 0 && close > open) tuple = r.Text.Substring(open + 1, close - open - 1);
            else
            {
                // No parentheses: find the first run of "d,d,d,d,d,d".
                var m = System.Text.RegularExpressions.Regex.Match(r.Text, @"\d+,\d+,\d+,\d+,\d+,\d+");
                if (!m.Success) throw new FtpException("Cannot parse PASV reply", r);
                tuple = m.Value;
            }
            var parts = tuple.Split(',');
            if (parts.Length != 6) throw new FtpException("Cannot parse PASV reply", r);
            var nums = new int[6];
            for (int i = 0; i < 6; i++)
                if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out nums[i]) || nums[i] < 0 || nums[i] > 255)
                    throw new FtpException("Cannot parse PASV reply", r);

            var ip = new IPAddress(new[] { (byte)nums[0], (byte)nums[1], (byte)nums[2], (byte)nums[3] });
            int port = nums[4] * 256 + nums[5];
            // Use the control connection's IP if the server advertises 0.0.0.0 / loopback / a different
            // (e.g. internal) address. PS4 FTP servers only ever serve data from the same host.
            if (_remoteIp != null && !ip.Equals(_remoteIp))
                ip = _remoteIp;
            return new IPEndPoint(ip, port);
        }

        private async Task SendAsync(string line, CancellationToken ct)
        {
            if (_stream == null) throw new FtpException("FTP connection lost");
            var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
            using var cts = Linked(ct, OperationTimeout);
            try
            {
                await _stream.WriteAsync(bytes.AsMemory(), cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("FTP send timed out");
            }
        }

        /// <summary>Reads a full reply, following "nnn-" continuations until "nnn ".</summary>
        private async Task<FtpReply> ReadReplyAsync(CancellationToken ct)
        {
            using var cts = Linked(ct, OperationTimeout);
            try
            {
                var sb = new StringBuilder();
                var first = await ReadLineAsync(cts.Token).ConfigureAwait(false);
                if (first.Length < 3 || !int.TryParse(first.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out int code))
                    throw new FtpException("Malformed FTP reply: " + first);
                sb.Append(first.Length > 4 ? first.Substring(4) : "");
                if (first.Length > 3 && first[3] == '-')
                {
                    var terminator = first.Substring(0, 3) + " ";
                    int guard = 0;
                    while (true)
                    {
                        var line = await ReadLineAsync(cts.Token).ConfigureAwait(false);
                        if (++guard > 1000) throw new FtpException("FTP reply too long");
                        if (line.StartsWith(terminator, StringComparison.Ordinal) || line == first.Substring(0, 3))
                        {
                            sb.Append('\n').Append(line.Length > 4 ? line.Substring(4) : "");
                            break;
                        }
                        sb.Append('\n').Append(line);
                    }
                }
                return new FtpReply(code, sb.ToString());
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Abort();
                throw new TimeoutException("FTP reply timed out");
            }
            catch (OperationCanceledException)
            {
                Abort(); // control channel state is unknown after a cancelled read
                throw;
            }
        }

        private async Task<string> ReadLineAsync(CancellationToken ct)
        {
            if (_stream == null) throw new FtpException("FTP connection lost");
            var bytes = new List<byte>(128);
            while (true)
            {
                if (_bufPos >= _bufLen)
                {
                    _bufLen = await _stream.ReadAsync(_buf.AsMemory(), ct).ConfigureAwait(false);
                    _bufPos = 0;
                    if (_bufLen <= 0)
                    {
                        _bufLen = 0;
                        throw new FtpException("FTP control connection closed");
                    }
                }
                byte b = _buf[_bufPos++];
                if (b == (byte)'\n') break;
                if (bytes.Count > 8192) throw new FtpException("FTP reply line too long");
                bytes.Add(b);
            }
            if (bytes.Count > 0 && bytes[^1] == (byte)'\r') bytes.RemoveAt(bytes.Count - 1);
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        /// <summary>Drops the control connection without QUIT (used after timeouts/desync).</summary>
        private void Abort()
        {
            try { _control?.Client.Close(0); } catch { }
            try { _stream?.Dispose(); } catch { }
            try { _control?.Dispose(); } catch { }
            _stream = null;
            _control = null;
        }

        private static CancellationTokenSource Linked(CancellationToken ct, TimeSpan timeout)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            return cts;
        }

        /// <summary>
        /// Parses a Unix-style LIST line ("drwxr-xr-x 1 user group 512 Jan 1 12:00 name with spaces").
        /// Falls back to DOS-style ("01-01-20 12:00PM &lt;DIR&gt; name").
        /// </summary>
        public static bool TryParseListLine(string line, out FtpEntry entry)
        {
            entry = default;
            if (string.IsNullOrWhiteSpace(line)) return false;
            if (line.StartsWith("total ", StringComparison.OrdinalIgnoreCase)) return false;

            char t = line[0];
            if ("dl-bcps".IndexOf(t) >= 0 && line.Length > 10)
            {
                // Skip 8 whitespace-separated fields (perms, links, owner, group, size, month, day, time/year).
                int pos = 0, fields = 0;
                long size = -1;
                var date = new string[3];
                while (fields < 8)
                {
                    while (pos < line.Length && IsWs(line[pos])) pos++;
                    if (pos >= line.Length) break;
                    int start = pos;
                    while (pos < line.Length && !IsWs(line[pos])) pos++;
                    if (fields == 4 && long.TryParse(line.AsSpan(start, pos - start), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                        size = n;
                    if (fields >= 5)
                        date[fields - 5] = line.Substring(start, pos - start);
                    fields++;
                }
                if (fields < 8) return false;
                // Skip separator padding (PS4 names never start with whitespace); internal spaces are kept.
                while (pos < line.Length && IsWs(line[pos])) pos++;
                if (pos >= line.Length) return false;
                var name = line.Substring(pos).TrimEnd();
                if (t == 'l')
                {
                    int arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
                    if (arrow > 0) name = name.Substring(0, arrow);
                }
                if (name.Length == 0) return false;
                entry = new FtpEntry(name, t == 'd', size, ParseListDate(date[0], date[1], date[2]));
                return true;
            }

            // DOS format
            var dos = System.Text.RegularExpressions.Regex.Match(line,
                @"^\d{2}-\d{2}-\d{2,4}\s+\d{1,2}:\d{2}(AM|PM)?\s+(<DIR>|\d+)\s+(.+)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (dos.Success)
            {
                entry = new FtpEntry(dos.Groups[3].Value, dos.Groups[2].Value.Equals("<DIR>", StringComparison.OrdinalIgnoreCase));
                return true;
            }
            return false;
        }

        private static bool IsWs(char c) => c == ' ' || c == '\t';

        /// <summary>"Sep 26 07:20" (this year, or last year when that's ahead) or "Sep 26 2025"; null when unreadable.</summary>
        private static DateTime? ParseListDate(string? month, string? day, string? timeOrYear)
        {
            if (month == null || day == null || timeOrYear == null)
                return null;
            int m = Array.FindIndex(CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames,
                x => x.Length > 0 && x.Equals(month, StringComparison.OrdinalIgnoreCase)) + 1;
            if (m < 1 || !int.TryParse(day, NumberStyles.None, CultureInfo.InvariantCulture, out var d) || d < 1 || d > 31)
                return null;
            if (timeOrYear.Contains(':'))
            {
                var parts = timeOrYear.Split(':');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var hh) || !int.TryParse(parts[1], out var mm) || hh > 23 || mm > 59)
                    return null;
                var now = DateTime.Now;
                var when = new DateTime(now.Year, m, Math.Min(d, DateTime.DaysInMonth(now.Year, m)), hh, mm, 0);
                return when > now.AddDays(2) ? when.AddYears(-1) : when;
            }
            if (int.TryParse(timeOrYear, NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y > 1970 && y < 3000)
                return new DateTime(y, m, Math.Min(d, DateTime.DaysInMonth(y, m)));
            return null;
        }
    }
}
