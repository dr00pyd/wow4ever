using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ForeverScout.Core;
using static ForeverScout.Mac.Texts;

namespace ForeverScout.Mac;

/// <summary>
/// Dashboard (UI V1), samma innehåll som MainForm på Windows: tre översiktskort, tabellen med Typ/Antal/Storlek/Status/
/// Beskrivning, "Mitt bidrag", lägerjournalen och statusraden. Stäng (röda knappen) ställer samma fråga som på Windows.
/// </summary>
internal sealed class MainWindow : Window
{
    private readonly ScoutController _app;
    private readonly TextBlock _who = Look.Label("", 13, Look.Muted);
    private readonly TextBlock _addonPill = Look.Label("", 12, Look.Ok);
    private readonly Border _pill;
    private readonly Button _upload = Look.GoldButton("↑  " + T("upload"));
    private readonly TextBlock _statCollected = StatValue();
    private readonly TextBlock _statLast = StatValue();
    private readonly TextBlock _statPoints = StatValue();
    private readonly Grid _table = new() { ColumnDefinitions = new ColumnDefinitions("2*,*,*,2*,4*") };
    private readonly Dictionary<DataKind, (TextBlock Count, TextBlock Size, TextBlock Status)> _rows = new();
    private readonly TextBlock _points = StatValue();
    private readonly TextBlock _rank = StatValue();
    private readonly TextBlock _confirmed = StatValue();
    private readonly TextBlock _accepted = StatValue();
    private readonly StackPanel _mineDetail = new() { Spacing = 4 };
    private readonly StackPanel _journal = new() { Spacing = 6 };
    /// <summary>"Addonen har inte sparat något än" – syns bara tills spelet sparat ForeverScout.lua första gången.</summary>
    private readonly Border _help;
    private readonly TextBlock _footAddon = Look.Label("", 12);
    private readonly TextBlock _footUpload = Look.Label("", 12);
    private readonly TextBlock _footNext = Look.Label("", 12, Look.Muted);
    private string? _flash;
    private DispatcherTimer? _flashTimer;
    private bool _closingAsked;

