using ForeverScout.Core;
using static ForeverScout.Core.Strings;

namespace ForeverScout;

/// <summary>Huvudfönstret (en vy över <see cref="ScoutApp"/>). Minimera/X göms till fältet enligt inställningarna.</summary>
internal sealed class MainForm : Form
{
    private readonly ScoutApp _app;
    private readonly ContextMenuStrip _gearMenu = DarkMenuRenderer.NewMenu();
    private readonly bool[] _allUploaded = new bool[Enum.GetValues<DataKind>().Length];
    private string? _flash;
    private readonly System.Windows.Forms.Timer _flashTimer = new() { Interval = 6000 };

    private readonly Label _title = new() { Text = "Forever Scout", AutoSize = true, Font = Theme.Display(20f), ForeColor = Theme.Text, BackColor = Color.Transparent };
    private readonly Label _who = Theme.Label("", 9.5f, Theme.Muted);
    private readonly Button _upload = Theme.GoldButton(S("upload"));
    private readonly Button _gear = Theme.QuietButton("⚙");
    private readonly DataGridView _grid = new();
    private readonly Label _points = new() { AutoSize = true, Font = Theme.Display(22f), ForeColor = Theme.Gold, Text = "–" };
    private readonly Label _rank = new() { AutoSize = true, Font = Theme.Display(22f), ForeColor = Theme.Text, Text = "–" };
    private readonly FlowLayoutPanel _confirmed = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
    private readonly Label _gameStatus = Theme.Label("", 9f, Theme.Muted);
    private readonly Label _addonStatus = Theme.Label("", 9f, Theme.Muted);
    private readonly Label _uploadStatus = Theme.Label("", 9f, Theme.Muted);

    public MainForm(ScoutApp app)
    {
        _app = app;
        Text = "Forever Scout";
        Icon = Theme.AppIcon;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Ui(9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(840, 700);
        MinimumSize = new Size(680, 560);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        BuildGearMenu();
        HandleCreated += (_, _) => Theme.DarkTitleBar(this);
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) HideToTray(); };
        FormClosing += OnClosing;
        _flashTimer.Tick += (_, _) => { _flashTimer.Stop(); _flash = null; ShowStatus(); };

