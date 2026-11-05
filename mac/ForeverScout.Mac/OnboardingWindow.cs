using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ForeverScout.Core;
using static ForeverScout.Mac.Texts;

namespace ForeverScout.Mac;

/// <summary>
/// Onboarding (UI V1): "Slå dig ner vid elden." Logga in med webbläsaren är den primära handlingen (1.0.9); registrering
/// med visningsnamn och koppling med kod ligger som alternativ under den. Rutan till höger förklarar vilken data som
/// delas. Samma flöde som FirstRunForm på Windows.
/// </summary>
internal sealed class OnboardingWindow : Window
{
    private readonly ApiClient _api;
    private readonly TextBox _name = Look.Input(null, 24);
    private readonly TextBox _code = Look.Input(T("onb.code.ph"), 16);
    private readonly Button _browser = Look.GoldButton(T("link.browser"));
    private readonly Button _register = Look.QuietButton(T("onb.start.title"));
    private readonly Button _link = Look.QuietButton(T("first.link"));
    private readonly Button _useCode = Look.QuietButton(T("onb.usecode"));
    private readonly TextBlock _status = Look.Label("", 13, Look.Muted);

    public Contributor? Result { get; private set; }

    public OnboardingWindow(ApiClient api)
    {
        _api = api;
        Look.StyleWindow(this, T("first.title"), 960, 700);
        MinWidth = 760;
        MinHeight = 560;

        // ---- vänster: rubrik, registrering, koppling
        var left = new StackPanel { Spacing = 10 };
        left.Children.Add(Look.Label(T("onb.eyebrow"), 12, Look.Gold, FontWeight.Bold));
        left.Children.Add(Look.Heading(T("onb.title"), 32));
        left.Children.Add(Look.Label(T("onb.sub"), 15, Look.Muted));

        var start = new StackPanel { Spacing = 8 };
        start.Children.Add(Look.Label(T("onb.start.title"), 17, Look.Text, FontWeight.Bold));
        start.Children.Add(Look.Label(T("link.explain"), 13, Look.Muted));
        start.Children.Add(_browser);
        start.Children.Add(new Border { Height = 1, Background = Look.B(Look.Line), Margin = new Thickness(0, 10, 0, 6) });
        start.Children.Add(Look.Label(T("onb.orname"), 14, Look.Text, FontWeight.Bold));
        start.Children.Add(Look.Label(T("onb.start.text"), 13, Look.Muted));
        start.Children.Add(Look.Label(T("onb.name"), 13, Look.Text));
        start.Children.Add(_name);
        var regRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        regRow.Children.Add(_register);
        var quick = Look.Label(T("onb.quick"), 12, Look.Faint);
        quick.VerticalAlignment = VerticalAlignment.Center;
        regRow.Children.Add(quick);
        start.Children.Add(regRow);
        var startPanel = Look.Panel(start, Look.Surface2, 20);
        startPanel.Margin = new Thickness(0, 14, 0, 0);
        left.Children.Add(startPanel);

        var have = new StackPanel { Spacing = 8 };
        have.Children.Add(Look.Label(T("onb.have"), 14, Look.Text, FontWeight.Bold));
        have.Children.Add(Look.Label(T("onb.code"), 13, Look.Muted));
        var codeRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        codeRow.Children.Add(_code);
        Grid.SetColumn(_link, 1);
        codeRow.Children.Add(_link);
        have.Children.Add(codeRow);
        var havePanel = Look.Panel(have, Look.Surface, 16);
        havePanel.Margin = new Thickness(0, 6, 0, 0);
        left.Children.Add(havePanel);
        _useCode.IsVisible = false;
        left.Children.Add(_status);
        left.Children.Add(_useCode);

        // ---- höger: vad du hjälper till med, integritet, värdshusvärden
        var right = new StackPanel { Spacing = 10 };
        if (Res.Has("logo-header.png"))
            right.Children.Add(new Image { Source = Res.Bitmap("logo-header.png"), Width = 220, HorizontalAlignment = HorizontalAlignment.Center });
        var helps = new StackPanel { Spacing = 6 };
        helps.Children.Add(Look.Label(T("onb.helps.title"), 15, Look.Text, FontWeight.Bold));
        foreach (var k in new[] { "onb.helps.1", "onb.helps.2", "onb.helps.3" }) helps.Children.Add(Look.Label("✦  " + T(k), 13, Look.Muted));
        right.Children.Add(Look.Panel(helps));
        var privacy = new StackPanel { Spacing = 6 };
        privacy.Children.Add(Look.Label(T("onb.privacy.title"), 15, Look.Text, FontWeight.Bold));
        privacy.Children.Add(Look.Label(T("onb.privacy.text"), 13, Look.Muted));
        right.Children.Add(Look.Panel(privacy));
        var tavern = new StackPanel { Spacing = 4 };
        tavern.Children.Add(Look.Label(T("onb.tavern.who"), 12, Look.Gold, FontWeight.Bold));
        var quote = Look.Label(T("onb.tavern.quote"), 13, Look.Text);
        quote.FontStyle = FontStyle.Italic;
        tavern.Children.Add(quote);
        var tavernPanel = Look.Panel(tavern, Look.GoldSoft);
        tavernPanel.BorderBrush = Look.B(Look.GoldEdge);
        right.Children.Add(tavernPanel);

        var cols = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,2*"), ColumnSpacing = 28, Margin = new Thickness(32, 28) };
        cols.Children.Add(left);
        Grid.SetColumn(right, 1);
        cols.Children.Add(right);

        var footer = new Border
        {
            Background = Look.B(Look.Footer), BorderBrush = Look.B(Look.Line), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(32, 10),
            Child = Look.Label("● " + T("onb.footer.ready") + "   ·   " + T("onb.footer.tag"), 12, Look.Ok),
        };
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = cols });
        Content = root;

        _register.IsDefault = true;
        _browser.Click += async (_, _) =>
        {
            // Nyckeln sparas av App (samma väg som namn och kod), därför ingen store här.
            if (await BrowserLinkWindow.Run(_api, this, null, null) is not { } who) return;
            Result = who;
            Close();
        };
        _register.Click += async (_, _) => await Run(async () =>
        {
            var n = (_name.Text ?? "").Trim();
            if (n.Length is < 2 or > 24) throw new ArgumentException(T("first.badname"));
            return await _api.RegisterAsync(n);
        });
        _link.Click += async (_, _) => await Run(async () =>
        {
            var c = (_code.Text ?? "").Trim();
            if (c.Length < 6) throw new ArgumentException(T("first.badcode"));
            return await _api.LinkAsync(c);
        });
        _useCode.Click += (_, _) => { _code.Focus(); _useCode.IsVisible = false; };
        Opened += (_, _) => _name.Focus();
    }

    private async Task Run(Func<Task<Contributor>> action)
    {
        _register.IsEnabled = _link.IsEnabled = _browser.IsEnabled = false;
        _status.Foreground = Look.B(Look.Muted);
        _status.Text = T("onb.working");
        _useCode.IsVisible = false;
        try
        {
            Result = await action();
            Close();
        }
        catch (Exception ex) when (ex is ArgumentException or ScoutException or IOException)
        {
            _status.Foreground = Look.B(Look.Err);
            _status.Text = ex is ArgumentException ? ex.Message : Strings.Error(ex);
            _useCode.IsVisible = ex is ApiException { FromServer: true, Code: "name-taken" };
        }
        finally { _register.IsEnabled = _link.IsEnabled = _browser.IsEnabled = true; }
    }
}
