using System.IO.Pipes;

namespace FaxDrucker.App;

internal static class Program
{
    public const string PipeName = "FaxDrucker";
    private const string MutexName = @"Local\FaxDrucker.Tray";

    /// <summary>
    /// FaxDrucker.exe                 Startet das Programm im Infobereich (nimmt Druckaufträge an).
    /// FaxDrucker.exe Datei.pdf       Faxt eine Datei (übergibt sie an das laufende Programm).
    /// FaxDrucker.exe --lizenz        Nur den Lizenzdialog zeigen (für das Setup).
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--lizenz", StringComparison.OrdinalIgnoreCase)))
        {
            ApplicationConfiguration.Initialize();
            return LicenseForm.EnsureLicensed() ? 0 : 3;
        }

        var file = args.FirstOrDefault(a => !a.StartsWith('-') && File.Exists(a));

        using var mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // Läuft schon: Datei wie einen Druckauftrag an die laufende Instanz übergeben.
            if (file != null) return ForwardFile(file) ? 0 : 1;
            return 0;
        }

        ApplicationConfiguration.Initialize();
        if (!LicenseForm.EnsureLicensed()) return 3;
        using var tray = new TrayApp(PipeName);
        if (file != null)
            tray.OpenSendDialogForFile(file, Path.GetFileName(file), deleteAfterwards: false);
        Application.Run(tray);
        return 0;
    }

    private static bool ForwardFile(string file)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000);
            using var fs = File.OpenRead(file);
            fs.CopyTo(client);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Datei konnte nicht an FaxDrucker übergeben werden: " + ex.Message, "FaxDrucker",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }
}
