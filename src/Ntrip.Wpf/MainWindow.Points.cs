using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Ntrip.Core;

namespace Ntrip.Wpf;

public partial class MainWindow
{
    // Erfassung (Mittelung über mehrere Epochen)
    List<GgaData>? _capture;
    int _captureTarget;
    bool _captureOnlyFixed;
    string _captureName = "";
    BoundaryPoint? _captureReplace;

    /// <summary>Liefert den aktuellen Namen aus dem Namensfeld und zählt ihn für den nächsten Punkt hoch.</summary>
    string TakeNextName()
    {
        var name = PointNameBox.Text.Trim();
        if (name.Length == 0) name = $"GP{_points.Count + 1}";

        var digits = new string(name.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        PointNameBox.Text = digits.Length > 0 && int.TryParse(digits, out int num)
            ? name[..^digits.Length] + (num + 1).ToString(new string('0', digits.Length))
            : name + "2";
        return name;
    }

    void AddPointToggle_Click(object sender, RoutedEventArgs e) =>
        SetTool(AddPointToggle.IsChecked == true ? Tool.AddPoint : Tool.Pan);

    void NewPoint_Click(object sender, RoutedEventArgs e)
    {
        double lat = _fallback.Latitude, lon = _fallback.Longitude;
        double? h = null;
        if (_gga is { HasPosition: true } g)
        {
            lat = g.Latitude; lon = g.Longitude;
            if (!double.IsNaN(g.EllipsoidalHeight)) h = g.EllipsoidalHeight;
        }
        var p = new BoundaryPoint { Name = TakeNextName(), Latitude = lat, Longitude = lon, Height = h, Source = "Manuell" };
        _points.Add(p);
        PointsGrid.SelectedItem = p;
        PointsGrid.ScrollIntoView(p);
        AppendLog($"Punkt {p.Name} angelegt – Koordinaten in der Tabelle anpassen.");
    }

    // ---------- Erfassen ----------

    void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (_capture != null) { CancelCapture("Abgebrochen"); return; }
        StartCapture(null);
    }

