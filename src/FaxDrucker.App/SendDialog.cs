using FaxDrucker.Core.Settings;
using FaxDrucker.Core.Sip;

namespace FaxDrucker.App;

/// <summary>Fenster nach dem Drucken: Empfänger und Faxnummer eingeben.</summary>
internal sealed class SendDialog : Form
{
    private readonly ComboBox _recipient = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly TextBox _number = new() { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 14f) };
    private readonly CheckBox _save = new() { Text = "Im Adressbuch speichern", AutoSize = true };
    private readonly Label _hint = new() { AutoSize = true, ForeColor = Color.Firebrick };
    private readonly AppSettings _settings;

    public string Number => SipFaxCall.NormalizeNumber(_number.Text);
    public string? RecipientName => string.IsNullOrWhiteSpace(_recipient.Text) ? null : _recipient.Text.Trim();
    public bool SaveToAddressBook => _save.Checked;

    public SendDialog(AppSettings settings, string title, int pageCount)
    {
        _settings = settings;
        Text = "Fax senden";
        Icon = AppIcon.Create();
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        TopMost = true; // erscheint vor dem druckenden Programm
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(480, 260);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 6,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var doc = new Label
        {
            AutoSize = true,
            Text = $"Dokument: {title}" + (pageCount > 0 ? $"  ({pageCount} Seite{(pageCount == 1 ? "" : "n")})" : ""),
            AutoEllipsis = true,
            MaximumSize = new Size(440, 0),
        };
        layout.Controls.Add(doc, 0, 0);
        layout.SetColumnSpan(doc, 2);

        layout.Controls.Add(new Label { Text = "Empfänger:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(_recipient, 1, 1);
        layout.Controls.Add(new Label { Text = "Faxnummer:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        layout.Controls.Add(_number, 1, 2);
        layout.Controls.Add(_save, 1, 3);
        layout.Controls.Add(_hint, 1, 4);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        var send = new Button { Text = "Senden", DialogResult = DialogResult.None, AutoSize = true, Padding = new Padding(12, 2, 12, 2) };
        var cancel = new Button { Text = "Abbrechen", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(send);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 5);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
        AcceptButton = send;
        CancelButton = cancel;

        foreach (var e in settings.AddressBook)
            _recipient.Items.Add(e.Name);
        _recipient.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _recipient.AutoCompleteSource = AutoCompleteSource.ListItems;
        _recipient.TextChanged += (_, _) =>
        {
            var hit = settings.AddressBook.FirstOrDefault(a => a.Name.Equals(_recipient.Text, StringComparison.CurrentCultureIgnoreCase));
            if (hit != null) _number.Text = hit.Number;
        };

        var recent = new AutoCompleteStringCollection();
        recent.AddRange(settings.RecentNumbers.ToArray());
        _number.AutoCompleteCustomSource = recent;
        _number.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _number.AutoCompleteSource = AutoCompleteSource.CustomSource;

        send.Click += (_, _) =>
        {
            if (Number.Length < 3)
            {
                _hint.Text = "Bitte eine gültige Faxnummer eingeben.";
                _number.Focus();
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        };
        Shown += (_, _) => { Activate(); _number.Focus(); };
    }
}

/// <summary>Kleines Statusfenster während des Sendens.</summary>
internal sealed class SendProgressForm : Form
{
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _cancel = new() { Text = "Abbrechen", AutoSize = true };
    private readonly Button _report = new() { Text = "Sendebericht öffnen", AutoSize = true, Visible = false };
    private string? _reportPath;
    private bool _finished;

    public event Action? CancelRequested;

    public SendProgressForm(Core.FaxRequest request)
    {
        Text = $"Fax an {request.RecipientName ?? request.Number}";
        Icon = AppIcon.Create();
        StartPosition = FormStartPosition.Manual;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(420, 120);
        Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(_status, 0, 0);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_report);
        layout.Controls.Add(buttons, 0, 1);
        Controls.Add(layout);
        _status.Text = "Wird vorbereitet…";

        _cancel.Click += (_, _) =>
        {
            if (_finished) { Close(); return; }
            _cancel.Enabled = false;
            _status.Text = "Wird abgebrochen…";
            CancelRequested?.Invoke();
        };
        _report.Click += (_, _) => { if (_reportPath != null) TrayApp.OpenFile(_reportPath); };
        FormClosing += (_, e) =>
        {
            // Schließen während des Sendens versteckt nur das Fenster.
            if (!_finished && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        };
    }

    public void SetStatus(string s)
    {
        if (!_finished) _status.Text = s;
    }

    public void SetResult(Core.JournalEntry e)
    {
        _finished = true;
        _status.ForeColor = e.Success ? Color.DarkGreen : Color.Firebrick;
        _status.Text = e.Success ? $"✔ {e.Message}" : $"✘ {e.Message}";
        _cancel.Text = "Schließen";
        _cancel.Enabled = true;
        _reportPath = e.ReportPath;
        _report.Visible = e.ReportPath != null;
        if (!Visible) Show();
        if (e.Success)
        {
            // Erfolgsmeldung schließt sich nach einer Weile selbst.
            var t = new System.Windows.Forms.Timer { Interval = 15000 };
            t.Tick += (_, _) => { t.Stop(); t.Dispose(); if (!IsDisposed) Close(); };
            t.Start();
        }
    }
}
