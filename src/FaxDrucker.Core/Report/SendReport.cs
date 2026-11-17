using System.Globalization;
using System.Text;

namespace FaxDrucker.Core.Report;

/// <summary>Erzeugt einen einseitigen Sendebericht als PDF (ohne externe Bibliothek).</summary>
public static class SendReport
{
    public sealed record Data(DateTime Time, string Number, string? Recipient, string Document, bool Success,
        string Message, int PagesSent, int PagesTotal, int Attempts, string? RemoteIdent, int BitRate,
        string? Transport, string StationId);

    public static void Write(string path, Data d)
    {
        var lines = new List<(string text, int size, bool bold)>
        {
            ("Sendebericht FaxDrucker", 18, true),
            ("", 12, false),
            ("Ergebnis:      " + (d.Success ? "OK - Fax gesendet" : "FEHLER - Fax nicht gesendet"), 13, true),
            ("Meldung:       " + d.Message, 11, false),
            ("", 11, false),
            ("Datum/Uhrzeit: " + d.Time.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.GetCultureInfo("de-DE")), 11, false),
            ("Empfänger:     " + (string.IsNullOrWhiteSpace(d.Recipient) ? "-" : d.Recipient), 11, false),
            ("Faxnummer:     " + d.Number, 11, false),
            ("Gegenstelle:   " + (string.IsNullOrWhiteSpace(d.RemoteIdent) ? "-" : d.RemoteIdent), 11, false),
            ("Dokument:      " + d.Document, 11, false),
            ($"Seiten:        {d.PagesSent} von {d.PagesTotal} übertragen", 11, false),
            ("Versuche:      " + d.Attempts, 11, false),
            ("Übertragung:   " + (d.Transport ?? "-") + (d.BitRate > 0 ? $", {d.BitRate} bit/s" : ""), 11, false),
            ("Absender-ID:   " + (string.IsNullOrWhiteSpace(d.StationId) ? "-" : d.StationId), 11, false),
        };

        var content = new StringBuilder();
        double y = 780;
        foreach (var (text, size, bold) in lines)
        {
            if (text.Length > 0)
                content.Append($"BT /{(bold ? "F2" : "F1")} {size} Tf 56 {y.ToString(CultureInfo.InvariantCulture)} Td ({Escape(text)}) Tj ET\n");
            y -= size + 8;
        }
        WritePdf(path, content.ToString());
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (c is '(' or ')' or '\\') sb.Append('\\').Append(c);
            else if (c < 32) sb.Append(' ');
            else if (c < 128) sb.Append(c);
            else if (c < 256) sb.Append('\\').Append(System.Convert.ToString(c, 8).PadLeft(3, '0')); // WinAnsi = Latin-1 für Umlaute
            else sb.Append('?');
        }
        return sb.ToString();
    }

    private static void WritePdf(string path, string content)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R /F2 5 0 R >> >> /Contents 6 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Courier-Bold /Encoding /WinAnsiEncoding >>",
            $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}endstream",
        };
        using var ms = new MemoryStream();
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        W("%PDF-1.4\n");
        var offsets = new List<long>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(ms.Position);
            W($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = ms.Position;
        W($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) W($"{o:D10} 00000 n \n");
        W($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path, ms.ToArray());
    }
}
