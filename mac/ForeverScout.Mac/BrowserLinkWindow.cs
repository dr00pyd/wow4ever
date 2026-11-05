using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ForeverScout.Core;
using static ForeverScout.Mac.Texts;

namespace ForeverScout.Mac;

/// <summary>
/// "Logga in med webbläsaren" (kontraktet 2b): hämtar en kod, öppnar guide.wow4ever.se i webbläsaren och väntar på
/// godkännandet. Visar koden (XXXX-XXXX) så att man kan jämföra med sidan, plus Avbryt. Samma som BrowserLinkForm på
/// Windows; flödet är <see cref="DeviceLink"/> i Core. Höjden följer innehållet (Look.StyleWindow med height: null).
/// </summary>
internal sealed class BrowserLinkWindow : Window
{
    private readonly ApiClient _api;
    private readonly Action<Contributor>? _store;
    private readonly TextBlock _code = new() { Text = "····-····", FontFamily = Look.Mono, FontSize = 30, FontWeight = FontWeight.Bold, Foreground = Look.B(Look.GoldHi) };
    private readonly TextBlock _status = Look.Label("", 13, Look.Muted);
    private readonly Button _reopen = Look.QuietButton(T("link.reopen"));
    private readonly Button _retry = Look.GoldButton(T("link.retry"));
    private readonly Button _cancel = Look.QuietButton(T("link.cancel"));
    private CancellationTokenSource? _cts;
    private Uri? _url;

    public Contributor? Result { get; private set; }

    /// <param name="store">Sparar nyckeln när den godkänts; null = anroparen sparar <see cref="Result"/> själv.</param>
    public BrowserLinkWindow(ApiClient api, string? currentName, Action<Contributor>? store)
    {
        _api = api;
        _store = store;
        Look.StyleWindow(this, T("link.browser"), 480, null);
        CanResize = false;

        var p = new StackPanel { Spacing = 8 };
        p.Children.Add(Look.Heading(T("link.browser"), 20));
        p.Children.Add(Look.Label(T("link.explain"), 13, Look.Muted));
        if (currentName is { Length: > 0 } n) p.Children.Add(Look.Label(TF("link.switch", n), 13, Look.Muted));
        var codeLabel = Look.Label(T("link.code"), 12, Look.Gold, FontWeight.Bold);
        codeLabel.Margin = new Thickness(0, 8, 0, 0);
        p.Children.Add(codeLabel);
        p.Children.Add(_code);
        p.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(_reopen);
        buttons.Children.Add(_retry);
        buttons.Children.Add(_cancel);
        p.Children.Add(buttons);
        Content = new Border { Padding = new Thickness(24), Child = p };

        _retry.IsVisible = false;
        _reopen.IsEnabled = false;
        _cancel.IsCancel = true;
        _reopen.Click += (_, _) => { if (_url != null) Links.Open(_api, _url.ToString()); };
        _retry.Click += async (_, _) => await Start();
        _cancel.Click += (_, _) => Close();
        Closed += (_, _) => { _cts?.Cancel(); _cts?.Dispose(); _cts = null; };
        Opened += async (_, _) => await Start();
    }

    /// <summary>Visar fönstret (modalt över <paramref name="owner"/> om det syns) och väntar tills det stängts.</summary>
    public static async Task<Contributor?> Run(ApiClient api, Window? owner, string? currentName, Action<Contributor>? store)
    {
        var w = new BrowserLinkWindow(api, currentName, store);
        if (owner is { IsVisible: true }) await w.ShowDialog(owner);
        else
        {
            var done = new TaskCompletionSource();
            w.Closed += (_, _) => done.TrySetResult();
            w.Topmost = true;
            w.Show();
            w.Activate();
            await done.Task;
        }
        return w.Result;
    }

    private async Task Start()
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _retry.IsVisible = false;
        _reopen.IsEnabled = false;
        _url = null;
        _code.Text = "····-····";
        SetStatus(T("link.starting"), Look.Muted);
        var flow = new DeviceLink(_api, "mac", BuildInfo.Version.ToString(3)) { OnApproved = _store };
        var result = await flow.RunAsync(t =>
        {
            _code.Text = t.DisplayCode;
            _url = t.Url;
            _reopen.IsEnabled = true;
            SetStatus(TF("link.waiting", t.DisplayCode), Look.Muted);
            Links.Open(_api, t.Url.ToString());
        }, ct);
        if (result.Outcome == DeviceLinkOutcome.Cancelled || ct.IsCancellationRequested) return;
        if (result.Outcome == DeviceLinkOutcome.Approved)
        {
            Result = result.Contributor;
            SetStatus(TF("link.ok", result.Contributor!.Name), Look.Ok);
            _reopen.IsEnabled = _cancel.IsEnabled = false;
            await Task.Delay(900);
            Close();
            return;
        }
        _reopen.IsEnabled = false;
        _retry.IsVisible = true;
        SetStatus(result.Outcome switch
        {
            DeviceLinkOutcome.Denied => T("link.denied"),
            DeviceLinkOutcome.Expired => T("link.expired"),
            DeviceLinkOutcome.TooManyKeys => T("link.toomany"),
            DeviceLinkOutcome.BadUrl => T("link.badurl"),
            _ => Strings.Error(result.Error!),
        }, Look.Err);
    }

    private void SetStatus(string text, Color color)
    {
        _status.Text = text;
        _status.Foreground = Look.B(color);
    }
}
