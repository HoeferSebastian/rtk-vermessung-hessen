using System.Collections.ObjectModel;
using Ntrip.Core;

namespace Ntrip.Wpf;

public sealed class RtcmRow : ObservableObject
{
    int _count; string _rate = "", _last = "", _info = "";
    public int Type { get; init; }
    public string Name { get; init; } = "";
    public int Count { get => _count; set => SetField(ref _count, value); }
    public string Rate { get => _rate; set => SetField(ref _rate, value); }
    public string Last { get => _last; set => SetField(ref _last, value); }
    public string Info { get => _info; set => SetField(ref _info, value); }
}

/// <summary>Sammelt die vom Caster empfangenen RTCM-Nachrichten (thread-sicher) und stellt Statistik für die Anzeige bereit.</summary>
public sealed class RtcmMonitor
{
    sealed class Entry
    {
        public int Count;
        public DateTime Last;
        public string Info = "";
        public readonly Queue<DateTime> Recent = new();
    }

    readonly object _l = new();
    readonly Dictionary<int, Entry> _entries = new();
    readonly Dictionary<string, (int Sats, DateTime T)> _sats = new();
    DateTime _start = DateTime.Now;

    public RtcmStation? Station { get; private set; }
    public RtcmDescriptor? Descriptor { get; private set; }
    public DateTime? LastMessage { get; private set; }

    public void Reset()
    {
        lock (_l)
        {
            _entries.Clear(); _sats.Clear(); Station = null; Descriptor = null; LastMessage = null; _start = DateTime.Now;
        }
    }

    public void Add(RtcmMessage m)
    {
        lock (_l)
        {
            if (!_entries.TryGetValue(m.Type, out var e)) _entries[m.Type] = e = new Entry();
            e.Count++;
            e.Last = m.Time;
            e.Recent.Enqueue(m.Time);
            LastMessage = m.Time;

            if (Rtcm.TryDecodeStation(m, out var st))
            {
                Station = st;
                var g = st.Geodetic;
                e.Info = $"Station {st.StationId}: {g.Lat:F7}°, {g.Lon:F7}°, h {g.H:F3} m" + (st.AntennaHeight is { } ah ? $", Ant.-Höhe {ah:F3} m" : "");
            }
            else if (Rtcm.TryDecodeDescriptor(m, out var d))
            {
                Descriptor = d;
                e.Info = $"{d.Receiver} (FW {d.Firmware}), Antenne {d.Antenna}";
            }
            else if (Rtcm.TryDecodeMsm(m, out var sys, out var n))
            {
                _sats[sys] = (n, m.Time);
                e.Info = $"{n} Satelliten ({sys})";
            }
        }
    }

    /// <summary>Aktualisiert die Tabelle (UI-Thread).</summary>
    public void Fill(ObservableCollection<RtcmRow> rows)
    {
        lock (_l)
        {
            var now = DateTime.Now;
            double window = Math.Clamp((now - _start).TotalSeconds, 1, 10);
            foreach (var (type, e) in _entries.OrderBy(k => k.Key))
            {
                while (e.Recent.Count > 0 && (now - e.Recent.Peek()).TotalSeconds > 10) e.Recent.Dequeue();
                var row = rows.FirstOrDefault(r => r.Type == type);
                if (row == null)
                {
                    row = new RtcmRow { Type = type, Name = Rtcm.Name(type) };
                    int idx = 0;
                    while (idx < rows.Count && rows[idx].Type < type) idx++;
                    rows.Insert(idx, row);
                }
                row.Count = e.Count;
                row.Rate = (e.Recent.Count / window).ToString("0.0#") + " /s";
                row.Last = e.Last.ToString("HH:mm:ss");
                row.Info = e.Info;
            }
        }
    }

    public string SatelliteSummary()
    {
        lock (_l)
        {
            var now = DateTime.Now;
            var parts = _sats.Where(s => (now - s.Value.T).TotalSeconds < 15)
                .OrderBy(s => s.Key).Select(s => $"{s.Key} {s.Value.Sats}").ToList();
            return parts.Count == 0 ? "–" : string.Join(" · ", parts) + $"  (Σ {_sats.Where(s => (now - s.Value.T).TotalSeconds < 15).Sum(s => s.Value.Sats)})";
        }
    }
}
