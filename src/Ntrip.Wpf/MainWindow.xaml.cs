using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Ntrip.Core;
using Ntrip.Wpf.Offline;

namespace Ntrip.Wpf;

public partial class MainWindow : Window
{
    enum Tool { Pan, AddPoint, Measure }

    // ---- Dienste ----
    readonly GnssReceiver _rx = new();
    readonly NtripClient _ntrip = new();
    readonly RtcmParser _rtcm = new();
    readonly RtcmMonitor _monitor = new();
    readonly MapBridge _map;
    readonly TileService _tiles;

    // ---- Daten ----
    readonly ObservableCollection<BoundaryPoint> _points = new();
    readonly ObservableCollection<TrackRecording> _tracks = new();
    readonly ObservableCollection<MeasurementItem> _measurements = new();
    readonly ObservableCollection<MeasVertex> _draft = new();
    readonly ObservableCollection<RtcmRow> _rtcmRows = new();

    // ---- GNSS-Zustand ----
    volatile GgaData? _gga;
    volatile RmcData? _rmc;
    volatile GstData? _gst;
    long _ggaTicks, _gstTicks, _nmeaTicks, _connectedAt, _rtcmToReceiver, _headingTicks;
    double _heading = double.NaN;
    string _ntripState = "off"; // off | connecting | connected
    NtripOptions _fallback = new();

    // ---- UI-Zustand ----
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _mapTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    CancellationTokenSource? _ntripCts;
    Tool _tool = Tool.Pan;
    bool _init;
    bool _pointsDirty, _tracksDirty, _measDirty, _draftDirty, _projectDirty;
    long _lastBytes;
    RtcmStation? _shownStation;

    public MainWindow()
    {
        InitializeComponent();

        // Startgröße an den verfügbaren Arbeitsbereich anpassen (kleine Bildschirme / hohe Skalierung).
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(Width, wa.Width - 16);
        Height = Math.Min(Height, wa.Height - 16);
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + (wa.Height - Height) / 2;

        PointsGrid.ItemsSource = _points;
        NavTargetBox.ItemsSource = _points;
        TracksGrid.ItemsSource = _tracks;
        VertexGrid.ItemsSource = _draft;
        MeasurementsGrid.ItemsSource = _measurements;
        RtcmGrid.ItemsSource = _rtcmRows;

        _tiles = CreateTileService();
        OnlineToggle.IsChecked = _tiles.AllowNetwork;
        _map = new MapBridge(Web, _tiles);
        HookCollections();
        HookMap();
        HookGnss();
        LoadSettings();
        RefreshPorts();
        UpdateMeasureHint();

        _timer.Tick += (_, _) => UpdateStatus();
        _mapTimer.Tick += (_, _) => PushMapState();
        _timer.Start();
        _mapTimer.Start();
        _init = true;
    }

