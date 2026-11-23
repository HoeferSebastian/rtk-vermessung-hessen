namespace Ntrip.Wpf.Offline;

/// <summary>Ein über WMS abrufbarer Kartenlayer der HVBG (Open Data, Geobasisdaten Hessen).</summary>
public sealed record TileLayerDef(string Key, string Title, string Endpoint, string WmsLayer, string Format, bool Transparent, int EstimatedBytes)
{
    public string Ext => Format == "image/jpeg" ? "jpg" : "png";
}

public static class TileLayers
{
    const string Base = "https://www.gds-srv.hessen.de/cgi-bin/lika-services/";

    public const string Attribution = "Geobasisdaten © Hessische Verwaltung für Bodenmanagement und Geoinformation (Open Data)";

    public static readonly TileLayerDef[] All =
    {
        new("alk", "ALKIS-Liegenschaftskarte", Base + "ogc-free-maps.ows", "he_alk", "image/png", false, 4600),
        new("dop", "Luftbild DOP20 (20 cm)", Base + "ogc-free-images.ows", "he_dop20_rgb", "image/jpeg", false, 8600),
        new("alk_t", "ALKIS-Grenzen transparent (für „Luftbild + Grenzen“)", Base + "ogc-free-maps.ows", "he_alk_t", "image/png", true, 2300)
    };

    public static TileLayerDef? Find(string key) => All.FirstOrDefault(l => l.Key == key);
}
