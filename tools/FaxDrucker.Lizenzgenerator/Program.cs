using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FaxDrucker.Core.Licensing;

// FaxDrucker Lizenzgenerator – NUR für den Herausgeber, nicht an Kunden weitergeben.
//
//   Lizenzgenerator                                     Fragt Kunde, Kundennummer und Ablaufdatum ab
//   Lizenzgenerator ausstellen --kunde "Firma" --nr 1001 [--bis 31.12.2027]
//   Lizenzgenerator liste                               Ausgestellte Lizenzen anzeigen
//   Lizenzgenerator pruefen <Schlüssel>                 Schlüssel prüfen und anzeigen
//   Lizenzgenerator schluesselpaar                      Neues Schlüsselpaar erzeugen (macht alle alten Lizenzen ungültig!)
//
// Dateien neben dem Programm (oder im Ordner aus FAXDRUCKER_LIZENZ_ORDNER):
//   lizenz-privat.pem   geheimer Signaturschlüssel – sicher aufbewahren, Sicherungskopie anlegen
//   lizenzen.csv        Liste aller ausgestellten Lizenzen (öffnet sich in Excel)

Console.OutputEncoding = Encoding.UTF8;
var folder = Environment.GetEnvironmentVariable("FAXDRUCKER_LIZENZ_ORDNER") is { Length: > 0 } f ? f : AppContext.BaseDirectory;
var keyPath = Path.Combine(folder, "lizenz-privat.pem");
var csvPath = Path.Combine(folder, "lizenzen.csv");
var de = CultureInfo.GetCultureInfo("de-DE");
bool interactive = args.Length == 0;

try
{
    int rc = (args.FirstOrDefault() ?? "").ToLowerInvariant() switch
    {
        "" => Interactive(),
        "ausstellen" => Issue(Opt("--kunde"), Opt("--nr"), Opt("--bis")),
        "liste" => List(),
        "pruefen" or "prüfen" => Check(string.Join("", args.Skip(1))),
        "schluesselpaar" or "schlüsselpaar" => NewKeyPair(),
        _ => Help(),
    };
    Pause();
    return rc;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Fehler: " + ex.Message);
    Pause();
    return 1;
}

string? Opt(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

void Pause()
{
    if (!interactive) return;
    Console.WriteLine();
    Console.Write("Mit Enter schließen …");
    Console.ReadLine();
}

int Help()
{
    Console.WriteLine("""
        FaxDrucker Lizenzgenerator

          Lizenzgenerator                                   Lizenz im Dialog ausstellen
          Lizenzgenerator ausstellen --kunde "Firma" --nr 1001 [--bis 31.12.2027]
          Lizenzgenerator liste                             ausgestellte Lizenzen anzeigen
          Lizenzgenerator pruefen <Schlüssel>               Schlüssel prüfen
          Lizenzgenerator schluesselpaar                    neues Schlüsselpaar (alte Lizenzen werden ungültig!)
        """);
    return 2;
}

int Interactive()
{
    Console.WriteLine("FaxDrucker Lizenzgenerator");
    Console.WriteLine("==========================");
    if (!File.Exists(keyPath))
    {
        Console.WriteLine($"Kein privater Schlüssel gefunden ({keyPath}).");
        Console.WriteLine("Lege die Datei lizenz-privat.pem in denselben Ordner wie dieses Programm.");
        return 1;
    }
    Console.WriteLine();
    Console.Write("Kunde (Firmenname): ");
    var customer = Console.ReadLine();
    uint suggested = NextCustomerNumber();
    Console.Write($"Kundennummer [{suggested}]: ");
    var nr = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(nr)) nr = suggested.ToString();
    Console.Write("Gültig bis (TT.MM.JJJJ, leer = unbefristet): ");
    var until = Console.ReadLine();
    return Issue(customer, nr, until);
}

