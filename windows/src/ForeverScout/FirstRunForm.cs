using ForeverScout.Core;
using static ForeverScout.Core.Strings;

namespace ForeverScout;

/// <summary>
/// Onboarding (UI V1): "Slå dig ner vid elden." Logga in med webbläsaren (primär handling, 1.0.8), annars registrering
/// med visningsnamn eller koppling med kod (alternativ, tydligt separerade).
/// </summary>
internal sealed class FirstRunForm : Form
{
    private readonly ApiClient _api;
    private readonly InputBox _name;
    private readonly InputBox _code;
    private readonly Button _browser = Theme.GoldButton(S("link.browser"));
    private readonly Button _register = Theme.QuietButton(S("onb.start.title"));
    private readonly Button _link = Theme.QuietButton(S("first.link"));
    private readonly Button _useCode = Theme.QuietButton(S("onb.usecode"));
    private readonly Label _nameError = Err();
    private readonly Label _codeError = Err();
    private readonly Label _quick;

    public Contributor? Result { get; private set; }

    public FirstRunForm(ApiClient api)
    {
        _api = api;
        Text = "Forever Scout";
        Icon = Theme.AppIcon;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Ui(10f);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(900, 820);
        KeyPreview = true;

        _name = new InputBox(380);
        _name.Box.MaxLength = 24;
        _name.Box.AccessibleName = S("onb.name");
        _code = new InputBox(380) { PlaceholderText = S("onb.code.ph") };
        _code.Box.MaxLength = 16;
        _code.Box.CharacterCasing = CharacterCasing.Upper;
        _code.Box.AccessibleName = S("onb.code");
        _quick = new Label { Text = S("onb.quick"), AutoSize = true, ForeColor = Theme.Faint, Font = Theme.Ui(9f), BackColor = Color.Transparent, Margin = new Padding(10, 10, 0, 0) };
        _useCode.Visible = false;

        // ---------- Hero ----------
        var hero = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = Theme.Bg2, Padding = new Padding(28, 20, 28, 20) };
        hero.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        hero.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var logo = Theme.Logo(150);
        logo.Margin = new Padding(0, 0, 22, 0);
        var heroText = Stack();
        heroText.Margin = new Padding(0, 12, 0, 0);
        heroText.Controls.Add(new SmallTitle(S("onb.eyebrow")) { ForeColor = Theme.GoldHi });
        heroText.Controls.Add(new Label { Text = S("onb.title"), AutoSize = true, Font = Theme.Display(22f), ForeColor = Theme.Text, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 4) });
        heroText.Controls.Add(new Label { Text = S("onb.sub"), AutoSize = true, MaximumSize = new Size(560, 0), Font = Theme.Ui(10.5f), ForeColor = ColorTranslator.FromHtml("#D1C2BA"), BackColor = Color.Transparent });
        hero.Controls.Add(logo, 0, 0);
        hero.Controls.Add(heroText, 1, 0);

        // ---------- Vänster kort: webbläsaren / registrera / koppla ----------
        var left = Card();
        var l = Stack();
        l.Controls.Add(H2(S("onb.start.title")));
        l.Controls.Add(Para(S("link.explain"), 400));
        _browser.Margin = new Padding(0, 4, 0, 0);
        l.Controls.Add(_browser);
        l.Controls.Add(Separator(400));
        l.Controls.Add(new SmallTitle(S("onb.orname")));
        l.Controls.Add(Para(S("onb.start.text"), 400));
        l.Controls.Add(FieldLabel(S("onb.name")));
        l.Controls.Add(_name);
        l.Controls.Add(Row(_register, _quick));
        l.Controls.Add(_nameError);
        l.Controls.Add(_useCode);
        l.Controls.Add(Separator(400));
        l.Controls.Add(new SmallTitle(S("onb.have")));
        l.Controls.Add(FieldLabel(S("onb.code")));
        l.Controls.Add(_code);
        l.Controls.Add(Row(_link));
        l.Controls.Add(_codeError);
        left.Controls.Add(l);

        // ---------- Höger: vad du hjälper till med + integritet + värdshusvärden ----------
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true, BackColor = Color.Transparent, Margin = Padding.Empty };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var helps = Card();
        var h = Stack();
        h.Controls.Add(H2(S("onb.helps.title")));
        foreach (var k in new[] { "onb.helps.1", "onb.helps.2", "onb.helps.3" }) h.Controls.Add(Check(S(k)));
        h.Controls.Add(Separator(300));
        h.Controls.Add(new SmallTitle(S("onb.privacy.title")));
        h.Controls.Add(Para(S("onb.privacy.text"), 300));
        helps.Controls.Add(h);
        var tavern = new RoundedPanel { FillColor = Theme.GoldSoft, BorderColor = Theme.GoldEdge, Radius = 8, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(16, 14, 16, 14), Margin = new Padding(0, 16, 0, 0) };
        var tv = Stack();
        tv.Controls.Add(new Label { Text = S("onb.tavern.who"), AutoSize = true, Font = Theme.Ui(10f, FontStyle.Bold), ForeColor = ColorTranslator.FromHtml("#FFD88A"), BackColor = Color.Transparent });
        tv.Controls.Add(new Label { Text = S("onb.tavern.quote"), AutoSize = true, MaximumSize = new Size(300, 0), Font = Theme.Ui(10f), ForeColor = ColorTranslator.FromHtml("#F2D7AC"), BackColor = Color.Transparent });
        tavern.Controls.Add(tv);
        right.Controls.Add(helps);
        right.Controls.Add(tavern);

        var cols = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(28, 22, 28, 16), BackColor = Theme.Bg, AutoScroll = true };
        cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        left.Margin = new Padding(0, 0, 18, 0);
        cols.Controls.Add(left, 0, 0);
        cols.Controls.Add(right, 1, 0);

        // ---------- Sidfot ----------
        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, BackColor = Theme.Footer, Padding = new Padding(24, 10, 24, 10) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        footer.Controls.Add(new Label { Text = "● " + S("onb.footer.ready"), AutoSize = true, ForeColor = Theme.Ok, Font = Theme.Ui(9f), Anchor = AnchorStyles.Left }, 0, 0);
        footer.Controls.Add(new Label { Text = S("onb.footer.tag"), AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Ui(9f), Anchor = AnchorStyles.Right }, 1, 0);

        Controls.Add(cols);
        Controls.Add(footer);
        Controls.Add(hero);

        AcceptButton = _register;
        _browser.Click += (_, _) =>
        {
            // Nyckeln sparas av Program (samma väg som namn och kod), därför ingen store här.
            using var f = new BrowserLinkForm(_api, null, null);
            f.ShowDialog(this);
            if (f.Result is not { } who) return;
            Result = who;
            DialogResult = DialogResult.OK;
            Close();
        };
        _register.Click += async (_, _) => await Run(_nameError, async () =>
        {
            var n = _name.Value.Trim();
            if (n.Length is < 2 or > 24) throw new ArgumentException(S("first.badname"));
            return await _api.RegisterAsync(n);
        });
        _link.Click += async (_, _) => await Run(_codeError, async () =>
        {
            var c = _code.Value.Trim().Replace(" ", "").Replace("-", "");
            if (c.Length < 6) throw new ArgumentException(S("first.badcode"));
            return await _api.LinkAsync(c);
        });
        _useCode.Click += (_, _) => _code.Box.Focus();
        _code.Box.Enter += (_, _) => AcceptButton = _link;
        _name.Box.Enter += (_, _) => AcceptButton = _register;
        HandleCreated += (_, _) => Theme.DarkTitleBar(this);
        Shown += (_, _) => _name.Box.Focus();
    }

    /// <summary>Visar ett fel som det ser ut efter ett svar från servern (för skärmbilder/test).</summary>
    public void ShowError(Exception ex, bool forCode = false) => SetError(forCode ? _codeError : _nameError, ex);

    public void SetName(string name) => _name.Value = name;

    private void SetError(Label target, Exception ex)
    {
        target.Text = ex is ArgumentException ? ex.Message : Error(ex);
        bool taken = ex is ApiException { FromServer: true, Code: "name-taken" };
        _useCode.Visible = taken && target == _nameError;
    }

    private async Task Run(Label errorLabel, Func<Task<Contributor>> action)
    {
        _nameError.Text = _codeError.Text = "";
        _useCode.Visible = false;
        _register.Enabled = _link.Enabled = _browser.Enabled = false;
        _quick.Text = S("onb.working");
        UseWaitCursor = true;
        try
        {
            Result = await action();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) when (ex is ArgumentException or ScoutException)
        {
            SetError(errorLabel, ex);
        }
        finally
        {
            UseWaitCursor = false;
            _register.Enabled = _link.Enabled = _browser.Enabled = true;
            _quick.Text = S("onb.quick");
        }
    }

    // ---------------------------------------------------------------- byggklossar

    private static RoundedPanel Card() => new() { FillColor = Theme.Surface, BorderColor = Theme.Line, Radius = 10, AutoSize = true, Padding = new Padding(20, 18, 20, 18), Dock = DockStyle.Top, Margin = Padding.Empty };

    private static FlowLayoutPanel Stack() => new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty };

    private static Label H2(string t) => new() { Text = t, AutoSize = true, Font = Theme.Display(15f), ForeColor = Theme.Text, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 8) };

    private static Label Para(string t, int width) => new() { Text = t, AutoSize = true, MaximumSize = new Size(width, 0), Font = Theme.Ui(10f), ForeColor = Theme.Muted, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 10) };

    private static Label FieldLabel(string t) => new() { Text = t, AutoSize = true, Font = Theme.Ui(9.5f), ForeColor = ColorTranslator.FromHtml("#D4C8BF"), BackColor = Color.Transparent, Margin = new Padding(0, 6, 0, 6) };

    private static Label Check(string t) => new() { Text = "✓  " + t, AutoSize = true, Font = Theme.Ui(10f), ForeColor = ColorTranslator.FromHtml("#E5D7CD"), BackColor = Color.Transparent, Margin = new Padding(0, 6, 0, 6) };

    private static Label Err() => new() { Text = "", AutoSize = true, MaximumSize = new Size(400, 0), Font = Theme.Ui(9.75f), ForeColor = Theme.Err, BackColor = Color.Transparent, Margin = new Padding(0, 8, 0, 0) };

    private static Control Separator(int width) => new Panel { Height = 1, Width = width, BackColor = ColorTranslator.FromHtml("#493B40"), Margin = new Padding(0, 18, 0, 16) };

    private static FlowLayoutPanel Row(params Control[] cs)
    {
        var r = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 14, 0, 0) };
        foreach (var c in cs) r.Controls.Add(c);
        return r;
    }
}
