using System.Text.Json;
using FaxDrucker.Core.Convert;
using FaxDrucker.Core.Fax;
using FaxDrucker.Core.Media;
using FaxDrucker.Core.Report;
using FaxDrucker.Core.Settings;
using FaxDrucker.Core.Sip;

namespace FaxDrucker.Core;

/// <summary>Ein Faxauftrag aus Sicht der Oberfläche.</summary>
public sealed class FaxRequest
{
    public required string SourceFile { get; init; }
    public required string Number { get; init; }
    public string? RecipientName { get; init; }
    public string DocumentName { get; init; } = "Dokument";
    /// <summary>Quelldatei nach dem Senden löschen (temporäre Druckdaten).</summary>
    public bool DeleteSourceAfterwards { get; init; }
    /// <summary>Abweichende Auflösung nur für diesen Auftrag (sonst aus den Einstellungen).</summary>
    public FaxResolution? Resolution { get; init; }
    /// <summary>Abweichender Bildmodus nur für diesen Auftrag.</summary>
    public ImageMode? ImageMode { get; init; }
}

/// <summary>Eintrag im Sendeprotokoll (journal.jsonl).</summary>
public sealed record JournalEntry(DateTime Time, string Number, string? Recipient, string Document, bool Success,
    string Message, int Pages, int Attempts, string? ReportPath);

/// <summary>Ablauf: umwandeln → wählen → senden → Wahlwiederholung → Bericht.</summary>
public sealed class FaxService
{
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _lineLock = new(1, 1); // eine Leitung

    public event Action<string>? Log;
    public event Action<string>? Status;

    public FaxService(AppSettings settings) => _settings = settings;

    public static string JournalPath => Path.Combine(AppSettings.DataFolder, "journal.jsonl");
    public static string LogPath => Path.Combine(AppSettings.DataFolder, "faxdrucker.log");

