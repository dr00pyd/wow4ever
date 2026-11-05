using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ForeverScout.Core;
using static ForeverScout.Mac.Texts;

namespace ForeverScout.Mac;

/// <summary>Små dialoger i Lägereld-stil (ersätter MessageBox). Utan ägarfönster visas de fristående och överst.</summary>
internal static class Dialogs
{
    private static Window Make(string title, Control body, double width = 440)
    {
        var w = Look.StyleWindow(new Window(), title, width, null);
        w.CanResize = false;
        w.Content = new Border { Padding = new Thickness(24), Child = body };
        return w;
    }

    private static async Task Show(Window w, Window? owner)
    {
        if (owner is { IsVisible: true }) { await w.ShowDialog(owner); return; }
        var done = new TaskCompletionSource();
        w.Closed += (_, _) => done.TrySetResult();
        w.Topmost = true;
        w.Show();
        w.Activate();
        await done.Task;
    }

    private static StackPanel Body(string text, out StackPanel buttons)
    {
        buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var p = new StackPanel { Spacing = 6 };
        foreach (var para in text.Split("\n\n")) p.Children.Add(Look.Label(para, 14));
        p.Children.Add(buttons);
        return p;
    }

    public static async Task Info(Window? owner, string text, string? title = null)
    {
        var body = Body(text, out var buttons);
        var w = Make(title ?? T("app"), body);
        var ok = Look.GoldButton("OK");
        ok.IsDefault = true;
        ok.Click += (_, _) => w.Close();
        buttons.Children.Add(ok);
        await Show(w, owner);
    }

    public static async Task<bool> YesNo(Window? owner, string text, string? yes = null, string? no = null)
    {
        bool result = false;
        var body = Body(text, out var buttons);
        var w = Make(T("app"), body);
        var y = Look.GoldButton(yes ?? (Strings.IsSv ? "Ja" : "Yes"));
        var n = Look.QuietButton(no ?? (Strings.IsSv ? "Nej" : "No"));
        y.IsDefault = true;
        n.IsCancel = true;
        y.Click += (_, _) => { result = true; w.Close(); };
        n.Click += (_, _) => w.Close();
        buttons.Children.Add(n);
        buttons.Children.Add(y);
        await Show(w, owner);
        return result;
    }

    /// <summary>
    /// "Vill du verkligen stänga Forever Scout?" med Stäng, Göm i menyraden och Avbryt, plus "Fråga inte igen".
    /// Samma fråga och samma sparade svar som på Windows (<see cref="CloseBehavior"/>).
    /// </summary>
    public static async Task<(CloseDecision Choice, bool DontAsk)> Close(Window? owner)
    {
        var choice = CloseDecision.Ask;
        var dontAsk = new CheckBox { Content = T("close.dontask"), Foreground = Look.B(Look.Muted), Margin = new Thickness(0, 12, 0, 0) };
        var p = new StackPanel { Spacing = 6 };
        p.Children.Add(Look.Label(T("close.question"), 16, Look.Text, FontWeight.Bold));
        p.Children.Add(Look.Label(T("close.body"), 13, Look.Muted));
        p.Children.Add(dontAsk);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        p.Children.Add(buttons);
        var w = Make(T("close.title"), p, 480);
        var exit = Look.QuietButton(T("close.exit"));
        var tray = Look.GoldButton(T("close.tray"));
        var cancel = Look.QuietButton(T("cancel"));
        tray.IsDefault = true;
        cancel.IsCancel = true;
        exit.Click += (_, _) => { choice = CloseDecision.Exit; w.Close(); };
        tray.Click += (_, _) => { choice = CloseDecision.Tray; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        buttons.Children.Add(cancel);
        buttons.Children.Add(exit);
        buttons.Children.Add(tray);
        await Show(w, owner);
        return (choice, dontAsk.IsChecked == true);
    }
}
