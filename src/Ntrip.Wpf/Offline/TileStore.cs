using System.IO;
using Microsoft.Data.Sqlite;

namespace Ntrip.Wpf.Offline;

public sealed record LayerStat(string Layer, long Count, long Bytes, DateTime Oldest, DateTime Newest, int ZMin, int ZMax);

/// <summary>Persistenter Kachelspeicher in einer einzelnen SQLite-Datei (kopierbar auf andere Rechner).</summary>
public sealed class TileStore : IDisposable
{
    readonly SqliteConnection _c;
    readonly object _l = new();

    public string Path { get; }

    public TileStore(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _c = new SqliteConnection($"Data Source={path}");
        _c.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Exec("CREATE TABLE IF NOT EXISTS tiles(layer TEXT NOT NULL, z INTEGER NOT NULL, x INTEGER NOT NULL, y INTEGER NOT NULL, " +
             "data BLOB NOT NULL, fetched TEXT NOT NULL, PRIMARY KEY(layer, z, x, y)) WITHOUT ROWID;");
    }

    void Exec(string sql)
    {
        using var cmd = _c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static void Key(SqliteCommand cmd, TileKey k)
    {
        cmd.Parameters.AddWithValue("$l", k.Layer);
        cmd.Parameters.AddWithValue("$z", k.Z);
        cmd.Parameters.AddWithValue("$x", k.X);
        cmd.Parameters.AddWithValue("$y", k.Y);
    }

    public byte[]? Get(TileKey k)
    {
        lock (_l)
        {
            using var cmd = _c.CreateCommand();
            cmd.CommandText = "SELECT data FROM tiles WHERE layer=$l AND z=$z AND x=$x AND y=$y";
            Key(cmd, k);
            return cmd.ExecuteScalar() as byte[];
        }
    }

    public bool Exists(TileKey k)
    {
        lock (_l)
        {
            using var cmd = _c.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM tiles WHERE layer=$l AND z=$z AND x=$x AND y=$y";
            Key(cmd, k);
            return cmd.ExecuteScalar() != null;
        }
    }

    public void Put(TileKey k, byte[] data)
    {
        lock (_l)
        {
            using var cmd = _c.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO tiles(layer,z,x,y,data,fetched) VALUES($l,$z,$x,$y,$d,$t)";
            Key(cmd, k);
            cmd.Parameters.AddWithValue("$d", data);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    public List<LayerStat> Stats()
    {
        var list = new List<LayerStat>();
        lock (_l)
        {
            using var cmd = _c.CreateCommand();
            cmd.CommandText = "SELECT layer, COUNT(*), SUM(LENGTH(data)), MIN(fetched), MAX(fetched), MIN(z), MAX(z) FROM tiles GROUP BY layer ORDER BY layer";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new LayerStat(r.GetString(0), r.GetInt64(1), r.GetInt64(2),
                    DateTime.Parse(r.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime(),
                    DateTime.Parse(r.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime(),
                    r.GetInt32(5), r.GetInt32(6)));
        }
        return list;
    }

    public void Clear(string? layer = null)
    {
        lock (_l)
        {
            using var cmd = _c.CreateCommand();
            cmd.CommandText = layer == null ? "DELETE FROM tiles" : "DELETE FROM tiles WHERE layer=$l";
            if (layer != null) cmd.Parameters.AddWithValue("$l", layer);
            cmd.ExecuteNonQuery();
            Exec("PRAGMA wal_checkpoint(TRUNCATE); VACUUM;");
        }
    }

    public void Dispose()
    {
        lock (_l)
        {
            try { Exec("PRAGMA wal_checkpoint(TRUNCATE);"); } catch { }
            _c.Dispose();
        }
    }
}
