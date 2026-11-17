using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace FaxDrucker.App;

/// <summary>Ein empfangener Druckauftrag (PostScript oder PDF) als temporäre Datei.</summary>
internal sealed record PrintJob(string File, string Title, int PageCount);

/// <summary>
/// Nimmt Druckaufträge vom Windows-Drucker "FaxDrucker" an. Der Drucker schreibt über einen
/// lokalen Anschluss namens \\.\pipe\FaxDrucker direkt in diese Named Pipe.
/// </summary>
internal sealed class PrintPipeServer : IDisposable
{
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<PrintJob> _onJob;
    private readonly Action<string> _onError;

    public PrintPipeServer(string pipeName, Action<PrintJob> onJob, Action<string> onError)
    {
        _pipeName = pipeName;
        _onJob = onJob;
        _onError = onError;
    }

    public void Start()
    {
        // Mehrere Instanzen, damit schnell aufeinanderfolgende Druckaufträge nicht blockieren.
        for (int i = 0; i < 2; i++)
            _ = Task.Run(() => AcceptLoop(_cts.Token));
    }

    private static PipeSecurity CreateSecurity()
    {
        var sec = new PipeSecurity();
        var me = WindowsIdentity.GetCurrent().User!;
        sec.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
        // Druckwarteschlange (SYSTEM) muss schreiben dürfen.
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return sec;
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 0, 0, CreateSecurity());
                await server.WaitForConnectionAsync(ct);

                var dir = Path.Combine(Path.GetTempPath(), "FaxDrucker", "Druckauftraege");
                Directory.CreateDirectory(dir);
                var tmp = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.tmp");
                await using (var fs = File.Create(tmp))
                    await server.CopyToAsync(fs, ct);
                server.Dispose();
                server = null;

                if (new FileInfo(tmp).Length == 0)
                {
                    File.Delete(tmp);
                    continue;
                }
                _onJob(Inspect(tmp));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _onError("Druckauftrag konnte nicht angenommen werden: " + ex.Message);
                await Task.Delay(1000, CancellationToken.None);
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    /// <summary>Erkennt PDF/PostScript, liest Titel und Seitenzahl aus den DSC-Kommentaren.</summary>
    internal static PrintJob Inspect(string tmp)
    {
        var head = new byte[8];
        using (var fs = File.OpenRead(tmp))
            fs.ReadExactly(head, 0, (int)Math.Min(8, fs.Length));
        var headText = Encoding.ASCII.GetString(head);
        bool pdf = headText.StartsWith("%PDF");
        var target = Path.ChangeExtension(tmp, pdf ? ".pdf" : ".ps");
        File.Move(tmp, target);

        string title = pdf ? "PDF-Dokument" : "Dokument";
        int pages = 0;
        if (!pdf)
        {
            // PJL-/Steuerzeichen vor %!PS werden von Ghostscript ignoriert. Nur Kommentarzeilen lesen.
            foreach (var line in File.ReadLines(target, Encoding.Latin1))
            {
                if (line.StartsWith("%%Title:"))
                    title = CleanTitle(line[8..]);
                else if (line.StartsWith("%%Page:"))
                    pages++;
            }
        }
        return new PrintJob(target, title, pages);
    }

    private static string CleanTitle(string t)
    {
        t = t.Trim();
        if (t.StartsWith('(') && t.EndsWith(')')) t = t[1..^1];
        t = Regex.Replace(t, @"\\(\d{3})", m => ((char)System.Convert.ToInt32(m.Groups[1].Value, 8)).ToString());
        t = t.Replace("Microsoft Word - ", "");
        return string.IsNullOrWhiteSpace(t) ? "Dokument" : t;
    }

    public void Dispose() => _cts.Cancel();
}
