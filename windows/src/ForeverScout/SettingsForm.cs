using ForeverScout.Core;
using static ForeverScout.Core.Strings;

namespace ForeverScout;

/// <summary>Inställningar: WoW-katalog, språk, starta med Windows, automatisk uppladdning, hotfix.</summary>
internal sealed class SettingsForm : Form
{
    private readonly Settings _settings;
    private readonly ComboBox _dir = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 420, FlatStyle = FlatStyle.Flat, BackColor = Theme.Bg, ForeColor = Theme.Text, Font = Theme.Ui(10f) };
    private readonly ComboBox _lang = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, FlatStyle = FlatStyle.Flat, BackColor = Theme.Bg, ForeColor = Theme.Text, Font = Theme.Ui(10f) };
    private readonly CheckBox _autostart = Check(S("set.autostart"));
    private readonly CheckBox _autoupload = Check(S("set.autoupload"));
    private readonly CheckBox _hotfix = Check(S("set.hotfix"));
    private readonly CheckBox _askClose = Check(S("set.askclose"));
    private readonly Label _error = Theme.Label("", 9.5f, Theme.Err);

    public bool Unlinked { get; private set; }

    /// <param name="linkWithBrowser">"Logga in med webbläsaren" (ScoutApp.LinkWithBrowser); null = ingen sådan knapp.</param>
    public SettingsForm(Settings settings, bool startInDirPicker = false, Func<IWin32Window, Task<Contributor?>>? linkWithBrowser = null)
    {
        _settings = settings;
        Text = startInDirPicker ? S("pick.title") : S("set.title");
        Icon = Theme.AppIcon;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(24, 20, 24, 20);

        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        flow.Controls.Add(new Label { Text = Text, AutoSize = true, Font = Theme.Display(16f), ForeColor = Theme.Text, Margin = new Padding(0, 0, 0, 12) });
        if (startInDirPicker)
            flow.Controls.Add(new Label { Text = S("pick.intro"), AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Theme.Muted, Font = Theme.Ui(10f), Margin = new Padding(0, 0, 0, 10) });

        flow.Controls.Add(Theme.Label(S("set.gamedir"), 9.5f, Theme.Muted));
        var browse = Theme.QuietButton(S("set.browse"));
        var find = Theme.QuietButton(S("set.find"));
        flow.Controls.Add(Row(_dir, browse, find));

        _lang.Items.AddRange([S("set.lang.auto"), S("lang.sv"), S("lang.en")]);
        _lang.SelectedIndex = settings.Language switch { "sv" => 1, "en" => 2, _ => 0 };
        if (!startInDirPicker)
        {
            flow.Controls.Add(Spacer());
            flow.Controls.Add(Theme.Label(S("set.lang"), 9.5f, Theme.Muted));
            flow.Controls.Add(_lang);
            flow.Controls.Add(Spacer());
            flow.Controls.Add(_autoupload);
            flow.Controls.Add(_hotfix);
            flow.Controls.Add(_autostart);
            flow.Controls.Add(_askClose);
            var openLog = Theme.QuietButton(S("set.openlog"));
            openLog.Margin = new Padding(0, 8, 0, 0);
            openLog.Click += (_, _) =>
            {
                try { if (!SafeFs.FileExists(UploadLog.PathFor)) UploadLog.Append("info", 0, "-", "log created"); SafeFs.OpenWithShell(UploadLog.PathFor); }
                catch (Exception ex) when (ex is SafeFsException or IOException or System.ComponentModel.Win32Exception) { _error.Text = Error(ex); }
            };
            flow.Controls.Add(openLog);
            var who = settings.ContributorName;
            if (who != null || linkWithBrowser != null)
            {
                flow.Controls.Add(Spacer());
                var lbl = Theme.Label(who is null ? "" : F("set.account", who), 9.5f, Theme.Muted);
                lbl.Margin = new Padding(0, 8, 10, 0);
                var row = Row(lbl);
                // Logga in med webbläsaren först (1.0.8), sedan Koppla från.
                if (linkWithBrowser != null)
                {
                    var login = Theme.QuietButton(S("link.browser"));
                    login.Click += async (_, _) =>
                    {
                        if (await linkWithBrowser(this) is { } linked) lbl.Text = F("set.account", linked.Name);
                    };
                    login.Margin = new Padding(0, 0, 8, 0);
                    row.Controls.Add(login);
                }
                if (who != null)
                {
                    var unlink = Theme.QuietButton(S("set.unlink"));
                    unlink.Click += (_, _) =>
                    {
                        if (MessageBox.Show(this, S("set.unlink.confirm"), S("app"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                        KeyStore.Delete();
                        _settings.ContributorName = _settings.ContributorId = null;
                        _settings.Save();
                        Unlinked = true;
                        DialogResult = DialogResult.OK;
                        Close();
                    };
                    unlink.Margin = new Padding(0, 0, 8, 0);
                    row.Controls.Add(unlink);
                }
                flow.Controls.Add(row);
            }
        }
        flow.Controls.Add(_error);

        var save = Theme.GoldButton(S("ok"));
        var cancel = Theme.QuietButton(S("cancel"));
        var buttons = Row(save, cancel);
        buttons.Margin = new Padding(0, 16, 0, 0);
        flow.Controls.Add(buttons);
        Controls.Add(flow);
        AcceptButton = save;
        CancelButton = cancel;

        _autostart.Checked = Autostart.IsEnabled;
        _autoupload.Checked = settings.AutoUpload;
        _hotfix.Checked = settings.UploadHotfix;
        _askClose.Checked = !settings.DontAskOnClose;
        FillDirs(WowLocator.Locate());
        if (!string.IsNullOrEmpty(settings.GameDir)) _dir.Text = settings.GameDir;
        else if (_dir.Items.Count > 0) _dir.SelectedIndex = 0;

        browse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = S("pick.intro"), UseDescriptionForTitle = true, ShowNewFolderButton = false };
            // Startkatalogen sätts bara om den redan är godkänd (inga existenskontroller utanför WoW-katalogen).
            if (!string.IsNullOrWhiteSpace(_dir.Text) && WowLocator.IsUsableGameDir(_dir.Text)) d.InitialDirectory = _dir.Text;
            if (d.ShowDialog(this) != DialogResult.OK) return;
            // Har användaren valt "World of Warcraft"-roten? Leta upp spelkatalogen i den.
            var inside = WowLocator.FindGameDirs([d.SelectedPath]);
            _dir.Text = WowLocator.IsUsableGameDir(d.SelectedPath) ? d.SelectedPath : inside.FirstOrDefault()?.Path ?? d.SelectedPath;
        };
        find.Click += (_, _) => { FillDirs(WowLocator.Locate()); if (_dir.Items.Count > 0) _dir.SelectedIndex = 0; };
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        HandleCreated += (_, _) => Theme.DarkTitleBar(this);
        Shown += (_, _) => { if (_dir.Items.Count == 0 && string.IsNullOrEmpty(_dir.Text)) _error.Text = S("pick.none"); };
    }

    private void FillDirs(List<GameDir> dirs)
    {
        _dir.Items.Clear();
        foreach (var d in dirs) _dir.Items.Add(d.Path);
    }

    private void Save()
    {
        var dir = _dir.Text.Trim();
        if (dir.Length > 0 && !WowLocator.IsUsableGameDir(dir)) { _error.Text = S("pick.invalid"); return; }
        _settings.GameDir = dir.Length > 0 ? SafeFs.Normalize(dir) : null;
        _settings.ConfirmGameDir(); // användaren har sett och sparat katalogen här (L11)
        _settings.Language = _lang.SelectedIndex switch { 1 => "sv", 2 => "en", _ => "auto" };
        _settings.AutoUpload = _autoupload.Checked;
        _settings.UploadHotfix = _hotfix.Checked;
        _settings.DontAskOnClose = !_askClose.Checked;
        try { if (_autostart.Checked != Autostart.IsEnabled) Autostart.Set(_autostart.Checked); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { _error.Text = S("err.io"); return; }
        _settings.Save();
        DialogResult = DialogResult.OK;
        Close();
    }

    private static CheckBox Check(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Theme.Text, Font = Theme.Ui(10f), Margin = new Padding(0, 4, 0, 4), FlatStyle = FlatStyle.Standard,
    };

    private static Control Spacer() => new Panel { Height = 10, Width = 1, Margin = Padding.Empty };

    private static FlowLayoutPanel Row(params Control[] cs)
    {
        var r = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        foreach (var c in cs) { c.Margin = new Padding(0, 0, 8, 0); r.Controls.Add(c); }
        return r;
    }
}
