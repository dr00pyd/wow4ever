using ForeverScout.Core;
using static ForeverScout.Core.Strings;

namespace ForeverScout;

/// <summary>"Visa vad som skickas" – exakt text som laddas upp (läsbar), plus en knapp till uploads.log.</summary>
internal sealed class PayloadForm : Form
{
    public PayloadForm(string text)
    {
        Text = S("payload.title");
        Icon = Theme.AppIcon;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(860, 620);
        MinimizeBox = false;
        ShowInTaskbar = false;

        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9.5f), BackColor = Theme.Surface, ForeColor = Theme.Text, BorderStyle = BorderStyle.None,
            Text = text,
        };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12, 10, 12, 10), BackColor = Theme.Bg2 };
        var close = Theme.GoldButton(S("close.exit"));
        var log = Theme.QuietButton(S("set.openlog"));
        close.Click += (_, _) => Close();
        log.Click += (_, _) =>
        {
            try { if (!SafeFs.FileExists(UploadLog.PathFor)) UploadLog.Append("info", 0, "-", "log created"); SafeFs.OpenWithShell(UploadLog.PathFor); }
            catch (Exception ex) when (ex is SafeFsException or IOException or System.ComponentModel.Win32Exception) { }
        };
        bottom.Controls.Add(close);
        bottom.Controls.Add(log);
        var wrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12), BackColor = Theme.Surface };
        wrap.Controls.Add(box);
        Controls.Add(wrap);
        Controls.Add(bottom);
        CancelButton = close;
        HandleCreated += (_, _) => Theme.DarkTitleBar(this);
        Shown += (_, _) => { box.SelectionStart = 0; box.SelectionLength = 0; };
    }
}
