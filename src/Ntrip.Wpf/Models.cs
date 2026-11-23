using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ntrip.Core;

namespace Ntrip.Wpf;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Grenzpunkt: Koordinaten in ETRS89 (Breite/Länge/Ellipsoidhöhe); Ost/Nord sind abgeleitete, editierbare UTM-Werte.</summary>
public sealed class BoundaryPoint : ObservableObject
{
    string _name = "";
    double _lat, _lon;
    double? _height;
    string _note = "";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => SetField(ref _name, value); }

    public double Latitude
    {
        get => _lat;
        set { if (SetField(ref _lat, value)) RaisePosition(nameof(Latitude)); }
    }

    public double Longitude
    {
        get => _lon;
        set { if (SetField(ref _lon, value)) RaisePosition(nameof(Longitude)); }
    }

    /// <summary>Ellipsoidische Höhe in Metern (optional).</summary>
    public double? Height { get => _height; set => SetField(ref _height, value); }

    public string Note { get => _note; set => SetField(ref _note, value); }
    public string Source { get; set; } = "";
    public string Quality { get; set; } = "";
    public int Epochs { get; set; }
    public double? StdEast { get; set; }
    public double? StdNorth { get; set; }
    public double? StdHeight { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;

    [JsonIgnore]
    public double Easting
    {
        get => Utm.FromLatLon(_lat, _lon).Easting;
        set
        {
            var (zone, _, n) = Utm.FromLatLon(_lat, _lon);
            SetPosition(Utm.ToLatLon(zone, value, n));
        }
    }

    [JsonIgnore]
    public double Northing
    {
        get => Utm.FromLatLon(_lat, _lon).Northing;
        set
        {
            var (zone, e, _) = Utm.FromLatLon(_lat, _lon);
            SetPosition(Utm.ToLatLon(zone, e, value));
        }
    }

    public void SetPosition((double Lat, double Lon) p) => SetPosition(p.Lat, p.Lon);

    public void SetPosition(double lat, double lon)
    {
        _lat = lat; _lon = lon;
        Raise(nameof(Latitude)); Raise(nameof(Longitude)); Raise(nameof(Easting)); Raise(nameof(Northing));
    }

    void RaisePosition(string changed)
    {
        if (changed != nameof(Latitude)) Raise(nameof(Latitude));
        if (changed != nameof(Longitude)) Raise(nameof(Longitude));
        Raise(nameof(Easting)); Raise(nameof(Northing));
    }

    public override string ToString() => Name;
}

public sealed class TrackPoint
{
    public DateTime T { get; set; }
    public double Lat { get; set; }
    public double Lon { get; set; }
    /// <summary>Ellipsoidische Höhe.</summary>
    public double H { get; set; }
    public double Hmsl { get; set; }
    public int Q { get; set; }
    public double Speed { get; set; }
}

public sealed class TrackRecording : ObservableObject
{
    string _name = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => SetField(ref _name, value); }
    public DateTime Start { get; set; } = DateTime.Now;
    public List<TrackPoint> Points { get; set; } = new();
    public string Color { get; set; } = "#e8590c";

    [JsonIgnore] public double LengthM { get; private set; }
    [JsonIgnore] public int Count => Points.Count;
    [JsonIgnore] public TimeSpan Duration => Points.Count > 1 ? Points[^1].T - Points[0].T : TimeSpan.Zero;
    [JsonIgnore] public string DurationText => Duration.ToString(Duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");
    [JsonIgnore] public double AvgSpeedKmh => Duration.TotalSeconds > 1 ? LengthM / Duration.TotalSeconds * 3.6 : 0;

    public void Add(TrackPoint p)
    {
        if (Points.Count > 0)
        {
            var l = Points[^1];
            LengthM += Geo.Distance(l.Lat, l.Lon, p.Lat, p.Lon);
        }
        Points.Add(p);
        RaiseStats();
    }

    public void Recompute()
    {
        LengthM = Geo.PathLength(Points.Select(p => (p.Lat, p.Lon)).ToList());
        RaiseStats();
    }

    void RaiseStats()
    {
        Raise(nameof(LengthM)); Raise(nameof(Count)); Raise(nameof(Duration));
        Raise(nameof(DurationText)); Raise(nameof(AvgSpeedKmh));
    }
}

public enum MeasureKind { Strecke, Flaeche, Hoehe }

public sealed class MeasVertex
{
    public double Lat { get; set; }
    public double Lon { get; set; }
    /// <summary>Ellipsoidische Höhe, falls bekannt.</summary>
    public double? H { get; set; }
    public string Source { get; set; } = "";
    [JsonIgnore] public int Index { get; set; }
}

public sealed class MeasurementItem : ObservableObject
{
    string _name = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => SetField(ref _name, value); }
    public MeasureKind Kind { get; set; }
    public List<MeasVertex> Vertices { get; set; } = new();
    public string Summary { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    [JsonIgnore] public string KindText => Kind switch { MeasureKind.Strecke => "Strecke", MeasureKind.Flaeche => "Fläche", _ => "Höhe" };
}

public sealed class ProjectData
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Neues Projekt";
    public List<BoundaryPoint> Points { get; set; } = new();
    public List<TrackRecording> Tracks { get; set; } = new();
    public List<MeasurementItem> Measurements { get; set; } = new();

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static ProjectData Load(string path) =>
        JsonSerializer.Deserialize<ProjectData>(File.ReadAllText(path), Json) ?? new ProjectData();

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
