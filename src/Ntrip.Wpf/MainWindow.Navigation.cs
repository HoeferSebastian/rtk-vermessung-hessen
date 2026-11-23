using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ntrip.Core;

namespace Ntrip.Wpf;

public partial class MainWindow
{
    bool NavActive => NavToggle.IsChecked == true;
    BoundaryPoint? NavTarget => NavTargetBox.SelectedItem as BoundaryPoint;

    void NavTarget_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_init) return;
        _pointsDirty = true;
        UpdateNavigation();
    }

    void NavPrev_Click(object sender, RoutedEventArgs e) => StepTarget(-1);
    void NavNext_Click(object sender, RoutedEventArgs e) => StepTarget(1);

    void StepTarget(int dir)
    {
        if (_points.Count == 0) return;
        int i = NavTargetBox.SelectedIndex;
        i = i < 0 ? 0 : (i + dir + _points.Count) % _points.Count;
        NavTargetBox.SelectedIndex = i;
    }

    void NavToggle_Click(object sender, RoutedEventArgs e)
    {
        if (NavToggle.IsChecked == true)
        {
            if (NavTarget == null)
            {
                Info("Bitte zuerst einen Zielpunkt wählen (Grenzpunkte müssen im Reiter „Punkte“ vorhanden sein).");
                NavToggle.IsChecked = false;
                return;
            }
            if (!_rx.IsOpen) AppendLog("Hinweis: Empfänger nicht verbunden – Navigation startet, sobald Positionen eintreffen.");
        }
        NavToggle.Content = NavToggle.IsChecked == true ? "Navigation beenden" : "Navigation starten";
        _pointsDirty = true;
        UpdateNavigation();
    }

    void StopNavigation()
    {
        NavToggle.IsChecked = false;
        NavToggle.Content = "Navigation starten";
        UpdateNavigation();
    }

    static string FormatDistance(double m) => m switch
    {
        < 1.0 => (m * 100).ToString("F1", CultureInfo.CurrentCulture) + " cm",
        < 1000 => m.ToString("F3", CultureInfo.CurrentCulture) + " m",
        _ => (m / 1000).ToString("F3", CultureInfo.CurrentCulture) + " km"
    };

    static string Signed(double v, string unit, string pos, string neg) =>
        $"{(v >= 0 ? pos : neg)} {Math.Abs(v).ToString("F3", CultureInfo.CurrentCulture)} {unit}";

    void ResetNavDisplay(string status, string distText = "–", string bearingText = "")
    {
        NavDistText.Text = distText;
        NavBearingText.Text = bearingText;
        NavStatusText.Text = status;
        NavStatusBorder.Background = Brushes.Gainsboro;
        NavForwardText.Text = NavRightText.Text = NavDEText.Text = NavDNText.Text = NavDHText.Text = "–";
        NavArrow.Fill = Brushes.LightGray;
        _map.SetNav(null, null, null, null);
    }

    void UpdateNavigation()
    {
        if (!_init) return;
        var target = NavTarget;
        if (!NavActive || target == null)
        {
            ResetNavDisplay(target == null ? "Kein Ziel gewählt" : "Navigation nicht aktiv", "–", target == null ? "" : $"Ziel: {target.Name}");
            return;
        }

        var g = _gga;
        if (g is not { HasPosition: true } || g.Quality == 0)
        {
            ResetNavDisplay("Warte auf Position …", "–", $"Ziel: {target.Name}");
            return;
        }

        var (dE, dN) = Geo.Offset(g.Latitude, g.Longitude, target.Latitude, target.Longitude);
        double dist = Math.Sqrt(dE * dE + dN * dN);
        double bearing = (Math.Atan2(dE, dN) * 180 / Math.PI + 360) % 360;

        double? h = CurrentHeading();
        bool headingUp = NavHeadingUp.IsChecked == true && h != null;
        double hdg = h ?? 0;

        NavArrowRotate.Angle = headingUp ? bearing - hdg : bearing;
        NavNorthRotate.Angle = headingUp ? -hdg : 0;

        double hr = hdg * Math.PI / 180;
        double fwd = dN * Math.Cos(hr) + dE * Math.Sin(hr);
        double right = dE * Math.Cos(hr) - dN * Math.Sin(hr);

        NavDistText.Text = FormatDistance(dist);
        NavBearingText.Text = $"{target.Name} · Peilung {bearing.ToString("F1", CultureInfo.CurrentCulture)}° ({Geo.CompassPoint(bearing)})"
            + (h != null ? $" · Fahrtrichtung {hdg.ToString("F0", CultureInfo.CurrentCulture)}°" : " · keine Fahrtrichtung");
        NavDEText.Text = Signed(dE, "m", "Ost +", "West −");
        NavDNText.Text = Signed(dN, "m", "Nord +", "Süd −");
        if (h != null)
        {
            NavForwardText.Text = Signed(fwd, "m", "↑ vor", "↓ zurück");
            NavRightText.Text = Signed(right, "m", "→ rechts", "← links");
        }
        else
        {
            NavForwardText.Text = NavRightText.Text = "– (in Bewegung setzen)";
        }
        NavDHText.Text = target.Height is { } th && !double.IsNaN(g.EllipsoidalHeight)
            ? Signed(th - g.EllipsoidalHeight, "m", "Ziel höher +", "Ziel tiefer −")
            : "– (Ziel ohne Höhe)";

        double tol = Math.Max(0.001, ParseOr(NavToleranceBox.Text, 0.03));
        bool rtk = g.Quality is 4 or 5;
        if (dist <= tol)
        {
            NavStatusText.Text = "✔ Punkt erreicht";
            NavStatusBorder.Background = Brushes.LightGreen;
            NavArrow.Fill = Brushes.ForestGreen;
        }
        else
        {
            NavStatusText.Text = rtk ? (g.Quality == 4 ? "Auf dem Weg (RTK Fixed)" : "Auf dem Weg (RTK Float – cm-ungenau)") : $"Achtung: kein RTK ({g.QualityText}) – Genauigkeit nur Meter";
            NavStatusBorder.Background = g.Quality == 4 ? Brushes.LightSkyBlue : Brushes.Moccasin;
            NavArrow.Fill = new SolidColorBrush(Color.FromRgb(0x1C, 0x7E, 0xD6));
        }

        _map.SetNav(g.Latitude, g.Longitude, target.Latitude, target.Longitude);
    }
}
