using System.Security.Cryptography;

namespace FaxDrucker.Core.Licensing;

/// <summary>
/// Speichert und prüft die installierte Lizenz.
/// Ort: %ProgramData%\FaxDrucker\lizenz.txt (vom Setup für alle Benutzer geschrieben),
/// ersatzweise %APPDATA%\FaxDrucker\lizenz.txt, falls ohne Administratorrechte eingegeben.
/// </summary>
public static class LicenseStore
{
    /// <summary>
    /// Öffentlicher Schlüssel des Lizenzgenerators. Passt nur zum privaten Schlüssel des Lizenzgenerators
    /// (lizenz-privat.pem, liegt nicht im Repository). Bei einem neuen Schlüsselpaar hier ersetzen.
    /// </summary>
    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE85+XWELhRLMExk2hxFOsfhi2pxuX
        G5QKnCBAM8JlAHxo1J5nckJVIGHY2CvxQBU47qZW3zC6RW814+ylYejMHw==
        -----END PUBLIC KEY-----
        """;

    private const string FileName = "lizenz.txt";

    public static string MachinePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FaxDrucker", FileName);

    public static string UserPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FaxDrucker", FileName);

    private static IEnumerable<string> Candidates()
    {
        var overridePath = Environment.GetEnvironmentVariable("FAXDRUCKER_LICENSE");
        if (!string.IsNullOrEmpty(overridePath)) return [overridePath];
        return [MachinePath, UserPath];
    }

    public static License? Verify(string? key, out string error)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(PublicKeyPem);
        var lic = License.Verify(key, ecdsa, out error);
        if (lic != null && lic.IsExpired(DateOnly.FromDateTime(DateTime.Today)))
        {
            error = $"Die Lizenz für {lic.Customer} ist am {lic.Expires:dd.MM.yyyy} abgelaufen.";
            return null;
        }
        return lic;
    }

    /// <summary>Liefert die gültige installierte Lizenz oder null mit Grund.</summary>
    public static License? Current(out string error)
    {
        error = "Es ist noch kein Lizenzschlüssel eingegeben.";
        foreach (var path in Candidates())
        {
            if (!File.Exists(path)) continue;
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception ex) { error = $"Lizenzdatei {path} nicht lesbar: {ex.Message}"; continue; }
            var lic = Verify(text, out error);
            if (lic != null) return lic;
        }
        return null;
    }

    /// <summary>Prüft und speichert einen Schlüssel. Versucht zuerst den Ort für alle Benutzer.</summary>
    public static License? Install(string key, out string error, out string savedTo)
    {
        savedTo = "";
        var lic = Verify(key, out error);
        if (lic == null) return null;
        var normalized = key.Trim();
        foreach (var path in Candidates())
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, normalized + Environment.NewLine);
                savedTo = path;
                return lic;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                error = $"Lizenz konnte nicht gespeichert werden: {ex.Message}";
            }
        }
        return null;
    }
}
