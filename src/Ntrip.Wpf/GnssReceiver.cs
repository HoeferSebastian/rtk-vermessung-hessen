using System.IO.Ports;
using System.Text;

namespace Ntrip.Wpf;

/// <summary>Serielle Verbindung zum GNSS-Modul: liest NMEA-Zeilen, schreibt RTCM-Korrekturdaten.</summary>
public sealed class GnssReceiver : IDisposable
{
    SerialPort? _port;
    CancellationTokenSource? _cts;
    readonly object _writeLock = new();

    public event Action<string>? LineReceived;
    public event Action<string>? Disconnected;

    /// <summary>Name für den Simulationsmodus in der Port-Liste.</summary>
    public const string DemoPortName = "Demo (Simulation)";

    bool _demoRunning;
    public bool IsDemo => _demoRunning;
    public bool IsOpen => _port?.IsOpen == true || _demoRunning;

    /// <summary>
    /// Simuliert einen RTK-Empfänger: läuft mit 1,5 m/s im Rechteck (40 × 30 m) um die Startposition und
    /// liefert GGA/RMC/GST im 1-Hz-Takt (RTK Fixed). Zum Ausprobieren ohne Hardware.
    /// </summary>
    public void OpenDemo(double lat, double lon, double altMsl)
    {
        Close();
        BytesReceived = BytesSent = 0;
        _cts = new CancellationTokenSource();
        _demoRunning = true;
        var ct = _cts.Token;
        Task.Run(() => DemoLoop(lat, lon, altMsl, ct));
    }

    void DemoLoop(double lat0, double lon0, double altMsl, CancellationToken ct)
    {
        var way = new (double E, double N)[] { (0, 0), (40, 0), (40, 30), (0, 30) };
        double total = 0;
        var seg = new double[way.Length];
        for (int i = 0; i < way.Length; i++)
        {
            var a = way[i]; var b = way[(i + 1) % way.Length];
            seg[i] = Math.Sqrt((b.E - a.E) * (b.E - a.E) + (b.N - a.N) * (b.N - a.N));
            total += seg[i];
        }

        const double walkSpeed = 1.5;
        var rnd = new Random(7);
        double dist = 0;
        int lastSeg = 0;
        int hold = 40; // Sekunden Stillstand am Start (wie bei einer echten Punktmessung)
        while (!ct.IsCancellationRequested)
        {
            double d = dist % total;
            int s = 0;
            while (d > seg[s]) { d -= seg[s]; s++; }
            var a = way[s]; var b = way[(s + 1) % way.Length];
            double f = d / seg[s];
            double e = a.E + (b.E - a.E) * f + (rnd.NextDouble() - 0.5) * 0.02;
            double n = a.N + (b.N - a.N) * f + (rnd.NextDouble() - 0.5) * 0.02;
            double course = (Math.Atan2(b.E - a.E, b.N - a.N) * 180 / Math.PI + 360) % 360;
            var (lat, lon) = Ntrip.Core.Geo.Move(lat0, lon0, e, n);

            bool standing = hold > 0;
            double speed = standing ? 0 : walkSpeed;

            var now = DateTime.UtcNow;
            foreach (var line in new[]
            {
                Ntrip.Core.Nmea.BuildGga(now, lat, lon, 4, 24, 0.6, altMsl + (rnd.NextDouble() - 0.5) * 0.02, 47.0, 1.0),
                Ntrip.Core.Nmea.BuildRmc(now, lat, lon, speed, course),
                Ntrip.Core.Nmea.BuildGst(now, 0.012, 0.010, 0.022)
            })
            {
                BytesReceived += line.Length + 2;
                LineReceived?.Invoke(line);
            }

            if (standing) hold--;
            else
            {
                dist += walkSpeed;
                int newSeg = 0;
                double dd = dist % total;
                while (dd > seg[newSeg]) { dd -= seg[newSeg]; newSeg++; }
                if (newSeg != lastSeg) { lastSeg = newSeg; hold = 6; } // an jeder Ecke kurz stehen bleiben
            }
            try { Task.Delay(1000, ct).Wait(ct); } catch { return; }
        }
    }
    public long BytesReceived { get; private set; }
    public long BytesSent { get; private set; }

    public void Open(string portName, int baud)
    {
        Close();
        BytesReceived = BytesSent = 0;
        var port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 500,
            WriteTimeout = 1000
        };
        port.Open();
        _port = port;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Task.Run(() => ReadLoop(port, ct));
    }

    void ReadLoop(SerialPort port, CancellationToken ct)
    {
        var buf = new byte[1024];
        var line = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = port.Read(buf, 0, buf.Length); }
            catch (TimeoutException) { continue; }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested) Disconnected?.Invoke(ex.Message);
                return;
            }

            BytesReceived += n;
            for (int i = 0; i < n; i++)
            {
                byte b = buf[i];
                if (b == '\n')
                {
                    if (line.Length > 0) LineReceived?.Invoke(line.ToString());
                    line.Clear();
                }
                else if (b != '\r' && b >= 0x20 && b < 0x7F)
                {
                    if (line.Length < 512) line.Append((char)b);
                }
            }
        }
    }

    public void Write(byte[] data, int count)
    {
        var port = _port;
        if (port is not { IsOpen: true }) return;
        lock (_writeLock)
        {
            port.Write(data, 0, count);
            BytesSent += count;
        }
    }

    public void Close()
    {
        _demoRunning = false;
        _cts?.Cancel();
        _cts = null;
        var port = _port;
        _port = null;
        if (port != null)
        {
            try { port.Close(); } catch { }
            port.Dispose();
        }
    }

    public void Dispose() => Close();
}