    async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        foreach (var w in _startupWarnings) AppendLog(w);
        UpdateTileInfo();
        RestoreAutosave();
        await _map.InitAsync();
    }

    // =====================================================================
    // Verdrahtung
    // =====================================================================

    void HookCollections()
    {
        _points.CollectionChanged += (_, e) =>
        {
            if (e.OldItems != null) foreach (BoundaryPoint p in e.OldItems) p.PropertyChanged -= OnPointChanged;
            if (e.NewItems != null) foreach (BoundaryPoint p in e.NewItems) p.PropertyChanged += OnPointChanged;
            _pointsDirty = _projectDirty = true;
        };
        _tracks.CollectionChanged += (_, _) => _tracksDirty = _projectDirty = true;
        _measurements.CollectionChanged += (_, _) => _measDirty = _projectDirty = true;
        _draft.CollectionChanged += (_, _) => _draftDirty = true;
    }

    void OnPointChanged(object? sender, PropertyChangedEventArgs e)
    {
        _pointsDirty = _projectDirty = true;
    }

    void HookMap()
    {
        _map.Error += msg => AppendLog(msg);
        _map.Ready += () =>
        {
            _map.SetLayer(SelectedLayer());
            _map.SetFollow(FollowToggle.IsChecked == true);
            _pointsDirty = _tracksDirty = _measDirty = _draftDirty = true;
            PushMapState();
            if (_gga is { HasPosition: true } g) _map.UpdatePosition(g.Latitude, g.Longitude, g.Quality, CurrentHeading());
            else if (_points.Count > 0) _map.Fit(_points.Select(p => (p.Latitude, p.Longitude)));
        };
        _map.UserPanned += () => { FollowToggle.IsChecked = false; };
        _map.MapClicked += OnMapClicked;
        _map.PointClicked += id =>
        {
            var p = _points.FirstOrDefault(x => x.Id == id);
            if (p == null) return;
            PointsGrid.SelectedItem = p;
            PointsGrid.ScrollIntoView(p);
        };
        _map.PointMoved += (id, lat, lon) =>
        {
            var p = _points.FirstOrDefault(x => x.Id == id);
            if (p == null) return;
            p.SetPosition(lat, lon);
            p.Source = "Karte (verschoben)";
            AppendLog($"Punkt {p.Name} in der Karte verschoben → {lat:F8}, {lon:F8}");
        };
    }

    void HookGnss()
    {
        _rx.LineReceived += OnLine;
        _rx.Disconnected += reason => Dispatcher.BeginInvoke(() =>
        {
            AppendLog($"Empfänger getrennt: {reason}");
            DisconnectReceiver();
        });

        _ntrip.Log += msg => Dispatcher.BeginInvoke(() => AppendLog(msg));
        _ntrip.Connected += () => Dispatcher.BeginInvoke(() =>
        {
            _ntripState = "connected";
            NtripStatus.Text = "NTRIP: verbunden, empfange Daten";
        });
        _rtcm.MessageParsed += _monitor.Add;
        _ntrip.DataReceived += OnRtcm;
        // Echte Empfängerposition an den Caster senden (VRS), solange sie frisch und gültig ist.
        _ntrip.GgaProvider = () =>
        {
            var g = _gga;
            return g is { Quality: > 0, HasPosition: true } && Environment.TickCount64 - Interlocked.Read(ref _ggaTicks) < 5000 ? g.Raw : null;
        };
    }

    // =====================================================================
    // Einstellungen, Ports
    // =====================================================================

    void LoadSettings()
    {
        try
        {
            var o = NtripOptions.LoadFromJson(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
            HostBox.Text = o.Host;
            NtripPortBox.Text = o.Port.ToString(CultureInfo.InvariantCulture);
            MountpointBox.Text = o.Mountpoint;
            UserBox.Text = o.Username;
            PasswordBox.Password = o.Password;
            _fallback = o;
        }
        catch (Exception ex)
        {
            AppendLog($"appsettings.json konnte nicht gelesen werden: {ex.Message}");
        }
    }

    void RefreshPorts()
    {
        var current = PortBox.Text;
        var ports = SerialPort.GetPortNames().OrderBy(p => p.Length).ThenBy(p => p).ToList();
        ports.Add(GnssReceiver.DemoPortName);
        PortBox.ItemsSource = ports;
        PortBox.Text = ports.Contains(current) ? current : ports.FirstOrDefault() ?? "";
    }

    void RefreshPorts_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    // =====================================================================
    // Empfänger
    // =====================================================================

    void Receiver_Click(object sender, RoutedEventArgs e)
    {
        if (_rx.IsOpen) { DisconnectReceiver(); return; }

        if (PortBox.Text.Trim().StartsWith("Demo", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Exchange(ref _rtcmToReceiver, 0);
            Interlocked.Exchange(ref _nmeaTicks, 0);
            _connectedAt = Environment.TickCount64;
            _rx.OpenDemo(_fallback.Latitude, _fallback.Longitude, 120.0);
            ReceiverButton.Content = "Empfänger trennen";
            ReceiverStatus.Text = "Empfänger: Demo-Modus (simulierte Daten, läuft im Rechteck um die Startposition)";
            AppendLog("Demo-Modus gestartet – es werden simulierte NMEA-Daten verwendet.");
            return;
        }

        if (!int.TryParse(BaudBox.Text, out int baud) || string.IsNullOrWhiteSpace(PortBox.Text))
        {
            MessageBox.Show(this, "Bitte COM-Port und gültige Baudrate angeben.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            _rx.Open(PortBox.Text.Trim(), baud);
            Interlocked.Exchange(ref _rtcmToReceiver, 0);
            Interlocked.Exchange(ref _nmeaTicks, 0);
            _connectedAt = Environment.TickCount64;
            ReceiverButton.Content = "Empfänger trennen";
            ReceiverStatus.Text = $"Empfänger: {PortBox.Text} @ {baud} Baud, warte auf NMEA …";
            AppendLog($"Empfänger verbunden: {PortBox.Text} @ {baud}");
        }
        catch (Exception ex)
        {
            AppendLog($"Empfänger-Fehler: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Seriellen Port öffnen fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void DisconnectReceiver()
    {
        _rx.Close();
        _gga = null;
        ReceiverButton.Content = "Empfänger verbinden";
        ReceiverStatus.Text = "Empfänger: nicht verbunden";
        ShowPosition(null);
        _map.ClearPosition();
        CancelCapture("Abgebrochen (Empfänger getrennt)");
        UpdateNavigation();
    }

    // Läuft im Lese-Thread des Empfängers.
    void OnLine(string line)
    {
        Interlocked.Exchange(ref _nmeaTicks, Environment.TickCount64);
        Dispatcher.BeginInvoke(() => AppendNmea(line));

        if (Nmea.TryParseGga(line, out var gga))
        {
            _gga = gga;
            Interlocked.Exchange(ref _ggaTicks, Environment.TickCount64);
            Dispatcher.BeginInvoke(() => OnGga(gga));
        }
        else if (Nmea.TryParseRmc(line, out var rmc))
        {
            _rmc = rmc;
            if (rmc.Valid && rmc.SpeedMs >= 0.5 && !double.IsNaN(rmc.CourseDeg))
            {
                _heading = rmc.CourseDeg;
                Interlocked.Exchange(ref _headingTicks, Environment.TickCount64);
            }
        }
        else if (Nmea.TryParseGst(line, out var gst))
        {
            _gst = gst;
            Interlocked.Exchange(ref _gstTicks, Environment.TickCount64);
        }
    }

    double? CurrentHeading()
    {
        long t = Interlocked.Read(ref _headingTicks);
        return t != 0 && Environment.TickCount64 - t < 15000 && !double.IsNaN(_heading) ? _heading : null;
    }

    /// <summary>Neue Epoche vom Empfänger (UI-Thread).</summary>
    void OnGga(GgaData g)
    {
        ShowPosition(g);
        if (g.HasPosition && g.Quality > 0)
            _map.UpdatePosition(g.Latitude, g.Longitude, g.Quality, CurrentHeading());
        else
            _map.ClearPosition();

        HandleCapture(g);
        RecordSample(g);
        UpdateNavigation();
    }

    // =====================================================================
    // NTRIP
    // =====================================================================

    NtripOptions BuildOptions() => new()
    {
        Host = HostBox.Text.Trim(),
        Port = int.TryParse(NtripPortBox.Text, out var p) ? p : 2101,
        Mountpoint = MountpointBox.Text.Trim(),
        Username = UserBox.Text.Trim(),
        Password = PasswordBox.Password,
        UseTls = _fallback.UseTls,
        SendGga = true,
        Latitude = _fallback.Latitude,
        Longitude = _fallback.Longitude,
        Altitude = _fallback.Altitude,
        GgaIntervalSeconds = _fallback.GgaIntervalSeconds > 0 ? _fallback.GgaIntervalSeconds : 10,
        ReconnectDelaySeconds = _fallback.ReconnectDelaySeconds > 0 ? _fallback.ReconnectDelaySeconds : 5
    };

    async void Sourcetable_Click(object sender, RoutedEventArgs e)
    {
        var btn = (Button)sender;
        btn.IsEnabled = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var text = await new NtripClient().GetSourcetableAsync(BuildOptions(), cts.Token);
            var mounts = text.Split('\n').Select(l => l.TrimEnd('\r').Split(';'))
                .Where(f => f.Length > 1 && f[0] == "STR").Select(f => f[1]).ToList();
            MountpointBox.ItemsSource = mounts;
            AppendLog($"Sourcetable: {mounts.Count} Mountpoints ({string.Join(", ", mounts)})");
            if (mounts.Count > 0 && !mounts.Contains(MountpointBox.Text)) MountpointBox.Text = mounts[0];
        }
        catch (Exception ex) { AppendLog($"Sourcetable-Fehler: {ex.Message}"); }
        finally { btn.IsEnabled = true; }
    }

    void Ntrip_Click(object sender, RoutedEventArgs e)
    {
        if (_ntripCts != null) { _ntripCts.Cancel(); return; }

        var opt = BuildOptions();
        if (string.IsNullOrWhiteSpace(opt.Host) || string.IsNullOrWhiteSpace(opt.Mountpoint))
        {
            MessageBox.Show(this, "Bitte Host und Mountpoint angeben.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _monitor.Reset();
        _rtcmRows.Clear();
        _shownStation = null;
        _ntripCts = new CancellationTokenSource();
        NtripButton.Content = "NTRIP trennen";
        var ct = _ntripCts.Token;
        _ = Task.Run(() => NtripLoopAsync(opt, ct));
    }

    // Läuft im Hintergrund; UI-Zugriffe über den Dispatcher.
    async Task NtripLoopAsync(NtripOptions opt, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Dispatcher.BeginInvoke(() => { _ntripState = "connecting"; NtripStatus.Text = "NTRIP: verbinde …"; });
            try { await _ntrip.StreamAsync(opt, ct); }
            catch (OperationCanceledException) { break; }
            catch (UnauthorizedAccessException ex)
            {
                Dispatcher.BeginInvoke(() => AppendLog($"NTRIP-Fehler: {ex.Message}"));
                break;
            }
            catch (Exception ex) { Dispatcher.BeginInvoke(() => AppendLog($"NTRIP-Fehler: {ex.Message}")); }

            if (ct.IsCancellationRequested) break;
            Dispatcher.BeginInvoke(() => { _ntripState = "connecting"; NtripStatus.Text = $"NTRIP: getrennt, neuer Versuch in {opt.ReconnectDelaySeconds} s"; });
            try { await Task.Delay(TimeSpan.FromSeconds(opt.ReconnectDelaySeconds), ct); } catch { break; }
        }
        Dispatcher.BeginInvoke(() =>
        {
            _ntripState = "off";
            NtripStatus.Text = "NTRIP: nicht verbunden";
            NtripButton.Content = "NTRIP verbinden";
            _ntripCts?.Dispose();
            _ntripCts = null;
        });
    }

    // Läuft im NTRIP-Thread.
    void OnRtcm(byte[] buf, int n)
    {
        _rtcm.Feed(buf, n);
        try
        {
            if (_rx.IsOpen)
            {
                _rx.Write(buf, n);
                Interlocked.Add(ref _rtcmToReceiver, n);
            }
        }
        catch (Exception ex)
        {
            Dispatcher.BeginInvoke(() => AppendLog($"Schreiben zum Empfänger fehlgeschlagen: {ex.Message}"));
        }
    }

    // =====================================================================
    // Anzeige: Position / Status
    // =====================================================================

    static string Fmt(double v, string f) => double.IsNaN(v) ? "–" : v.ToString(f, CultureInfo.CurrentCulture);

    static Brush FixBrush(int q) => q switch
    {
        4 => Brushes.ForestGreen,
        5 => Brushes.DarkOrange,
        0 => Brushes.Gray,
        _ => Brushes.Firebrick
    };

    void ShowPosition(GgaData? g)
    {
        FixBorder.Background = FixBrush(g?.Quality ?? 0);
        FixText.Text = g?.QualityText ?? "Kein Fix";

        if (g == null || !g.HasPosition)
        {
            LatText.Text = LonText.Text = EastText.Text = NorthText.Text = HEllText.Text = HMslText.Text = "–";
            StripPos.Text = "–"; StripUtm.Text = ""; StripHeight.Text = "";
            SatText.Text = g == null ? "–" : $"{g.Satellites} / {Fmt(g.Hdop, "F1")}";
            StripSat.Text = g == null ? "" : $"Sat {g.Satellites}  HDOP {Fmt(g.Hdop, "F1")}";
            return;
        }

        var (zone, e, n) = Utm.FromLatLon(g.Latitude, g.Longitude);
        string lat = g.Latitude.ToString("F9", CultureInfo.CurrentCulture) + "°";
        string lon = g.Longitude.ToString("F9", CultureInfo.CurrentCulture) + "°";
        LatText.Text = lat; LonText.Text = lon;
        EastText.Text = $"{zone}  {e.ToString("F3", CultureInfo.CurrentCulture)} m";
        NorthText.Text = n.ToString("F3", CultureInfo.CurrentCulture) + " m";
        HEllText.Text = Fmt(g.EllipsoidalHeight, "F3") + " m";
        HMslText.Text = Fmt(g.AltitudeMsl, "F3") + " m";
        SatText.Text = $"{g.Satellites} / {Fmt(g.Hdop, "F1")}";

        StripPos.Text = $"{g.Latitude.ToString("F8", CultureInfo.CurrentCulture)}°  {g.Longitude.ToString("F8", CultureInfo.CurrentCulture)}°";
        StripUtm.Text = $"E {e.ToString("F3", CultureInfo.CurrentCulture)}  N {n.ToString("F3", CultureInfo.CurrentCulture)}";
        StripHeight.Text = $"h {Fmt(g.EllipsoidalHeight, "F3")} m";
        StripSat.Text = $"Sat {g.Satellites}  HDOP {Fmt(g.Hdop, "F1")}" + (g.DiffAgeSeconds is { } a ? $"  Korr. {a.ToString("F0", CultureInfo.CurrentCulture)} s" : "");
    }

    static Brush Chip(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;

    void UpdateStatus()
    {
        long now = Environment.TickCount64;

        // Empfänger
        if (_rx.IsOpen)
        {
            long last = Interlocked.Read(ref _nmeaTicks);
            if (last == 0 && now - _connectedAt > 4000)
            {
                ReceiverStatus.Text = $"Empfänger: {PortBox.Text} – keine NMEA-Daten! Baudrate/Port prüfen (LC29H: meist 460800).";
                ReceiverDot.Fill = Chip("#F08C00");
            }
            else if (last != 0)
            {
                ReceiverStatus.Text = $"Empfänger: {PortBox.Text} verbunden, {_rx.BytesReceived / 1024} kB empfangen";
                ReceiverDot.Fill = now - last < 3000 ? Chip("#40C057") : Chip("#F08C00");
            }
        }
        else ReceiverDot.Fill = Chip("#868E96");
        ReceiverChip.Text = _rx.IsOpen ? $"Empfänger {PortBox.Text}" : "Empfänger";

        // NTRIP
        var lastMsg = _monitor.LastMessage;
        bool flowing = lastMsg != null && (DateTime.Now - lastMsg.Value).TotalSeconds < 5;
        NtripDot.Fill = _ntripState switch
        {
            "connected" => flowing ? Chip("#40C057") : Chip("#F08C00"),
            "connecting" => Chip("#F08C00"),
            _ => Chip("#868E96")
        };
        NtripChip.Text = _ntripState == "connected" ? $"NTRIP {MountpointBox.Text}" : "NTRIP";

        // Genauigkeit (GST)
        var gst = _gst;
        AccText.Text = gst != null && now - Interlocked.Read(ref _gstTicks) < 5000 && !double.IsNaN(gst.StdLat)
            ? $"σ Lage {Math.Sqrt(gst.StdLat * gst.StdLat + gst.StdLon * gst.StdLon) * 100:F1} cm, σ Höhe {gst.StdAlt * 100:F1} cm"
            : "– (GST-Satz nicht aktiv)";

        // Datenübersicht + RTCM
        long bytes = _ntrip.TotalBytes;
        double kbps = Math.Max(0, bytes - _lastBytes) / 1024.0;
        _lastBytes = bytes;
        DataStatus.Text = $"RTCM vom Caster: {bytes / 1024.0:F1} kB ({kbps:F1} kB/s), {_rtcm.TotalFrames} Nachrichten\n" +
                          $"RTCM an Empfänger: {Interlocked.Read(ref _rtcmToReceiver) / 1024.0:F1} kB";
        _monitor.Fill(_rtcmRows);
        UpdateStationInfo(kbps, lastMsg);

        // Autosave
        if (_projectDirty && now - _lastAutosave > 10000) Autosave();
    }

    void UpdateStationInfo(double kbps, DateTime? lastMsg)
    {
        var st = _monitor.Station;
        var d = _monitor.Descriptor;
        if (st == null && d == null && lastMsg == null)
        {
            RtcmStationText.Text = "Noch keine Korrekturdaten empfangen.\nVerbinden Sie NTRIP im Reiter „Verbindung“.";
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("REFERENZSTATION (RTCM 1005/1006)");
        if (st != null)
        {
            var g = st.Geodetic;
            sb.AppendLine($" Stations-ID : {st.StationId}");
            sb.AppendLine($" Breite      : {g.Lat.ToString("F8", CultureInfo.CurrentCulture)}°");
            sb.AppendLine($" Länge       : {g.Lon.ToString("F8", CultureInfo.CurrentCulture)}°");
            sb.AppendLine($" Höhe ellips.: {g.H.ToString("F3", CultureInfo.CurrentCulture)} m");
            if (_gga is { HasPosition: true } cur)
            {
                double dist = Geo.Distance(cur.Latitude, cur.Longitude, g.Lat, g.Lon);
                sb.AppendLine($" Basislinie  : {(dist < 1000 ? dist.ToString("F1", CultureInfo.CurrentCulture) + " m" : (dist / 1000).ToString("F2", CultureInfo.CurrentCulture) + " km")}");
            }
            if (st != _shownStation)
            {
                _shownStation = st;
                _map.SetReference(g.Lat, g.Lon, $"Referenz {st.StationId}");
            }
        }
        else sb.AppendLine(" (noch nicht empfangen)");

        if (d != null)
        {
            sb.AppendLine();
            sb.AppendLine("REFERENZ-AUSRÜSTUNG (1033)");
            sb.AppendLine($" Empfänger   : {d.Receiver}");
            sb.AppendLine($" Firmware    : {d.Firmware}");
            sb.AppendLine($" Antenne     : {d.Antenna}");
        }

        sb.AppendLine();
        sb.AppendLine("KORREKTURDATEN");
        sb.AppendLine($" Satelliten  : {_monitor.SatelliteSummary()}");
        sb.AppendLine($" Datenrate   : {kbps.ToString("F1", CultureInfo.CurrentCulture)} kB/s");
        sb.AppendLine($" Nachrichten : {_rtcm.TotalFrames}");
        sb.AppendLine($" Letzte      : " + (lastMsg == null ? "–" : $"vor {(DateTime.Now - lastMsg.Value).TotalSeconds:F0} s"));
        RtcmStationText.Text = sb.ToString();
    }

    // =====================================================================
    // Protokoll / NMEA
    // =====================================================================

    void AppendLog(string msg)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {msg}\n");
        TrimBox(LogBox);
        LogBox.ScrollToEnd();
    }

    void AppendNmea(string line)
    {
        if (NmeaPauseBox.IsChecked == true) return;
        NmeaBox.AppendText(line + "\n");
        TrimBox(NmeaBox);
        NmeaBox.ScrollToEnd();
    }

    static void TrimBox(TextBox box)
    {
        if (box.Text.Length < 30000) return;
        var t = box.Text;
        int cut = t.IndexOf('\n', t.Length / 2);
        if (cut > 0) box.Text = t[(cut + 1)..];
    }

    // =====================================================================
    // Karte: Werkzeuge, Layer, Push
    // =====================================================================

    string SelectedLayer() => (LayerBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "osm";

    void LayerBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_init) _map.SetLayer(SelectedLayer());
    }

    void FollowToggle_Click(object sender, RoutedEventArgs e)
    {
        _map.SetFollow(FollowToggle.IsChecked == true);
        if (FollowToggle.IsChecked == true && _gga is { HasPosition: true } g)
            _map.UpdatePosition(g.Latitude, g.Longitude, g.Quality, CurrentHeading());
    }

    void DragToggle_Click(object sender, RoutedEventArgs e)
    {
        _map.SetDraggable(DragToggle.IsChecked == true);
        _pointsDirty = true;
    }

    void FitButton_Click(object sender, RoutedEventArgs e)
    {
        var pts = new List<(double, double)>();
        pts.AddRange(_points.Select(p => (p.Latitude, p.Longitude)));
        pts.AddRange(_tracks.SelectMany(t => t.Points.Where((_, i) => i % 10 == 0)).Select(p => (p.Lat, p.Lon)));
        if (_gga is { HasPosition: true } g) pts.Add((g.Latitude, g.Longitude));
        if (pts.Count == 0) return;
        FollowToggle.IsChecked = false;
        _map.SetFollow(false);
        _map.Fit(pts);
    }

    void SetTool(Tool tool)
    {
        _tool = tool;
        AddPointToggle.IsChecked = tool == Tool.AddPoint;
        MeasureMapToggle.IsChecked = tool == Tool.Measure;
        _map.SetTool(tool == Tool.Pan ? "pan" : "cross");
        HintBar.Visibility = tool == Tool.Pan ? Visibility.Collapsed : Visibility.Visible;
        HintText.Text = tool switch
        {
            Tool.AddPoint => "Grenzpunkt setzen: Klick in die Karte legt einen Punkt an.  Esc beendet den Modus.",
            Tool.Measure => CurrentKind == MeasureKind.Flaeche
                ? "Fläche messen: Klicken Sie die Eckpunkte nacheinander in die Karte.  Esc beendet den Modus."
                : "Strecke messen: Klicken Sie die Messpunkte nacheinander in die Karte.  Esc beendet den Modus.",
            _ => ""
        };
    }

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _tool != Tool.Pan)
        {
            SetTool(Tool.Pan);
            e.Handled = true;
        }
    }

    void RightTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_init || !ReferenceEquals(e.OriginalSource, RightTabs)) return;
        // Beim Wechsel des Reiters ein evtl. aktives Karten-Werkzeug beenden.
        if (_tool != Tool.Pan) SetTool(Tool.Pan);
    }

    void OnMapClicked(double lat, double lon)
    {
        switch (_tool)
        {
            case Tool.AddPoint:
                var p = new BoundaryPoint { Name = TakeNextName(), Latitude = lat, Longitude = lon, Source = "Karte" };
                _points.Add(p);
                PointsGrid.SelectedItem = p;
                PointsGrid.ScrollIntoView(p);
                AppendLog($"Grenzpunkt {p.Name} gesetzt: {lat:F8}, {lon:F8}");
                break;
            case Tool.Measure:
                if (CurrentKind != MeasureKind.Hoehe) AddVertex(new MeasVertex { Lat = lat, Lon = lon, Source = "Karte" });
                break;
        }
    }

    void PushMapState()
    {
        if (!_map.IsReady) return;

        if (_pointsDirty)
        {
            _pointsDirty = false;
            _map.SetPoints(_points, (PointsGrid.SelectedItem as BoundaryPoint)?.Id, NavActive ? NavTarget?.Id : null);
            UpdateNavigation();
        }
        if (_tracksDirty)
        {
            _tracksDirty = false;
            _map.SetTracks(_tracks.Where(t => t != _rec), (TracksGrid.SelectedItem as TrackRecording)?.Id);
        }
        if (_measDirty)
        {
            _measDirty = false;
            _map.SetMeasures(_measurements, (MeasurementsGrid.SelectedItem as MeasurementItem)?.Id);
        }
        if (_draftDirty)
        {
            _draftDirty = false;
            _map.SetDraft(CurrentKind, _draft);
        }
    }

    // =====================================================================
    // Hilfen
    // =====================================================================

    static bool TryParseDouble(string? s, out double v)
    {
        s = (s ?? "").Trim();
        return double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out v) ||
               double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    static double ParseOr(string? s, double fallback) => TryParseDouble(s, out var v) ? v : fallback;

    void Window_Closing(object? sender, CancelEventArgs e)
    {
        _timer.Stop();
        _mapTimer.Stop();
        _replayTimer.Stop();
        FinishRecordingIfAny();
        Autosave();
        _ntripCts?.Cancel();
        _rx.Dispose();
        _tiles.Store?.Dispose();
    }

    // =====================================================================
    // Offline-Karte
    // =====================================================================

    /// <summary>Kachelspeicher laut appsettings.json ("Map": { "TileStorePath", "OnlineFallback" }) öffnen.</summary>
    TileService CreateTileService()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NtripRtk", "Offline", "hessen-kacheln.sqlite");
        bool online = true;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json")));
            if (doc.RootElement.TryGetProperty("Map", out var map))
            {
                if (map.TryGetProperty("TileStorePath", out var p) && !string.IsNullOrWhiteSpace(p.GetString()))
                    path = Environment.ExpandEnvironmentVariables(p.GetString()!);
                if (map.TryGetProperty("OnlineFallback", out var o) && o.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                    online = o.GetBoolean();
            }
        }
        catch { /* Standardwerte */ }

        TileStore? store = null;
        try { store = new TileStore(path); }
        catch (Exception ex) { _startupWarnings.Add($"Offline-Kachelspeicher konnte nicht geöffnet werden ({path}): {ex.Message}"); }

        var svc = new TileService(store, online);
        svc.Log += msg => Dispatcher.BeginInvoke(() => AppendLog(msg));
        return svc;
    }

    readonly List<string> _startupWarnings = new();

    void UpdateTileInfo()
    {
        var stats = _tiles.Store?.Stats();
        if (stats == null) { TileInfoText.Text = "Kein Offline-Speicher"; return; }
        long n = stats.Sum(s => s.Count), b = stats.Sum(s => s.Bytes);
        TileInfoText.Text = n == 0 ? "Offline-Karte: leer" : $"Offline-Karte: {n:N0} Kacheln, {b / 1024.0 / 1024.0:F0} MB";
    }

    void OnlineToggle_Click(object sender, RoutedEventArgs e)
    {
        _tiles.AllowNetwork = OnlineToggle.IsChecked == true;
        AppendLog(_tiles.AllowNetwork
            ? "Online nachladen: EIN – fehlende Kacheln werden geladen und gespeichert."
            : "Online nachladen: AUS – es werden nur gespeicherte Offline-Kacheln angezeigt.");
    }

    void MenuOffline_Click(object sender, RoutedEventArgs e)
    {
        var win = new OfflineMapWindow(_tiles,
            () => _map.GetBoundsAsync(),
            () => _points.Select(p => (p.Latitude, p.Longitude)).ToList(),
            () => _tracks.SelectMany(t => t.Points).Select(p => (p.Lat, p.Lon)).ToList(),
            () => _gga is { HasPosition: true, Quality: > 0 } g ? (g.Latitude, g.Longitude) : null,
            () => { _map.RedrawTiles(); UpdateTileInfo(); })
        { Owner = this };
        win.ShowDialog();
        UpdateTileInfo();
    }

    void MenuOpenTileFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = _tiles.Store != null ? Path.GetDirectoryName(_tiles.Store.Path) : null;
        if (dir != null && Directory.Exists(dir))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }
}
