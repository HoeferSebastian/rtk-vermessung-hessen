using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Ntrip.Wpf.Offline;

public partial class OfflineMapWindow : Window
{
    readonly TileService _tiles;
    readonly Func<Task<MapBounds?>> _getBounds;
    readonly Func<IReadOnlyList<(double Lat, double Lon)>> _points;
    readonly Func<IReadOnlyList<(double Lat, double Lon)>> _trackPoints;
    readonly Func<(double Lat, double Lon)?> _position;
    readonly Action _tilesChanged;

    CancellationTokenSource? _cts;
    bool _ready;
    int _planVersion;
    List<TileKey>? _plan;

    const int MaxTilesPerJob = 600_000;
    const int ConfirmTiles = 20_000;

    public OfflineMapWindow(TileService tiles, Func<Task<MapBounds?>> getBounds,
        Func<IReadOnlyList<(double, double)>> points, Func<IReadOnlyList<(double, double)>> trackPoints,
        Func<(double, double)?> position, Action tilesChanged)
    {
        InitializeComponent();
        _tiles = tiles;
        _getBounds = getBounds;
        _points = points;
        _trackPoints = trackPoints;
        _position = position;
        _tilesChanged = tilesChanged;

        for (int z = 10; z <= 20; z++) { ZoomMin.Items.Add(z); ZoomMax.Items.Add(z); }
        ZoomMin.SelectedItem = 16;
        ZoomMax.SelectedItem = 20;

        _ready = true;
        RefreshStore();
        _ = RecalculateAsync();
    }

