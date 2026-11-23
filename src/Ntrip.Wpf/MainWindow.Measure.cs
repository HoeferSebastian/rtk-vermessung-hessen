using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Ntrip.Core;

namespace Ntrip.Wpf;

public partial class MainWindow
{
    MeasureKind CurrentKind =>
        MeasureFlaeche.IsChecked == true ? MeasureKind.Flaeche :
        MeasureHoehe.IsChecked == true ? MeasureKind.Hoehe : MeasureKind.Strecke;

    static string N(double v, int d) => v.ToString("F" + d, CultureInfo.CurrentCulture);
    static string Sgn(double v, int d) => (v >= 0 ? "+" : "−") + Math.Abs(v).ToString("F" + d, CultureInfo.CurrentCulture);

    void UpdateMeasureHint()
    {
        MeasureHint.Text = CurrentKind switch
        {
            MeasureKind.Strecke => "Ergebnis: Länge entlang der Punkte, Luftlinie, Peilung und – bei Punkten mit Höhe – 3D-Länge und Höhenunterschied.",
            MeasureKind.Flaeche => "Mindestens 3 Eckpunkte in Umlaufrichtung. Ergebnis: Fläche in m² und ha sowie Umfang (auf dem Ellipsoid berechnet).",
            _ => "Höhen stammen aus der GNSS-Messung: „Aktuelle Position“ (Gerät am Punkt aufstellen, ruhig halten) oder Grenzpunkte mit Höhe. Karten-Klicks haben keine Höhe."
        };
    }

    void MeasureKind_Checked(object sender, RoutedEventArgs e)
    {
        if (!_init) return;
        bool hoehe = CurrentKind == MeasureKind.Hoehe;
        MeasureMapToggle.IsEnabled = !hoehe;
        if (hoehe && _tool == Tool.Measure) SetTool(Tool.Pan);
        else if (_tool == Tool.Measure) SetTool(Tool.Measure); // Hinweistext aktualisieren
        UpdateMeasureHint();
        _draftDirty = true;
        RefreshMeasureResult();
    }

    void MeasureMapToggle_Click(object sender, RoutedEventArgs e) =>
        SetTool(MeasureMapToggle.IsChecked == true ? Tool.Measure : Tool.Pan);

    void AddVertex(MeasVertex v)
    {
        _draft.Add(v);
        RefreshMeasureResult();
    }

