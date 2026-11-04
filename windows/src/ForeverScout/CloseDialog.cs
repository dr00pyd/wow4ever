using ForeverScout.Core;
using static ForeverScout.Core.Strings;

namespace ForeverScout;

/// <summary>"Vill du verkligen stänga Forever Scout?" – Stäng / Minimera till fältet / Avbryt + "Fråga inte igen".</summary>
internal sealed class CloseDialog : Form
{
    private readonly CheckBox _dontAsk = new()
    {
        Text = S("close.dontask"), AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Ui(9.5f), Margin = new Padding(0, 14, 0, 0),
    };

    public CloseDecision Choice { get; private set; } = CloseDecision.Ask; // Ask = avbryt
    public bool DontAskAgain => _dontAsk.Checked;

    public CloseDialog()
    {
        Text = S("close.title");
        Icon = Theme.AppIcon;
        BackColor = Theme.Bg2;
        ForeColor = Theme.Text;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(24, 20, 24, 18);

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, BackColor = Color.Transparent };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var icon = Theme.Logo(120);
        icon.Margin = new Padding(0, 0, 16, 0);
        var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty };
        text.Controls.Add(new Label { Text = S("close.question"), AutoSize = true, MaximumSize = new Size(400, 0), Font = Theme.Ui(11.5f, FontStyle.Bold), ForeColor = Theme.Text, Margin = new Padding(0, 0, 0, 6) });
        text.Controls.Add(new Label { Text = S("close.body"), AutoSize = true, MaximumSize = new Size(400, 0), Font = Theme.Ui(10f), ForeColor = Theme.Muted, Margin = Padding.Empty });
        text.Controls.Add(_dontAsk);
        grid.Controls.Add(icon, 0, 0);
        grid.Controls.Add(text, 1, 0);

        var exit = Theme.QuietButton(S("close.exit"));
        var tray = Theme.GoldButton(S("close.tray"));
        var cancel = Theme.QuietButton(S("cancel"));
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0, 18, 0, 0), BackColor = Color.Transparent };
        foreach (var b in new[] { cancel, tray, exit }) { b.Margin = new Padding(8, 0, 0, 0); buttons.Controls.Add(b); }

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, BackColor = Color.Transparent };
        root.Controls.Add(grid, 0, 0);
        root.Controls.Add(buttons, 0, 1);
        Controls.Add(root);

        exit.Click += (_, _) => Done(CloseDecision.Exit);
        tray.Click += (_, _) => Done(CloseDecision.Tray);
        cancel.Click += (_, _) => Done(CloseDecision.Ask);
        AcceptButton = tray;
        CancelButton = cancel;
        HandleCreated += (_, _) => Theme.DarkTitleBar(this);
    }

    private void Done(CloseDecision d)
    {
        Choice = d;
        DialogResult = d == CloseDecision.Ask ? DialogResult.Cancel : DialogResult.OK;
        Close();
    }
}
