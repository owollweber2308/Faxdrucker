using FaxDrucker.Core;
using FaxDrucker.Core.Convert;
using FaxDrucker.Core.Fax;
using FaxDrucker.Core.Media;
using FaxDrucker.Core.Settings;
using FaxDrucker.Core.Sip;

// faxsend – Kommandozeile zum Testen und für Skripte.
//   faxsend einrichten
//   faxsend senden <Nummer> <Datei.pdf|.ps|.tif> [--name Empfänger] [--g711] [--trace]
//                  [--normal|--fein|--superfein] [--text|--foto]
//   faxsend selbsttest
//   faxsend protokoll

var argList = args.ToList();
if (argList.Count == 0 || argList[0] is "-h" or "--help" or "hilfe")
{
    PrintHelp();
    return 0;
}

try
{
    switch (argList[0].ToLowerInvariant())
    {
        case "einrichten":
        case "setup":
            return Setup();
        case "senden":
        case "send":
            return await Send(argList.Skip(1).ToList());
        case "selbsttest":
        case "selftest":
            return await SelfTest();
        case "protokoll":
        case "journal":
            foreach (var e in FaxService.ReadJournal(30))
                Console.WriteLine($"{e.Time:dd.MM.yyyy HH:mm}  {(e.Success ? "OK    " : "FEHLER")}  {e.Number,-18} {e.Pages} S.  {e.Message}");
            return 0;
        default:
            PrintHelp();
            return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine("Fehler: " + ex.Message);
    return 1;
}

static void PrintHelp()
{
    Console.WriteLine("""
        FaxDrucker – Fax über SIP (T.38/G.711)

          faxsend einrichten                    SIP-Zugang (z. B. Placetel) eintragen
          faxsend senden <Nummer> <Datei>       PDF/PostScript/TIFF faxen
                 [--name Empfänger] [--g711] [--trace]
                 [--normal|--fein|--superfein] [--text|--foto]
          faxsend selbsttest                    Ghostscript + Faxmodem lokal prüfen
          faxsend protokoll                     letzte Sendungen anzeigen

        Einstellungen: %APPDATA%\FaxDrucker\settings.json
        """);
}

static int Setup()
{
    var s = AppSettings.Load();
    string Ask(string label, string current, bool secret = false)
    {
        Console.Write($"{label}{(string.IsNullOrEmpty(current) || secret ? "" : $" [{current}]")}: ");
        var v = secret ? ReadSecret() : Console.ReadLine();
        return string.IsNullOrWhiteSpace(v) ? current : v.Trim();
    }
    Console.WriteLine("SIP-Zugang einrichten (Placetel: Domain fpbx.de, Benutzer z. B. 777123456).");
    s.Sip.Domain = Ask("SIP-Domain", s.Sip.Domain);
    s.Sip.Username = Ask("SIP-Benutzername", s.Sip.Username);
    s.Sip.Password = Ask("SIP-Passwort (Eingabe unsichtbar)", s.Sip.Password, secret: true);
    s.StationId = Ask("Eigene Faxkennung, z. B. +49 221 1234567", s.StationId);
    s.HeaderText = Ask("Kopfzeile (z. B. Firmenname)", s.HeaderText);
    s.Save();
    Console.WriteLine($"Gespeichert in {AppSettings.DefaultPath}");
    return 0;
}

static string ReadSecret()
{
    if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
    var sb = new System.Text.StringBuilder();
    while (true)
    {
        var k = Console.ReadKey(true);
        if (k.Key == ConsoleKey.Enter) break;
        if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
        sb.Append(k.KeyChar);
    }
    Console.WriteLine();
    return sb.ToString();
}

static async Task<int> Send(List<string> a)
{
    string? name = null;
    bool g711 = false;
    FaxResolution? res = null;
    ImageMode? mode = null;
    var positional = new List<string>();
    for (int i = 0; i < a.Count; i++)
    {
        if (a[i] == "--name" && i + 1 < a.Count) name = a[++i];
        else if (a[i] == "--g711") g711 = true;
        else if (a[i] == "--normal") res = FaxResolution.Normal;
        else if (a[i] == "--fein") res = FaxResolution.Fein;
        else if (a[i] == "--superfein") res = FaxResolution.Superfein;
        else if (a[i] == "--foto") mode = ImageMode.Foto;
        else if (a[i] == "--text") mode = ImageMode.Text;
        else if (a[i] == "--trace") Environment.SetEnvironmentVariable("FAXDRUCKER_SIP_TRACE", "1");
        else positional.Add(a[i]);
    }
    if (positional.Count != 2)
    {
        Console.Error.WriteLine("Aufruf: faxsend senden <Nummer> <Datei>");
        return 2;
    }
    var s = AppSettings.Load();
    if (string.IsNullOrEmpty(s.Sip.Username))
    {
        Console.Error.WriteLine("Noch kein SIP-Zugang eingerichtet. Bitte zuerst: faxsend einrichten");
        return 2;
    }
    if (g711) s.Sip.T38 = T38Policy.Off;
    var service = new FaxService(s);
    service.Status += m => Console.WriteLine("» " + m);
    service.Log += m => { if (s.VerboseLog || !m.StartsWith("spandsp:")) Console.WriteLine("  " + m); };
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    var entry = await service.SendAsync(new FaxRequest
    {
        Number = positional[0],
        SourceFile = Path.GetFullPath(positional[1]),
        RecipientName = name,
        DocumentName = Path.GetFileName(positional[1]),
        Resolution = res,
        ImageMode = mode,
    }, cts.Token);
    Console.WriteLine(entry.Success ? $"OK: {entry.Message}" : $"FEHLER: {entry.Message}");
    if (entry.ReportPath != null) Console.WriteLine("Sendebericht: " + entry.ReportPath);
    return entry.Success ? 0 : 1;
}

static async Task<int> SelfTest()
{
    Console.WriteLine($"spandsp-Version: {FaxSession.SpanDspVersion}");
    var gs = GhostscriptConverter.Find();
    Console.WriteLine($"Ghostscript: {gs ?? "NICHT GEFUNDEN"}");
    if (gs == null) return 1;

    var dir = Path.Combine(Path.GetTempPath(), "FaxDrucker-Selbsttest");
    Directory.CreateDirectory(dir);
    var ps = Path.Combine(dir, "test.ps");
    File.WriteAllText(ps, "%!PS\n/Helvetica findfont 28 scalefont setfont 72 720 moveto (FaxDrucker Selbsttest) show showpage\n");
    var tif = Path.Combine(dir, "test.tif");
    var pages = await new GhostscriptConverter(gs).ToFaxTiffAsync(ps, tif);
    Console.WriteLine($"Umwandlung: {pages} Seite(n) OK");

    var rx = Path.Combine(dir, "empfangen.tif");
    using var a = new FaxSession(FaxTransport.T38, true, tif, null, "SENDER", "Selbsttest");
    using var b = new FaxSession(FaxTransport.T38, false, null, rx, "EMPFAENGER", null);
    var encA = new UdptlEncoder(); var decB = new UdptlDecoder();
    var encB = new UdptlEncoder(); var decA = new UdptlDecoder();
    var qa = new Queue<byte[]>(); var qb = new Queue<byte[]>();
    a.T38PacketOut += (p, _) => qb.Enqueue(encA.Encode(p));
    b.T38PacketOut += (p, _) => qa.Enqueue(encB.Encode(p));
    for (int i = 0; i < 15000 && !(a.IsDone && b.IsDone); i++)
    {
        a.TickT38(160); b.TickT38(160);
        while (qb.Count > 0) foreach (var p in decB.Decode(qb.Dequeue())) b.ReceiveT38(p.Data, p.Seq);
        while (qa.Count > 0) foreach (var p in decA.Decode(qa.Dequeue())) a.ReceiveT38(p.Data, p.Seq);
    }
    Console.WriteLine($"T.38-Probeübertragung: {FaxSession.ResultText(a.Result)}, {a.PagesSent} Seite(n), {a.BitRate} bit/s");
    return a.Result == 0 ? 0 : 1;
}