    void StartCapture(BoundaryPoint? replace)
    {
        if (!_rx.IsOpen)
        {
            MessageBox.Show(this, "Bitte zuerst im Reiter „Verbindung“ den Empfänger verbinden.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!int.TryParse(EpochsBox.Text, out int n) || n < 1 || n > 3600)
        {
            MessageBox.Show(this, "Die Epochenzahl muss zwischen 1 und 3600 liegen.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _captureTarget = n;
        _captureOnlyFixed = OnlyFixedBox.IsChecked == true;
        _captureReplace = replace;
        _captureName = replace?.Name ?? TakeNextName();
        _capture = new List<GgaData>();
        CaptureProgress.Value = 0;
        CaptureText.Text = $"0/{n} Epochen";
        CaptureButton.Content = "✕ Erfassung abbrechen";
    }

    void CancelCapture(string text)
    {
        if (_capture == null) return;
        _capture = null;
        _captureReplace = null;
        CaptureButton.Content = "◉ Aktuelle Position erfassen";
        CaptureProgress.Value = 0;
        CaptureText.Text = text;
    }

    void HandleCapture(GgaData g)
    {
        if (_capture == null) return;

        if (!g.HasPosition || (_captureOnlyFixed ? g.Quality != 4 : g.Quality == 0))
        {
            CaptureText.Text = $"{_capture.Count}/{_captureTarget} – warte auf {(_captureOnlyFixed ? "RTK Fixed" : "gültigen Fix")} (aktuell: {g.QualityText})";
            return;
        }
        _capture.Add(g);
        CaptureProgress.Value = (double)_capture.Count / _captureTarget;
        CaptureText.Text = $"{_capture.Count}/{_captureTarget} Epochen";
        if (_capture.Count >= _captureTarget) FinishCapture();
    }

    void FinishCapture()
    {
        var s = _capture!;
        var replace = _captureReplace;
        _capture = null;
        _captureReplace = null;
        CaptureButton.Content = "◉ Aktuelle Position erfassen";

        double lat = s.Average(g => g.Latitude);
        double lon = s.Average(g => g.Longitude);
        var hs = s.Select(g => g.EllipsoidalHeight).Where(h => !double.IsNaN(h)).ToList();
        double? hEll = hs.Count > 0 ? hs.Average() : null;

        var utm = s.Select(g => Utm.FromLatLon(g.Latitude, g.Longitude)).ToList();
        double sdE = StdDev(utm.Select(u => u.Easting)) * 1000;
        double sdN = StdDev(utm.Select(u => u.Northing)) * 1000;
        double sdH = StdDev(hs) * 1000;

        int worst = s.Min(g => g.Quality == 4 ? 3 : g.Quality == 5 ? 2 : 1);
        string quality = worst == 3 ? "RTK Fixed" : worst == 2 ? "RTK Float" : "unter RTK";
        if (s.Select(g => g.Quality).Distinct().Count() > 1) quality += " (gemischt)";

        var bp = replace ?? new BoundaryPoint { Name = _captureName };
        bp.SetPosition(lat, lon);
        bp.Height = hEll;
        bp.Source = "GNSS";
        bp.Quality = quality;
        bp.Epochs = s.Count;
        bp.StdEast = sdE; bp.StdNorth = sdN; bp.StdHeight = sdH;
        bp.Created = DateTime.Now;
        if (replace == null) _points.Add(bp);
        else PointsGrid.Items.Refresh();
        _pointsDirty = _projectDirty = true;

        PointsGrid.SelectedItem = bp;
        PointsGrid.ScrollIntoView(bp);
        CaptureProgress.Value = 1;
        CaptureText.Text = $"„{bp.Name}“ gespeichert · σ Lage {Math.Sqrt(sdE * sdE + sdN * sdN):F1} mm, σ Höhe {sdH:F1} mm";
        AppendLog($"Erfasst: {bp.Name}  {lat:F9}, {lon:F9}  h={Fmt(hEll ?? double.NaN, "F3")} m  σE={sdE:F1} σN={sdN:F1} σH={sdH:F1} mm  ({quality}, {s.Count} Epochen)");
    }

    static double StdDev(IEnumerable<double> values)
    {
        var v = values.ToList();
        if (v.Count < 2) return 0;
        double m = v.Average();
        return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / (v.Count - 1));
    }

    // ---------- Tabelle / Aktionen ----------

    BoundaryPoint? SelectedPoint => PointsGrid.SelectedItem as BoundaryPoint;

    void PointsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => _pointsDirty = true;

    void PointsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        // Nach dem Commit Karte und Projekt aktualisieren.
        Dispatcher.BeginInvoke(() => { _pointsDirty = _projectDirty = true; });
    }

    void PointZoom_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPoint is not { } p) { Info("Bitte zuerst einen Punkt in der Tabelle wählen."); return; }
        FollowToggle.IsChecked = false;
        _map.SetFollow(false);
        _map.ZoomTo(p.Latitude, p.Longitude);
    }

    void PointRemeasure_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPoint is not { } p) { Info("Bitte zuerst einen Punkt in der Tabelle wählen."); return; }
        if (_capture != null) { Info("Es läuft bereits eine Erfassung."); return; }
        StartCapture(p);
    }

    void PointNavigate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPoint is not { } p) { Info("Bitte zuerst einen Punkt in der Tabelle wählen."); return; }
        NavTargetBox.SelectedItem = p;
        NavToggle.IsChecked = true;
        NavToggle_Click(NavToggle, new RoutedEventArgs());
        RightTabs.SelectedIndex = 2;
    }

    void PointDelete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPoint is not { } p) { Info("Bitte zuerst einen Punkt in der Tabelle wählen."); return; }
        if (MessageBox.Show(this, $"Punkt „{p.Name}“ löschen?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (NavTarget == p) StopNavigation();
        _points.Remove(p);
    }

    void Info(string text) => MessageBox.Show(this, text, Title, MessageBoxButton.OK, MessageBoxImage.Information);
}
