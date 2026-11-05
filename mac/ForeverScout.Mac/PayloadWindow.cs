using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using static ForeverScout.Mac.Texts;

namespace ForeverScout.Mac;

/// <summary>"Visa vad som skickas": exakt innehåll i nästa (eller senaste) uppladdning (UploadPreview i Core).</summary>
internal sealed class PayloadWindow : Window
{
    public PayloadWindow(string text)
    {
        Look.StyleWindow(this, T("payload.title"), 900, 640);
        MinWidth = 520;
        MinHeight = 320;
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            FontFamily = Look.Mono, FontSize = 12, Background = Look.B(Look.InputBg), Foreground = Look.B(Look.Text),
            BorderBrush = Look.B(Look.Line), BorderThickness = new Thickness(1),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        var close = Look.QuietButton(T("close.exit"));
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        close.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(16) };
        var head = Look.Label(T("payload.title"), 16, Look.Text, FontWeight.Bold);
        head.Margin = new Thickness(0, 0, 0, 10);
        grid.Children.Add(head);
        Grid.SetRow(box, 1);
        grid.Children.Add(box);
        close.Margin = new Thickness(0, 10, 0, 0);
        Grid.SetRow(close, 2);
        grid.Children.Add(close);
        Content = grid;
    }
}
