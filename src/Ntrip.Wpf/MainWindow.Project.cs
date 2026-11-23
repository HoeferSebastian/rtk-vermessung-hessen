using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Xml.Linq;
using Microsoft.Win32;
using Ntrip.Core;

namespace Ntrip.Wpf;

public partial class MainWindow
{
    static readonly string AutosavePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NtripRtk", "autosave.rtkproj.json");

    string? _projectPath;
    string _projectName = "Neues Projekt";
    long _lastAutosave;

    void SetProjectName(string name)
    {
        _projectName = name;
        Title = $"RTK Vermessung – {name} – Quectel LC29H + SAPOS NTRIP";
    }

    ProjectData BuildProject() => new()
    {
        Name = _projectName,
        Points = _points.ToList(),
        Tracks = _tracks.ToList(),
        Measurements = _measurements.ToList()
    };

    void LoadProject(ProjectData d)
    {
        StopNavigation();
        FinishRecordingIfAny();
        SetRecordingButtons(false);
        _points.Clear(); _tracks.Clear(); _measurements.Clear(); _draft.Clear();
        foreach (var p in d.Points) _points.Add(p);
        foreach (var t in d.Tracks) { t.Recompute(); _tracks.Add(t); }
        foreach (var m in d.Measurements) _measurements.Add(m);
        SetProjectName(d.Name);
        RefreshMeasureResult();
        _pointsDirty = _tracksDirty = _measDirty = _draftDirty = true;
    }

    // ---------- Autosave ----------

    void Autosave()
    {
        try
        {
            BuildProject().Save(AutosavePath);
            _lastAutosave = Environment.TickCount64;
            _projectDirty = false;
        }
        catch (Exception ex) { AppendLog($"Autosave fehlgeschlagen: {ex.Message}"); }
    }

    void RestoreAutosave()
    {
        try
        {
            if (!File.Exists(AutosavePath)) return;
            var d = ProjectData.Load(AutosavePath);
            if (d.Points.Count + d.Tracks.Count + d.Measurements.Count == 0) return;
            LoadProject(d);
            _projectDirty = false;
            AppendLog($"Letzten Stand wiederhergestellt: {d.Points.Count} Punkte, {d.Tracks.Count} Spuren, {d.Measurements.Count} Messungen.");
        }
        catch (Exception ex)
        {
            // Defekte Datei sichern, damit sie nicht beim nächsten Autosave überschrieben wird.
            try { File.Copy(AutosavePath, AutosavePath.Replace(".rtkproj.json", ".defekt.json"), overwrite: true); } catch { }
            AppendLog($"Autosave konnte nicht geladen werden (Kopie: autosave.defekt.json): {ex.Message}");
        }
    }

    // ---------- Menü: Projekt ----------

    const string ProjectFilter = "RTK-Projekt (*.rtkproj.json)|*.rtkproj.json|JSON (*.json)|*.json";

