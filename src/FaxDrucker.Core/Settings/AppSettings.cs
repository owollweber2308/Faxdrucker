using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FaxDrucker.Core.Convert;
using FaxDrucker.Core.Sip;

namespace FaxDrucker.Core.Settings;

/// <summary>Programmeinstellungen, gespeichert unter %APPDATA%\FaxDrucker\settings.json.</summary>
public sealed class AppSettings
{
    public SipAccount Sip { get; set; } = new();

    /// <summary>Passwort, unter Windows per DPAPI für den aktuellen Benutzer verschlüsselt.</summary>
    public string? PasswordProtected { get; set; }

    /// <summary>Kennung der eigenen Faxnummer (TSI), z. B. "+49 221 1234567". Max. 20 Zeichen.</summary>
    public string StationId { get; set; } = "";
    /// <summary>Kopfzeile auf jeder Seite, z. B. Firmenname.</summary>
    public string HeaderText { get; set; } = "";
    public FaxResolution Resolution { get; set; } = FaxResolution.Fein;
    public ImageMode ImageMode { get; set; } = ImageMode.Text;
    public bool Ecm { get; set; } = true;
    public int RetryCount { get; set; } = 3;
    public int RetryDelaySeconds { get; set; } = 120;
    public bool SaveReportPdf { get; set; } = true;
    public string? ReportFolder { get; set; }
    public bool VerboseLog { get; set; }
    public List<string> RecentNumbers { get; set; } = new();
    public List<AddressBookEntry> AddressBook { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DataFolder
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("FAXDRUCKER_HOME");
            if (string.IsNullOrEmpty(dir))
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FaxDrucker");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string DefaultPath => Path.Combine(DataFolder, "settings.json");

    public string EffectiveReportFolder
    {
        get
        {
            var dir = string.IsNullOrWhiteSpace(ReportFolder) ? Path.Combine(DataFolder, "Sendeberichte") : ReportFolder!;
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            return new AppSettings();
        var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings();
        s.Sip.Password = Unprotect(s.PasswordProtected) ?? s.Sip.Password;
        return s;
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var clearPassword = Sip.Password;
        try
        {
            PasswordProtected = Protect(clearPassword);
            Sip.Password = "";
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, path, true);
        }
        finally
        {
            Sip.Password = clearPassword;
        }
    }

    public void RememberNumber(string number)
    {
        RecentNumbers.Remove(number);
        RecentNumbers.Insert(0, number);
        if (RecentNumbers.Count > 20) RecentNumbers.RemoveRange(20, RecentNumbers.Count - 20);
    }

    private static string? Protect(string? clear)
    {
        if (string.IsNullOrEmpty(clear)) return null;
        var bytes = Encoding.UTF8.GetBytes(clear);
        if (OperatingSystem.IsWindows())
            return "dpapi:" + System.Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
        // Außerhalb von Windows (nur Entwicklung/Tests) lediglich kodiert.
        return "plain:" + System.Convert.ToBase64String(bytes);
    }

    private static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        try
        {
            if (stored.StartsWith("dpapi:") && OperatingSystem.IsWindows())
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(System.Convert.FromBase64String(stored[6..]), null, DataProtectionScope.CurrentUser));
            if (stored.StartsWith("plain:"))
                return Encoding.UTF8.GetString(System.Convert.FromBase64String(stored[6..]));
        }
        catch (CryptographicException) { }
        return null;
    }
}

public sealed class AddressBookEntry
{
    public string Name { get; set; } = "";
    public string Number { get; set; } = "";
    public override string ToString() => $"{Name} ({Number})";
}
