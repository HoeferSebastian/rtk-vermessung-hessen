using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Ntrip.Wpf.Offline;

public sealed class DownloadProgress
{
    public long Total { get; init; }
    public long Done { get; init; }
    public long Downloaded { get; init; }
    public long Skipped { get; init; }
    public long Failed { get; init; }
    public long Bytes { get; init; }
}

/// <summary>Liefert Kacheln aus dem lokalen Speicher und lädt sie bei Bedarf von den HVBG-WMS-Diensten (Open Data).</summary>
public sealed class TileService
{
    /// <summary>Beim Download wird ein Block aus Meta×Meta Kacheln mit einem einzigen WMS-Aufruf geholt (viel schneller als Einzelkacheln).</summary>
    public const int Meta = 8;
    const int TileSize = 256;

    static readonly HttpClient Http = CreateClient();

    readonly TileStore? _store;
    long _netBlockedUntil;
    int _consecutiveFailures;

    public TileService(TileStore? store, bool allowNetwork)
    {
        _store = store;
        AllowNetwork = allowNetwork;
    }

    public TileStore? Store => _store;

    /// <summary>Fehlende Kacheln beim Betrachten online nachladen und speichern.</summary>
    public volatile bool AllowNetwork;

    public event Action<string>? Log;

    static HttpClient CreateClient()
    {
        var h = new SocketsHttpHandler { MaxConnectionsPerServer = 8, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("NtripRtkSurvey/1.0 (offline tile cache)");
        return c;
    }

    // =====================================================================
    // Anzeige
    // =====================================================================

    /// <summary>Kachel für die Kartenanzeige: erst lokal, sonst (falls erlaubt) online und dann speichern.</summary>
    public async Task<byte[]?> GetForViewAsync(TileKey key, CancellationToken ct = default)
    {
        var data = _store?.Get(key);
        if (data != null) return data;
        if (!AllowNetwork || Environment.TickCount64 < Interlocked.Read(ref _netBlockedUntil)) return null;

        data = await FetchAsync(key, ct);
        if (data != null)
        {
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            _store?.Put(key, data);
        }
        else if (Interlocked.Increment(ref _consecutiveFailures) >= 3)
        {
            // Offline / Dienst nicht erreichbar: kurze Pause, damit die Anzeige nicht blockiert.
            Interlocked.Exchange(ref _netBlockedUntil, Environment.TickCount64 + 30_000);
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            Log?.Invoke("Kachel-Dienst nicht erreichbar – Online-Nachladen für 30 s pausiert (Offline-Daten werden weiter genutzt).");
        }
        return data;
    }

    // =====================================================================
    // WMS
    // =====================================================================

    static string GetMapUrl(TileLayerDef def, string bbox, int pixels) =>
        $"{def.Endpoint}?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&LAYERS={def.WmsLayer}&STYLES=&CRS=EPSG:3857" +
        $"&BBOX={bbox}&WIDTH={pixels}&HEIGHT={pixels}&FORMAT={def.Format}&TRANSPARENT={(def.Transparent ? "TRUE" : "FALSE")}";

    static async Task<byte[]?> GetImageAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            // Bei WMS-Fehlern kommt XML mit Status 200 – nur echte Bilder akzeptieren.
            if (resp.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length > 0 ? bytes : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    /// <summary>Ruft eine einzelne Kachel per WMS GetMap ab (EPSG:3857, 256×256). null bei Fehler.</summary>
    public static Task<byte[]?> FetchAsync(TileKey key, CancellationToken ct)
    {
        var def = TileLayers.Find(key.Layer);
        return def == null
            ? Task.FromResult<byte[]?>(null)
            : GetImageAsync(GetMapUrl(def, TileMath.Bbox3857(key.Z, key.X, key.Y), TileSize), ct);
    }

    /// <summary>Holt einen Block aus Meta×Meta Kacheln mit einem Aufruf und schneidet die gewünschten Kacheln heraus.</summary>
    static async Task<List<(TileKey Key, byte[] Data)>?> FetchBlockAsync(TileLayerDef def, int z, int blockX, int blockY,
        IReadOnlyList<TileKey> wanted, CancellationToken ct)
    {
        int size = Meta * TileSize;
        var image = await GetImageAsync(GetMapUrl(def, TileMath.BboxBlock(z, blockX * Meta, blockY * Meta, Meta), size), ct);
        if (image == null) return null;
        try { return await Task.Run(() => Slice(image, def, blockX, blockY, wanted), ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    static List<(TileKey, byte[])>? Slice(byte[] image, TileLayerDef def, int blockX, int blockY, IReadOnlyList<TileKey> wanted)
    {
        using var ms = new MemoryStream(image);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth != Meta * TileSize || frame.PixelHeight != Meta * TileSize) return null;

        var result = new List<(TileKey, byte[])>(wanted.Count);
        foreach (var key in wanted)
        {
            var crop = new CroppedBitmap(frame, new Int32Rect((key.X - blockX * Meta) * TileSize, (key.Y - blockY * Meta) * TileSize, TileSize, TileSize));
            crop.Freeze();
            BitmapEncoder enc = def.Ext == "jpg" ? new JpegBitmapEncoder { QualityLevel = 85 } : new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(crop));
            using var o = new MemoryStream();
            enc.Save(o);
            result.Add((key, o.ToArray()));
        }
        return result;
    }

    // =====================================================================
    // Download
    // =====================================================================

    /// <summary>Lädt Kacheln für alle Layer in den lokalen Speicher (blockweise, parallel).</summary>
    public async Task DownloadAsync(IReadOnlyList<TileKey> tiles, bool overwrite, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        if (_store == null) throw new InvalidOperationException("Kein Kachelspeicher verfügbar.");

        long done = 0, downloaded = 0, skipped = 0, failed = 0, bytes = 0, consecutiveFailedCalls = 0;
        long total = tiles.Count;
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(ct);
        string? abortReason = null;

        void Report() => progress.Report(new DownloadProgress
        {
            Total = total, Done = Interlocked.Read(ref done), Downloaded = Interlocked.Read(ref downloaded),
            Skipped = Interlocked.Read(ref skipped), Failed = Interlocked.Read(ref failed), Bytes = Interlocked.Read(ref bytes)
        });

        // Kacheln zu Blöcken (Layer, Zoom, Block-X, Block-Y) bündeln.
        var groups = tiles.GroupBy(t => (t.Layer, t.Z, BX: t.X / Meta, BY: t.Y / Meta)).ToList();

        try
        {
            await Parallel.ForEachAsync(groups, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = abort.Token }, async (g, token) =>
            {
                var all = g.ToList();
                var need = overwrite ? all : all.Where(t => !_store.Exists(t)).ToList();
                Interlocked.Add(ref skipped, all.Count - need.Count);

                if (need.Count > 0)
                {
                    var def = TileLayers.Find(g.Key.Layer)!;
                    List<(TileKey Key, byte[] Data)>? got = null;

                    // Mehrere Kacheln im Block → ein großer Aufruf; sonst (oder bei Fehler) Einzelkacheln.
                    if (need.Count > 3)
                    {
                        got = await FetchBlockAsync(def, g.Key.Z, g.Key.BX, g.Key.BY, need, token);
                        if (got == null) { await Task.Delay(500, token); got = await FetchBlockAsync(def, g.Key.Z, g.Key.BX, g.Key.BY, need, token); }
                    }

                    if (got != null)
                    {
                        foreach (var (key, data) in got) { _store.Put(key, data); Interlocked.Add(ref bytes, data.Length); }
                        Interlocked.Add(ref downloaded, got.Count);
                        Interlocked.Exchange(ref consecutiveFailedCalls, 0);
                    }
                    else
                    {
                        foreach (var key in need)
                        {
                            var data = await FetchAsync(key, token);
                            if (data != null)
                            {
                                _store.Put(key, data);
                                Interlocked.Increment(ref downloaded);
                                Interlocked.Add(ref bytes, data.Length);
                                Interlocked.Exchange(ref consecutiveFailedCalls, 0);
                            }
                            else
                            {
                                Interlocked.Increment(ref failed);
                                if (Interlocked.Increment(ref consecutiveFailedCalls) >= 12)
                                {
                                    abortReason = "Der Kartendienst ist nicht erreichbar. Netzverbindung prüfen und erneut starten – bereits geladene Kacheln bleiben erhalten.";
                                    abort.Cancel();
                                    return;
                                }
                            }
                        }
                    }
                }
                Interlocked.Add(ref done, all.Count);
                Report();
            });
        }
        catch (OperationCanceledException) when (abortReason != null)
        {
            Report();
            throw new IOException(abortReason);
        }
        Report();
    }
}
