namespace Ntrip.Core;

/// <summary>Geodätische Hilfsfunktionen auf dem GRS80-Ellipsoid (ETRS89). Lokale Näherungen sind für Distanzen bis wenige km mm-genau.</summary>
public static class Geo
{
    public const double A = 6378137.0;
    public const double F = 1 / 298.257222101;
    public static readonly double E2 = F * (2 - F);
    const double Deg = Math.PI / 180;

    /// <summary>Meridian- und Querkrümmungsradius bei gegebener Breite.</summary>
    public static (double M, double N) Radii(double latDeg)
    {
        double s = Math.Sin(latDeg * Deg);
        double w = Math.Sqrt(1 - E2 * s * s);
        return (A * (1 - E2) / (w * w * w), A / w);
    }

    /// <summary>Ost-/Nord-Versatz (Meter) von Punkt 1 nach Punkt 2 in der lokalen Tangentialebene.</summary>
    public static (double East, double North) Offset(double lat1, double lon1, double lat2, double lon2)
    {
        double latm = (lat1 + lat2) / 2;
        var (m, n) = Radii(latm);
        return ((lon2 - lon1) * Deg * n * Math.Cos(latm * Deg), (lat2 - lat1) * Deg * m);
    }

    public static double Distance(double lat1, double lon1, double lat2, double lon2)
    {
        var (e, n) = Offset(lat1, lon1, lat2, lon2);
        return Math.Sqrt(e * e + n * n);
    }

    /// <summary>Peilung (rechtweisend, 0–360°) von Punkt 1 nach Punkt 2.</summary>
    public static double Bearing(double lat1, double lon1, double lat2, double lon2)
    {
        var (e, n) = Offset(lat1, lon1, lat2, lon2);
        return (Math.Atan2(e, n) / Deg + 360) % 360;
    }

    /// <summary>Verschiebt eine Position um Ost-/Nord-Meter.</summary>
    public static (double Lat, double Lon) Move(double lat, double lon, double dEast, double dNorth)
    {
        var (m, n) = Radii(lat);
        return (lat + dNorth / m / Deg, lon + dEast / (n * Math.Cos(lat * Deg)) / Deg);
    }

    /// <summary>Fläche (m²) eines Polygons (Reihenfolge der Eckpunkte; automatisch geschlossen).</summary>
    public static double PolygonArea(IReadOnlyList<(double Lat, double Lon)> pts)
    {
        if (pts.Count < 3) return 0;
        double cLat = pts.Average(p => p.Lat), cLon = pts.Average(p => p.Lon);
        var xy = pts.Select(p => Offset(cLat, cLon, p.Lat, p.Lon)).ToList();
        double sum = 0;
        for (int i = 0; i < xy.Count; i++)
        {
            var a = xy[i];
            var b = xy[(i + 1) % xy.Count];
            sum += a.East * b.North - b.East * a.North;
        }
        return Math.Abs(sum) / 2;
    }

    public static double PathLength(IReadOnlyList<(double Lat, double Lon)> pts, bool closed = false)
    {
        double len = 0;
        for (int i = 1; i < pts.Count; i++) len += Distance(pts[i - 1].Lat, pts[i - 1].Lon, pts[i].Lat, pts[i].Lon);
        if (closed && pts.Count > 2) len += Distance(pts[^1].Lat, pts[^1].Lon, pts[0].Lat, pts[0].Lon);
        return len;
    }

    public static string CompassPoint(double bearing)
    {
        string[] n = { "N", "NO", "O", "SO", "S", "SW", "W", "NW" };
        return n[(int)Math.Round(bearing / 45.0) % 8];
    }

    /// <summary>ECEF (WGS84/ETRS89) → geodätisch (Grad, Ellipsoidhöhe).</summary>
    public static (double Lat, double Lon, double H) EcefToGeodetic(double x, double y, double z)
    {
        double p = Math.Sqrt(x * x + y * y);
        double lon = Math.Atan2(y, x);
        double lat = Math.Atan2(z, p * (1 - E2));
        double h = 0;
        for (int i = 0; i < 6; i++)
        {
            double s = Math.Sin(lat);
            double n = A / Math.Sqrt(1 - E2 * s * s);
            h = p / Math.Cos(lat) - n;
            lat = Math.Atan2(z, p * (1 - E2 * n / (n + h)));
        }
        return (lat / Deg, lon / Deg, h);
    }
}