        ShowStats();
        ShowContribution();
        ShowStatus();
    }

    // ------------------------------------------------------------------ layout

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, BackColor = Theme.Bg, Padding = Padding.Empty };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, BackColor = Theme.Bg2, Padding = new Padding(16, 8, 18, 8), Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var logo = Theme.Logo(160);
        logo.Margin = new Padding(0, 0, 16, 0);
        var titles = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 30, 0, 0), BackColor = Color.Transparent };
        _title.Margin = Padding.Empty;
        _who.Margin = new Padding(2, 2, 0, 0);
        titles.Controls.Add(_title);
        titles.Controls.Add(_who);
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 34, 0, 0), BackColor = Color.Transparent };
        _upload.Margin = new Padding(0, 0, 8, 0);
        _gear.Font = new Font("Segoe UI Symbol", 12f);
        _gear.Padding = new Padding(6, 0, 6, 0);
        _gear.Margin = Padding.Empty;
        actions.Controls.Add(_upload);
        actions.Controls.Add(_gear);
        header.Controls.Add(logo, 0, 0);
        header.Controls.Add(titles, 1, 0);
        header.Controls.Add(actions, 2, 0);
        root.Controls.Add(header, 0, 0);

        root.Controls.Add(SectionHead(S("general"), S("general.sub")), 0, 1);

        StyleGrid();
        var gridWrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22, 0, 22, 0), BackColor = Theme.Bg, Margin = Padding.Empty };
        gridWrap.Controls.Add(_grid);
        root.Controls.Add(gridWrap, 0, 2);

        root.Controls.Add(SectionHead(S("mine"), null), 0, 3);

        var card = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, BackColor = Theme.Surface, Padding = new Padding(18, 14, 18, 14), Margin = Padding.Empty };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.Controls.Add(Stat(_points, S("points")), 0, 0);
        card.Controls.Add(Stat(_rank, S("rank")), 1, 0);
        _confirmed.Margin = new Padding(18, 4, 0, 0);
        card.Controls.Add(_confirmed, 2, 0);
        var cardWrap = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(22, 0, 22, 0), Margin = Padding.Empty };
        cardWrap.Controls.Add(card);
        root.Controls.Add(cardWrap, 0, 4);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Theme.Bg2, Padding = new Padding(20, 8, 20, 8), Margin = new Padding(0, 16, 0, 0), WrapContents = true };
        foreach (var l in new[] { _gameStatus, _addonStatus, _uploadStatus }) { l.Margin = new Padding(0, 0, 22, 0); footer.Controls.Add(l); }
        root.Controls.Add(footer, 0, 5);

        Controls.Add(root);

        _upload.Click += async (_, _) => await _app.UploadNow(quietIfNothing: false);
        _gear.Click += (_, _) => _gearMenu.Show(_gear, new Point(_gear.Width - _gearMenu.Width, _gear.Height));
    }

    private static Control SectionHead(string title, string? sub)
    {
        var p = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = false, Padding = new Padding(22, 18, 22, 8), Margin = Padding.Empty, BackColor = Theme.Bg };
        p.Controls.Add(new Label { Text = title, AutoSize = true, Font = Theme.Display(14f), ForeColor = Theme.Text, Margin = new Padding(0, 0, 10, 0) });
        if (sub != null) p.Controls.Add(new Label { Text = "– " + sub, AutoSize = true, Font = Theme.Ui(10f), ForeColor = Theme.Muted, Margin = new Padding(0, 5, 0, 0) });
        return p;
    }

    private static Control Stat(Label value, string caption)
    {
        var f = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 28, 0), BackColor = Color.Transparent };
        value.Margin = Padding.Empty;
        f.Controls.Add(value);
        f.Controls.Add(new Label { Text = caption.ToUpper(Culture), AutoSize = true, Font = Theme.Ui(8f, FontStyle.Bold), ForeColor = Theme.Faint, Margin = new Padding(2, 0, 0, 0) });
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
        g.BackgroundColor = Theme.Surface;
        g.GridColor = Theme.Line;
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.ColumnHeadersHeight = 34;
        g.RowTemplate.Height = 34;
        g.ScrollBars = ScrollBars.Vertical;
        g.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Surface, ForeColor = Theme.Faint, SelectionBackColor = Theme.Surface, SelectionForeColor = Theme.Faint,
            Font = Theme.Ui(8.5f, FontStyle.Bold), Padding = new Padding(10, 0, 10, 0),
        };
        g.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Surface, ForeColor = Theme.Text, SelectionBackColor = Theme.Surface2, SelectionForeColor = Theme.Text,
            Font = Theme.Ui(10f), Padding = new Padding(10, 0, 10, 0),
        };
        DataGridViewTextBoxColumn Col(string key, int fill, bool right = false)
        {
            var c = new DataGridViewTextBoxColumn
            {
                HeaderText = S(key).ToUpper(Culture), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = fill,
                SortMode = DataGridViewColumnSortMode.NotSortable,
            };
            if (right) { c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight; c.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight; }
            return c;
        }
        g.Columns.Add(Col("col.type", 23));
        g.Columns.Add(Col("col.count", 10, right: true));
        g.Columns.Add(Col("col.size", 12, right: true));
        g.Columns.Add(Col("col.uploaded", 16, right: true));
        g.Columns.Add(Col("col.desc", 50));
        g.Columns[0].DefaultCellStyle.Font = Theme.Ui(10f, FontStyle.Bold);
        g.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.CellStyle is not { } st) return;
            st.ForeColor = e.ColumnIndex switch
            {
                3 when e.RowIndex < _allUploaded.Length && _allUploaded[e.RowIndex] => Theme.Ok,
                4 => Theme.Muted,
                _ => Theme.Text,
            };
            st.SelectionForeColor = st.ForeColor;
        };
        foreach (DataKind k in Enum.GetValues<DataKind>()) g.Rows.Add(Kind(k), "0", "–", "0", Desc(k));
        g.SelectionChanged += (_, _) => g.ClearSelection();
    }

    private void BuildGearMenu()
    {
        _gearMenu.Items.Add(S("menu.settings"), null, (_, _) => _app.OpenSettings(false));
        _gearMenu.Items.Add(S("menu.payload"), null, (_, _) => _app.ShowPayload());
        _gearMenu.Items.Add(S("menu.update"), null, async (_, _) => await _app.CheckUpdates(interactive: true));
        _gearMenu.Items.Add(new ToolStripSeparator());
        _gearMenu.Items.Add(S("menu.guide"), null, (_, _) => Links.Open(_app.Api, Links.Guide(_app.Api)));
        _gearMenu.Items.Add(S("menu.faq"), null, (_, _) => Links.Open(_app.Api, Links.Faq(_app.Api)));
        _gearMenu.Items.Add(S("menu.terms"), null, (_, _) => Links.Open(_app.Api, Links.Terms(_app.Api)));
        _gearMenu.Items.Add(S("menu.privacy"), null, (_, _) => Links.Open(_app.Api, Links.Privacy(_app.Api)));
        _gearMenu.Items.Add(S("menu.report"), null, (_, _) => Links.Open(_app.Api, Links.Report(_app.Api)));
        _gearMenu.Items.Add(new ToolStripSeparator());
        _gearMenu.Items.Add(S("menu.exit"), null, (_, _) => _app.Exit());
    }

    // ------------------------------------------------------------------ vyer

    public void ShowStats()
    {
        var stats = _app.Stats;
        int i = 0;
        foreach (DataKind k in Enum.GetValues<DataKind>())
        {
            var s = stats[k];
            var row = _grid.Rows[i];
            row.Cells[1].Value = s.Count.ToString("N0", Culture);
            row.Cells[2].Value = Size(s.Bytes);
            row.Cells[3].Value = s.Count == 0 ? "–" : s.Uploaded.ToString("N0", Culture);
            _allUploaded[i++] = s.Count > 0 && s.Uploaded >= s.Count;
        }
        _grid.Invalidate();
    }

    public void ShowContribution()
    {
        var st = _app.Settings;
        _who.Text = st.ContributorName is { } n ? F("as", n) : "";
        _points.Text = st.Points is int p ? p.ToString("N0", Culture) : "–";
        _rank.Text = st.Rank is int r ? "#" + r.ToString("N0", Culture) : "–";
        _confirmed.SuspendLayout();
        _confirmed.Controls.Clear();
        if (st.LastConfirmed.Count == 0)
        {
            _confirmed.Controls.Add(new Label { Text = S("confirmed.none"), AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = Theme.Muted, Font = Theme.Ui(9.5f) });
        }
        else
        {
            _confirmed.Controls.Add(new Label { Text = S("confirmed"), AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Ui(9.5f), Margin = new Padding(0, 0, 0, 2) });
            foreach (var item in st.LastConfirmed.Take(3))
            {
                var url = item.Url;
                Control c = string.IsNullOrEmpty(url)
                    ? new Label { Text = "› " + item.Name, AutoSize = true, Font = Theme.Ui(10.5f, FontStyle.Bold), ForeColor = Theme.Text, Margin = new Padding(0, 1, 0, 1) }
                    : new LinkLabel
                    {
                        Text = "› " + item.Name, AutoSize = true, Font = Theme.Ui(10.5f, FontStyle.Bold), LinkColor = Theme.Gold,
                        ActiveLinkColor = Theme.GoldHi, VisitedLinkColor = Theme.Gold, LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(0, 1, 0, 1),
                    };
                if (c is LinkLabel ll) ll.LinkClicked += (_, _) => Links.Open(_app.Api, url!);
                _confirmed.Controls.Add(c);
            }
        }
        _confirmed.ResumeLayout();
    }

    public void ShowStatus()
    {
        var dir = _app.CurrentDir;
        _gameStatus.Text = "● " + (dir is null ? S("game.none") : S("game.idle"));
        _gameStatus.ForeColor = dir is null ? Theme.Warn : Theme.Muted;
        var inst = dir is null ? null : AddonInstaller.InstalledVersion(dir);
        _addonStatus.Text = "● " + (inst != null ? F("addon.ok", inst) : S("addon.missing"));
        _addonStatus.ForeColor = inst != null ? Theme.Ok : Theme.Warn;
        _uploadStatus.Text = "● " + (_flash ?? _app.StatusText);
        _uploadStatus.ForeColor = _flash != null ? Theme.Ok : _app.StatusColor;
        bool busy = _app.Uploading;
        _upload.Enabled = !busy;
        _upload.Text = busy ? S("uploading") : S("upload");
    }

    /// <summary>Kort besked i statusraden (t.ex. "Inget nytt att ladda upp").</summary>
    public void Flash(string text)
    {
        _flash = text;
        _flashTimer.Stop();
        _flashTimer.Start();
        ShowStatus();
    }

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