int Issue(string? customer, string? nr, string? until)
{
    if (string.IsNullOrWhiteSpace(customer)) throw new ArgumentException("Kundenname fehlt (--kunde).");
    if (!uint.TryParse(nr, out var number)) throw new ArgumentException("Kundennummer muss eine Zahl sein (--nr).");
    DateOnly? expires = null;
    if (!string.IsNullOrWhiteSpace(until))
    {
        if (!DateOnly.TryParse(until, de, out var d) && !DateOnly.TryParse(until, CultureInfo.InvariantCulture, out d))
            throw new ArgumentException("Datum nicht erkannt. Bitte als TT.MM.JJJJ angeben.");
        expires = d;
        if (d < DateOnly.FromDateTime(DateTime.Today)) throw new ArgumentException("Das Ablaufdatum liegt in der Vergangenheit.");
    }

    using var key = LoadPrivateKey();
    var license = new License(customer.Trim(), number, DateOnly.FromDateTime(DateTime.Today), expires);
    var text = license.Sign(key);

    // Gegenprobe mit dem im Programm eingebauten öffentlichen Schlüssel
    if (LicenseStore.Verify(text, out var err) == null)
        throw new InvalidOperationException("Der erzeugte Schlüssel passt nicht zum öffentlichen Schlüssel im Programm: " + err);

    bool newFile = !File.Exists(csvPath);
    using (var w = new StreamWriter(csvPath, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: newFile)))
    {
        if (newFile) w.WriteLine("Ausgestellt;Kundennummer;Kunde;Gültig bis;Lizenzschlüssel");
        w.WriteLine(string.Join(';', license.Issued.ToString("dd.MM.yyyy"), number, Csv(license.Customer),
            expires?.ToString("dd.MM.yyyy") ?? "unbefristet", text));
    }

    Console.WriteLine();
    Console.WriteLine("Lizenz ausgestellt: " + license.Describe());
    Console.WriteLine();
    Console.WriteLine(text);
    Console.WriteLine();
    Console.WriteLine($"Eingetragen in {csvPath}");
    return 0;
}

static string Csv(string s) => s.Replace(';', ',');

uint NextCustomerNumber()
{
    uint max = 1000;
    if (File.Exists(csvPath))
        foreach (var line in File.ReadLines(csvPath).Skip(1))
        {
            var parts = line.Split(';');
            if (parts.Length > 1 && uint.TryParse(parts[1], out var n) && n > max) max = n;
        }
    return max + 1;
}

int List()
{
    if (!File.Exists(csvPath))
    {
        Console.WriteLine("Noch keine Lizenzen ausgestellt.");
        return 0;
    }
    foreach (var line in File.ReadLines(csvPath))
    {
        var p = line.Split(';');
        if (p.Length >= 4) Console.WriteLine($"{p[0],-11} {p[1],-12} {p[3],-12} {p[2]}");
    }
    return 0;
}

int Check(string key)
{
    var lic = LicenseStore.Verify(key, out var err);
    Console.WriteLine(lic != null ? "Gültig: " + lic.Describe() + $", ausgestellt am {lic.Issued:dd.MM.yyyy}" : "Ungültig: " + err);
    return lic != null ? 0 : 1;
}

ECDsa LoadPrivateKey()
{
    if (!File.Exists(keyPath))
        throw new FileNotFoundException($"Privater Schlüssel nicht gefunden: {keyPath}");
    var ecdsa = ECDsa.Create();
    ecdsa.ImportFromPem(File.ReadAllText(keyPath));
    return ecdsa;
}

int NewKeyPair()
{
    if (File.Exists(keyPath))
        throw new InvalidOperationException($"{keyPath} existiert bereits. Zum Ersetzen erst umbenennen. " +
            "Achtung: Mit einem neuen Schlüsselpaar werden alle bisher ausgestellten Lizenzen ungültig.");
    using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(keyPath, ecdsa.ExportECPrivateKeyPem());
    Console.WriteLine($"Privater Schlüssel gespeichert: {keyPath}");
    Console.WriteLine("Öffentlicher Schlüssel (in LicenseStore.PublicKeyPem eintragen und Programm neu bauen):");
    Console.WriteLine(ecdsa.ExportSubjectPublicKeyInfoPem());
    return 0;
}
