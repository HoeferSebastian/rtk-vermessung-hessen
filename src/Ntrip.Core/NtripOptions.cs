using System.Text.Json;

namespace Ntrip.Core;

public class NtripOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 2101;
    public string Mountpoint { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool UseTls { get; set; }
    public bool SendGga { get; set; } = true;
    /// <summary>Fallback-Position für GGA, solange der Empfänger noch keine eigene liefert.</summary>
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Altitude { get; set; }
    public int GgaIntervalSeconds { get; set; } = 10;
    public string OutputFile { get; set; } = "";
    public int ReconnectDelaySeconds { get; set; } = 5;
    public bool ListSourcetable { get; set; }

    /// <summary>Liest den Abschnitt "Ntrip" aus einer appsettings.json.</summary>
    public static NtripOptions LoadFromJson(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!doc.RootElement.TryGetProperty("Ntrip", out var section)) return new NtripOptions();
        return section.Deserialize<NtripOptions>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new NtripOptions();
    }
}
