using FaxDrucker.Core;
using FaxDrucker.Core.Convert;
using FaxDrucker.Core.Settings;
using FaxDrucker.Core.Sip;

namespace FaxDrucker.App;

/// <summary>Einstellungen: SIP-Zugang (Placetel), Absenderkennung, Übertragung.</summary>
internal sealed class SettingsForm : Form
{
    public AppSettings Settings { get; }

    private readonly TextBox _domain = Box();
    private readonly TextBox _proxy = Box();
    private readonly TextBox _user = Box();
    private readonly TextBox _authUser = Box();
    private readonly TextBox _password = Box(password: true);
    private readonly TextBox _display = Box();
    private readonly TextBox _station = Box();
    private readonly TextBox _header = Box();
    private readonly CheckBox _t38 = new() { Text = "T.38 verwenden (empfohlen)", AutoSize = true };
    private readonly ComboBox _resolution = QualityControls.ResolutionBox();
    private readonly ComboBox _imageMode = QualityControls.ImageModeBox();
    private readonly CheckBox _ecm = new() { Text = "Fehlerkorrektur (ECM)", AutoSize = true };
    private readonly NumericUpDown _retries = new() { Minimum = 0, Maximum = 10, Width = 60 };
    private readonly NumericUpDown _retryDelay = new() { Minimum = 10, Maximum = 3600, Increment = 30, Width = 80 };
    private readonly CheckBox _report = new() { Text = "Sendebericht als PDF speichern", AutoSize = true };
    private readonly TextBox _reportFolder = Box();
    private readonly CheckBox _verbose = new() { Text = "Ausführliches Protokoll (Fehlersuche)", AutoSize = true };

    private static TextBox Box(bool password = false) => new() { Dock = DockStyle.Fill, UseSystemPasswordChar = password };