    public MainWindow(ScoutController app)
    {
        _app = app;
        Look.StyleWindow(this, "Forever Scout", 1080, 760);
        MinWidth = 820;
        MinHeight = 560;

        // ---- huvud
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 16 };
        if (Res.Has("logo-header.png"))
            header.Children.Add(new Image { Source = Res.Bitmap("logo-header.png"), Height = 72 });
        var titles = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Look.Heading("Forever Scout", 26));
        titles.Children.Add(_who);
        _pill = Look.Pill(_addonPill);
        _pill.HorizontalAlignment = HorizontalAlignment.Left;
        titles.Children.Add(_pill);
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        _upload.Click += async (_, _) => await _app.UploadNow(quietIfNothing: false);
        HotKeyManager.SetHotKey(_upload, new KeyGesture(Key.F5));
        actions.Children.Add(_upload);
        var gear = Look.QuietButton("⚙");
        gear.FontSize = 16;
        gear.Flyout = GearMenu();
        actions.Children.Add(gear);
        Grid.SetColumn(actions, 2);
        header.Children.Add(actions);
        var headerBox = new Border
        {
            Background = Look.B(Look.Bg2), BorderBrush = Look.B(Look.Line), BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(24, 16), Child = header,
        };

        // ---- översiktskort
        var stats = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 12 };
        AddCol(stats, StatCard(T("dash.stat.collected"), _statCollected), 0);
        AddCol(stats, StatCard(T("dash.stat.last"), _statLast), 1);
        AddCol(stats, StatCard(T("dash.stat.points"), _statPoints), 2);

        // ---- tabell
        BuildTable();
        var tableBox = new StackPanel { Spacing = 4 };
        tableBox.Children.Add(Look.Label(T("dash.table.title"), 17, Look.Text, FontWeight.Bold));
        tableBox.Children.Add(Look.Label(T("dash.table.sub"), 13, Look.Muted));
        tableBox.Children.Add(new Border { Margin = new Thickness(0, 8, 0, 0), CornerRadius = new CornerRadius(8), ClipToBounds = true, BorderBrush = Look.B(Look.Line), BorderThickness = new Thickness(1), Child = _table });

        // ---- mitt bidrag + lägerjournal
        var mine = new StackPanel { Spacing = 8 };
        mine.Children.Add(Look.Label(T("mine"), 17, Look.Text, FontWeight.Bold));
        var mineStats = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), ColumnSpacing = 8 };
        AddCol(mineStats, Mini(T("points"), _points), 0);
        AddCol(mineStats, Mini(T("rank"), _rank), 1);
        AddCol(mineStats, Mini(T("mine.confirmed"), _confirmed), 2);
        AddCol(mineStats, Mini(T("mine.accepted"), _accepted), 3);
        mine.Children.Add(mineStats);
        mine.Children.Add(_mineDetail);
        mine.Children.Add(Look.Label(T("mine.hint"), 12, Look.Faint));
        var journal = new StackPanel { Spacing = 8 };
        journal.Children.Add(Look.Label(T("journal.title"), 17, Look.Text, FontWeight.Bold));
        journal.Children.Add(_journal);
        var lower = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };
        AddCol(lower, Look.Panel(mine), 0);
        AddCol(lower, Look.Panel(journal), 1);

        var help = new StackPanel { Spacing = 6 };
        help.Children.Add(Look.Label(T("help.nosv.title"), 15, Look.GoldHi, FontWeight.Bold));
        help.Children.Add(Look.Label(T("help.nosv.body"), 13, Look.Text));
        _help = Look.Panel(help, Color.Parse("#2E2619"));
        _help.BorderBrush = Look.B(Look.Gold);
        _help.IsVisible = false;

        var body = new StackPanel { Spacing = 16, Margin = new Thickness(24, 20) };
        body.Children.Add(_help);
        body.Children.Add(stats);
        body.Children.Add(Look.Panel(tableBox));
        body.Children.Add(lower);

        // ---- statusrad
        var foot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 };
        foot.Children.Add(_footAddon);
        foot.Children.Add(_footUpload);
        foot.Children.Add(_footNext);
        var footer = new Border
        {
            Background = Look.B(Look.Footer), BorderBrush = Look.B(Look.Line), BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(24, 8), Child = foot,
        };

        var root = new DockPanel();
        DockPanel.SetDock(headerBox, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(headerBox);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = body });
        Content = root;

        Closing += OnClosing;
        ShowStats();
        ShowContribution();
        ShowJournal();
        ShowStatus();
    }

    private static void AddCol(Grid g, Control c, int col) { Grid.SetColumn(c, col); g.Children.Add(c); }

    private static TextBlock StatValue() => new() { FontSize = 22, FontWeight = FontWeight.Bold, Foreground = Look.B(Look.Text), FontFamily = Look.Display };

    private static Border StatCard(string title, TextBlock value)
    {
        var p = new StackPanel { Spacing = 6 };
        p.Children.Add(Look.Label(title, 13, Look.Muted));
        p.Children.Add(value);
        return Look.Panel(p, Look.Surface2);
    }

    private static Control Mini(string title, TextBlock value)
    {
        value.FontSize = 18;
        var p = new StackPanel { Spacing = 2 };
        p.Children.Add(Look.Label(title, 12, Look.Muted));
        p.Children.Add(value);
        return p;
    }

    private MenuFlyout GearMenu()
    {
        var m = new MenuFlyout();
        MenuItem Item(string text, Action? click, bool enabled = true)
        {
            var i = new MenuItem { Header = text, IsEnabled = enabled };
            if (click != null) i.Click += (_, _) => click();
            m.Items.Add(i);
            return i;
        }
        Item(T("menu.payload"), _app.ShowPayload);
        Item(T("menu.settings"), () => _ = _app.OpenSettings(pickDir: false));
        Item(T("menu.update"), () => _ = _app.CheckUpdates(interactive: true));
        m.Items.Add(new Separator());
        Item(T("menu.guide"), () => Links.Open(_app.Api, Links.Guide(_app.Api)));
        Item(T("menu.faq"), () => Links.Open(_app.Api, Links.Faq(_app.Api)));
        Item(T("menu.terms"), () => Links.Open(_app.Api, Links.Terms(_app.Api)));
        Item(T("menu.privacy"), () => Links.Open(_app.Api, Links.Privacy(_app.Api)));
        Item(T("menu.report"), () => Links.Open(_app.Api, Links.Report(_app.Api)));
        m.Items.Add(new Separator());
        Item(T("menu.login"), () => _ = _app.LinkWithBrowser(this));
        Item(T("menu.about"), () => _ = _app.ShowAbout());
        Item(T("menu.exit"), _app.Exit);
        return m;
    }

    private void BuildTable()
    {
        var kinds = Enum.GetValues<DataKind>();
        _table.RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", kinds.Length + 1)));
        string[] heads = [T("col.type"), T("col.count"), T("col.size"), T("col.status"), T("col.desc")];
        for (int c = 0; c < heads.Length; c++) Cell(Look.Label(heads[c], 12, Look.Muted, FontWeight.Bold), 0, c, Look.TableHead);
        int r = 1;
        foreach (var k in kinds)
        {
            var bg = r % 2 == 0 ? Look.Surface2 : Look.Surface;
            Cell(Look.Label(Strings.Kind(k), 13, Look.Text, FontWeight.Bold), r, 0, bg);
            var count = Look.Label("0", 13);
            var size = Look.Label("–", 13, Look.Muted);
            var status = Look.Label("", 13);
            Cell(count, r, 1, bg);
            Cell(size, r, 2, bg);
            Cell(status, r, 3, bg);
            Cell(Look.Label(Strings.Desc(k), 13, Look.Muted), r, 4, bg);
            _rows[k] = (count, size, status);
            r++;
        }
    }

    private void Cell(TextBlock t, int row, int col, Color bg)
    {
        var b = new Border { Background = Look.B(bg), Padding = new Thickness(12, 8), Child = t };
        Grid.SetRow(b, row);
        Grid.SetColumn(b, col);
        _table.Children.Add(b);
    }

    // ------------------------------------------------------------------ vyer (samma regler som MainForm)

    private (string, Color) RowStatus(KindStats s)
    {
        bool pending = s.Count > s.Uploaded;
        if (_app.CurrentDir is null) return (T("st.nodir"), Look.Warn); // 1.0.12 (H5): inte "Väntar på /reload" utan spelkatalog
        if (s.Count == 0)
            return _app.HasSavedVariables || s.Kind == DataKind.Hotfix ? (T("st.nodata"), Look.Faint) : (T("st.waiting"), Look.Muted);
        if (_app.Uploading && pending) return (T("st.uploading"), Look.Gold);
        if (pending && _app.ErrorText != null) return (T("st.error"), Look.Err);
        if (!pending) return (T("st.uploaded"), Look.Ok);
        return (T("st.ready"), Look.GoldHi);
    }

    public void ShowStats()
    {
        _help.IsVisible = _app.CurrentDir != null && !_app.HasSavedVariables;
        var stats = _app.Stats;
        int total = 0;
        foreach (var (k, row) in _rows)
        {
            var s = stats[k];
            if (k != DataKind.Hotfix) total += s.Count;
            row.Count.Text = s.Count.ToString("N0", Strings.Culture);
            row.Size.Text = s.Bytes > 0 ? Strings.Size(s.Bytes) : "–";
            var (text, color) = RowStatus(s);
            row.Status.Text = text;
            row.Status.Foreground = Look.B(color);
        }
        _statCollected.Text = total.ToString("N0", Strings.Culture);
        ShowLastUpload();
    }

    private void ShowLastUpload()
    {
        var st = _app.Settings;
        (string text, Color color) = _app.Uploading ? (T("dash.last.uploading"), Look.Gold)
            : _app.ErrorText != null ? (T("dash.last.error"), Look.Err)
            : st.NextRetryUtc != null ? (T("dash.last.retry"), Look.Warn)
            : st.LastUploadUtc is { } t ? (Strings.When(t), Look.Ok)
            : (T("dash.last.never"), Look.Muted);
        _statLast.Text = text;
        _statLast.Foreground = Look.B(color);
    }

    public void ShowContribution()
    {
        var st = _app.Settings;
        _who.Text = st.ContributorName is { } n ? TF("as", n) : "";
        var pts = st.Points is int p ? p.ToString("N0", Strings.Culture) : "–";
        _statPoints.Text = pts + (st.Points is null ? "" : " " + T("dash.points.unit"));
        _points.Text = pts;
        _rank.Text = st.Rank is int r ? "#" + r.ToString("N0", Strings.Culture) : "–";
        _confirmed.Text = st.ConfirmedTotal is int cc ? cc.ToString("N0", Strings.Culture) : "–"; // bara från servern
        _accepted.Text = st.AcceptedTotal is int a ? a.ToString("N0", Strings.Culture) : "–";

        _mineDetail.Children.Clear();
        if (st.LastConfirmed.Count == 0)
        {
            if ((st.Points ?? 0) == 0) _mineDetail.Children.Add(Look.Label(T("mine.newbie"), 14, Look.Text, FontWeight.Bold));
            _mineDetail.Children.Add(Look.Label((st.Points ?? 0) == 0 ? T("mine.newbie.text") : T("confirmed.none"), 13, Look.Muted));
        }
        else
        {
            _mineDetail.Children.Add(Look.Label(T("confirmed"), 13, Look.Muted));
            foreach (var item in st.LastConfirmed.Take(3))
            {
                var url = item.Url;
                if (string.IsNullOrEmpty(url)) { _mineDetail.Children.Add(Look.Label("› " + item.Name, 14, Look.Text, FontWeight.Bold)); continue; }
                var link = new Button
                {
                    Content = "› " + item.Name, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
                    Foreground = Look.B(Look.Gold), FontWeight = FontWeight.Bold, Cursor = new Cursor(StandardCursorType.Hand),
                };
                link.Click += (_, _) => Links.Open(_app.Api, url);
                _mineDetail.Children.Add(link);
            }
        }
    }

    public void ShowJournal()
    {
        _journal.Children.Clear();
        foreach (var (_, text, color) in _app.Journal.Take(5)) _journal.Children.Add(JournalLine(text, color));
        _journal.Children.Add(JournalLine(T("journal.waiting"), Look.Muted));
    }

    private static TextBlock JournalLine(string text, Color color)
    {
        var t = Look.Label("● " + text, 12, color);
        t.FontFamily = Look.Mono;
        return t;
    }

    public void ShowStatus()
    {
        var dir = _app.CurrentDir;
        var inst = dir is null ? null : AddonInstaller.InstalledVersion(dir);
        _addonPill.Text = inst != null ? TF("pill.addon", inst) : dir is null ? T("game.none") : T("pill.noaddon");
        _addonPill.Foreground = Look.B(inst != null ? Look.Ok : Look.Err);
        _pill.Background = Look.B(inst != null ? Look.OkSoft : Look.ErrSoft);
        _footAddon.Text = "● " + (inst != null ? TF("pill.addon", inst) : T("pill.noaddon"));
        _footAddon.Foreground = Look.B(inst != null ? Look.Ok : Look.Warn);
        // 1.0.12 (H4): "Allt fungerar" bara när det stämmer – spelkatalog, addon på plats, addonen har sparat, automatisk
        // uppladdning på och inga fel. Annars visas det som faktiskt gäller.
        var st = _app.Settings;
        bool healthy = Rules112.Healthy(_app.State == TrayState.Normal, dir != null, inst != null, _app.HasSavedVariables,
            _app.ErrorText != null, st.NextRetryUtc != null, _app.AutoUploadActive);
        _footUpload.Text = "● " + (_flash ?? (healthy && st.LastUploadUtc is null ? T("footer.ok") : _app.StatusText));
        _footUpload.Foreground = Look.B(_flash != null || healthy ? Look.Ok : _app.StatusColor);
        var nextKey = Rules112.FooterNextKey(st.NextRetryUtc != null, dir != null, _app.AutoUploadActive);
        _footNext.Text = st.NextRetryUtc is { } next ? TF(nextKey, Strings.When(next)) : T(nextKey);
        _footNext.Foreground = Look.B(nextKey == "footer.next" ? Look.Muted : Look.Warn);
        bool busy = _app.Uploading;
        _upload.IsEnabled = !busy;
        // 1.0.12 (H5): utan spelkatalog är knappen vägen framåt (den öppnar valet av katalog) – säg det.
        var buttonKey = Rules112.MainButtonKey(busy, dir != null);
        _upload.Content = buttonKey switch { "uploading" => T("uploading"), "pick.dir" => FolderGlyph + T("pick.dir"), _ => "↑  " + T("upload") };
        ShowLastUpload();
    }

