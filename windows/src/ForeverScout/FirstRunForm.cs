using ForeverScout.Core;
using static ForeverScout.Core.Strings;

namespace ForeverScout;

/// <summary>Första start: välj visningsnamn eller koppla med kod. "Logga in" finns men är utgråad.</summary>
internal sealed class FirstRunForm : Form
{
    private readonly ApiClient _api;
    private readonly TextBox _name = Theme.Input();
    private readonly TextBox _code = Theme.Input();
    private readonly Button _register = Theme.GoldButton(S("first.register"));
    private readonly Button _link = Theme.QuietButton(S("first.link"));
    private readonly Button _login = Theme.QuietButton(S("first.login"));
    private readonly Label _error = Theme.Label("", 9.5f, Theme.Err);

    public Contributor? Result { get; private set; }

    public FirstRunForm(ApiClient api)
    {
        _api = api;
        Text = S("app");
        Icon = Theme.AppIcon;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(28, 24, 28, 24);

        _login.Enabled = false; // kommer snart
        _name.Width = _code.Width = 300;
        _name.MaxLength = 24;
        _code.MaxLength = 16;
        _code.CharacterCasing = CharacterCasing.Upper;

        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Theme.Bg };
        var title = new Label { Text = S("first.title"), AutoSize = true, Font = Theme.Display(18f), ForeColor = Theme.Text, Margin = new Padding(0, 0, 0, 8) };
        var intro = new Label { Text = S("first.intro"), MaximumSize = new Size(440, 0), AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Ui(10f), Margin = new Padding(0, 0, 0, 18) };
        var logo = Theme.Logo(220);
        logo.Margin = new Padding(0, 0, 0, 8);
        flow.Controls.Add(logo);
        flow.Controls.Add(title);
        flow.Controls.Add(intro);
        flow.Controls.Add(Theme.Label(S("first.name"), 9.5f, Theme.Muted));
        flow.Controls.Add(Row(_name, _register));
        flow.Controls.Add(new Label { Text = S("first.or"), AutoSize = true, ForeColor = Theme.Faint, Font = Theme.Ui(9.5f), Margin = new Padding(0, 18, 0, 6) });
        flow.Controls.Add(Theme.Label(S("first.code"), 9.5f, Theme.Muted));
        flow.Controls.Add(Row(_code, _link));
        var loginRow = Row(_login);
        loginRow.Margin = new Padding(0, 18, 0, 0);
        flow.Controls.Add(loginRow);
        flow.Controls.Add(_error);
        Controls.Add(flow);

        AcceptButton = _register;
        _register.Click += async (_, _) => await Run(async () =>
        {
            var n = _name.Text.Trim();
            if (n.Length is < 2 or > 24) throw new ArgumentException(S("first.badname"));
            return await _api.RegisterAsync(n);
        });
        _link.Click += async (_, _) => await Run(async () =>
        {
            var c = _code.Text.Trim().Replace(" ", "").Replace("-", "");
            if (c.Length < 6) throw new ArgumentException(S("first.badcode"));
            return await _api.LinkAsync(c);
        });
        _code.Enter += (_, _) => AcceptButton = _link;
        _name.Enter += (_, _) => AcceptButton = _register;
        HandleCreated += (_, _) => Theme.DarkTitleBar(this);
    }

    private static FlowLayoutPanel Row(params Control[] cs)
    {
        var r = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0), BackColor = Color.Transparent };
        foreach (var c in cs) { c.Margin = new Padding(0, 0, 10, 0); r.Controls.Add(c); }
        return r;
    }

    private async Task Run(Func<Task<Contributor>> action)
    {
        _error.Text = "";
        _register.Enabled = _link.Enabled = false;
        UseWaitCursor = true;
        try
        {
            Result = await action();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (ArgumentException ex)
        {
            _error.Text = ex.Message; // redan översatt (S("first.badname") m.fl.)
        }
        catch (ScoutException ex)
        {
            _error.Text = Error(ex);
        }
        finally
        {
            UseWaitCursor = false;
            _register.Enabled = _link.Enabled = true;
        }
    }
}