    public SettingsForm(AppSettings settings, bool firstRun)
    {
        Settings = settings;
        Text = "FaxDrucker – Einstellungen";
        Icon = AppIcon.Create();
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(560, 640);
        TopMost = firstRun;

        var t = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, AutoScroll = true };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Section(string title)
        {
            var l = new Label { Text = title, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 10, 0, 4) };
            t.Controls.Add(l);
            t.SetColumnSpan(l, 2);
        }
        void Row(string label, Control c)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left });
            t.Controls.Add(c);
        }
        void Full(Control c)
        {
            t.Controls.Add(new Label());
            t.Controls.Add(c);
        }

        if (firstRun)
        {
            var intro = new Label
            {
                Text = "Bitte die SIP-Zugangsdaten eintragen. Bei Placetel finden Sie diese im Webportal unter " +
                       "Routing/Endgeräte → SIP-Zugangsdaten (Domain fpbx.de).",
                AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = Color.DimGray,
            };
            t.Controls.Add(intro);
            t.SetColumnSpan(intro, 2);
        }

        Section("SIP-Zugang");
        Row("SIP-Domain / Registrar:", _domain);
        Row("Outbound-Proxy (optional):", _proxy);
        Row("SIP-Benutzername:", _user);
        Row("Anmeldename (falls abweichend):", _authUser);
        Row("SIP-Passwort:", _password);
        Row("Anzeigename (optional):", _display);

        Section("Absender");
        Row("Eigene Faxkennung:", _station);
        Row("Kopfzeile auf jeder Seite:", _header);

        Section("Übertragung");
        Full(_t38);
        Row("Auflösung (Standard):", _resolution);
        Row("Bildmodus (Standard):", _imageMode);
        var qHint = new Label
        {
            Text = "Superfein und Foto verbessern Schrift und Bilder, die Übertragung dauert aber deutlich länger. " +
                   "Kann der Empfänger kein Superfein, wird automatisch in Fein gesendet.",
            AutoSize = true, MaximumSize = new Size(340, 0), ForeColor = Color.DimGray,
        };
        Full(qHint);
        Full(_ecm);
        var retry = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
        retry.Controls.Add(_retries);
        retry.Controls.Add(new Label { Text = "Wiederholungen, Abstand (s):", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) });
        retry.Controls.Add(_retryDelay);
        Row("Bei besetzt/Fehler:", retry);

        Section("Berichte");
        Full(_report);
        var folderRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = Padding.Empty };
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderRow.Controls.Add(_reportFolder);
        var browse = new Button { Text = "…", Width = 32 };
        browse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog();
            if (d.ShowDialog(this) == DialogResult.OK) _reportFolder.Text = d.SelectedPath;
        };
        folderRow.Controls.Add(browse);
        Row("Ordner für Berichte:", folderRow);
        Full(_verbose);
        var openLog = new LinkLabel { Text = "Protokolldatei öffnen", AutoSize = true };
        openLog.Click += (_, _) => { if (File.Exists(FaxService.LogPath)) TrayApp.OpenFile(FaxService.LogPath); };
        Full(openLog);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(10) };
        var ok = new Button { Text = "Speichern", AutoSize = true };
        var cancel = new Button { Text = "Abbrechen", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        Controls.Add(t);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;

        LoadValues();
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_domain.Text) || string.IsNullOrWhiteSpace(_user.Text))
            {
                MessageBox.Show(this, "Bitte mindestens SIP-Domain und Benutzername angeben.", Text);
                return;
            }
            StoreValues();
            DialogResult = DialogResult.OK;
            Close();
        };
    }

    private void LoadValues()
    {
        var s = Settings;
        _domain.Text = s.Sip.Domain;
        _proxy.Text = s.Sip.OutboundProxy ?? "";
        _user.Text = s.Sip.Username;
        _authUser.Text = s.Sip.AuthUsername ?? "";
        _password.Text = s.Sip.Password;
        _display.Text = s.Sip.DisplayName ?? "";
        _station.Text = s.StationId;
        _header.Text = s.HeaderText;
        _t38.Checked = s.Sip.T38 == T38Policy.Auto;
        _resolution.SelectedIndex = (int)s.Resolution;
        _imageMode.SelectedIndex = (int)s.ImageMode;
        _ecm.Checked = s.Ecm;
        _retries.Value = Math.Clamp(s.RetryCount, 0, 10);
        _retryDelay.Value = Math.Clamp(s.RetryDelaySeconds, 10, 3600);
        _report.Checked = s.SaveReportPdf;
        _reportFolder.Text = s.ReportFolder ?? "";
        _verbose.Checked = s.VerboseLog;
    }

    private void StoreValues()
    {
        var s = Settings;
        s.Sip.Domain = _domain.Text.Trim();
        s.Sip.OutboundProxy = NullIfEmpty(_proxy.Text);
        s.Sip.Username = _user.Text.Trim();
        s.Sip.AuthUsername = NullIfEmpty(_authUser.Text);
        s.Sip.Password = _password.Text;
        s.Sip.DisplayName = NullIfEmpty(_display.Text);
        s.StationId = _station.Text.Trim();
        s.HeaderText = _header.Text.Trim();
        s.Sip.T38 = _t38.Checked ? T38Policy.Auto : T38Policy.Off;
        s.Resolution = (FaxResolution)_resolution.SelectedIndex;
        s.ImageMode = (ImageMode)_imageMode.SelectedIndex;
        s.Ecm = _ecm.Checked;
        s.RetryCount = (int)_retries.Value;
        s.RetryDelaySeconds = (int)_retryDelay.Value;
        s.SaveReportPdf = _report.Checked;
        s.ReportFolder = NullIfEmpty(_reportFolder.Text);
        s.VerboseLog = _verbose.Checked;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Liste der gesendeten Faxe.</summary>
internal sealed class JournalForm : Form
{
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true,
    };

    public JournalForm()
    {
        Text = "FaxDrucker – Sendeprotokoll";
        Icon = AppIcon.Create();
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(860, 420);
        StartPosition = FormStartPosition.CenterScreen;
        _list.Columns.Add("Zeit", 130);
        _list.Columns.Add("Empfänger", 160);
        _list.Columns.Add("Nummer", 130);
        _list.Columns.Add("Dokument", 160);
        _list.Columns.Add("Seiten", 55);
        _list.Columns.Add("Ergebnis", 220);
        Controls.Add(_list);
        var hint = new Label { Dock = DockStyle.Bottom, Text = "Doppelklick öffnet den Sendebericht.", Padding = new Padding(6), AutoSize = true, ForeColor = Color.DimGray };
        Controls.Add(hint);

        foreach (var e in FaxService.ReadJournal())
        {
            var item = new ListViewItem(new[]
            {
                e.Time.ToString("dd.MM.yyyy HH:mm"), e.Recipient ?? "", e.Number, e.Document, e.Pages.ToString(),
                (e.Success ? "OK – " : "FEHLER – ") + e.Message,
            })
            {
                Tag = e.ReportPath,
                ForeColor = e.Success ? Color.Black : Color.Firebrick,
            };
            _list.Items.Add(item);
        }
        _list.DoubleClick += (_, _) =>
        {
            if (_list.SelectedItems.Count == 1 && _list.SelectedItems[0].Tag is string p && File.Exists(p))
                TrayApp.OpenFile(p);
        };
    }
}

/// <summary>Auswahllisten für Auflösung und Bildmodus (Reihenfolge = Enum-Werte).</summary>
internal static class QualityControls
{
    public static ComboBox ResolutionBox()
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
        foreach (FaxResolution r in Enum.GetValues<FaxResolution>())
            c.Items.Add(r.DisplayName());
        return c;
    }

    public static ComboBox ImageModeBox()
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
        c.Items.Add("Text (scharfe Schrift)");
        c.Items.Add("Foto (Graustufen gerastert)");
        return c;
    }
}
