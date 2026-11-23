using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace Ntrip.Core;

/// <summary>NTRIP-Client (v2 mit v1-Fallback). Liefert RTCM-Bytes per Event und sendet optional GGA zurück.</summary>
public sealed class NtripClient
{
    public event Action<string>? Log;
    public event Action? Connected;
    /// <summary>Gepufferter Datenblock; der Puffer wird wiederverwendet und muss synchron verarbeitet werden.</summary>
    public event Action<byte[], int>? DataReceived;

    /// <summary>Liefert den zu sendenden GGA-Satz oder null (dann wird die Fallback-Position aus den Optionen genutzt).</summary>
    public Func<string?>? GgaProvider { get; set; }

    public long TotalBytes { get; private set; }

    sealed class Connection : IDisposable
    {
        public required TcpClient Tcp { get; init; }
        public required Stream Stream { get; init; }
        public required string Header { get; init; }
        public string StatusLine => Header.Split("\r\n")[0];
        public bool IsSourcetable =>
            StatusLine.Contains("SOURCETABLE", StringComparison.OrdinalIgnoreCase) ||
            Header.Contains("gnss/sourcetable", StringComparison.OrdinalIgnoreCase);
        public void Dispose() { Stream.Dispose(); Tcp.Dispose(); }
    }

    static async Task<Connection> OpenAsync(NtripOptions o, string mountpoint, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(o.Host, o.Port, ct);
            Stream stream = tcp.GetStream();
            if (o.UseTls)
            {
                var ssl = new SslStream(stream);
                await ssl.AuthenticateAsClientAsync(o.Host);
                stream = ssl;
            }

            var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{o.Username}:{o.Password}"));
            var req = $"GET /{mountpoint} HTTP/1.1\r\nHost: {o.Host}:{o.Port}\r\nNtrip-Version: Ntrip/2.0\r\n" +
                      $"User-Agent: NTRIP SimpleNtripClient/1.0\r\nAuthorization: Basic {auth}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(req), ct);

            var header = new StringBuilder();
            var one = new byte[1];
            while (true)
            {
                if (await stream.ReadAsync(one, ct) == 0) throw new IOException("Verbindung vom Caster geschlossen.");
                header.Append((char)one[0]);
                var h = header.ToString();
                if (h.EndsWith("\r\n\r\n") || (h.StartsWith("ICY 200 OK") && h.EndsWith("\r\n"))) break;
                if (header.Length > 65536) throw new IOException("Header zu lang.");
            }

            var conn = new Connection { Tcp = tcp, Stream = stream, Header = header.ToString() };
            if (conn.StatusLine.Contains("401"))
            {
                conn.Dispose();
                throw new UnauthorizedAccessException("Benutzername/Passwort falsch oder keine Berechtigung.");
            }
            if (!conn.StatusLine.Contains("200"))
            {
                var status = conn.StatusLine;
                conn.Dispose();
                throw new IOException($"Unerwartete Antwort: {status}");
            }
            return conn;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Lädt die Sourcetable (Liste der Mountpoints) als Text.</summary>
    public async Task<string> GetSourcetableAsync(NtripOptions o, CancellationToken ct)
    {
        using var conn = await OpenAsync(o, "", ct);
        var sb = new StringBuilder(conn.Header);
        var buf = new byte[8192];
        while (!sb.ToString().Contains("ENDSOURCETABLE"))
        {
            int n = await conn.Stream.ReadAsync(buf, ct);
            if (n == 0) break;
            sb.Append(Encoding.ASCII.GetString(buf, 0, n));
        }
        return sb.ToString();
    }

    /// <summary>Verbindet und streamt, bis die Verbindung abbricht (Exception) oder abgebrochen wird.</summary>
    public async Task StreamAsync(NtripOptions o, CancellationToken ct)
    {
        Log?.Invoke($"NTRIP: Verbinde zu {o.Host}:{o.Port}, Mountpoint '{o.Mountpoint}' ...");
        using var conn = await OpenAsync(o, o.Mountpoint, ct);
        if (conn.IsSourcetable)
            throw new IOException($"Mountpoint '{o.Mountpoint}' existiert nicht (Server lieferte die Sourcetable).");

        Log?.Invoke($"NTRIP: {conn.StatusLine}");
        TotalBytes = 0;
        Connected?.Invoke();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? ggaTask = null;
        if (o.SendGga)
        {
            ggaTask = Task.Run(async () =>
            {
                while (!linked.IsCancellationRequested)
                {
                    var gga = GgaProvider?.Invoke() ?? Nmea.BuildGga(o.Latitude, o.Longitude, o.Altitude);
                    await conn.Stream.WriteAsync(Encoding.ASCII.GetBytes(gga + "\r\n"), linked.Token);
                    await conn.Stream.FlushAsync(linked.Token);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, o.GgaIntervalSeconds)), linked.Token);
                }
            }, linked.Token);
        }

        try
        {
            var buf = new byte[4096];
            while (!ct.IsCancellationRequested)
            {
                int n = await conn.Stream.ReadAsync(buf, ct);
                if (n == 0) throw new IOException("Verbindung vom Caster geschlossen.");
                TotalBytes += n;
                DataReceived?.Invoke(buf, n);
            }
        }
        finally
        {
            linked.Cancel();
            if (ggaTask != null) { try { await ggaTask; } catch { } }
        }
    }
}