    public async Task<JournalEntry> SendAsync(FaxRequest request, CancellationToken ct = default)
    {
        var work = Path.Combine(Path.GetTempPath(), "FaxDrucker", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var tiff = Path.Combine(work, "fax.tif");
        int attempts = 0;
        FaxCallResult? last = null;
        int pages = 0;
        try
        {
            Status?.Invoke("Bereite Dokument vor…");
            var resolution = request.Resolution ?? _settings.Resolution;
            var imageMode = request.ImageMode ?? _settings.ImageMode;
            pages = await PrepareAsync(request.SourceFile, tiff, resolution, imageMode, ct);

            var header = string.IsNullOrWhiteSpace(_settings.HeaderText) ? _settings.StationId : _settings.HeaderText;
            var job = new FaxJobParameters(tiff, null, true, Truncate(_settings.StationId, 20), header,
                _settings.Ecm, _settings.VerboseLog);

            await _lineLock.WaitAsync(ct);
            try
            {
                for (attempts = 1; attempts <= 1 + Math.Max(0, _settings.RetryCount); attempts++)
                {
                    ct.ThrowIfCancellationRequested();
                    var call = new SipFaxCall(_settings.Sip);
                    call.Log += WriteLog;
                    call.Status += s => Status?.Invoke(s);
                    WriteLog($"Versuch {attempts}: {request.Number}");
                    last = await call.SendAsync(request.Number, job, ct);
                    WriteLog($"Ergebnis: {last.Status} – {last.Message}");
                    if (!last.Success && last.Fax?.ResultCode == FaxSession.CodeNoResolutionSupport
                        && resolution == FaxResolution.Superfein && !IsTiff(request.SourceFile))
                    {
                        // Gegenstelle kann z. B. kein Superfein: sofort in "Fein" neu senden.
                        resolution = FaxResolution.Fein;
                        WriteLog("Gegenstelle unterstützt die Auflösung nicht, sende erneut in Fein.");
                        Status?.Invoke("Empfänger kann diese Auflösung nicht, sende in Fein…");
                        File.Delete(tiff);
                        pages = await PrepareAsync(request.SourceFile, tiff, resolution, imageMode, ct);
                        attempts--; // zählt nicht als Wahlwiederholung
                        continue;
                    }
                    if (last.Success || !last.Retryable || attempts > _settings.RetryCount)
                        break;
                    Status?.Invoke($"{last.Message}. Neuer Versuch in {_settings.RetryDelaySeconds} s…");
                    await Task.Delay(TimeSpan.FromSeconds(_settings.RetryDelaySeconds), ct);
                }
            }
            finally
            {
                _lineLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            last = new FaxCallResult(FaxCallStatus.Error, "Abgebrochen", 0, null);
        }
        catch (Exception ex)
        {
            WriteLog("Fehler: " + ex);
            last = new FaxCallResult(FaxCallStatus.Error, ex.Message, 0, null);
        }

        var success = last?.Success == true;
        var message = last?.Message ?? "Unbekannter Fehler";
        string? reportPath = null;
        try
        {
            if (_settings.SaveReportPdf)
            {
                reportPath = Path.Combine(_settings.EffectiveReportFolder,
                    $"{DateTime.Now:yyyy-MM-dd_HHmmss}_{SafeName(request.Number)}_{(success ? "OK" : "FEHLER")}.pdf");
                SendReport.Write(reportPath, new SendReport.Data(DateTime.Now, request.Number, request.RecipientName,
                    request.DocumentName, success, message, last?.Fax?.Pages ?? 0, pages, Math.Max(attempts, 1),
                    last?.Fax?.RemoteIdent, last?.Fax?.BitRate ?? 0,
                    last?.Fax == null ? null : (last.Fax.Transport == Fax.FaxTransport.T38 ? "T.38" : "G.711"),
                    _settings.StationId));
            }
        }
        catch (Exception ex)
        {
            WriteLog("Sendebericht konnte nicht geschrieben werden: " + ex.Message);
            reportPath = null;
        }

        var entry = new JournalEntry(DateTime.Now, request.Number, request.RecipientName, request.DocumentName,
            success, message, last?.Fax?.Pages ?? 0, Math.Max(attempts, 1), reportPath);
        AppendJournal(entry);

        try { Directory.Delete(work, true); } catch { }
        if (request.DeleteSourceAfterwards)
            try { File.Delete(request.SourceFile); } catch { }
        Status?.Invoke(success ? "Fax gesendet." : "Fax nicht gesendet: " + message);
        return entry;
    }

    private async Task<int> PrepareAsync(string source, string tiff, FaxResolution resolution, ImageMode mode, CancellationToken ct)
    {
        int pages;
        if (IsTiff(source))
        {
            File.Copy(source, tiff, true);
            pages = TiffInfo.CountPages(tiff);
        }
        else
        {
            pages = await new GhostscriptConverter().ToFaxTiffAsync(source, tiff, resolution, mode, ct);
        }
        WriteLog($"Dokument umgewandelt: {pages} Seite(n), {resolution.DisplayName()}, Modus {mode}");
        return pages;
    }

    public static IReadOnlyList<JournalEntry> ReadJournal(int max = 200)
    {
        if (!File.Exists(JournalPath)) return Array.Empty<JournalEntry>();
        return File.ReadAllLines(JournalPath)
            .Reverse()
            .Take(max)
            .Select(l => { try { return JsonSerializer.Deserialize<JournalEntry>(l); } catch { return null; } })
            .Where(e => e != null)
            .Cast<JournalEntry>()
            .ToList();
    }

    private static readonly object JournalLock = new();

    private static void AppendJournal(JournalEntry e)
    {
        lock (JournalLock)
            File.AppendAllText(JournalPath, JsonSerializer.Serialize(e) + Environment.NewLine);
    }

    private static readonly object LogLock = new();

    private void WriteLog(string message)
    {
        Log?.Invoke(message);
        try
        {
            lock (LogLock)
            {
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 5_000_000)
                    File.Move(LogPath, LogPath + ".old", true);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }

    private static bool IsTiff(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".tif" or ".tiff";
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static string SafeName(string s) => new(s.Where(c => char.IsLetterOrDigit(c) || c == '+').ToArray());
}