#if LINUX
    private const string FolderGlyph = ""; // alla skrivbord har inte ett färgemoji-typsnitt – hellre ingen symbol än en ruta
#else
    private const string FolderGlyph = "📁  ";
#endif

    /// <summary>Kort meddelande i statusraden (t.ex. "Inget nytt att ladda upp", "Uppdaterad till …").</summary>
    public void Flash(string text, int ms = 5000)
    {
        _flash = text;
        ShowStatus();
        _flashTimer?.Stop();
        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        _flashTimer.Tick += (_, _) => { _flashTimer?.Stop(); _flash = null; ShowStatus(); };
        _flashTimer.Start();
    }

    public void ShowFromMenuBar()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Stäng: samma fråga och samma sparade svar som på Windows. Avsluta i menyn och systemets avstängning frågar inte.</summary>
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingAsked && !_app.IsExiting) { e.Cancel = true; return; } // frågan visas redan
        var cause = e.CloseReason switch
        {
            WindowCloseReason.WindowClosing when !e.IsProgrammatic => CloseCause.UserClosing,
            WindowCloseReason.OSShutdown => CloseCause.WindowsShutDown,
            WindowCloseReason.ApplicationShutdown => CloseCause.ApplicationExit,
            _ => CloseCause.Other,
        };
        var decision = CloseBehavior.Decide(cause, _app.IsExiting, _app.Settings);
        if (decision == CloseDecision.Exit)
        {
            // Användaren valde Stäng (sparat svar): avsluta appen ordnat, men inte inifrån stängningshändelsen.
            // Avsluta i menyn, utloggning och avstängning stängs direkt.
            if (!_app.IsExiting && cause == CloseCause.UserClosing) { e.Cancel = true; Dispatcher.UIThread.Post(_app.Exit); }
            return;
        }
        e.Cancel = true; // fönstret göms i stället för att stängas
        if (decision == CloseDecision.Tray) { HideToMenuBar(); return; }
        _closingAsked = true;
        try
        {
            var (choice, dontAsk) = await Dialogs.Close(this);
            CloseBehavior.Remember(_app.Settings, choice, dontAsk);
            _app.Settings.Save();
            if (choice == CloseDecision.Exit) _app.Exit();
            else if (choice == CloseDecision.Tray) HideToMenuBar();
        }
        finally { _closingAsked = false; }
    }

    private void HideToMenuBar()
    {
        Hide();
        _app.OnHiddenToMenuBar();
    }
}
