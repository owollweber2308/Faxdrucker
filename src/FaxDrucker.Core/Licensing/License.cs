using System.Security.Cryptography;
using System.Text;

namespace FaxDrucker.Core.Licensing;

/// <summary>
/// Offline-Lizenz: Kundendaten + ECDSA-P256-Signatur, als Base32-Text kodiert.
/// Nur der Lizenzgenerator kennt den privaten Schlüssel; das Programm enthält den öffentlichen
/// und kann damit prüfen, aber keine Lizenzen erzeugen.
/// </summary>
public sealed record License(string Customer, uint CustomerNumber, DateOnly Issued, DateOnly? Expires)
{
    public const string Prefix = "FD1-";
    private const byte FormatVersion = 1;
    private static readonly DateOnly Epoch = new(2020, 1, 1);

    public bool IsExpired(DateOnly today) => Expires is { } e && today > e;

    public string Describe() =>
        $"{Customer} (Kd.-Nr. {CustomerNumber}), " + (Expires is { } e ? $"gültig bis {e:dd.MM.yyyy}" : "unbefristet");

    /// <summary>Erzeugt einen Lizenzschlüssel (nur im Lizenzgenerator mit privatem Schlüssel).</summary>
    public string Sign(ECDsa privateKey)
    {
        var payload = Payload();
        var sig = privateKey.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Format(Base32.Encode([.. payload, .. sig]));
    }

    private byte[] Payload()
    {
        var name = Encoding.UTF8.GetBytes(Customer.Trim());
        if (name.Length is 0 or > 120) throw new ArgumentException("Kundenname muss 1–120 Zeichen lang sein.");
        var b = new List<byte> { FormatVersion };
        b.AddRange(BitConverter.IsLittleEndian ? BitConverter.GetBytes(CustomerNumber).Reverse() : BitConverter.GetBytes(CustomerNumber));
        AddDays(b, Issued);
        AddDays(b, Expires);
        b.Add((byte)name.Length);
        b.AddRange(name);
        return b.ToArray();
    }

    private static void AddDays(List<byte> b, DateOnly? d)
    {
        int days = d is { } x ? x.DayNumber - Epoch.DayNumber + 1 : 0; // 0 = kein Datum
        if (days is < 0 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(d));
        b.Add((byte)(days >> 8));
        b.Add((byte)days);
    }

    private static string Format(string base32)
    {
        var sb = new StringBuilder(Prefix);
        for (int i = 0; i < base32.Length; i += 5)
        {
            if (i > 0) sb.Append('-');
            sb.Append(base32, i, Math.Min(5, base32.Length - i));
        }
        return sb.ToString();
    }

    /// <summary>Prüft einen Lizenzschlüssel. Liefert null und eine Fehlermeldung, wenn er ungültig ist.</summary>
    public static License? Verify(string? key, ECDsa publicKey, out string error)
    {
        error = "";
        var text = new string((key ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        if (!text.StartsWith(Prefix))
        {
            error = "Das ist kein FaxDrucker-Lizenzschlüssel (er beginnt mit „FD1-“).";
            return null;
        }
        byte[] raw;
        try { raw = Base32.Decode(text[Prefix.Length..].Replace("-", "")); }
        catch (FormatException)
        {
            error = "Der Lizenzschlüssel enthält ungültige Zeichen. Bitte vollständig kopieren und einfügen.";
            return null;
        }
        const int sigLen = 64;
        if (raw.Length < 10 + sigLen || raw[0] != FormatVersion)
        {
            error = "Der Lizenzschlüssel ist unvollständig.";
            return null;
        }
        var payload = raw[..^sigLen];
        var sig = raw[^sigLen..];
        int nameLen = payload[9];
        if (payload.Length != 10 + nameLen ||
            !publicKey.VerifyData(payload, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            error = "Der Lizenzschlüssel ist ungültig.";
            return null;
        }
        uint nr = (uint)(payload[1] << 24 | payload[2] << 16 | payload[3] << 8 | payload[4]);
        DateOnly? Day(int i) { int d = payload[i] << 8 | payload[i + 1]; return d == 0 ? null : Epoch.AddDays(d - 1); }
        var name = Encoding.UTF8.GetString(payload, 10, nameLen);
        return new License(name, nr, Day(5) ?? Epoch, Day(7));
    }
}

/// <summary>Base32 nach Crockford (ohne I, L, O, U – gut abtippbar), ohne Auffüllung.</summary>
internal static class Base32
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Encode(byte[] data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = buffer << 8 | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[buffer >> (bits - 5) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Alphabet[buffer << (5 - bits) & 31]);
        return sb.ToString();
    }

    public static byte[] Decode(string s)
    {
        var result = new List<byte>(s.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var ch in s)
        {
            int v = ch switch { 'O' => 0, 'I' or 'L' => 1, _ => Alphabet.IndexOf(ch) };
            if (v < 0) throw new FormatException();
            buffer = (buffer << 5 | v) & 0xFFFF;
            bits += 5;
            if (bits >= 8)
            {
                result.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }
        return result.ToArray();
    }
}
