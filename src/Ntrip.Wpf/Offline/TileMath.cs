using System.Globalization;

namespace Ntrip.Wpf.Offline;

public readonly record struct TileKey(string Layer, int Z, int X, int Y);

public sealed record GeoRect(double South, double West, double North, double East);

/// <summary>Kachelgeometrie im „Slippy-Map“-Schema (Web Mercator, EPSG:3857, 256 px).</summary>
public static class TileMath
{
    const double Origin = 20037508.342789244;
    public const double MaxLat = 85.0511;

    public static (int X, int Y) LonLatToTile(double lat, double lon, int z)
    {
        lat = Math.Clamp(lat, -MaxLat, MaxLat);
        double n = 1 << z;
        double x = (lon + 180.0) / 360.0 * n;
        double r = lat * Math.PI / 180.0;
        double y = (1.0 - Math.Log(Math.Tan(r) + 1.0 / Math.Cos(r)) / Math.PI) / 2.0 * n;
        return ((int)Math.Clamp(Math.Floor(x), 0, n - 1), (int)Math.Clamp(Math.Floor(y), 0, n - 1));
    }

    /// <summary>WMS-BBOX (minx,miny,maxx,maxy) der Kachel in EPSG:3857.</summary>
    public static string Bbox3857(int z, int x, int y)
    {
        double size = 2 * Origin / (1 << z);
        double minX = -Origin + x * size;
        double maxY = Origin - y * size;
        var c = CultureInfo.InvariantCulture;
        return string.Join(',', minX.ToString("F4", c), (maxY - size).ToString("F4", c), (minX + size).ToString("F4", c), maxY.ToString("F4", c));
    }

    /// <summary>WMS-BBOX eines Blocks aus n×n Kacheln, dessen linke obere Kachel (x, y) ist.</summary>
    public static string BboxBlock(int z, int x, int y, int n)
    {
        double size = 2 * Origin / (1 << z);
        double minX = -Origin + x * size;
        double maxY = Origin - y * size;
        var c = CultureInfo.InvariantCulture;
        return string.Join(',', minX.ToString("F4", c), (maxY - n * size).ToString("F4", c), (minX + n * size).ToString("F4", c), maxY.ToString("F4", c));
    }

    /// <summary>Alle Kacheln, die die Rechtecke abdecken (ohne Duplikate). Bricht bei mehr als <paramref name="cap"/> Kacheln mit null ab.</summary>
    public static List<(int Z, int X, int Y)>? Enumerate(IReadOnlyList<GeoRect> rects, int zMin, int zMax, int cap, CancellationToken ct = default)
    {
        var set = new HashSet<(int, int, int)>();
        for (int z = zMin; z <= zMax; z++)
        {
            foreach (var r in rects)
            {
                var (x0, y1) = LonLatToTile(r.South, r.West, z);
                var (x1, y0) = LonLatToTile(r.North, r.East, z);
                for (int x = x0; x <= x1; x++)
                {
                    for (int y = y0; y <= y1; y++)
                    {
                        set.Add((z, x, y));
                        if (set.Count > cap) return null;
                    }
                    ct.ThrowIfCancellationRequested();
                }
            }
        }
        return set.ToList();
    }

    /// <summary>Rechteck um einen Punkt mit Puffer in Metern.</summary>
    public static GeoRect Around(double lat, double lon, double bufferM)
    {
        double dLat = bufferM / 111_320.0;
        double dLon = bufferM / (111_320.0 * Math.Max(0.05, Math.Cos(lat * Math.PI / 180)));
        return new GeoRect(lat - dLat, lon - dLon, lat + dLat, lon + dLon);
    }
}
