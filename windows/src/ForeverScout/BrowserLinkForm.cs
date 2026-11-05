using ForeverScout.Core;
using static ForeverScout.Core.Strings;

namespace ForeverScout;

/// <summary>
/// "Logga in med webbläsaren" (kontraktet 2b): hämtar en kod, öppnar guide.wow4ever.se i webbläsaren och väntar på
/// godkännandet. Visar koden (XXXX-XXXX) så att man kan jämföra med sidan, plus Avbryt. Själva flödet är
/// <see cref="DeviceLink"/> i Core; <c>store</c> sparar nyckeln (null = anroparen sparar <see cref="Result"/> själv).
/// </summary>
internal sealed class BrowserLinkForm : Form
{
    private readonly ApiClient _api;
    private readonly Action<Contributor>? _store;
    private readonly Label _code = new() { Text = "····-····", AutoSize = true, Font = new Font("Consolas", 26f, FontStyle.Bold), ForeColor = Theme.GoldHi, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 8) };
    private readonly Label _status = new() { Text = "", AutoSize = true, MaximumSize = new Size(440, 0), Font = Theme.Ui(10f), ForeColor = Theme.Muted, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 4) };
    private readonly Button _reopen = Theme.QuietButton(S("link.reopen"));
    private readonly Button _retry = Theme.GoldButton(S("link.retry"));
    private readonly Button _cancel = Theme.QuietButton(S("link.cancel"));
    private CancellationTokenSource? _cts;
    private Uri? _url;

    public Contributor? Result { get; private set; }

    public BrowserLinkForm(ApiClient api, string? currentName, Action<Contributor>? store)
    {
        _api = api;
        _store = store;
        Text = S("link.browser");
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

        var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty };
        text.Controls.Add(new Label { Text = S("link.browser"), AutoSize = true, Font = Theme.Display(16f), ForeColor = Theme.Text, Margin = new Padding(0, 0, 0, 8) });
        text.Controls.Add(Para(S("link.explain")));
        if (currentName is { Length: > 0 } n) text.Controls.Add(Para(F("link.switch", n)));
        text.Controls.Add(new SmallTitle(S("link.code")) { Margin = new Padding(0, 8, 0, 0) });
        text.Controls.Add(_code);
        text.Controls.Add(_status);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0, 18, 0, 0), BackColor = Color.Transparent };
        foreach (var b in new[] { _cancel, _retry, _reopen }) { b.Margin = new Padding(8, 0, 0, 0); buttons.Controls.Add(b); }

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, BackColor = Color.Transparent };
        root.Controls.Add(text, 0, 0);
        root.Controls.Add(buttons, 0, 1);
        Controls.Add(root);

        _retry.Visible = false;
        _reopen.Enabled = false;
        _reopen.Click += (_, _) => { if (_url != null) Links.Open(_api, _url.ToString()); };
        _retry.Click += async (_, _) => await Start();
        _cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        CancelButton = _cancel;
        FormClosing += (_, _) => _cts?.Cancel();
        HandleCreated += (_, _) => Theme.DarkTitleBar(this);
        Shown += async (_, _) => await Start();
    }

    private static Label Para(string t) => new() { Text = t, AutoSize = true, MaximumSize = new Size(440, 0), Font = Theme.Ui(10f), ForeColor = Theme.Muted, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 6) };

    private async Task Start()
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _retry.Visible = false;
        _reopen.Enabled = false;
        _url = null;
        _code.Text = "····-····";
        SetStatus(S("link.starting"), Theme.Muted);
        var flow = new DeviceLink(_api, "windows", BuildInfo.Version.ToString(3)) { OnApproved = _store };
        var result = await flow.RunAsync(t =>
        {
            _code.Text = t.DisplayCode;
            _url = t.Url;
            _reopen.Enabled = true;
            SetStatus(F("link.waiting", t.DisplayCode), Theme.Muted);
            Links.Open(_api, t.Url.ToString());
        }, _cts.Token);
        if (IsDisposed) return;
        switch (result.Outcome)
        {
            case DeviceLinkOutcome.Cancelled:
                return;
            case DeviceLinkOutcome.Approved:
                Result = result.Contributor;
                SetStatus(F("link.ok", result.Contributor!.Name), Theme.Ok);
                _reopen.Enabled = _cancel.Enabled = false;
                await Task.Delay(900);
                if (IsDisposed) return;
                DialogResult = DialogResult.OK;
                Close();
                return;
        }
        _reopen.Enabled = false;
        _retry.Visible = true;
        SetStatus(result.Outcome switch
        {
            DeviceLinkOutcome.Denied => S("link.denied"),
            DeviceLinkOutcome.Expired => S("link.expired"),
            DeviceLinkOutcome.TooManyKeys => S("link.toomany"),
            DeviceLinkOutcome.BadUrl => S("link.badurl"),
            _ => Error(result.Error!),
        }, Theme.Err);
    }

    private void SetStatus(string text, Color color)
    {
        _status.Text = text;
        _status.ForeColor = color;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _cts?.Cancel(); _cts?.Dispose(); }
        base.Dispose(disposing);
    }
}
