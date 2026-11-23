namespace Ntrip.Core;

/// <summary>Einfacher RTCM3-Framer: 0xD3, 6 Bit reserviert + 10 Bit Länge, Payload, 3 Byte CRC24Q.</summary>
public class RtcmParser
{
    readonly List<byte> _buf = new();
    readonly SortedDictionary<int, int> _counts = new();
    int _crcErrors;

    /// <summary>Anzahl gültiger RTCM-Nachrichten seit Programmstart.</summary>
    public long TotalFrames { get; private set; }

    /// <summary>Wird für jede CRC-geprüfte Nachricht aufgerufen (im Thread von Feed).</summary>
    public event Action<RtcmMessage>? MessageParsed;

    public void Feed(byte[] data, int n)
    {
        for (int i = 0; i < n; i++) _buf.Add(data[i]);
        while (true)
        {
            int start = _buf.IndexOf(0xD3);
            if (start < 0) { _buf.Clear(); return; }
            if (start > 0) _buf.RemoveRange(0, start);
            if (_buf.Count < 3) return;
            int len = ((_buf[1] & 0x03) << 8) | _buf[2];
            int frame = len + 6;
            if (_buf.Count < frame) return;
            var arr = _buf.GetRange(0, frame).ToArray();
            uint crc = Crc24Q(arr, frame - 3);
            uint rx = (uint)(arr[frame - 3] << 16 | arr[frame - 2] << 8 | arr[frame - 1]);
            if (crc == rx)
            {
                int type = len >= 2 ? (arr[3] << 4) | (arr[4] >> 4) : 0;
                _counts[type] = _counts.GetValueOrDefault(type) + 1;
                TotalFrames++;
                MessageParsed?.Invoke(new RtcmMessage(type, arr[3..(3 + len)], DateTime.Now));
                _buf.RemoveRange(0, frame);
            }
            else
            {
                _crcErrors++;
                _buf.RemoveAt(0);
            }
        }
    }

    public string Summary() =>
        (_counts.Count == 0 ? "-" : string.Join(" ", _counts.Select(kv => $"{kv.Key}x{kv.Value}")))
        + (_crcErrors > 0 ? $" (CRC-Fehler: {_crcErrors})" : "");

    public void Reset() { _counts.Clear(); _crcErrors = 0; }

    static uint Crc24Q(byte[] d, int len)
    {
        uint crc = 0;
        for (int i = 0; i < len; i++)
        {
            crc ^= (uint)d[i] << 16;
            for (int b = 0; b < 8; b++)
            {
                crc <<= 1;
                if ((crc & 0x1000000) != 0) crc ^= 0x1864CFB;
            }
        }
        return crc & 0xFFFFFF;
    }
}
