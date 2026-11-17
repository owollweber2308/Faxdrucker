using System.Diagnostics;
using System.Drawing.Drawing2D;
using FaxDrucker.Core;
using FaxDrucker.Core.Settings;

namespace FaxDrucker.App;

/// <summary>Programm im Infobereich: nimmt Druckaufträge an und öffnet das Sendefenster.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly PrintPipeServer _pipe;
    private readonly SynchronizationContext _ui;
    private AppSettings _settings;
    private int _activeJobs;

    public TrayApp(string pipeName)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settings = AppSettings.Load();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Datei faxen…", null, (_, _) => ChooseFile());
        menu.Items.Add("Sendeprotokoll", null, (_, _) => new JournalForm().Show());
        menu.Items.Add("Einstellungen…", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => Quit());

        _icon = new NotifyIcon
        {
            Icon = AppIcon.Create(),
            Text = "FaxDrucker – bereit",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => new JournalForm().Show();

        _pipe = new PrintPipeServer(pipeName,
            job => _ui.Post(_ => OpenSendDialogForFile(job.File, job.Title, deleteAfterwards: true, job.PageCount), null),
            err => _ui.Post(_ => Balloon(err, ToolTipIcon.Error), null));
        _pipe.Start();

        if (string.IsNullOrWhiteSpace(_settings.Sip.Username))
            _ui.Post(_ => ShowSettings(firstRun: true), null);

        if (Core.Convert.GhostscriptConverter.Find() == null)
            _ui.Post(_ => WarnGhostscriptMissing(), null);
    }

    private void ChooseFile()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Datei faxen",
            Filter = "Dokumente (*.pdf;*.ps;*.tif;*.tiff)|*.pdf;*.ps;*.tif;*.tiff|Alle Dateien (*.*)|*.*",
        };
        if (dlg.ShowDialog() == DialogResult.OK)
            OpenSendDialogForFile(dlg.FileName, Path.GetFileName(dlg.FileName), deleteAfterwards: false);
    }

    public void OpenSendDialogForFile(string file, string title, bool deleteAfterwards, int pageCount = 0)
    {
        _settings = AppSettings.Load();
        if (string.IsNullOrWhiteSpace(_settings.Sip.Username) && !ShowSettings(firstRun: true))
        {
            if (deleteAfterwards) TryDelete(file);
            return;
        }

        using var dlg = new SendDialog(_settings, title, pageCount);
        if (dlg.ShowDialog() != DialogResult.OK)
        {
            if (deleteAfterwards) TryDelete(file);
            return;
        }

        _settings.RememberNumber(dlg.Number);
        if (dlg.SaveToAddressBook && !string.IsNullOrWhiteSpace(dlg.RecipientName))
        {
            _settings.AddressBook.RemoveAll(e => e.Name.Equals(dlg.RecipientName, StringComparison.OrdinalIgnoreCase));
            _settings.AddressBook.Add(new AddressBookEntry { Name = dlg.RecipientName!, Number = dlg.Number });
            _settings.AddressBook.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        }
        _settings.Save();

        var request = new FaxRequest
        {
            SourceFile = file,
            Number = dlg.Number,
            RecipientName = dlg.RecipientName,
            DocumentName = title,
            DeleteSourceAfterwards = deleteAfterwards,
            Resolution = dlg.Resolution,
            ImageMode = dlg.ImageMode,
        };
        StartSending(request);
    }

    private void StartSending(FaxRequest request)
    {
        var progress = new SendProgressForm(request);
        var cts = new CancellationTokenSource();
        progress.CancelRequested += () => cts.Cancel();
        progress.Show();

        var service = new FaxService(_settings);
        service.Status += s => _ui.Post(_ => { progress.SetStatus(s); _icon.Text = Trim63("FaxDrucker – " + s); }, null);

        Interlocked.Increment(ref _activeJobs);
        _ = Task.Run(async () =>
        {
            JournalEntry entry;
            try
            {
                entry = await service.SendAsync(request, cts.Token);
            }
            catch (Exception ex)
            {
                entry = new JournalEntry(DateTime.Now, request.Number, request.RecipientName, request.DocumentName,
                    false, ex.Message, 0, 1, null);
            }
            _ui.Post(_ =>
            {
                Interlocked.Decrement(ref _activeJobs);
                progress.SetResult(entry);
                _icon.Text = "FaxDrucker – bereit";
                Balloon(entry.Success ? $"Fax an {request.Number} gesendet." : $"Fax an {request.Number} fehlgeschlagen: {entry.Message}",
                    entry.Success ? ToolTipIcon.Info : ToolTipIcon.Error);
            }, null);
        });
    }

    private bool ShowSettings(bool firstRun = false)
    {
        using var form = new SettingsForm(AppSettings.Load(), firstRun);
        if (form.ShowDialog() == DialogResult.OK)
        {
            form.Settings.Save();
            _settings = form.Settings;
            return true;
        }
        return false;
    }

    private static void WarnGhostscriptMissing()
    {
        const string url = "https://ghostscript.com/releases/gsdnld.html";
        if (MessageBox.Show(
                "Ghostscript ist nicht installiert. FaxDrucker braucht es, um Ausdrucke in Faxseiten umzuwandeln.\n\n" +
                "Bitte die 64-Bit-Version von Ghostscript installieren (alternativ in der Eingabeaufforderung: " +
                "winget install ArtifexSoftware.GhostScript).\n\nDownloadseite jetzt öffnen?",
                "FaxDrucker", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            OpenFile(url);
    }

    private void Balloon(string text, ToolTipIcon icon)
    {
        _icon.BalloonTipTitle = "FaxDrucker";
        _icon.BalloonTipText = text;
        _icon.BalloonTipIcon = icon;
        _icon.ShowBalloonTip(5000);
    }

    private void Quit()
    {
        if (_activeJobs > 0 && MessageBox.Show("Es wird gerade ein Fax gesendet. Trotzdem beenden?", "FaxDrucker",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        _icon.Visible = false;
        ExitThread();
    }

    private static string Trim63(string s) => s.Length <= 63 ? s : s[..62] + "…";

    private static void TryDelete(string f)
    {
        try { File.Delete(f); } catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pipe.Dispose();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }

    public static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "FaxDrucker"); }
    }
}

/// <summary>Zeichnet ein einfaches Faxsymbol, damit keine Icon-Datei nötig ist.</summary>
internal static class AppIcon
{
    public static Icon Create()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var body = new SolidBrush(Color.FromArgb(0x1F, 0x5F, 0xA8));
            using var paper = new SolidBrush(Color.White);
            using var pen = new Pen(Color.FromArgb(0x1F, 0x5F, 0xA8), 1.5f);
            g.FillRectangle(paper, 9, 2, 14, 14);
            g.DrawRectangle(pen, 9, 2, 14, 14);
            g.DrawLine(pen, 12, 7, 20, 7);
            g.DrawLine(pen, 12, 11, 20, 11);
            g.FillRectangle(body, 3, 15, 26, 13);
            g.FillRectangle(paper, 7, 19, 10, 3);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
