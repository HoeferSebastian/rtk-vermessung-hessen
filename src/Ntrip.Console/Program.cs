using Ntrip.Core;

var opt = NtripOptions.LoadFromJson(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var client = new NtripClient();
client.Log += Console.WriteLine;

if (opt.ListSourcetable)
{
    try { Console.WriteLine(await client.GetSourcetableAsync(opt, cts.Token)); }
    catch (Exception ex) { Console.WriteLine($"[Fehler] {ex.Message}"); }
    return;
}

FileStream? file = string.IsNullOrWhiteSpace(opt.OutputFile) ? null : File.Create(opt.OutputFile);
var parser = new RtcmParser();
long lastTotal = 0;
var lastReport = DateTime.UtcNow;

client.DataReceived += (buf, n) =>
{
    file?.Write(buf, 0, n);
    parser.Feed(buf, n);
    if ((DateTime.UtcNow - lastReport).TotalSeconds >= 5)
    {
        Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {client.TotalBytes - lastTotal} Bytes/5s, gesamt {client.TotalBytes}  | RTCM: {parser.Summary()}");
        lastTotal = client.TotalBytes; lastReport = DateTime.UtcNow;
        parser.Reset();
    }
};
client.Connected += () => { lastTotal = 0; lastReport = DateTime.UtcNow; Console.WriteLine("Verbunden (Strg+C zum Beenden)."); };

while (!cts.IsCancellationRequested)
{
    try { await client.StreamAsync(opt, cts.Token); }
    catch (OperationCanceledException) { break; }
    catch (UnauthorizedAccessException ex) { Console.WriteLine($"[Fehler] {ex.Message}"); break; }
    catch (Exception ex) { Console.WriteLine($"[Fehler] {ex.Message}"); }

    Console.WriteLine($"Neuverbindung in {opt.ReconnectDelaySeconds}s ...");
    try { await Task.Delay(TimeSpan.FromSeconds(opt.ReconnectDelaySeconds), cts.Token); } catch { break; }
}
file?.Dispose();
