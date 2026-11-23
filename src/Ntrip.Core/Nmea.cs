using System.Globalization;

namespace Ntrip.Core;

/// <summary>Ausgewertete GGA-Nachricht. Lat/Lon in Dezimalgrad (WGS84/ETRS89), NaN wenn kein Fix.</summary>
public sealed record GgaData(
    string TimeText, double Latitude, double Longitude, int Quality, int Satellites,
    double Hdop, double AltitudeMsl, double GeoidSeparation, double? DiffAgeSeconds, string Raw)
{
    /// <summary>Ellipsoidische Höhe = Höhe über MSL + Geoidundulation.</summary>
    public double EllipsoidalHeight => AltitudeMsl + GeoidSeparation;
    public bool HasPosition => !double.IsNaN(Latitude) && !double.IsNaN(Longitude);

    public string QualityText => Quality switch
    {
        0 => "Kein Fix",
        1 => "Autonom (GNSS)",
        2 => "DGPS",
        4 => "RTK Fixed",
        5 => "RTK Float",
        6 => "Koppelnavigation",
        _ => $"Qualität {Quality}"
    };
}

public sealed record RmcData(bool Valid, double SpeedMs, double CourseDeg);
public sealed record GstData(double StdLat, double StdLon, double StdAlt);

public static class Nmea
{
    public static bool ValidChecksum(string line)
    {
        int star = line.IndexOf('*');
        if (!line.StartsWith('$') || star < 0 || star + 3 > line.Length) return false;
        int cs = 0;
        for (int i = 1; i < star; i++) cs ^= line[i];
        return int.TryParse(line.AsSpan(star + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rx) && rx == cs;
    }

    public static bool TryParseGga(string line, out GgaData gga)
    {
        gga = null!;
        if (!ValidChecksum(line)) return false;
        int star = line.IndexOf('*');
        var f = line.Substring(1, star - 1).Split(',');
        if (f.Length < 10 || f[0].Length < 5 || !f[0].EndsWith("GGA", StringComparison.Ordinal)) return false;

        double D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
        int I(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

        string time = f[1].Length >= 6 ? $"{f[1][..2]}:{f[1][2..4]}:{f[1][4..]}" : "";
        double lat = ParseCoord(f[2], f[3]);
        double lon = ParseCoord(f[4], f[5]);
        double? age = f.Length > 13 && f[13].Length > 0 ? D(f[13]) : null;

        gga = new GgaData(time, lat, lon, I(f[6]), I(f[7]), D(f[8]),
            D(f[9]), f.Length > 11 ? D(f[11]) : double.NaN, age, line[..(star + 3)]);
        return true;
    }

    static double Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;

    /// <summary>RMC: Geschwindigkeit (m/s) und Kurs über Grund (Grad, NaN wenn unbekannt).</summary>
    public static bool TryParseRmc(string line, out RmcData rmc)
    {
        rmc = null!;
        if (!ValidChecksum(line)) return false;
        var f = line.Substring(1, line.IndexOf('*') - 1).Split(',');
        if (f.Length < 9 || !f[0].EndsWith("RMC", StringComparison.Ordinal)) return false;
        double knots = Num(f[7]);
        rmc = new RmcData(f[2] == "A", double.IsNaN(knots) ? 0 : knots * 0.514444, Num(f[8]));
        return true;
    }

    /// <summary>GST: Standardabweichungen (m) der Position, sofern der Empfänger sie ausgibt.</summary>
    public static bool TryParseGst(string line, out GstData gst)
    {
        gst = null!;
        if (!ValidChecksum(line)) return false;
        var f = line.Substring(1, line.IndexOf('*') - 1).Split(',');
        if (f.Length < 9 || !f[0].EndsWith("GST", StringComparison.Ordinal)) return false;
        gst = new GstData(Num(f[6]), Num(f[7]), Num(f[8]));
        return true;
    }

    static double ParseCoord(string v, string hemi)
    {
        int dot = v.IndexOf('.');
        if (dot < 3 || hemi.Length == 0) return double.NaN;
        if (!int.TryParse(v.AsSpan(0, dot - 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out int deg)) return double.NaN;
        if (!double.TryParse(v.AsSpan(dot - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out double min)) return double.NaN;
        double r = deg + min / 60.0;
        return hemi is "S" or "W" ? -r : r;
    }

    static string WithChecksum(string body)
    {
        int cs = 0;
        foreach (var c in body) cs ^= c;
        return $"${body}*{cs:X2}";
    }

    static string Coord(double deg, bool isLat)
    {
        var a = Math.Abs(deg);
        int d = (int)a;
        double m = (a - d) * 60;
        return (isLat ? d.ToString("00") : d.ToString("000")) + m.ToString("00.00000", CultureInfo.InvariantCulture);
    }

    /// <summary>Vollständiger GGA-Satz (für die Simulation).</summary>
    public static string BuildGga(DateTime utc, double lat, double lon, int quality, int satellites, double hdop,
        double altMsl, double geoidSeparation, double diffAge) =>
        WithChecksum(string.Create(CultureInfo.InvariantCulture,
            $"GNGGA,{utc:HHmmss}.00,{Coord(lat, true)},{(lat >= 0 ? 'N' : 'S')},{Coord(lon, false)},{(lon >= 0 ? 'E' : 'W')},{quality},{satellites:00},{hdop:F1},{altMsl:F3},M,{geoidSeparation:F3},M,{diffAge:F1},0000"));

    /// <summary>RMC-Satz mit Geschwindigkeit (m/s) und Kurs (Grad) für die Simulation.</summary>
    public static string BuildRmc(DateTime utc, double lat, double lon, double speedMs, double courseDeg) =>
        WithChecksum(string.Create(CultureInfo.InvariantCulture,
            $"GNRMC,{utc:HHmmss}.00,A,{Coord(lat, true)},{(lat >= 0 ? 'N' : 'S')},{Coord(lon, false)},{(lon >= 0 ? 'E' : 'W')},{speedMs / 0.514444:F3},{courseDeg:F2},{utc:ddMMyy},,,R"));

    /// <summary>GST-Satz (Standardabweichungen in Metern) für die Simulation.</summary>
    public static string BuildGst(DateTime utc, double stdLat, double stdLon, double stdAlt) =>
        WithChecksum(string.Create(CultureInfo.InvariantCulture,
            $"GNGST,{utc:HHmmss}.00,0.5,0.01,0.01,90.0,{stdLat:F3},{stdLon:F3},{stdAlt:F3}"));

    /// <summary>Baut einen GGA-Satz (für VRS-Anfragen beim Caster).</summary>
    public static string BuildGga(double lat, double lon, double alt)
    {
        var t = DateTime.UtcNow;
        static string Coord(double deg, bool isLat)
        {
            var a = Math.Abs(deg);
            int d = (int)a;
            double m = (a - d) * 60;
            return (isLat ? d.ToString("00") : d.ToString("000")) + m.ToString("00.00000", CultureInfo.InvariantCulture);
        }
        var body = string.Create(CultureInfo.InvariantCulture,
            $"GPGGA,{t:HHmmss}.00,{Coord(lat, true)},{(lat >= 0 ? 'N' : 'S')},{Coord(lon, false)},{(lon >= 0 ? 'E' : 'W')},1,12,1.0,{alt:F1},M,47.0,M,,");
        int cs = 0;
        foreach (var c in body) cs ^= c;
        return $"${body}*{cs:X2}";
    }
}

/// <summary>ETRS89/GRS80 → UTM (Krüger-Reihe, Genauigkeit im mm-Bereich innerhalb der Zone).</summary>
public static class Utm
{
    public static (int Zone, double Easting, double Northing) FromLatLon(double latDeg, double lonDeg)
    {
        int zone = (int)Math.Floor((lonDeg + 180) / 6) + 1;
        const double a = 6378137.0, f = 1 / 298.257222101;
        double n = f / (2 - f);
        double A = a / (1 + n) * (1 + n * n / 4 + n * n * n * n / 64);
        double[] alpha =
        {
            n / 2 - 2 * n * n / 3 + 5 * n * n * n / 16,
            13 * n * n / 48 - 3 * n * n * n / 5,
            61 * n * n * n / 240
        };
        double lon0 = (zone * 6 - 183) * Math.PI / 180;
        double phi = latDeg * Math.PI / 180, lam = lonDeg * Math.PI / 180 - lon0;
        double e = Math.Sqrt(f * (2 - f));
        double t = Math.Sinh(Math.Atanh(Math.Sin(phi)) - e * Math.Atanh(e * Math.Sin(phi)));
        double xi0 = Math.Atan2(t, Math.Cos(lam));
        double eta0 = Math.Atanh(Math.Sin(lam) / Math.Sqrt(1 + t * t));
        double xi = xi0, eta = eta0;
        for (int j = 1; j <= 3; j++)
        {
            xi += alpha[j - 1] * Math.Sin(2 * j * xi0) * Math.Cosh(2 * j * eta0);
            eta += alpha[j - 1] * Math.Cos(2 * j * xi0) * Math.Sinh(2 * j * eta0);
        }
        return (zone, 500000 + 0.9996 * A * eta, 0.9996 * A * xi);
    }

    /// <summary>UTM (Nordhalbkugel) → geodätische Breite/Länge in Grad.</summary>
    public static (double Lat, double Lon) ToLatLon(int zone, double easting, double northing)
    {
        const double a = 6378137.0, f = 1 / 298.257222101;
        double n = f / (2 - f);
        double A = a / (1 + n) * (1 + n * n / 4 + n * n * n * n / 64);
        double[] beta =
        {
            n / 2 - 2 * n * n / 3 + 37 * n * n * n / 96,
            n * n / 48 + n * n * n / 15,
            17 * n * n * n / 480
        };
        double[] delta =
        {
            2 * n - 2 * n * n / 3 - 2 * n * n * n,
            7 * n * n / 3 - 8 * n * n * n / 5,
            56 * n * n * n / 15
        };
        double xi0 = northing / (0.9996 * A), eta0 = (easting - 500000) / (0.9996 * A);
        double xi = xi0, eta = eta0;
        for (int j = 1; j <= 3; j++)
        {
            xi -= beta[j - 1] * Math.Sin(2 * j * xi0) * Math.Cosh(2 * j * eta0);
            eta -= beta[j - 1] * Math.Cos(2 * j * xi0) * Math.Sinh(2 * j * eta0);
        }
        double chi = Math.Asin(Math.Sin(xi) / Math.Cosh(eta));
        double phi = chi;
        for (int j = 1; j <= 3; j++) phi += delta[j - 1] * Math.Sin(2 * j * chi);
        double lon0 = (zone * 6 - 183) * Math.PI / 180;
        double lam = lon0 + Math.Atan2(Math.Sinh(eta), Math.Cos(xi));
        return (phi * 180 / Math.PI, lam * 180 / Math.PI);
    }
}
