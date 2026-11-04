using ForeverScout.Core;
using static ForeverScout.Core.Strings;

namespace ForeverScout;

/// <summary>
/// Dashboard (UI V1, en vy över <see cref="ScoutApp"/>): header, tre översiktskort, datatabell med statusläge per typ,
/// "Mitt bidrag" (bara data som servern faktiskt levererar) och lägerjournal, statusrad. Minimera/X göms till fältet.
/// </summary>
internal sealed class MainForm : Form
{
    private const int NarrowWidth = 760;

    private readonly ScoutApp _app;
    private readonly ContextMenuStrip _gearMenu = DarkMenuRenderer.NewMenu();
    private string? _flash;
    private readonly System.Windows.Forms.Timer _flashTimer = new() { Interval = 6000 };

    // header
    private readonly Label _who = new() { AutoSize = true, Font = Theme.Ui(9.5f), ForeColor = ColorTranslator.FromHtml("#C6B8AC"), BackColor = Color.Transparent, Margin = new Padding(2, 2, 0, 0) };
    private readonly Button _upload = Theme.GoldButton("↑  " + S("upload"));
    private readonly Button _gear = Theme.QuietButton("⚙");
    // översikt
    private readonly Label _statCollected = StatValue(Theme.Text);
    private readonly Label _statLast = StatValue(Theme.Ok);
    private readonly Label _statPoints = StatValue(ColorTranslator.FromHtml("#F6C766"));
    // tabell
    private readonly Pill _addonPill = new();
    private readonly DataGridView _grid = new();
    private readonly (string Text, Color Color)[] _rowStatus = new (string, Color)[Enum.GetValues<DataKind>().Length];
    // mitt bidrag
    private readonly Label _points = BigNumber(ColorTranslator.FromHtml("#F4C15B"));
    private readonly Label _rank = BigNumber(Theme.Text);
    private readonly Label _confirmedCount = BigNumber(Theme.Ok);
    private readonly Label _accepted = BigNumber(Theme.Text);
    private readonly FlowLayoutPanel _mineDetail = Stack();
    private readonly FlowLayoutPanel _journal = Stack();
    private TableLayoutPanel _lower = null!;
    private TableLayoutPanel _stats = null!;
    // sidfot
    private readonly Label _footAddon = FootLabel();
    private readonly Label _footUpload = FootLabel();
    private readonly Label _footNext = FootLabel();

