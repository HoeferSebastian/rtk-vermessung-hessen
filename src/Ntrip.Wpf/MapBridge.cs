using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Ntrip.Wpf.Offline;

namespace Ntrip.Wpf;

public sealed record MapBounds(double South, double West, double North, double East, double Zoom);

/// <summary>Kapselt die Leaflet-Karte im WebView2 und die Kommunikation C# ⇄ JavaScript.</summary>
public sealed class MapBridge
{
    readonly WebView2 _web;
    readonly List<string> _pending = new();
    bool _ready;

    public event Action<double, double>? MapClicked;
    public event Action<Guid, double, double>? PointMoved;
    public event Action<Guid>? PointClicked;
    public event Action? UserPanned;
    public event Action? Ready;
    public event Action<string>? Error;

    public bool IsReady => _ready;

    readonly TileService _tiles;
    CoreWebView2Environment? _env;

    public MapBridge(WebView2 web, TileService tiles)
    {
        _web = web;
        _tiles = tiles;
    }

    /// <summary>Beantwortet Kachelanfragen der Karte (https://tiles.local/{layer}/{z}/{x}/{y}.ext) aus dem Offline-Speicher.</summary>
    async void OnTileRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var seg = new Uri(e.Request.Uri).AbsolutePath.Trim('/').Split('/');
            byte[]? data = null;
            string contentType = "image/png";
            if (seg.Length == 4 && int.TryParse(seg[1], out int z) && int.TryParse(seg[2], out int x) &&
                int.TryParse(Path.GetFileNameWithoutExtension(seg[3]), out int y))
            {
                var def = TileLayers.Find(seg[0]);
                if (def != null)
                {
                    contentType = def.Format;
                    data = await _tiles.GetForViewAsync(new TileKey(def.Key, z, x, y));
                }
            }
            e.Response = data != null
                ? _env!.CreateWebResourceResponse(new MemoryStream(data), 200, "OK", $"Content-Type: {contentType}\nCache-Control: no-store")
                : _env!.CreateWebResourceResponse(null, 404, "Not Found", "Cache-Control: no-store");
        }
        catch
        {
            try { e.Response = _env!.CreateWebResourceResponse(null, 404, "Not Found", ""); } catch { }
        }
        finally { deferral.Complete(); }
    }

    public async Task<MapBounds?> GetBoundsAsync()
    {
        if (!_ready) return null;
        try
        {
            var raw = await _web.ExecuteScriptAsync("JSON.stringify(app.bounds())");
            var json = JsonSerializer.Deserialize<string>(raw);
            if (json == null) return null;
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return new MapBounds(r.GetProperty("s").GetDouble(), r.GetProperty("w").GetDouble(),
                r.GetProperty("n").GetDouble(), r.GetProperty("e").GetDouble(), r.GetProperty("z").GetDouble());
        }
        catch { return null; }
    }

    public void RedrawTiles() => Run("app.redrawTiles()");

    public async Task InitAsync()
    {
        try
        {
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NtripRtk", "WebView2");
            _env = await CoreWebView2Environment.CreateAsync(null, data);
            await _web.EnsureCoreWebView2Async(_env);
            var core = _web.CoreWebView2;
            core.AddWebResourceRequestedFilter("https://tiles.local/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnTileRequested;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.SetVirtualHostNameToFolderMapping("ntrip.local", Path.Combine(AppContext.BaseDirectory, "Map"),
                CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnMessage;
            core.Navigate("https://ntrip.local/map.html");
        }
        catch (Exception ex)
        {
            Error?.Invoke("Karte konnte nicht initialisiert werden (WebView2-Runtime installiert?): " + ex.Message);
        }
    }

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var r = doc.RootElement;
            switch (r.GetProperty("t").GetString())
            {
                case "ready":
                    _ready = true;
                    foreach (var s in _pending) _ = _web.ExecuteScriptAsync(s);
                    _pending.Clear();
                    Ready?.Invoke();
                    break;
                case "click": MapClicked?.Invoke(r.GetProperty("lat").GetDouble(), r.GetProperty("lon").GetDouble()); break;
                case "pointClick": PointClicked?.Invoke(r.GetProperty("id").GetGuid()); break;
                case "pointMoved":
                    PointMoved?.Invoke(r.GetProperty("id").GetGuid(), r.GetProperty("lat").GetDouble(), r.GetProperty("lon").GetDouble());
                    break;
                case "userPan": UserPanned?.Invoke(); break;
            }
        }
        catch (Exception ex) { Error?.Invoke("Karten-Nachricht: " + ex.Message); }
    }

    void Run(string script)
    {
        if (!_ready) { if (_pending.Count < 200) _pending.Add(script); return; }
        _ = _web.ExecuteScriptAsync(script);
    }

    static string J(object? o) => JsonSerializer.Serialize(o);

    public void SetLayer(string name) => Run($"app.setLayer({J(name)})");
    public void SetTool(string tool) => Run($"app.setTool({J(tool)})");
    public void SetFollow(bool on) => Run($"app.setFollow({(on ? "true" : "false")})");
    public void SetDraggable(bool on) => Run($"app.setDraggable({(on ? "true" : "false")})");

    public void UpdatePosition(double lat, double lon, int quality, double? heading) =>
        Run($"app.update({J(new { lat, lon, q = quality, heading })})");
    public void ClearPosition() => Run("app.update(null)");

    public void SetPoints(IEnumerable<BoundaryPoint> pts, Guid? selected, Guid? target) =>
        Run($"app.setPoints({J(pts.Select(p => new { id = p.Id, name = p.Name, lat = p.Latitude, lon = p.Longitude, sel = p.Id == selected, target = p.Id == target }))})");

    public void SetTracks(IEnumerable<TrackRecording> tracks, Guid? selected) =>
        Run($"app.setTracks({J(tracks.Select(t => new { color = t.Color, sel = t.Id == selected, coords = Decimate(t.Points) }))})");

    public void SetLive(IEnumerable<TrackPoint> pts) => Run($"app.setLive({J(Decimate(pts.ToList()))})");
    public void AppendLive(double lat, double lon) => Run($"app.appendLive([{lat.ToString(System.Globalization.CultureInfo.InvariantCulture)},{lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}])");

    public void SetNav(double? fromLat, double? fromLon, double? toLat, double? toLon)
    {
        if (fromLat is null || fromLon is null || toLat is null || toLon is null) Run("app.setNav(null)");
        else Run($"app.setNav({J(new { from = new[] { fromLat, fromLon }, to = new[] { toLat, toLon } })})");
    }

    public void SetReference(double? lat, double? lon, string label) =>
        Run(lat is null || lon is null ? "app.setRef(null)" : $"app.setRef({J(new { lat, lon, label })})");

    public void SetReplay(double? lat, double? lon) =>
        Run(lat is null || lon is null ? "app.setReplay(null)" : $"app.setReplay([{lat.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)},{lon.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}])");

    public void SetDraft(MeasureKind kind, IEnumerable<MeasVertex> v) =>
        Run($"app.setDraft({J(new { kind = kind.ToString(), pts = v.Select(x => new[] { x.Lat, x.Lon }) })})");

    public void SetMeasures(IEnumerable<MeasurementItem> items, Guid? selected) =>
        Run($"app.setMeasures({J(items.Select(m => new { kind = m.Kind.ToString(), sel = m.Id == selected, label = m.Summary, pts = m.Vertices.Select(x => new[] { x.Lat, x.Lon }) }))})");

    public void Fit(IEnumerable<(double Lat, double Lon)> pts) =>
        Run($"app.fit({J(pts.Select(p => new[] { p.Lat, p.Lon }))})");

    public void ZoomTo(double lat, double lon, double zoom = 20) => Run($"app.zoomTo({J(new[] { lat, lon })},{zoom.ToString(System.Globalization.CultureInfo.InvariantCulture)})");

    /// <summary>Dünnt lange Spuren für die Anzeige aus (max. ca. 4000 Punkte).</summary>
    static double[][] Decimate(List<TrackPoint> pts)
    {
        int step = Math.Max(1, pts.Count / 4000);
        var res = new List<double[]>();
        for (int i = 0; i < pts.Count; i += step) res.Add(new[] { pts[i].Lat, pts[i].Lon });
        if (pts.Count > 0 && (pts.Count - 1) % step != 0) res.Add(new[] { pts[^1].Lat, pts[^1].Lon });
        return res.ToArray();
    }
}
