namespace FaxDrucker.Core.Media;

/// <summary>
/// UDPTL-Rahmen für T.38 (ITU-T T.38, Abschnitt 9.1) mit Redundanz-Fehlerkorrektur.
/// Jedes Paket enthält das aktuelle IFP-Paket und die letzten N Pakete als Sicherung.
/// </summary>
public sealed class UdptlEncoder
{
    private readonly int _redundancy;
    private readonly LinkedList<byte[]> _history = new();
    private ushort _seq;

    public UdptlEncoder(int redundancy = 3) => _redundancy = Math.Max(0, redundancy);

    public byte[] Encode(ReadOnlySpan<byte> ifp)
    {
        var buf = new List<byte>(ifp.Length * (_redundancy + 1) + 16)
        {
            (byte)(_seq >> 8),
            (byte)_seq,
        };
        WriteOpenType(buf, ifp);

        // error-recovery: secondary-ifp-packets (CHOICE-Index 0)
        buf.Add(0x00);
        WriteLength(buf, _history.Count);
        foreach (var old in _history)
            WriteOpenType(buf, old);

        _history.AddFirst(ifp.ToArray());
        while (_history.Count > _redundancy)
            _history.RemoveLast();
        _seq++;
        return buf.ToArray();
    }

    private static void WriteLength(List<byte> buf, int len)
    {
        if (len < 0x80)
            buf.Add((byte)len);
        else if (len < 0x4000)
        {
            buf.Add((byte)(0x80 | (len >> 8)));
            buf.Add((byte)len);
        }
        else
            throw new ArgumentOutOfRangeException(nameof(len), "IFP-Paket zu groß für UDPTL.");
    }

    private static void WriteOpenType(List<byte> buf, ReadOnlySpan<byte> data)
    {
        WriteLength(buf, data.Length);
        foreach (var b in data)
            buf.Add(b);
    }
}

/// <summary>Entpackt UDPTL und stellt verlorene Pakete aus der Redundanz wieder her.</summary>
public sealed class UdptlDecoder
{
    private int _expected = -1;

    /// <summary>Ein entpacktes IFP-Paket mit seiner Sequenznummer.</summary>
    public readonly record struct Ifp(ushort Seq, byte[] Data);

    /// <summary>
    /// Liefert die IFP-Pakete in richtiger Reihenfolge: zuerst wiederhergestellte, dann das aktuelle.
    /// Doppelte oder veraltete Pakete werden verworfen.
    /// </summary>
    public IReadOnlyList<Ifp> Decode(ReadOnlySpan<byte> packet)
    {
        var result = new List<Ifp>();
        if (!TryParse(packet, out var seq, out var primary, out var secondaries))
            return result;

        if (_expected >= 0)
        {
            var diff = (ushort)(seq - _expected);
            if (diff >= 0x8000)
                return result; // altes oder doppeltes Paket

            // Verlorene Pakete aus der Redundanz (secondaries[0] = seq-1, ...)
            for (int missing = Math.Min(diff, secondaries.Count); missing >= 1; missing--)
                result.Add(new Ifp((ushort)(seq - missing), secondaries[missing - 1]));
        }

        result.Add(new Ifp(seq, primary));
        _expected = (ushort)(seq + 1);
        return result;
    }

    public static bool TryParse(ReadOnlySpan<byte> p, out ushort seq, out byte[] primary, out List<byte[]> secondaries)
    {
        seq = 0;
        primary = Array.Empty<byte>();
        secondaries = new List<byte[]>();
        if (p.Length < 3)
            return false;
        seq = (ushort)((p[0] << 8) | p[1]);
        int pos = 2;
        if (!ReadOpenType(p, ref pos, out primary))
            return false;
        if (pos >= p.Length)
            return true; // keine Fehlerkorrektur
        var choice = p[pos++];
        if ((choice & 0x80) != 0)
            return true; // FEC wird nicht ausgewertet, primäres Paket genügt
        if (!ReadLength(p, ref pos, out var count))
            return true;
        for (int i = 0; i < count; i++)
        {
            if (!ReadOpenType(p, ref pos, out var sec))
                break;
            secondaries.Add(sec);
        }
        return true;
    }

    private static bool ReadLength(ReadOnlySpan<byte> p, ref int pos, out int len)
    {
        len = 0;
        if (pos >= p.Length) return false;
        var b = p[pos++];
        if ((b & 0x80) == 0)
        {
            len = b;
            return true;
        }
        if ((b & 0x40) != 0) return false; // Fragmentierung wird nicht unterstützt
        if (pos >= p.Length) return false;
        len = ((b & 0x3F) << 8) | p[pos++];
        return true;
    }

    private static bool ReadOpenType(ReadOnlySpan<byte> p, ref int pos, out byte[] data)
    {
        data = Array.Empty<byte>();
        if (!ReadLength(p, ref pos, out var len) || pos + len > p.Length)
            return false;
        data = p.Slice(pos, len).ToArray();
        pos += len;
        return true;
    }
}
