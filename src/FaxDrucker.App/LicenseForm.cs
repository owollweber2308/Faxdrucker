using FaxDrucker.Core.Licensing;

namespace FaxDrucker.App;

/// <summary>Eingabe bzw. Anzeige des Lizenzschlüssels.</summary>
internal sealed class LicenseForm : Form
{
    private readonly TextBox _key;
    private readonly Label _status;

    public License? License { get; private set; }

    public LicenseForm(string? reason = null)
    {
        Text = "FaxDrucker – Lizenz";
        Icon = AppIcon.Create();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 260);
        Font = new Font("Segoe UI", 9f);
        TopMost = true;

        License = LicenseStore.Current(out var currentError);

        var intro = new Label
        {
            Left = 16, Top = 14, Width = 488, Height = 40,
            Text = License != null
                ? "Lizenziert für:"
                : "Bitte geben Sie Ihren Lizenzschlüssel ein. Sie haben ihn zusammen mit FaxDrucker erhalten.",
        };
        _key = new TextBox
        {
            Left = 16, Top = 58, Width = 488, Height = 80,
            Multiline = true, WordWrap = true, ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9.5f),
            PlaceholderText = "FD1-XXXXX-XXXXX-…",
        };
        _status = new Label { Left = 16, Top = 146, Width = 488, Height = 60 };
        if (License != null)
        {
            _status.Text = License.Describe();
            _status.Font = new Font(Font, FontStyle.Bold);
        }
        else if ((reason ?? currentError) is { Length: > 0 } msg)
        {
            _status.Text = msg;
            _status.ForeColor = Color.Firebrick;
        }

        var ok = new Button { Text = License != null ? "Schlüssel ändern" : "Aktivieren", Left = 304, Top = 218, Width = 110 };
        var cancel = new Button { Text = License != null ? "Schließen" : "Abbrechen", Left = 420, Top = 218, Width = 84, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => TryActivate();
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.AddRange([intro, _key, _status, ok, cancel]);
    }

    private void TryActivate()
    {
        var lic = LicenseStore.Install(_key.Text, out var error, out _);
        if (lic == null)
        {
            _status.Font = Font;
            _status.ForeColor = Color.Firebrick;
            _status.Text = error;
            _key.Focus();
            return;
        }
        License = lic;
        MessageBox.Show(this, "FaxDrucker ist jetzt lizenziert für " + lic.Describe() + ".", "FaxDrucker",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>Stellt sicher, dass eine gültige Lizenz vorhanden ist; fragt sonst danach.</summary>
    public static bool EnsureLicensed()
    {
        if (LicenseStore.Current(out _) != null) return true;
        using var form = new LicenseForm();
        return form.ShowDialog() == DialogResult.OK && form.License != null;
    }
}