    void Input_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready) _ = RecalculateAsync();
    }

    // ---------- Planung ----------

    IReadOnlyList<string> SelectedLayers() => new[]
    {
        LayerAlk.IsChecked == true ? "alk" : null,
        LayerDop.IsChecked == true ? "dop" : null,
        LayerAlkT.IsChecked == true ? "alk_t" : null
    }.Where(l => l != null).Cast<string>().ToList();

    async Task<List<GeoRect>?> BuildAreaAsync()
    {
        double buffer = double.TryParse(BufferBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var b)
            ? Math.Clamp(b, 0, 20000) : 300;

        if (AreaView.IsChecked == true)
        {
            var mb = await _getBounds();
            return mb == null ? null : new List<GeoRect> { new(mb.South, mb.West, mb.North, mb.East) };
        }
        if (AreaPoints.IsChecked == true)
        {
            var pts = _points();
            return pts.Count == 0 ? null : pts.Select(p => TileMath.Around(p.Lat, p.Lon, buffer)).ToList();
        }
        if (AreaTracks.IsChecked == true)
        {
            var pts = _trackPoints();
            if (pts.Count == 0) return null;
            int step = Math.Max(1, pts.Count / 400);
            return pts.Where((_, i) => i % step == 0).Select(p => TileMath.Around(p.Lat, p.Lon, buffer)).ToList();
        }
        var pos = _position();
        return pos == null ? null : new List<GeoRect> { TileMath.Around(pos.Value.Lat, pos.Value.Lon, buffer) };
    }

    async Task RecalculateAsync()
    {
        int version = ++_planVersion;
        _plan = null;
        StartButton.IsEnabled = false;
        PlanText.Text = "Berechne …";

        int zMin = ZoomMin.SelectedItem as int? ?? 16, zMax = ZoomMax.SelectedItem as int? ?? 20;
        var layers = SelectedLayers();
        if (zMin > zMax) { PlanText.Text = "„von“ darf nicht größer als „bis“ sein."; return; }
        if (layers.Count == 0) { PlanText.Text = "Bitte mindestens einen Karteninhalt wählen."; return; }

        var rects = await BuildAreaAsync();
        if (version != _planVersion) return;
        if (rects == null)
        {
            PlanText.Text = AreaView.IsChecked == true ? "Kartenausschnitt nicht verfügbar (Karte noch nicht geladen)."
                : AreaPoints.IsChecked == true ? "Es sind keine Grenzpunkte vorhanden."
                : AreaTracks.IsChecked == true ? "Es sind keine Spuren vorhanden."
                : "Keine GNSS-Position vorhanden (Empfänger verbinden).";
            return;
        }

        var xy = await Task.Run(() => TileMath.Enumerate(rects, zMin, zMax, MaxTilesPerJob / layers.Count));
        if (version != _planVersion) return;
        if (xy == null)
        {
            PlanText.Text = $"Zu viele Kacheln (mehr als {MaxTilesPerJob / layers.Count:N0} je Karteninhalt). Gebiet verkleinern oder weniger Detailstufen wählen.";
            return;
        }

        var plan = new List<TileKey>(xy.Count * layers.Count);
        foreach (var l in layers) foreach (var t in xy) plan.Add(new TileKey(l, t.Z, t.X, t.Y));
        _plan = plan;

        long est = layers.Sum(l => (long)TileLayers.Find(l)!.EstimatedBytes * xy.Count);
        PlanText.Text = $"{plan.Count:N0} Kacheln ({xy.Count:N0} je Karteninhalt × {layers.Count}), geschätzt {FormatBytes(est)}, Zoom {zMin}–{zMax}.";
        StartButton.IsEnabled = _cts == null;
    }

    static string FormatBytes(long b) => b switch
    {
        < 1024 * 1024 => $"{b / 1024.0:F0} kB",
        < 1024L * 1024 * 1024 => $"{b / 1024.0 / 1024.0:F0} MB",
        _ => $"{b / 1024.0 / 1024.0 / 1024.0:F2} GB"
    };

    // ---------- Download ----------

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_plan == null || _plan.Count == 0 || _tiles.Store == null)
        {
            MessageBox.Show(this, "Es gibt nichts herunterzuladen oder der Kachelspeicher ist nicht verfügbar.", Title);
            return;
        }
        if (_plan.Count > ConfirmTiles &&
            MessageBox.Show(this, $"{_plan.Count:N0} Kacheln herunterladen? Das kann lange dauern und viel Speicherplatz belegen.\n\n{PlanText.Text}",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _cts = new CancellationTokenSource();
        SetBusy(true);
        var started = Stopwatch.StartNew();
        var progress = new Progress<DownloadProgress>(p =>
        {
            Progress.Value = p.Total == 0 ? 0 : (double)p.Done / p.Total;
            double rate = p.Done / Math.Max(1, started.Elapsed.TotalSeconds);
            string eta = rate > 0.5 && p.Done < p.Total ? $", noch ca. {TimeSpan.FromSeconds((p.Total - p.Done) / rate):hh\\:mm\\:ss}" : "";
            ProgressText.Text = $"{p.Done:N0} / {p.Total:N0} Kacheln · neu {p.Downloaded:N0} · vorhanden {p.Skipped:N0} · Fehler {p.Failed:N0} · {FormatBytes(p.Bytes)}{eta}";
        });

        try
        {
            await _tiles.DownloadAsync(_plan, OverwriteBox.IsChecked == true, progress, _cts.Token);
            ProgressText.Text += "\n✔ Fertig.";
        }
        catch (OperationCanceledException) { ProgressText.Text += "\nAbgebrochen – bereits geladene Kacheln bleiben gespeichert."; }
        catch (Exception ex) { ProgressText.Text += "\n✖ " + ex.Message; }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
            RefreshStore();
            _tilesChanged();
        }
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    void SetBusy(bool busy)
    {
        StartButton.IsEnabled = !busy && _plan is { Count: > 0 };
        CancelButton.IsEnabled = busy;
        foreach (var c in new Control[] { AreaView, AreaPoints, AreaTracks, AreaPosition, BufferBox, LayerAlk, LayerDop, LayerAlkT, ZoomMin, ZoomMax, OverwriteBox })
            c.IsEnabled = !busy;
    }

    // ---------- Speicher ----------

    void RefreshStore()
    {
        var store = _tiles.Store;
        if (store == null)
        {
            StoreText.Text = "Kein Kachelspeicher verfügbar (Datei konnte nicht geöffnet werden).";
            StorePathText.Text = "";
            return;
        }
        var stats = store.Stats();
        if (stats.Count == 0) StoreText.Text = "Noch keine Offline-Daten gespeichert.";
        else
        {
            var lines = stats.Select(s =>
            {
                var def = TileLayers.Find(s.Layer);
                return $"{(def?.WmsLayer ?? s.Layer),-14} {s.Count,9:N0} Kacheln  {FormatBytes(s.Bytes),8}  Zoom {s.ZMin}–{s.ZMax}  Stand {s.Oldest:dd.MM.yyyy}";
            });
            StoreText.Text = string.Join("\n", lines) + $"\n{"Gesamt",-14} {stats.Sum(s => s.Count),9:N0} Kacheln  {FormatBytes(stats.Sum(s => s.Bytes)),8}";
        }
        StorePathText.Text = store.Path;
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = _tiles.Store != null ? Path.GetDirectoryName(_tiles.Store.Path) : null;
        if (dir != null && Directory.Exists(dir)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (_tiles.Store == null) return;
        if (MessageBox.Show(this, "Wirklich ALLE gespeicherten Offline-Kacheln löschen?\nDanach ist die Karte ohne Internet leer.",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _tiles.Store.Clear();
        RefreshStore();
        _tilesChanged();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_cts == null) return;
        if (MessageBox.Show(this, "Der Download läuft noch. Abbrechen und schließen?", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            e.Cancel = true;
        else _cts.Cancel();
    }
}
