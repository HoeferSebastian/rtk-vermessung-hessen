using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Ntrip.Core;

namespace Ntrip.Wpf;

public partial class MainWindow
{
    static readonly string[] TrackColors = { "#e8590c", "#1c7ed6", "#2f9e44", "#ae3ec9", "#e03131", "#0c8599", "#f08c00" };

    TrackRecording? _rec;
    bool _recPaused;
    TrackPoint? _lastRec;
    readonly DispatcherTimer _replayTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    bool _replayHooked;

    TrackRecording? SelectedTrack => TracksGrid.SelectedItem as TrackRecording;

    // ---------- Aufnahme ----------

    void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_rec != null) return;
        if (!_rx.IsOpen)
        {
            Info("Bitte zuerst im Reiter „Verbindung“ den Empfänger verbinden.");
            return;
        }
        _rec = new TrackRecording
        {
            Name = $"Spur {DateTime.Now:dd.MM. HH:mm}",
            Start = DateTime.Now,
            Color = TrackColors[_tracks.Count % TrackColors.Length]
        };
        _recPaused = false;
        _lastRec = null;
        _tracks.Add(_rec);
        _map.SetLive(Array.Empty<TrackPoint>());
        TracksGrid.SelectedItem = _rec;
        SetRecordingButtons(true);
        AppendLog($"Aufnahme gestartet: {_rec.Name}");
        UpdateLiveStats();
    }

    void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_rec == null) return;
        _recPaused = !_recPaused;
        PauseButton.Content = _recPaused ? "▶ Fortsetzen" : "❚❚ Pause";
        _lastRec = null; // nach Pause sofort den nächsten Punkt aufnehmen
        UpdateLiveStats();
    }

    void Stop_Click(object sender, RoutedEventArgs e) => StopRecording();

    void StopRecording()
    {
        var rec = _rec;
        if (rec == null) return;
        _rec = null;
        _recPaused = false;
        SetRecordingButtons(false);
        _map.SetLive(Array.Empty<TrackPoint>());
        if (rec.Points.Count < 2)
        {
            _tracks.Remove(rec);
            AppendLog("Aufnahme verworfen (weniger als 2 Punkte).");
        }
        else
        {
            AppendLog($"Aufnahme beendet: {rec.Name} – {rec.Count} Punkte, {rec.LengthM:F1} m, {rec.DurationText}");
            TracksGrid.SelectedItem = rec;
        }
        _tracksDirty = _projectDirty = true;
        LiveStatsText.Text = "Keine Aufnahme aktiv.";
        UpdateReplayControls(); // Auswahl bleibt gleich, daher kommt kein SelectionChanged
    }

    void FinishRecordingIfAny()
    {
        if (_rec == null) return;
        if (_rec.Points.Count < 2) _tracks.Remove(_rec);
        _rec = null;
    }

    void SetRecordingButtons(bool recording)
    {
        RecordButton.IsEnabled = !recording;
        PauseButton.IsEnabled = recording;
        StopButton.IsEnabled = recording;
        PauseButton.Content = "❚❚ Pause";
    }

    void RecordSample(GgaData g)
    {
        if (_rec == null || _recPaused || !g.HasPosition) return;

        bool ok = TrackQualityBox.SelectedIndex switch
        {
            0 => g.Quality > 0,
            1 => g.Quality is 4 or 5,
            _ => g.Quality == 4
        };
        if (!ok) { UpdateLiveStats(waitingFor: g.QualityText); return; }

        double minDist = Math.Max(0, ParseOr(MinDistBox.Text, 0.5));
        if (_lastRec != null && Geo.Distance(_lastRec.Lat, _lastRec.Lon, g.Latitude, g.Longitude) < minDist) return;

        var tp = new TrackPoint
        {
            T = DateTime.Now, Lat = g.Latitude, Lon = g.Longitude,
            H = g.EllipsoidalHeight, Hmsl = g.AltitudeMsl, Q = g.Quality,
            Speed = _rmc?.SpeedMs ?? 0
        };
        _rec.Add(tp);
        _lastRec = tp;
        _map.AppendLive(tp.Lat, tp.Lon);
        _projectDirty = true;
        UpdateLiveStats();
    }

    void UpdateLiveStats(string? waitingFor = null)
    {
        if (_rec == null) return;
        LiveStatsText.Text = (_recPaused ? "❚❚ Pausiert – " : "● Aufnahme läuft – ") +
            $"{_rec.Count} Punkte · {_rec.LengthM.ToString("F1", CultureInfo.CurrentCulture)} m · {_rec.DurationText} · Ø {_rec.AvgSpeedKmh.ToString("F1", CultureInfo.CurrentCulture)} km/h" +
            (waitingFor != null ? $"\nWarte auf geforderte Qualität (aktuell: {waitingFor})" : "");
    }

    // ---------- Liste ----------

    void TracksGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _tracksDirty = true;
        UpdateReplayControls();
    }

    void UpdateReplayControls()
    {
        StopReplay();
        var t = SelectedTrack;
        bool ok = t != null && t.Points.Count > 1;
        ReplaySlider.IsEnabled = ok;
        ReplayPlayButton.IsEnabled = ok;
        ReplaySlider.Maximum = ok ? t!.Points.Count - 1 : 1;
        ReplaySlider.Value = 0;
        ReplayText.Text = "";
        _map.SetReplay(null, null);
    }

    void TrackZoom_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTrack is not { } t || t.Points.Count == 0) { Info("Bitte zuerst eine Spur wählen."); return; }
        FollowToggle.IsChecked = false;
        _map.SetFollow(false);
        _map.Fit(t.Points.Where((_, i) => i % Math.Max(1, t.Points.Count / 500) == 0).Select(p => (p.Lat, p.Lon)));
    }

    void TrackDelete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTrack is not { } t) { Info("Bitte zuerst eine Spur wählen."); return; }
        if (t == _rec) { Info("Die laufende Aufnahme muss zuerst beendet werden."); return; }
        if (MessageBox.Show(this, $"Spur „{t.Name}“ löschen?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _tracks.Remove(t);
    }

    // ---------- Wiedergabe ----------

    void ReplaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_init || SelectedTrack is not { } t || t.Points.Count == 0) return;
        int i = Math.Clamp((int)ReplaySlider.Value, 0, t.Points.Count - 1);
        var p = t.Points[i];
        _map.SetReplay(p.Lat, p.Lon);
        ReplayText.Text = $"{p.T:HH:mm:ss} · {(p.Speed * 3.6).ToString("F1", CultureInfo.CurrentCulture)} km/h · h {Fmt(p.H, "F2")} m · {i + 1}/{t.Points.Count}";
    }

    void ReplayPlay_Click(object sender, RoutedEventArgs e)
    {
        if (!_replayHooked) { _replayTimer.Tick += ReplayTick; _replayHooked = true; }
        if (_replayTimer.IsEnabled) { StopReplay(); return; }
        if (SelectedTrack == null) return;
        if (ReplaySlider.Value >= ReplaySlider.Maximum) ReplaySlider.Value = 0;
        ReplayPlayButton.Content = "❚❚ Stopp";
        _replayTimer.Start();
    }

    void ReplayTick(object? sender, EventArgs e)
    {
        int step = int.TryParse((ReplaySpeedBox.SelectedItem as ComboBoxItem)?.Tag as string, out var s) ? s : 5;
        ReplaySlider.Value = Math.Min(ReplaySlider.Maximum, ReplaySlider.Value + step);
        if (ReplaySlider.Value >= ReplaySlider.Maximum) StopReplay();
    }

    void StopReplay()
    {
        _replayTimer.Stop();
        ReplayPlayButton.Content = "▶ Abspielen";
    }
}
