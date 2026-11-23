using System.Text;

namespace Ntrip.Core;

/// <summary>Eine gültige RTCM3-Nachricht (Payload ohne Preamble/Länge/CRC).</summary>
public sealed record RtcmMessage(int Type, byte[] Payload, DateTime Time);

public sealed record RtcmStation(int StationId, double X, double Y, double Z, double? AntennaHeight)
{
    public (double Lat, double Lon, double H) Geodetic => Geo.EcefToGeodetic(X, Y, Z);
}

public sealed record RtcmDescriptor(string Antenna, string AntennaSerial, string Receiver, string Firmware, string ReceiverSerial);

public static class Rtcm
{
    public static string Name(int type)
    {
        int d = type % 10;
        string? sys = type switch
        {
            >= 1070 and < 1080 => "GPS",
            >= 1080 and < 1090 => "GLONASS",
            >= 1090 and < 1100 => "Galileo",
            >= 1100 and < 1110 => "SBAS",
            >= 1110 and < 1120 => "QZSS",
            >= 1120 and < 1130 => "BeiDou",
            >= 1130 and < 1140 => "NavIC",
            _ => null
        };
        if (sys != null && d is >= 1 and <= 7) return $"{sys} MSM{d} Beobachtungen";

        return type switch
        {
            1004 => "GPS L1/L2 Beobachtungen (erweitert)",
            1005 => "Stationskoordinaten (ARP)",
            1006 => "Stationskoordinaten + Antennenhöhe",
            1007 => "Antennenbeschreibung",
            1008 => "Antennenbeschreibung + Seriennr.",
            1012 => "GLONASS L1/L2 Beobachtungen (erweitert)",
            1013 => "Systemparameter",
            1019 => "GPS Ephemeriden",
            1020 => "GLONASS Ephemeriden",
            1021 => "Koordinatentransformation (Helmert/Molodensky)",
            1023 => "Netz-Residuen-Gitter",
            1025 => "Kartenprojektion (z. B. UTM)",
            1030 => "GPS Netz-RTK-Residuen",
            1031 => "GLONASS Netz-RTK-Residuen",
            1032 => "Physische Referenzstationsposition",
            1033 => "Empfänger- und Antennenbeschreibung",
            1042 => "BeiDou Ephemeriden",
            1044 => "QZSS Ephemeriden",
            1045 => "Galileo F/NAV Ephemeriden",
            1046 => "Galileo I/NAV Ephemeriden",
            1230 => "GLONASS Code-Phasen-Biases",
            1303 => "BeiDou Netz-RTK-Residuen",
            1304 => "Galileo Netz-RTK-Residuen",
            >= 4001 and <= 4095 => "Herstellerspezifisch",
            _ => "Unbekannt"
        };
    }

    public static string? Constellation(int type) => type switch
    {
        >= 1071 and <= 1077 => "GPS",
        >= 1081 and <= 1087 => "GLONASS",
        >= 1091 and <= 1097 => "Galileo",
        >= 1101 and <= 1107 => "SBAS",
        >= 1111 and <= 1117 => "QZSS",
        >= 1121 and <= 1127 => "BeiDou",
        >= 1131 and <= 1137 => "NavIC",
        _ => null
    };

    static long Bits(byte[] d, int pos, int count)
    {
        long v = 0;
        for (int i = 0; i < count; i++)
        {
            int bit = pos + i;
            v = (v << 1) | (long)((d[bit >> 3] >> (7 - (bit & 7))) & 1);
        }
        return v;
    }

    static long SignedBits(byte[] d, int pos, int count)
    {
        long v = Bits(d, pos, count);
        return (v & (1L << (count - 1))) != 0 ? v - (1L << count) : v;
    }

    /// <summary>1005/1006: Stationskoordinaten (ECEF, Meter).</summary>
    public static bool TryDecodeStation(RtcmMessage m, out RtcmStation station)
    {
        station = null!;
        if (m.Type is not (1005 or 1006)) return false;
        int need = m.Type == 1005 ? 19 : 21;
        if (m.Payload.Length < need) return false;
        var p = m.Payload;
        int id = (int)Bits(p, 12, 12);
        double x = SignedBits(p, 34, 38) * 0.0001;
        double y = SignedBits(p, 74, 38) * 0.0001;
        double z = SignedBits(p, 114, 38) * 0.0001;
        double? ah = m.Type == 1006 ? Bits(p, 152, 16) * 0.0001 : null;
        station = new RtcmStation(id, x, y, z, ah);
        return true;
    }

    /// <summary>1033: Antennen-/Empfängerbeschreibung.</summary>
    public static bool TryDecodeDescriptor(RtcmMessage m, out RtcmDescriptor desc)
    {
        desc = null!;
        if (m.Type != 1033) return false;
        var p = m.Payload;
        int pos = 3; // 12 Bit Typ + 12 Bit Stations-ID
        try
        {
            string Str()
            {
                int n = p[pos++];
                var s = Encoding.ASCII.GetString(p, pos, n);
                pos += n;
                return s;
            }
            string ant = Str();
            pos++; // Antenna Setup ID
            string antSn = Str();
            string rx = Str();
            string fw = Str();
            string rxSn = Str();
            desc = new RtcmDescriptor(ant, antSn, rx, fw, rxSn);
            return true;
        }
        catch { return false; }
    }

    /// <summary>MSM-Nachrichten: Anzahl der in der Korrektur enthaltenen Satelliten (Satellitenmaske).</summary>
    public static bool TryDecodeMsm(RtcmMessage m, out string constellation, out int satellites)
    {
        constellation = Constellation(m.Type) ?? "";
        satellites = 0;
        if (constellation.Length == 0 || m.Payload.Length < 18) return false;
        long hi = Bits(m.Payload, 73, 32), lo = Bits(m.Payload, 105, 32);
        satellites = System.Numerics.BitOperations.PopCount((ulong)hi) + System.Numerics.BitOperations.PopCount((ulong)lo);
        return true;
    }
}