    void MenuNew_Click(object sender, RoutedEventArgs e)
    {
        if (_points.Count + _tracks.Count + _measurements.Count > 0 &&
            MessageBox.Show(this, "Aktuelles Projekt verwerfen? Nicht gespeicherte Daten gehen verloren.", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _projectPath = null;
        LoadProject(new ProjectData());
        _projectDirty = true;
    }

    void MenuOpen_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = ProjectFilter };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            LoadProject(ProjectData.Load(dlg.FileName));
            _projectPath = dlg.FileName;
            AppendLog($"Projekt geöffnet: {dlg.FileName}");
            _projectDirty = true;
            if (_points.Count > 0) FitButton_Click(sender, e);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Öffnen fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    void MenuSave_Click(object sender, RoutedEventArgs e)
    {
        if (_projectPath == null) { MenuSaveAs_Click(sender, e); return; }
        SaveProjectTo(_projectPath);
    }

    void MenuSaveAs_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = ProjectFilter, FileName = $"{_projectName}.rtkproj.json" };
        if (dlg.ShowDialog(this) != true) return;
        _projectPath = dlg.FileName;
        var name = Path.GetFileName(dlg.FileName);
        SetProjectName(name.EndsWith(".rtkproj.json", StringComparison.OrdinalIgnoreCase) ? name[..^13] : Path.GetFileNameWithoutExtension(name));
        SaveProjectTo(dlg.FileName);
    }

    void SaveProjectTo(string path)
    {
        try
        {
            BuildProject().Save(path);
            AppendLog($"Projekt gespeichert: {path}");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Speichern fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    void MenuExit_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- Export ----------

    static string Inv(double v, string f = "F9") => v.ToString(f, CultureInfo.InvariantCulture);

    void MenuExportGpx_Click(object sender, RoutedEventArgs e)
    {
        if (_points.Count + _tracks.Count == 0) { Info("Es gibt keine Punkte oder Spuren zum Exportieren."); return; }
        var dlg = new SaveFileDialog { Filter = "GPX (*.gpx)|*.gpx", FileName = $"{_projectName}_{DateTime.Now:yyyyMMdd_HHmm}.gpx" };
        if (dlg.ShowDialog(this) != true) return;

        XNamespace ns = "http://www.topografix.com/GPX/1/1";
        var gpx = new XElement(ns + "gpx",
            new XAttribute("version", "1.1"),
            new XAttribute("creator", "RTK Vermessung"),
            new XElement(ns + "metadata",
                new XElement(ns + "name", _projectName),
                new XElement(ns + "desc", "ETRS89/WGS84; <ele> = ellipsoidische Höhe in Metern")));

        foreach (var p in _points)
        {
            var w = new XElement(ns + "wpt", new XAttribute("lat", Inv(p.Latitude)), new XAttribute("lon", Inv(p.Longitude)));
            if (p.Height is { } h) w.Add(new XElement(ns + "ele", Inv(h, "F3")));
            w.Add(new XElement(ns + "time", p.Created.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")));
            w.Add(new XElement(ns + "name", p.Name));
            if (!string.IsNullOrWhiteSpace(p.Note)) w.Add(new XElement(ns + "cmt", p.Note));
            gpx.Add(w);
        }

        foreach (var t in _tracks)
        {
            var seg = new XElement(ns + "trkseg");
            foreach (var tp in t.Points)
            {
                var pt = new XElement(ns + "trkpt", new XAttribute("lat", Inv(tp.Lat)), new XAttribute("lon", Inv(tp.Lon)));
                if (!double.IsNaN(tp.H)) pt.Add(new XElement(ns + "ele", Inv(tp.H, "F3")));
                pt.Add(new XElement(ns + "time", tp.T.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")));
                if (!double.IsNaN(tp.H) && !double.IsNaN(tp.Hmsl)) pt.Add(new XElement(ns + "geoidheight", Inv(tp.H - tp.Hmsl, "F3")));
                seg.Add(pt);
            }
            gpx.Add(new XElement(ns + "trk", new XElement(ns + "name", t.Name), seg));
        }

        try
        {
            new XDocument(new XDeclaration("1.0", "utf-8", null), gpx).Save(dlg.FileName);
            AppendLog($"GPX exportiert: {dlg.FileName} ({_points.Count} Punkte, {_tracks.Count} Spuren)");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    void MenuExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_points.Count == 0) { Info("Es sind keine Grenzpunkte vorhanden."); return; }
        var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"Grenzpunkte_{DateTime.Now:yyyyMMdd_HHmm}.csv" };
        if (dlg.ShowDialog(this) != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("Name;UTM_Zone;Ost_m;Nord_m;Breite_deg;Laenge_deg;h_ellipsoid_m;Quelle;Qualitaet;Epochen;sigma_Ost_mm;sigma_Nord_mm;sigma_Hoehe_mm;Bemerkung");
        foreach (var p in _points)
        {
            var (zone, east, north) = Utm.FromLatLon(p.Latitude, p.Longitude);
            string Opt(double? v, string f) => v is { } x ? Inv(x, f) : "";
            sb.AppendLine(string.Join(';', p.Name, zone, Inv(east, "F3"), Inv(north, "F3"), Inv(p.Latitude), Inv(p.Longitude),
                Opt(p.Height, "F3"), p.Source, p.Quality, p.Epochs, Opt(p.StdEast, "F1"), Opt(p.StdNorth, "F1"), Opt(p.StdHeight, "F1"),
                p.Note.Replace(';', ',')));
        }
        try
        {
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            AppendLog($"CSV exportiert: {dlg.FileName}");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    // ---------- Import ----------

    void MenuImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "GPX oder CSV (*.gpx;*.csv;*.txt)|*.gpx;*.csv;*.txt" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            if (dlg.FileName.EndsWith(".gpx", StringComparison.OrdinalIgnoreCase)) ImportGpx(dlg.FileName);
            else ImportCsv(dlg.FileName);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Import fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    static double GpxNum(XElement? e) =>
        e != null && double.TryParse(e.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;

    void ImportGpx(string path)
    {
        var doc = XDocument.Load(path);
        XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        int pc = 0, tc = 0;

        foreach (var w in doc.Descendants(ns + "wpt"))
        {
            double lat = GpxNum(new XElement("x", (string?)w.Attribute("lat"))), lon = GpxNum(new XElement("x", (string?)w.Attribute("lon")));
            if (double.IsNaN(lat) || double.IsNaN(lon)) continue;
            double ele = GpxNum(w.Element(ns + "ele"));
            _points.Add(new BoundaryPoint
            {
                Name = (string?)w.Element(ns + "name") ?? $"WP{_points.Count + 1}",
                Latitude = lat, Longitude = lon, Height = double.IsNaN(ele) ? null : ele,
                Note = (string?)w.Element(ns + "cmt") ?? "", Source = "Import"
            });
            pc++;
        }

        foreach (var trk in doc.Descendants(ns + "trk"))
        {
            var t = new TrackRecording
            {
                Name = (string?)trk.Element(ns + "name") ?? $"Spur {_tracks.Count + 1}",
                Color = TrackColors[_tracks.Count % TrackColors.Length]
            };
            foreach (var pt in trk.Descendants(ns + "trkpt"))
            {
                double lat = GpxNum(new XElement("x", (string?)pt.Attribute("lat"))), lon = GpxNum(new XElement("x", (string?)pt.Attribute("lon")));
                if (double.IsNaN(lat) || double.IsNaN(lon)) continue;
                double ele = GpxNum(pt.Element(ns + "ele")), geoid = GpxNum(pt.Element(ns + "geoidheight"));
                DateTime time = DateTime.TryParse((string?)pt.Element(ns + "time"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt) ? dt.ToLocalTime() : DateTime.Now;
                t.Points.Add(new TrackPoint { T = time, Lat = lat, Lon = lon, H = ele, Hmsl = double.IsNaN(geoid) ? double.NaN : ele - geoid });
            }
            if (t.Points.Count == 0) continue;
            t.Start = t.Points[0].T;
            t.Recompute();
            _tracks.Add(t);
            tc++;
        }
        AppendLog($"GPX importiert: {pc} Punkte, {tc} Spuren aus {Path.GetFileName(path)}");
        FitButton_Click(this, new RoutedEventArgs());
    }

    /// <summary>CSV: Name;Ost;Nord[;Höhe] (UTM, ETRS89) oder Name;Breite;Länge[;Höhe]. Trenner ; , oder Tab; Dezimalpunkt oder -komma.</summary>
    void ImportCsv(string path)
    {
        var lines = File.ReadAllLines(path).Where(l => l.Trim().Length > 0).ToList();
        int n = 0, skipped = 0;
        foreach (var line in lines)
        {
            char sep = line.Contains(';') ? ';' : line.Contains('\t') ? '\t' : ',';
            var f = line.Split(sep).Select(s => s.Trim().Trim('"')).ToArray();
            if (f.Length < 3) { skipped++; continue; }

            double Num(string s) => double.TryParse(sep == ',' ? s : s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
            double a = Num(f[1]), b = Num(f[2]);
            if (double.IsNaN(a) || double.IsNaN(b)) { skipped++; continue; } // Kopfzeile o. ä.

            double lat, lon;
            if (Math.Abs(a) <= 90 && Math.Abs(b) <= 180) { lat = a; lon = b; }
            else
            {
                int zone = 32;
                if (a >= 10_000_000) { zone = (int)(a / 1_000_000); a -= zone * 1_000_000.0; }
                (lat, lon) = Utm.ToLatLon(zone, a, b);
            }
            double h = f.Length > 3 ? Num(f[3]) : double.NaN;
            _points.Add(new BoundaryPoint
            {
                Name = f[0].Length > 0 ? f[0] : $"P{_points.Count + 1}",
                Latitude = lat, Longitude = lon, Height = double.IsNaN(h) ? null : h, Source = "Import"
            });
            n++;
        }
        AppendLog($"CSV importiert: {n} Punkte ({skipped} Zeilen übersprungen) aus {Path.GetFileName(path)}");
        if (n > 0) FitButton_Click(this, new RoutedEventArgs());
    }
}
