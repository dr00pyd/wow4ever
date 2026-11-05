using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ForeverScout.Core;
using static ForeverScout.Mac.Texts;

namespace ForeverScout.Mac;

/// <summary>Inställningar: WoW-katalog, språk, inloggning, uppladdning, stäng-frågan, logg och koppla från.</summary>
internal sealed class SettingsWindow : Window
{
    private readonly Settings _settings;
    private readonly TextBox _dir = Look.Input();
    private readonly ComboBox _found = new() { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _lang = new() { Width = 220 };
    private readonly CheckBox _autostart = Check(T("set.autostart"));
    private readonly CheckBox _autoupload = Check(T("set.autoupload"));
    private readonly CheckBox _hotfix = Check(T("set.hotfix"));
    private readonly CheckBox _askClose = Check(T("set.askclose"));
    private readonly TextBlock _error = Look.Label("", 13, Look.Err);

    /// <summary>True när användaren sparade.</summary>
    public bool Saved { get; private set; }
    public bool Unlinked { get; private set; }

    /// <param name="linkWithBrowser">"Logga in med webbläsaren" (ScoutController.LinkWithBrowser); null = ingen sådan knapp.</param>
    public SettingsWindow(Settings settings, bool pickDir, Func<Window, Task<Contributor?>>? linkWithBrowser = null)
    {
        _settings = settings;
        Look.StyleWindow(this, T("set.title"), 640, null);
        CanResize = false;

        var flow = new StackPanel { Spacing = 8, Margin = new Thickness(24) };
        flow.Children.Add(Look.Heading(T("set.title"), 22));

        // WoW-katalog
        flow.Children.Add(Look.Label(T("set.gamedir"), 13, Look.Muted));
        flow.Children.Add(_dir);
        var browse = Look.QuietButton(T("set.browse"));
        var find = Look.QuietButton(T("set.find"));
        var dirRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        dirRow.Children.Add(_found);
        Grid.SetColumn(browse, 1);
        dirRow.Children.Add(browse);
        Grid.SetColumn(find, 2);
        dirRow.Children.Add(find);
        flow.Children.Add(dirRow);

        flow.Children.Add(Spacer());
        flow.Children.Add(Look.Label(T("set.lang"), 13, Look.Muted));
        _lang.ItemsSource = new[] { T("set.lang.auto"), T("lang.sv"), T("lang.en") };
        _lang.SelectedIndex = settings.Language switch { "sv" => 1, "en" => 2, _ => 0 };
        flow.Children.Add(_lang);

        flow.Children.Add(Spacer());
        flow.Children.Add(_autoupload);
        flow.Children.Add(_hotfix);
        flow.Children.Add(_autostart);
        flow.Children.Add(_askClose);

        var openLog = Look.QuietButton(T("set.openlog"));
        openLog.Click += (_, _) =>
        {
            try { if (!SafeFs.FileExists(UploadLog.PathFor)) UploadLog.Append("info", 0, "-", "log created"); SafeFs.OpenWithShell(UploadLog.PathFor); }
            catch (Exception ex) when (ex is SafeFsException or IOException or System.ComponentModel.Win32Exception) { _error.Text = Strings.Error(ex); }
        };
        flow.Children.Add(openLog);

        var who = settings.ContributorName;
        if (who != null || linkWithBrowser != null)
        {
            flow.Children.Add(Spacer());
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            var lbl = Look.Label(who is null ? "" : TF("set.account", who), 13, Look.Muted);
            lbl.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(lbl);
            // Logga in med webbläsaren först (1.0.9), sedan Koppla från.
            if (linkWithBrowser != null)
            {
                var login = Look.QuietButton(T("link.browser"));
                login.Click += async (_, _) =>
                {
                    if (await linkWithBrowser(this) is { } linked) lbl.Text = TF("set.account", linked.Name);
                };
                row.Children.Add(login);
            }
            if (who != null)
            {
                var unlink = Look.QuietButton(T("set.unlink"));
                unlink.Click += async (_, _) =>
                {
                    if (!await Dialogs.YesNo(this, T("set.unlink.confirm"))) return;
                    KeychainStore.Delete();
                    _settings.ContributorName = _settings.ContributorId = null;
                    _settings.Save();
                    Unlinked = true;
                    Saved = true;
                    Close();
                };
                row.Children.Add(unlink);
            }
            flow.Children.Add(row);
        }

        flow.Children.Add(_error);
        var save = Look.GoldButton(T("ok"));
        var cancel = Look.QuietButton(T("cancel"));
        save.IsDefault = true;
        cancel.IsCancel = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        flow.Children.Add(buttons);
        Content = new ScrollViewer { Content = flow };

        _autostart.IsChecked = SafeFs.AutostartEnabled;
        _autoupload.IsChecked = settings.AutoUpload;
        _hotfix.IsChecked = settings.UploadHotfix;
        _askClose.IsChecked = !settings.DontAskOnClose;
        FillDirs(WowLocator.Locate());
        if (!string.IsNullOrEmpty(settings.GameDir)) _dir.Text = settings.GameDir;
        else if (_found.ItemCount > 0) _found.SelectedIndex = 0;
        _found.SelectionChanged += (_, _) => { if (_found.SelectedItem is string s) _dir.Text = s; };

        browse.Click += async (_, _) => await Browse();
        find.Click += (_, _) => { FillDirs(WowLocator.Locate()); if (_found.ItemCount > 0) _found.SelectedIndex = 0; };
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => Close();
        Opened += (_, _) =>
        {
            if (_found.ItemCount == 0 && string.IsNullOrEmpty(_dir.Text)) _error.Text = T("pick.none");
            else if (pickDir) _error.Text = T("pick.intro");
        };
    }

    private void FillDirs(List<GameDir> dirs) => _found.ItemsSource = dirs.Select(d => d.Path).ToList();

    private async Task Browse()
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = T("pick.intro"), AllowMultiple = false });
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path) return;
        // Har användaren valt "World of Warcraft"-roten? Leta upp spelkatalogen i den.
        var inside = WowLocator.FindGameDirs([path]);
        _dir.Text = WowLocator.IsUsableGameDir(path) ? path : inside.FirstOrDefault()?.Path ?? path;
    }

    private void Save()
    {
        var dir = (_dir.Text ?? "").Trim();
        if (dir.Length > 0 && !WowLocator.IsUsableGameDir(dir)) { _error.Text = T("pick.invalid"); return; }
        _settings.GameDir = dir.Length > 0 ? SafeFs.Normalize(dir) : null;
        _settings.ConfirmGameDir(); // användaren har sett och sparat katalogen här (L11)
        _settings.Language = _lang.SelectedIndex switch { 1 => "sv", 2 => "en", _ => "auto" };
        _settings.AutoUpload = _autoupload.IsChecked == true;
        _settings.UploadHotfix = _hotfix.IsChecked == true;
        _settings.DontAskOnClose = _askClose.IsChecked != true;
        try { if ((_autostart.IsChecked == true) != SafeFs.AutostartEnabled) SafeFs.SetAutostart(_autostart.IsChecked == true); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SafeFsException) { _error.Text = T("err.io"); return; }
        _settings.Save();
        Saved = true;
        Close();
    }

    private static CheckBox Check(string text) => new() { Content = text, Foreground = Look.B(Look.Text), FontSize = 14 };

    private static Control Spacer() => new Border { Height = 6 };
}