    public MainForm(ScoutApp app)
    {
        _app = app;
        Text = "Forever Scout";
        Icon = Theme.AppIcon;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Ui(10f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(940, 800);
        MinimumSize = new Size(620, 560);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        BuildLayout();
        BuildGearMenu();
        HandleCreated += (_, _) => Theme.DarkTitleBar(this);
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) HideToTray(); else Reflow(); };
        FormClosing += OnClosing;
        _flashTimer.Tick += (_, _) => { _flashTimer.Stop(); _flash = null; ShowStatus(); };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.F5) { e.Handled = true; _ = _app.UploadNow(quietIfNothing: false); } };

        ShowStats();
        ShowContribution();
        ShowJournal();
        ShowStatus();
        Reflow();
    }

    // ------------------------------------------------------------------ layout

    private void BuildLayout()
    {
        // Header
        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, BackColor = Theme.Bg2, Padding = new Padding(20, 12, 20, 12) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var logo = Theme.Logo(104);
        logo.Margin = new Padding(0, 0, 14, 0);
        var titles = Stack();
        titles.Anchor = AnchorStyles.Left;
        titles.Controls.Add(new Label { Text = "Forever Scout", AutoSize = true, Font = Theme.Display(18f), ForeColor = Theme.Text, BackColor = Color.Transparent, Margin = Padding.Empty });
        titles.Controls.Add(_who);
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, BackColor = Color.Transparent, Margin = Padding.Empty };
        _upload.Margin = new Padding(0, 0, 8, 0);
        _upload.AccessibleName = S("upload");
        _gear.Font = new Font("Segoe UI Symbol", 12f);
        _gear.AccessibleName = S("set.title");
        _gear.Margin = Padding.Empty;
        actions.Controls.Add(_upload);
        actions.Controls.Add(_gear);
        header.Controls.Add(logo, 0, 0);
        header.Controls.Add(titles, 1, 0);
        header.Controls.Add(actions, 2, 0);
        var headerLine = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Line };

        // Innehåll (rullbart i små fönster)
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Theme.Bg, Padding = new Padding(22, 18, 22, 14), AutoScroll = true };
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        // Tabellen får exakt plats för rubrik + 7 rader; korten under tar resten (rullning i små fönster).
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, LogicalToDeviceUnits(36 * 8 + 2 + 14)));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // får bli högre än fönstret → rullning

        _stats = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 14) };
        for (int i = 0; i < 3; i++) _stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        _stats.Controls.Add(StatCard(S("dash.stat.collected"), _statCollected), 0, 0);
        _stats.Controls.Add(StatCard(S("dash.stat.last"), _statLast), 1, 0);
        _stats.Controls.Add(StatCard(S("dash.stat.points"), _statPoints), 2, 0);
        body.Controls.Add(_stats, 0, 0);

        var headRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 8) };
        headRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        headRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var ht = Stack();
        ht.Controls.Add(new Label { Text = S("dash.table.title"), AutoSize = true, Font = Theme.Display(15f), ForeColor = Theme.Text, BackColor = Color.Transparent, Margin = Padding.Empty });
        ht.Controls.Add(new Label { Text = S("dash.table.sub"), AutoSize = true, Font = Theme.Ui(9f), ForeColor = Theme.Muted, BackColor = Color.Transparent, Margin = new Padding(0, 3, 0, 0) });
        _addonPill.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        headRow.Controls.Add(ht, 0, 0);
        headRow.Controls.Add(_addonPill, 1, 0);
        body.Controls.Add(headRow, 0, 1);

        StyleGrid();
        var gridFrame = new RoundedPanel { Dock = DockStyle.Fill, FillColor = ColorTranslator.FromHtml("#201B20"), BorderColor = ColorTranslator.FromHtml("#4B3D41"), Radius = 8, Padding = new Padding(1), Margin = new Padding(0, 0, 0, 14) };
        gridFrame.Controls.Add(_grid);
        body.Controls.Add(gridFrame, 0, 2);

        _lower = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = Color.Transparent, Margin = Padding.Empty };
        _lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        _lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        _lower.Controls.Add(MineCard(), 0, 0);
        _lower.Controls.Add(JournalCard(), 1, 0);
        body.Controls.Add(_lower, 0, 3);

        // Sidfot
        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 3, BackColor = Theme.Footer, Padding = new Padding(20, 9, 20, 9) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _footUpload.Anchor = AnchorStyles.None;
        _footNext.Anchor = AnchorStyles.Right;
        footer.Controls.Add(_footAddon, 0, 0);
        footer.Controls.Add(_footUpload, 1, 0);
        footer.Controls.Add(_footNext, 2, 0);
        var footerLine = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = ColorTranslator.FromHtml("#352B31") };

        Controls.Add(body);
        Controls.Add(headerLine);
        Controls.Add(header);
        Controls.Add(footerLine);
        Controls.Add(footer);

        _upload.Click += async (_, _) => await _app.UploadNow(quietIfNothing: false);
        _gear.Click += (_, _) => _gearMenu.Show(_gear, new Point(_gear.Width - _gearMenu.Width, _gear.Height));
    }

    private static RoundedPanel StatCard(string caption, Label value)
    {
        var card = new RoundedPanel { FillColor = ColorTranslator.FromHtml("#262025"), BorderColor = ColorTranslator.FromHtml("#4D3B3C"), Radius = 9, Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(14, 12, 14, 12), Margin = new Padding(0, 0, 10, 0) };
        var s = Stack();
        s.Controls.Add(new Label { Text = caption.ToUpper(Culture), AutoSize = true, Font = Theme.Ui(8.25f), ForeColor = ColorTranslator.FromHtml("#D0BEB2"), BackColor = Color.Transparent, Margin = Padding.Empty });
        s.Controls.Add(value);
        card.Controls.Add(s);
        return card;
    }

    private RoundedPanel MineCard()
    {
        var card = Card();
        card.Margin = new Padding(0, 0, 12, 0);
        var s = Stack();
        s.Controls.Add(new SmallTitle(S("mine")));
        var nums = new FlowLayoutPanel { AutoSize = true, WrapContents = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 6), MaximumSize = new Size(520, 0) };
        nums.Controls.Add(Num(_points, S("points")));
        nums.Controls.Add(Num(_rank, S("rank")));
        nums.Controls.Add(Num(_confirmedCount, S("mine.confirmed")));
        nums.Controls.Add(Num(_accepted, S("mine.accepted")));
        s.Controls.Add(nums);
        s.Controls.Add(_mineDetail);
        s.Controls.Add(new Label { Text = S("mine.hint"), AutoSize = true, MaximumSize = new Size(460, 0), Font = Theme.Ui(8.5f), ForeColor = Theme.Faint, BackColor = Color.Transparent, Margin = new Padding(0, 8, 0, 0) });
        card.Controls.Add(s);
        return card;
    }

    private RoundedPanel JournalCard()
    {
        var card = Card();
        var s = Stack();
        s.Controls.Add(new SmallTitle(S("journal.title")));
        s.Controls.Add(_journal);
        card.Controls.Add(s);
        return card;
    }

    private static Control Num(Label value, string caption)
    {
        var f = Stack();
        f.Margin = new Padding(0, 0, 22, 4);
        value.Margin = Padding.Empty;
        f.Controls.Add(value);
        f.Controls.Add(new Label { Text = caption.ToUpper(Culture), AutoSize = true, Font = Theme.Ui(7.75f, FontStyle.Bold), ForeColor = Theme.Faint, BackColor = Color.Transparent, Margin = new Padding(1, 0, 0, 0) });
        return f;
    }

    private void StyleGrid()
    {
        var g = _grid;
        g.Dock = DockStyle.Fill;
        g.ReadOnly = true;
        g.AllowUserToAddRows = g.AllowUserToDeleteRows = g.AllowUserToResizeRows = g.AllowUserToResizeColumns = false;
        g.RowHeadersVisible = false;
        g.MultiSelect = false;
        g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        g.BorderStyle = BorderStyle.None;
        g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        g.EnableHeadersVisualStyles = false;
        g.BackgroundColor = ColorTranslator.FromHtml("#201B20");
        g.GridColor = Theme.LineSoft;
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.ColumnHeadersHeight = g.LogicalToDeviceUnits(36);
        g.RowTemplate.Height = g.LogicalToDeviceUnits(36);
        g.ScrollBars = ScrollBars.Vertical;
        g.StandardTab = true; // Tab lämnar tabellen (tangentbordsnavigering)
        g.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.TableHead, ForeColor = ColorTranslator.FromHtml("#C3AE99"), SelectionBackColor = Theme.TableHead, SelectionForeColor = ColorTranslator.FromHtml("#C3AE99"),
            Font = Theme.Ui(8.25f, FontStyle.Bold), Padding = new Padding(10, 0, 10, 0),
        };
        g.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = ColorTranslator.FromHtml("#201B20"), ForeColor = Theme.Text, SelectionBackColor = Theme.Surface2, SelectionForeColor = Theme.Text,
            Font = Theme.Ui(10f), Padding = new Padding(10, 0, 10, 0),
        };
        DataGridViewTextBoxColumn Col(string key, int fill, int min = 60)
        {
            return new DataGridViewTextBoxColumn
            {
                HeaderText = S(key).ToUpper(Culture), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = fill, MinimumWidth = g.LogicalToDeviceUnits(min),
                SortMode = DataGridViewColumnSortMode.NotSortable,
            };
        }
        g.Columns.Add(Col("col.type", 22, 110));
        g.Columns.Add(Col("col.count", 10));
        g.Columns.Add(Col("col.size", 11));
        g.Columns.Add(Col("col.status", 21, 130));
        g.Columns.Add(Col("col.desc", 40, 120));
        g.Columns[0].DefaultCellStyle.Font = Theme.Ui(10f, FontStyle.Bold);
        g.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.CellStyle is not { } st) return;
            st.ForeColor = e.ColumnIndex switch
            {
                1 or 2 => ColorTranslator.FromHtml("#F2D6A2"), // tabellsiffror
                3 when e.RowIndex < _rowStatus.Length => _rowStatus[e.RowIndex].Color,
                4 => Theme.Muted,
                _ => Theme.Text,
            };
            st.SelectionForeColor = st.ForeColor;
        };
        foreach (DataKind k in Enum.GetValues<DataKind>()) g.Rows.Add(Kind(k), "0", "–", "", Desc(k));
        g.SelectionChanged += (_, _) => { if (!g.Focused) g.ClearSelection(); };
        g.Leave += (_, _) => g.ClearSelection();
    }

    private void BuildGearMenu()
    {
        _gearMenu.Items.Add(S("menu.settings"), null, (_, _) => _app.OpenSettings(false));
        _gearMenu.Items.Add(S("menu.payload"), null, (_, _) => _app.ShowPayload());
        _gearMenu.Items.Add(S("menu.update"), null, async (_, _) => await _app.CheckUpdates(interactive: true));
        _gearMenu.Items.Add(new ToolStripMenuItem(S("menu.login")) { Enabled = false }); // kommer snart
        _gearMenu.Items.Add(new ToolStripSeparator());
        _gearMenu.Items.Add(S("menu.guide"), null, (_, _) => Links.Open(_app.Api, Links.Guide(_app.Api)));
        _gearMenu.Items.Add(S("menu.faq"), null, (_, _) => Links.Open(_app.Api, Links.Faq(_app.Api)));
        _gearMenu.Items.Add(S("menu.terms"), null, (_, _) => Links.Open(_app.Api, Links.Terms(_app.Api)));
        _gearMenu.Items.Add(S("menu.privacy"), null, (_, _) => Links.Open(_app.Api, Links.Privacy(_app.Api)));
        _gearMenu.Items.Add(S("menu.report"), null, (_, _) => Links.Open(_app.Api, Links.Report(_app.Api)));
        _gearMenu.Items.Add(S("menu.about"), null, (_, _) => _app.ShowAbout());
        _gearMenu.Items.Add(new ToolStripSeparator());
        _gearMenu.Items.Add(S("menu.exit"), null, (_, _) => _app.Exit());
    }

    /// <summary>Smalt fönster: korten under varandra och ingen beskrivningskolumn.</summary>
    private void Reflow()
    {
        if (_lower is null || _grid.Columns.Count < 5) return;
        bool narrow = ClientSize.Width < LogicalToDeviceUnits(NarrowWidth);
        _grid.Columns[4].Visible = !narrow;
        if (narrow == (_lower.ColumnCount == 1)) return;
        _lower.SuspendLayout();
        var a = _lower.Controls[0];
        var b = _lower.Controls[1];
        _lower.Controls.Clear();
        _lower.ColumnStyles.Clear();
        if (narrow)
        {
            _lower.ColumnCount = 1;
            _lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            a.Margin = new Padding(0, 0, 0, 12);
            _lower.Controls.Add(a, 0, 0);
            _lower.Controls.Add(b, 0, 1);
        }
        else
        {
            _lower.ColumnCount = 2;
            _lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            _lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            a.Margin = new Padding(0, 0, 12, 0);
            _lower.Controls.Add(a, 0, 0);
            _lower.Controls.Add(b, 1, 0);
        }
        _lower.ResumeLayout();
    }

    // ------------------------------------------------------------------ vyer

    /// <summary>Statusläge per datatyp: ingen data · väntar på spelsparning · redo · pågår · uppladdad · fel.</summary>
    private (string, Color) RowStatus(KindStats s)
    {
        bool uploading = _app.Uploading;
        bool pending = s.Count > s.Uploaded;
        if (s.Count == 0)
            return _app.HasSavedVariables || s.Kind == DataKind.Hotfix ? (S("st.nodata"), Theme.Faint) : (S("st.waiting"), Theme.Muted);
        if (uploading && pending) return (S("st.uploading"), Theme.Gold);
        if (pending && _app.ErrorText != null) return (S("st.error"), Theme.Err);
        if (!pending) return (S("st.uploaded"), Theme.Ok);
        return (S("st.ready"), Theme.GoldHi);
    }

    public void ShowStats()
    {
        var stats = _app.Stats;
        int i = 0, total = 0;
        foreach (DataKind k in Enum.GetValues<DataKind>())
        {
            var s = stats[k];
            if (k != DataKind.Hotfix) total += s.Count;
            var row = _grid.Rows[i];
            row.Cells[1].Value = s.Count.ToString("N0", Culture);
            row.Cells[2].Value = s.Bytes > 0 ? Size(s.Bytes) : "–";
            _rowStatus[i] = RowStatus(s);
            row.Cells[3].Value = _rowStatus[i].Text;
            i++;
        }
        _statCollected.Text = total.ToString("N0", Culture);
        _grid.Invalidate();
        ShowLastUpload();
    }

    private void ShowLastUpload()
    {
        var st = _app.Settings;
        if (_app.Uploading) { _statLast.Text = S("dash.last.uploading"); _statLast.ForeColor = Theme.Gold; }
        else if (_app.ErrorText != null) { _statLast.Text = S("dash.last.error"); _statLast.ForeColor = Theme.Err; }
        else if (st.NextRetryUtc != null) { _statLast.Text = S("dash.last.retry"); _statLast.ForeColor = Theme.Warn; }
        else if (st.LastUploadUtc is { } t) { _statLast.Text = When(t); _statLast.ForeColor = Theme.Ok; }
        else { _statLast.Text = S("dash.last.never"); _statLast.ForeColor = Theme.Muted; }
    }

    public void ShowContribution()
    {
        var st = _app.Settings;
        _who.Text = st.ContributorName is { } n ? F("as", n) : "";
        var pts = st.Points is int p ? p.ToString("N0", Culture) : "–";
        _statPoints.Text = pts + (st.Points is null ? "" : " " + S("dash.points.unit"));
        _points.Text = pts;
        _rank.Text = st.Rank is int r ? "#" + r.ToString("N0", Culture) : "–";
        _confirmedCount.Text = st.ConfirmedTotal is int cc ? cc.ToString("N0", Culture) : "–";  // bara från servern
        _accepted.Text = st.AcceptedTotal is int a ? a.ToString("N0", Culture) : "–";

        _mineDetail.SuspendLayout();
        _mineDetail.Controls.Clear();
        if (st.LastConfirmed.Count == 0)
        {
            if ((st.Points ?? 0) == 0)
                _mineDetail.Controls.Add(new Label { Text = S("mine.newbie"), AutoSize = true, Font = Theme.Ui(10f, FontStyle.Bold), ForeColor = Theme.Text, BackColor = Color.Transparent, Margin = Padding.Empty });
            _mineDetail.Controls.Add(new Label { Text = (st.Points ?? 0) == 0 ? S("mine.newbie.text") : S("confirmed.none"), AutoSize = true, MaximumSize = new Size(460, 0), Font = Theme.Ui(9.75f), ForeColor = Theme.Muted, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 0) });
        }
        else
        {
            _mineDetail.Controls.Add(new Label { Text = S("confirmed"), AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Ui(9.75f), BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 2) });
            foreach (var item in st.LastConfirmed.Take(3))
            {
                var url = item.Url;
                Control c = string.IsNullOrEmpty(url)
                    ? new Label { Text = "› " + item.Name, AutoSize = true, Font = Theme.Ui(10f, FontStyle.Bold), ForeColor = Theme.Text, BackColor = Color.Transparent, Margin = new Padding(0, 1, 0, 1) }
                    : new LinkLabel
                    {
                        Text = "› " + item.Name, AutoSize = true, Font = Theme.Ui(10f, FontStyle.Bold), LinkColor = Theme.Gold, BackColor = Color.Transparent,
                        ActiveLinkColor = Theme.GoldHi, VisitedLinkColor = Theme.Gold, LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(0, 1, 0, 1),
                    };
                if (c is LinkLabel ll) ll.LinkClicked += (_, _) => Links.Open(_app.Api, url!);
                _mineDetail.Controls.Add(c);
            }
        }
        _mineDetail.ResumeLayout();
    }

    public void ShowJournal()
    {
        _journal.SuspendLayout();
        _journal.Controls.Clear();
        var entries = _app.Journal.Take(5).ToList();
        foreach (var (_, text, color) in entries) _journal.Controls.Add(JournalLine(text, color));
        _journal.Controls.Add(JournalLine(S("journal.waiting"), Theme.Muted));
        _journal.ResumeLayout();
    }

    private static Label JournalLine(string text, Color color) => new()
    {
        Text = "● " + text, AutoSize = true, MaximumSize = new Size(380, 0), Font = new Font("Consolas", 9f), ForeColor = color,
        BackColor = Color.Transparent, Margin = new Padding(0, 3, 0, 3),
    };

    public void ShowStatus()
    {
        var dir = _app.CurrentDir;
        var inst = dir is null ? null : AddonInstaller.InstalledVersion(dir);
        _addonPill.Set(inst != null ? F("pill.addon", inst) : dir is null ? S("game.none") : S("pill.noaddon"), inst != null ? Theme.Ok : Theme.Err);
        _footAddon.Text = "● " + (inst != null ? F("pill.addon", inst) : S("pill.noaddon"));
        _footAddon.ForeColor = inst != null ? Theme.Ok : Theme.Warn;
        _footUpload.Text = "● " + (_flash ?? (_app.State == TrayState.Normal && dir != null && _app.ErrorText is null && _app.Settings.NextRetryUtc is null && !_app.Settings.AutoUploadPaused && _app.Settings.LastUploadUtc is null ? S("footer.ok") : _app.StatusText));
        _footUpload.ForeColor = _flash != null ? Theme.Ok : _app.State == TrayState.Normal && dir != null && !_app.Settings.AutoUploadPaused ? Theme.Ok : _app.StatusColor;
        _footNext.Text = _app.Settings.NextRetryUtc is { } next ? F("retry", When(next)) : S("footer.next");
        _footNext.ForeColor = _app.Settings.NextRetryUtc is null ? Theme.Muted : Theme.Warn;
        bool busy = _app.Uploading;
        _upload.Enabled = !busy;
        _upload.Text = busy ? S("uploading") : "↑  " + S("upload");
        _upload.Invalidate();
        ShowLastUpload();
    }

    /// <summary>Kort besked i statusraden (t.ex. "Inget nytt att ladda upp").</summary>
    public void Flash(string text)
    {
        _flash = text;
        _flashTimer.Stop();
        _flashTimer.Start();
        ShowStatus();
    }

    // ------------------------------------------------------------------ byggklossar

    private static RoundedPanel Card() => new() { FillColor = Theme.Surface, BorderColor = Theme.Line, Radius = 10, Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(16, 14, 16, 14), Margin = Padding.Empty };

    private static FlowLayoutPanel Stack() => new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty };

    private static Label StatValue(Color c) => new() { Text = "–", AutoSize = true, Font = Theme.Display(19f), ForeColor = c, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 0) };

    private static Label BigNumber(Color c) => new() { Text = "–", AutoSize = true, Font = Theme.Display(19f), ForeColor = c, BackColor = Color.Transparent };

    private static Label FootLabel() => new() { AutoSize = true, Font = Theme.Ui(9f), ForeColor = Theme.Muted, BackColor = Color.Transparent, Margin = new Padding(0, 0, 18, 0) };

    // ------------------------------------------------------------------ fält / stäng

    public void ShowFromTray()
    {
        ShowInTaskbar = true;
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
        _app.RefreshStats(force: false);
        ShowContribution();
        ShowStatus();
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
        _app.OnHiddenToTray();
    }

    private static CloseCause Cause(CloseReason r) => r switch
    {
        CloseReason.UserClosing => CloseCause.UserClosing,
        CloseReason.WindowsShutDown => CloseCause.WindowsShutDown,
        CloseReason.TaskManagerClosing => CloseCause.TaskManagerClosing,
        CloseReason.ApplicationExitCall => CloseCause.ApplicationExit,
        _ => CloseCause.Other,
    };

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        var decision = CloseBehavior.Decide(Cause(e.CloseReason), _app.IsExiting, _app.Settings);
        if (decision == CloseDecision.Ask)
        {
            using var dlg = new CloseDialog();
            dlg.ShowDialog(this);
            decision = dlg.Choice;
            CloseBehavior.Remember(_app.Settings, decision, dlg.DontAskAgain);
            _app.Settings.Save();
        }
        switch (decision)
        {
            case CloseDecision.Exit:
                if (!_app.IsExiting) BeginInvoke(_app.Exit);
                break;
            case CloseDecision.Tray:
                e.Cancel = true;
                HideToTray();
                break;
            default: // Avbryt
                e.Cancel = true;
                break;
        }
    }

    // ------------------------------------------------------------------ skärmbilder (--screenshots)

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    public static void SaveImage(Control c, string path)
    {
        using var bmp = new Bitmap(c.Width, c.Height);
        bool ok;
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            ok = PrintWindow(c.Handle, hdc, 2 /* PW_RENDERFULLCONTENT */);
            g.ReleaseHdc(hdc);
        }
        if (!ok) c.DrawToBitmap(bmp, new Rectangle(0, 0, c.Width, c.Height));
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        SafeFs.CreateDirectory(Path.GetDirectoryName(path)!);
        SafeFs.WriteAllBytes(path, ms.ToArray()); // bara i appens datakatalog
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _flashTimer.Dispose(); _gearMenu.Dispose(); }
        base.Dispose(disposing);
    }
}