    void MeasureAddCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_gga is not { HasPosition: true, Quality: > 0 } g)
        {
            Info("Keine gültige GNSS-Position vorhanden. Empfänger verbinden und auf Fix warten.");
            return;
        }
        if (g.Quality is not (4 or 5)) AppendLog($"Hinweis: Messpunkt ohne RTK ({g.QualityText}) – nur Meter-Genauigkeit.");
        AddVertex(new MeasVertex
        {
            Lat = g.Latitude, Lon = g.Longitude,
            H = double.IsNaN(g.EllipsoidalHeight) ? null : g.EllipsoidalHeight,
            Source = "GNSS " + g.QualityText
        });
    }

    void MeasureAddPoint_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPoint is not { } p) { Info("Bitte im Reiter „Punkte“ einen Grenzpunkt wählen."); return; }
        AddVertex(new MeasVertex { Lat = p.Latitude, Lon = p.Longitude, H = p.Height, Source = p.Name });
    }

    void MeasureAddAllPoints_Click(object sender, RoutedEventArgs e)
    {
        if (_points.Count == 0) { Info("Es sind keine Grenzpunkte vorhanden."); return; }
        foreach (var p in _points) _draft.Add(new MeasVertex { Lat = p.Latitude, Lon = p.Longitude, H = p.Height, Source = p.Name });
        RefreshMeasureResult();
    }

    void MeasureUndo_Click(object sender, RoutedEventArgs e)
    {
        if (_draft.Count == 0) return;
        _draft.RemoveAt(_draft.Count - 1);
        RefreshMeasureResult();
    }

    void MeasureClear_Click(object sender, RoutedEventArgs e)
    {
        _draft.Clear();
        RefreshMeasureResult();
    }

    void RefreshMeasureResult()
    {
        for (int i = 0; i < _draft.Count; i++) _draft[i].Index = i + 1;
        VertexGrid.Items.Refresh();
        var (text, _, _) = Evaluate(CurrentKind, _draft);
        MeasureResultText.Text = text;
        _draftDirty = true;
    }

    /// <summary>Berechnet Ergebnistext (mehrzeilig), Kurzfassung und Gültigkeit.</summary>
    static (string Text, string Summary, bool Valid) Evaluate(MeasureKind kind, IReadOnlyList<MeasVertex> v)
    {
        var xy = v.Select(p => (p.Lat, p.Lon)).ToList();
        var sb = new StringBuilder();

        switch (kind)
        {
            case MeasureKind.Strecke:
            {
                if (v.Count < 2) return ("Mindestens 2 Messpunkte nötig.", "", false);
                double len = Geo.PathLength(xy);
                double straight = Geo.Distance(v[0].Lat, v[0].Lon, v[^1].Lat, v[^1].Lon);
                double bearing = Geo.Bearing(v[0].Lat, v[0].Lon, v[^1].Lat, v[^1].Lon);
                sb.AppendLine($"Punkte             : {v.Count}");
                sb.AppendLine($"Länge (horizontal) : {N(len, 3)} m");
                string summary = $"L = {N(len, 2)} m";
                if (v.All(p => p.H != null))
                {
                    double l3 = 0;
                    for (int i = 1; i < v.Count; i++)
                    {
                        double d = Geo.Distance(v[i - 1].Lat, v[i - 1].Lon, v[i].Lat, v[i].Lon);
                        double dh = v[i].H!.Value - v[i - 1].H!.Value;
                        l3 += Math.Sqrt(d * d + dh * dh);
                    }
                    sb.AppendLine($"Länge (3D)         : {N(l3, 3)} m");
                    sb.AppendLine($"Δh Start → Ende    : {Sgn(v[^1].H!.Value - v[0].H!.Value, 3)} m");
                    summary += $" (3D {N(l3, 2)} m)";
                }
                sb.AppendLine($"Luftlinie          : {N(straight, 3)} m");
                sb.Append($"Peilung Start→Ende : {N(bearing, 1)}° ({Geo.CompassPoint(bearing)})");
                return (sb.ToString(), summary, true);
            }

            case MeasureKind.Flaeche:
            {
                if (v.Count < 3) return ("Mindestens 3 Eckpunkte nötig.", "", false);
                double area = Geo.PolygonArea(xy);
                double per = Geo.PathLength(xy, closed: true);
                sb.AppendLine($"Eckpunkte : {v.Count}");
                sb.AppendLine($"Fläche    : {N(area, 3)} m²");
                sb.AppendLine($"          = {N(area / 10000, 5)} ha");
                sb.Append($"Umfang    : {N(per, 3)} m");
                return (sb.ToString(), $"A = {N(area, 2)} m² ({N(area / 10000, 4)} ha) · U = {N(per, 2)} m", true);
            }

            default:
            {
                var withH = v.Where(p => p.H != null).ToList();
                if (withH.Count == 0) return ("Noch kein Punkt mit Höhe.\nNutzen Sie „Aktuelle Position“ oder Grenzpunkte mit Höhe.", "", false);
                double h0 = withH[0].H!.Value;
                double? prev = null;
                for (int i = 0; i < v.Count; i++)
                {
                    if (v[i].H is not { } h) { sb.AppendLine($"#{i + 1,-2} {v[i].Source,-14}  keine Höhe"); continue; }
                    sb.AppendLine($"#{i + 1,-2} h = {N(h, 3),9} m   " +
                                  (prev != null ? $"Δ zu vorher {Sgn(h - prev.Value, 3)} m   " : "") +
                                  (i > 0 ? $"Δ zu #1 {Sgn(h - h0, 3)} m" : ""));
                    prev = h;
                }
                if (withH.Count < 2) return (sb.ToString().TrimEnd(), $"h = {N(h0, 3)} m", true);

                var first = withH[0]; var last = withH[^1];
                double dh = last.H!.Value - first.H!.Value;
                double horiz = Geo.Distance(first.Lat, first.Lon, last.Lat, last.Lon);
                sb.AppendLine();
                sb.AppendLine($"Höhenunterschied (erster→letzter): {Sgn(dh, 3)} m");
                sb.AppendLine($"Höchster − tiefster Punkt        : {N(withH.Max(p => p.H!.Value) - withH.Min(p => p.H!.Value), 3)} m");
                sb.AppendLine($"Horizontale Entfernung           : {N(horiz, 3)} m");
                string slope = horiz > 0.05 ? $"{Sgn(dh / horiz * 100, 1)} % ({Sgn(Math.Atan2(dh, horiz) * 180 / Math.PI, 1)}°)" : "–";
                sb.Append($"Neigung                          : {slope}");
                return (sb.ToString(), $"Δh = {Sgn(dh, 3)} m" + (horiz > 0.05 ? $" · Neigung {Sgn(dh / horiz * 100, 1)} %" : ""), true);
            }
        }
    }

    void MeasureSave_Click(object sender, RoutedEventArgs e)
    {
        var kind = CurrentKind;
        var (_, summary, valid) = Evaluate(kind, _draft);
        if (!valid) { Info("Die Messung ist noch nicht vollständig (siehe Ergebnis)."); return; }

        string kindText = kind switch { MeasureKind.Strecke => "Strecke", MeasureKind.Flaeche => "Fläche", _ => "Höhe" };
        var name = MeasureNameBox.Text.Trim();
        if (name.Length == 0) name = $"{kindText} {_measurements.Count(m => m.Kind == kind) + 1}";

        var item = new MeasurementItem
        {
            Name = name, Kind = kind, Summary = summary,
            Vertices = _draft.Select(v => new MeasVertex { Lat = v.Lat, Lon = v.Lon, H = v.H, Source = v.Source }).ToList()
        };
        _measurements.Add(item);
        _draft.Clear();
        MeasureNameBox.Clear();
        RefreshMeasureResult();
        MeasurementsGrid.SelectedItem = item;
        AppendLog($"Messung gespeichert: {item.Name} – {summary}");
    }

    void MeasurementsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => _measDirty = true;

    void MeasurementDelete_Click(object sender, RoutedEventArgs e)
    {
        if (MeasurementsGrid.SelectedItem is not MeasurementItem m) { Info("Bitte zuerst eine gespeicherte Messung wählen."); return; }
        _measurements.Remove(m);
    }
}
