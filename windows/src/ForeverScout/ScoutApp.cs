using System.Text.Json;
using ForeverScout.Core;
using static ForeverScout.Core.Strings;

namespace ForeverScout;

/// <summary>
/// Appen bor i aktivitetsfältet: ikon, fältmeny, filbevakning och uppladdning. Huvudfönstret (<see cref="MainForm"/>)
/// skapas bara när användaren öppnar det. Uppladdning startas när spelet sparat ForeverScout.lua (<see cref="SyncCoordinator"/>);
/// appen listar inga processer och läser inget utanför WoW-katalogen och sin egen datakatalog (<see cref="SafeFs"/>).
/// </summary>
internal sealed class ScoutApp : ApplicationContext
{
    public Settings Settings { get; }
    public ApiClient Api { get; }
    public ScoutStats Stats { get; private set; } = new();
    public TrayState State { get; private set; } = TrayState.Normal;
    public string StatusText { get; private set; } = "";
    public Color StatusColor { get; private set; } = Theme.Muted;
    public bool Uploading => _uploader.IsBusy || _uploadingUi || _simUploading;

    /// <summary>Senaste fel som visas (redan översatt), null om inget.</summary>
    public string? ErrorText => _errorText;
    /// <summary>Finns minst en ForeverScout.lua (dvs. har spelet sparat addonens data någon gång)?</summary>
    public bool HasSavedVariables { get; private set; }

    /// <summary>Scoutens lägerjournal: senaste händelserna (tid, text, färg), nyast först, högst 6.</summary>
    public IReadOnlyList<(DateTime At, string Text, Color Color)> Journal => _journal;
    private readonly List<(DateTime At, string Text, Color Color)> _journal = new();
    private bool _simUploading;

    public void Log(string text, Color color)
    {
        _journal.Insert(0, (DateTime.Now, text, color));
        if (_journal.Count > 6) _journal.RemoveAt(_journal.Count - 1);
        _form?.ShowJournal();
    }

    private int _humor;
    /// <summary>Ett litet skämt – bara efter att servern bekräftat, aldrig i stället för fel.</summary>
    private string Humor() => S("humor." + (1 + (_humor++ % 3)));

    private readonly Uploader _uploader;
    private readonly SyncCoordinator _sync;
    private string? _gameRoot; // verklig sökväg, godkänd av SafeFs
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _trayMenu;
    private readonly ToolStripMenuItem _miOpen, _miUpload, _miStatus, _miLogin, _miPause, _miPayload, _miUpdate, _miGuide, _miSettings, _miAbout, _miExit;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 15_000 };
    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 350 };
    private readonly Control _ui = new();
    private MainForm? _form;
    private int _frame;
    private bool _uploadingUi;
    private bool _exiting;
    private string _statsStamp = "";
    private string? _errorText;

    public ScoutApp(Settings settings, ApiClient api)
    {
        Settings = settings;
        Api = api;
        _uploader = new Uploader(api, settings);
        _ui.CreateControl();
        _ = _ui.Handle;

        _trayMenu = DarkMenuRenderer.NewMenu();
        _miOpen = Item(S("menu.open"), (_, _) => ShowWindow());
        _miOpen.Font = new Font(_trayMenu.Font, FontStyle.Bold);
        _miUpload = Item(S("menu.upload"), async (_, _) => await UploadNow(quietIfNothing: false));
        _miStatus = Item("", null);
        _miStatus.Enabled = false;
        _miLogin = Item(S("menu.login"), null);
        _miLogin.Enabled = false; // kommer snart
        _miPause = Item(S("menu.pause"), (_, _) => { Settings.AutoUploadPaused = _miPause!.Checked; Settings.Save(); UpdateStatus(); });
        _miPause.CheckOnClick = true;
        _miPayload = Item(S("menu.payload"), (_, _) => ShowPayload());
        _miUpdate = Item(S("menu.update"), async (_, _) => await CheckUpdates(interactive: true));
        _miGuide = Item(S("menu.guide"), (_, _) => Links.Open(Api, Links.Guide(Api)));
        _miAbout = Item(S("menu.about"), (_, _) => ShowAbout());
        _miSettings = Item(S("menu.settings"), (_, _) => OpenSettings(pickDir: false));
        _miExit = Item(S("menu.exit"), (_, _) => Exit());
        _trayMenu.Items.AddRange([_miOpen, _miUpload, _miStatus, new ToolStripSeparator(), _miLogin, _miPause, _miPayload, _miUpdate, _miGuide, _miSettings, _miAbout, new ToolStripSeparator(), _miExit]);
        _trayMenu.Opening += (_, _) => UpdateMenu();

        _tray = new NotifyIcon { Icon = TrayIcons.Normal, Text = "Forever Scout", Visible = true, ContextMenuStrip = _trayMenu };
        _tray.DoubleClick += (_, _) => ShowWindow();
        _tray.BalloonTipClicked += (_, _) => ShowWindow();

        _sync = new SyncCoordinator(
            () => CurrentDir is { } d ? WowLocator.SavedVariableFiles(d) : [],
            async ct =>
            {
                if (!AutoUploadActive || string.IsNullOrEmpty(Api.Key)) return;
                var done = new TaskCompletionSource();
                OnUi(async () => { try { await UploadNow(quietIfNothing: true); } finally { done.TrySetResult(); } });
                await done.Task.WaitAsync(ct);
            });
        _tick.Tick += async (_, _) => await OnTick();
        _anim.Tick += (_, _) => { _frame = (_frame + 1) % TrayIcons.Uploading.Length; _tray.Icon = TrayIcons.Uploading[_frame]; };
    }

    private static ToolStripMenuItem Item(string text, EventHandler? click)
    {
        var i = new ToolStripMenuItem(text) { Padding = new Padding(4, 3, 4, 3) };
        if (click != null) i.Click += click;
        return i;
    }

    public void OnUi(Action a)
    {
        try { if (!_ui.IsDisposed) _ui.BeginInvoke(a); } catch (InvalidOperationException) { }
    }

    // ------------------------------------------------------------------ start

    /// <param name="showWindow">Visa fönstret direkt (efter första starten). Annars bara ikonen i fältet.</param>
    public async Task StartAsync(bool showWindow)
    {
        if (showWindow) ShowWindow();
        RefreshAutostartPath();
        EnsureGameDir();
        ApplyGameDir();
        InstallBundledAddonIfNeeded();
        RefreshStats(force: true);
        UpdateStatus();
        _tick.Start();
        await RefreshMe();
        await CheckUpdates(interactive: false); // signerad addon + spärrlistan (tyst)
        _sync.Notify(); // ny data sedan förra gången? (samma väg som filändringar – inga dubbletter)
    }

    private bool AutoUploadActive => Settings.AutoUpload && !Settings.AutoUploadPaused;

    /// <summary>"Starta med Windows" på: peka Run-värdet på den exe som körs nu (alltid den senaste filen).</summary>
    private static void RefreshAutostartPath()
    {
        try { if (SafeFs.AutostartEnabled) SafeFs.SetAutostart(true); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or SafeFsException) { }
    }

    /// <summary>"Om Forever Scout": version, variant och var programmet ligger.</summary>
    public void ShowAbout()
    {
        var text = F("about.text", SelfUpdater.CurrentVersion.ToString(3), S(BuildInfo.IsLite ? "about.variant.lite" : "about.variant.full"), Api.BaseUri.Host);
        MessageBox.Show(_form is { Visible: true } ? _form : null, text, S("about.title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    public GameDir? CurrentDir => _gameRoot is null ? null : new GameDir(_gameRoot, Path.GetFileName(_gameRoot));

    /// <summary>Godkänner vald spelkatalog i SafeFs (allt annat nekas) och börjar bevaka WTF\Account.</summary>
    public void ApplyGameDir()
    {
        _gameRoot = null;
        try
        {
            if (!string.IsNullOrEmpty(Settings.GameDir) && WowLocator.IsUsableGameDir(Settings.GameDir))
                _gameRoot = SafeFs.SetGameDir(Settings.GameDir);
            else SafeFs.SetGameDir(null);
        }
        catch (SafeFsException) { SafeFs.SetGameDir(null); }
        _sync.Watch(CurrentDir?.AccountDir);
    }

    private void EnsureGameDir()
    {
        if (!string.IsNullOrEmpty(Settings.GameDir) && WowLocator.IsUsableGameDir(Settings.GameDir)) return;
        var best = WowLocator.Locate().Where(d => WowLocator.Rank(d) == 0).ToList();
        if (best.Count == 1)
        {
            Settings.GameDir = best[0].Path;
            Settings.Save();
            return;
        }
        // Flera eller ingen: fråga bara om ingen katalog någonsin valts (annars visas felet i status).
        if (string.IsNullOrEmpty(Settings.GameDir)) OnUi(() => OpenSettings(pickDir: true));
    }

    private void InstallBundledAddonIfNeeded()
    {
        if (CurrentDir is not { } dir) return;
        try
        {
            var bundled = AddonBundle.Version;
            if (Settings.BlockedAddons.Contains(bundled ?? "")) return; // spärrad: väntar på signerad addon
            if (AddonInstaller.NeedsInstall(dir, AddonBundle.Files))
            {
                AddonInstaller.Install(dir, AddonBundle.Files);
                Toast("Forever Scout", F("addon.installed", bundled));
                Log(F("journal.addon", bundled), Theme.Ok);
            }
        }
        catch (Exception ex) when (ex is ScoutException or IOException or UnauthorizedAccessException) { SetError(Error(ex)); }
    }

    // ------------------------------------------------------------------ data

    public void RefreshStats(bool force)
    {
        if (CurrentDir is not { } dir) { Stats = new ScoutStats(); HasSavedVariables = false; _form?.ShowStats(); return; }
        var sv = WowLocator.SavedVariableFiles(dir);
        HasSavedVariables = sv.Count > 0;
        var hf = Settings.UploadHotfix ? WowLocator.HotfixFiles(dir) : [];
        string stamp = FileStability.Snapshot(sv.Concat(hf).ToList())
                       + "|" + string.Join(",", Settings.UploadedSeq.Values) + "|" + string.Join(",", Settings.HotfixSha.Values);
        if (!force && stamp == _statsStamp) return;
        _statsStamp = stamp;

        var stats = new ScoutStats();
        foreach (var f in sv) stats.AddFile(f, Settings.UploadedSeq.TryGetValue(Privacy.FileKey(f), out var q) ? q : 0);
        foreach (var f in hf)
        {
            try
            {
                long len = SafeFs.Stat(f)?.Length ?? 0;
                bool sent = Settings.HotfixSha.TryGetValue(Privacy.FileKey(f), out var sha) && sha == ApiClient.Sha256Hex(f);
                stats.AddHotfix(len, sent);
            }
            catch (IOException) { }
            catch (SafeFsException) { }
        }
        Stats = stats;
        _form?.ShowStats();
    }

    public async Task RefreshMe()
    {
        if (string.IsNullOrEmpty(Api.Key)) return;
        try
        {
            var me = await Api.MeAsync();
            if (me.Points is int p) Settings.Points = p;
            if (me.Rank is int r) Settings.Rank = r;
            if (me.Name is { Length: > 0 } n) Settings.ContributorName = n;
            Settings.AcceptedTotal = me.AcceptedTotal();
            Settings.ConfirmedTotal = me.Confirmed; // null tills servern skickar fältet "confirmed" → visas som "–"
            if (me.Recent is { Count: > 0 } recent && Settings.LastConfirmed.Count == 0)
                Settings.LastConfirmed = recent.Select(ToConfirmed).OfType<ConfirmedItem>().Take(5).ToList();
            Settings.Save();
            _form?.ShowContribution();
            UpdateStatus();
        }
        catch (ApiException) { /* offline – visa det vi har */ }
    }

    private static ConfirmedItem? ToConfirmed(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        string? Get(string n) => e.TryGetProperty(n, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;
        var name = Get("name");
        if (string.IsNullOrEmpty(name)) return null;
        return new ConfirmedItem
        {
            Name = name, Kind = Get("kind") ?? "", Id = Get("id") ?? "",
            Url = e.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null,
        };
    }

    // ------------------------------------------------------------------ status

    private void SetError(string text)
    {
        _errorText = text;
        UpdateStatus();
    }

    /// <summary>Uppdaterar statusraden, fältikonen och verktygstipset.</summary>
    public void UpdateStatus()
    {
        if (Uploading) { State = TrayState.Uploading; StatusText = S("uploading"); StatusColor = Theme.Gold; }
        else if (_errorText != null) { State = TrayState.Error; StatusText = _errorText; StatusColor = Theme.Err; }
        else if (Settings.NextRetryUtc is { } next) { State = TrayState.Error; StatusText = F("retry", When(next)); StatusColor = Theme.Warn; }
        else
        {
            State = TrayState.Normal;
            StatusText = CurrentDir is null ? S("game.none")
                : Settings.AutoUploadPaused ? S("paused")
                : Settings.LastUploadUtc is { } t ? F("last", When(t)) : S("never");
            StatusColor = CurrentDir is null || Settings.AutoUploadPaused ? Theme.Warn : Theme.Muted;
        }

        if (State == TrayState.Uploading) { if (!_anim.Enabled) _anim.Start(); }
        else
        {
            _anim.Stop();
            _tray.Icon = State == TrayState.Error ? TrayIcons.Error : TrayIcons.Normal;
        }
        var tip = "Forever Scout";
        if (Settings.Points is int p) tip += " · " + F("tray.tooltip.points", p.ToString("N0", Culture));
        tip += "\n" + StatusText;
        _tray.Text = tip.Length > 127 ? tip[..126] + "…" : tip;
        _form?.ShowStatus();
    }

    private void UpdateMenu()
    {
        _miUpload.Enabled = !Uploading && CurrentDir != null && !string.IsNullOrEmpty(Api.Key);
        _miStatus.Text = StatusText;
        _miPause.Checked = Settings.AutoUploadPaused;
    }

    // ------------------------------------------------------------------ händelser

    private async Task OnTick()
    {
        RefreshStats(force: false);
        UpdateStatus();
        if (_uploader.RetryDue && AutoUploadActive) _sync.Notify();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Laddar upp det som ändrats. Uploader köar körningar (aldrig två samtidigt) och skickar bara filer vars sha256
    /// skiljer sig från förra lyckade uppladdningen – både knappen och filbevakningen går hit.
    /// </summary>
    public async Task UploadNow(bool quietIfNothing = false)
    {
        if (CurrentDir is not { } dir) { OpenSettings(pickDir: true); return; }
        if (string.IsNullOrEmpty(Api.Key)) return;
        _uploadingUi = true;
        UpdateStatus();
        SyncReport report;
        try { report = await Task.Run(() => _uploader.SyncAsync(dir, force: false)); }
        finally { _uploadingUi = false; }

        _errorText = report.Error is { } err && !report.WillRetry ? Error(err) : null;
        var now = When(DateTime.UtcNow);
        if (report.Error is { } e2) Log(F("journal.error", now, Error(e2)), report.WillRetry ? Theme.Warn : Theme.Err);
        if (report.FilesUploaded > 0) Log(F("journal.uploaded", now, report.FilesUploaded) + " – " + Humor(), Theme.Ok);
        if (report.HotfixStored > 0) Log(F("journal.hotfix", now), Theme.Ok);
        if (report.NothingToDo && !quietIfNothing) Log(F("journal.nothing", now), Theme.Muted);
        if (report.FilesUploaded > 0 || report.HotfixStored > 0)
            Toast(null, F("done", report.FilesUploaded, report.HotfixStored), onlyIfHidden: true);
        else if (report.NothingToDo && !quietIfNothing)
            _form?.Flash(S("nothing"));

        if (report.NewConfirmed.Count > 0)
        {
            var first = report.NewConfirmed[0].Name;
            var what = report.NewConfirmed.Count == 1 ? first : F("toast.more", first, report.NewConfirmed.Count - 1);
            Toast(S("toast.title"), F("toast.body", what));
        }
        if (report.AddonBlocked) await UpdateAddon(force: true, interactive: false, announceBlocked: true);

        RefreshStats(force: true);
        _form?.ShowContribution();
        UpdateStatus();
        if (report.FilesUploaded > 0) await RefreshMe();
    }

    // ------------------------------------------------------------------ uppdateringar (signerade)

    /// <summary>Hämtar signerad versionsinfo; uppdaterar addonen vid behov, och (interaktivt) klienten.</summary>
    public async Task CheckUpdates(bool interactive)
    {
        try
        {
            var release = await Api.VersionAsync(UpdateKeys.PublicKeyPem);
            Settings.BlockedAddons = release.BlockedAddons.ToList();
            Settings.Save();
            await UpdateAddon(release, force: false, interactive, announceBlocked: false);
            // Efter en versionskontroll: försök igen med filer som servern tidigare avvisade (servern kan ha rättats).
            // Oförändrade, redan uppladdade filer skickas inte (sha256), så detta kostar inget annars.
            _sync.Notify();
            if (!interactive) return;

            // Varje variant uppdaterar sig från sin egen fil: full → clientFile, lite → liteFile.
            var kind = BuildInfo.UpdateKind;
            var newVersion = release.VersionOf(kind);
            if (release.File(kind) is null || !SelfUpdater.IsNewer(newVersion))
            {
                MessageBox.Show(_form, F("update.none", SelfUpdater.CurrentVersion.ToString(3)), S("app"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            byte[] exe;
            try { exe = await Api.DownloadReleaseFileAsync(release, kind, UpdateKeys.PublicKeyPem, 400L * 1024 * 1024); }
            catch (Exception ex) when (ex is ScoutException or IOException or UnauthorizedAccessException) { throw new UpdateStepException(UpdateSteps.Download, ex, null); }
            // Stage och Swap gör blockerande omförsök (upp till ~15 s per steg) – kör dem utanför UI-tråden.
            await Task.Run(() => SelfUpdater.Stage(exe));
            if (MessageBox.Show(_form, F("update.ready", newVersion), S("app"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                // Markör för den nya versionen: visa fönstret och "Uppdaterad till …" efter omstarten.
                Settings.JustUpdatedFrom = SelfUpdater.CurrentVersion.ToString(3);
                Settings.Save();
                try { await Task.Run(() => SelfUpdater.Swap()); }
                catch (UpdateStepException) { Settings.JustUpdatedFrom = null; Settings.Save(); throw; }
                UploadLog.Append("update", exe.Length, ApiClient.Sha256Hex(exe), $"ok swapped to {newVersion}");
                Settings.UpdateFailures = 0;
                Settings.Save();
                // Avsluta bara om den nya processen verkligen startade och lever efter ~2 s (den väntar då på mutexen).
                if (!await StartNewInstanceVerifiedAsync())
                    throw new UpdateStepException(UpdateSteps.Restart, new InvalidOperationException("new process did not start"), Path.GetFileName(SelfUpdater.CurrentExe));
                Exit();
            }
        }
        catch (UpdateStepException ux)
        {
            // Diagnostik: steg, typ, HResult och bara filnamnet – till uploads.log och lägerjournalen.
            UploadLog.Append("update", 0, "-", "fail " + ux.Diagnostic);
            Log(F("journal.updatefail", When(DateTime.UtcNow), ux.Diagnostic), Theme.Err);
            Settings.UpdateFailures++;
            Settings.Save();
            if (!interactive) return;
            var msg = ux.Step switch
            {
                UpdateSteps.Download => F("update.download", Error(ux.InnerException!)),
                UpdateSteps.Restart => S("update.notstarted"),
                _ when ux.FileInUse => S("update.inuse"),
                _ => F("update.fail", ux.Diagnostic),
            };
            if (Settings.UpdateFailures >= 2)
            {
                if (MessageBox.Show(_form, msg + "\n\n" + S("update.openpage"), S("app"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    Links.Open(Api, Links.Download(Api));
            }
            else MessageBox.Show(_form, msg, S("app"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex) when (ex is ScoutException or IOException or UnauthorizedAccessException)
        {
            if (interactive) MessageBox.Show(_form, F("update.fail", Error(ex)), S("app"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            // Tyst kontroll: nät-/serverfel visas inte som fel (uppladdningen har egen status).
        }
    }

    private async Task UpdateAddon(bool force, bool interactive, bool announceBlocked)
    {
        try
        {
            var release = await Api.VersionAsync(UpdateKeys.PublicKeyPem);
            Settings.BlockedAddons = release.BlockedAddons.ToList();
            Settings.Save();
            await UpdateAddon(release, force, interactive, announceBlocked);
        }
        catch (Exception ex) when (ex is ScoutException or IOException or UnauthorizedAccessException)
        {
            SetError(Error(ex));
        }
    }

    /// <summary>
    /// Installerar den signerade addonen om den installerade är spärrad, äldre, saknas – eller om servern svarat
    /// addon-blocked (<paramref name="force"/>). Bara signerade zip-filer installeras.
    /// </summary>
    private async Task UpdateAddon(SignedRelease release, bool force, bool interactive, bool announceBlocked)
    {
        if (CurrentDir is not { } dir || release.AddonFile is null) return;
        var installed = AddonInstaller.InstalledVersion(dir);
        bool blocked = installed != null && release.IsAddonBlocked(installed);
        bool newer = AddonInstaller.CompareVersions(release.Addon, installed) > 0;
        bool differs = !string.Equals(release.Addon, installed, StringComparison.Ordinal);
        if (!(installed is null || blocked || newer || (force && differs))) return;
        if (release.IsAddonBlocked(release.Addon)) return; // släppet självt är spärrat – installera inte
        var zip = await Api.DownloadReleaseFileAsync(release, ReleaseKind.Addon, UpdateKeys.PublicKeyPem, 20 * 1024 * 1024);
        AddonInstaller.Install(dir, AddonInstaller.FromZip(zip));
        _errorText = null;
        var msg = announceBlocked ? F("addon.blocked", release.Addon) : F("update.addon", release.Addon);
        if (interactive) MessageBox.Show(_form, msg, S("app"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        else Toast("Forever Scout", msg);
        _form?.ShowStatus();
    }

    // ------------------------------------------------------------------ fönster

    public void ShowWindow()
    {
        if (_form is null || _form.IsDisposed) _form = new MainForm(this);
        _form.ShowFromTray();
    }

    public MainForm? Window => _form;

    public void OpenSettings(bool pickDir)
    {
        var oldLang = Settings.Language;
        using var f = new SettingsForm(Settings, pickDir);
        var owner = _form is { Visible: true } ? _form : null;
        if ((owner is null ? f.ShowDialog() : f.ShowDialog(owner)) != DialogResult.OK) return;
        if (f.Unlinked || oldLang != Settings.Language) { Restart(); return; }
        ApplyGameDir();
        InstallBundledAddonIfNeeded();
        RefreshStats(force: true);
        UpdateStatus();
    }

    /// <summary>Engångstips när fönstret göms första gången.</summary>
    public void OnHiddenToTray()
    {
        if (Settings.TrayHintShown) return;
        Settings.TrayHintShown = true;
        Settings.Save();
        _tray.ShowBalloonTip(6000, S("tray.hint"), S("tray.hint.body"), ToolTipIcon.Info);
    }

    public void Toast(string? title, string text, bool onlyIfHidden = false)
    {
        if (onlyIfHidden && _form is { Visible: true }) { _form.Flash(text); return; }
        _tray.ShowBalloonTip(8000, title ?? "Forever Scout", text, ToolTipIcon.Info);
    }

    /// <summary>"Visa vad som skickas": exakt innehåll i nästa/senaste uppladdning.</summary>
    public void ShowPayload()
    {
        var text = UploadPreview.Build(Api, CurrentDir, Settings);
        using var f = new PayloadForm(text);
        if (_form is { Visible: true }) f.ShowDialog(_form); else f.ShowDialog();
    }

    // ------------------------------------------------------------------ skärmbilder (bara --screenshots)

    internal void SimulateUploading(bool on) { _simUploading = on; UpdateStatus(); _form?.ShowStats(); }
    internal void SimulateError(string? text) { _errorText = text; if (text != null) Log(F("journal.error", When(DateTime.UtcNow), text), Theme.Err); UpdateStatus(); _form?.ShowStats(); }
    internal void SimulateEmpty() { Stats = new ScoutStats(); HasSavedVariables = false; Settings.Points = null; Settings.Rank = null; Settings.AcceptedTotal = null; Settings.LastConfirmed = new(); Settings.LastUploadUtc = null; _journal.Clear(); _form?.ShowStats(); _form?.ShowContribution(); _form?.ShowJournal(); UpdateStatus(); }

    public ContextMenuStrip TrayMenu { get { UpdateMenu(); return _trayMenu; } }

    public void Restart()
    {
        StartNewInstance();
        Exit();
    }

    /// <summary>
    /// Startar exe:n på samma sökväg (efter Swap är det den nya filen) med --restarted: den nya instansen väntar då
    /// upp till 10 s på mutexen tills den här processen avslutats (Exit → Application.Run returnerar → mutexen släpps).
    /// </summary>
    /// <summary>Startar den nya exe:n och kontrollerar att processen fortfarande lever efter ~2 s.</summary>
    private static async Task<bool> StartNewInstanceVerifiedAsync()
    {
        System.Diagnostics.Process? p;
        try { p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SelfUpdater.CurrentExe) { UseShellExecute = false, Arguments = "--restarted" }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ScoutException) { return false; }
        if (p is null) return false;
        using (p)
        {
            bool exited = await Task.Run(() => p.WaitForExit(2000));
            if (exited) UploadLog.Append("update", 0, "-", $"fail step=restart new process exited code={p.ExitCode}");
            return !exited;
        }
    }

    /// <summary>
    /// Efter en uppdatering: fönstret fram, "Uppdaterad till …" i fönstret och som ballong, och en rad i lägerjournalen
    /// och uploads.log. <paramref name="from"/> = den gamla versionen (null om okänd, t.ex. uppdaterad från ≤ 1.0.5).
    /// </summary>
    public void AnnounceUpdate(string? from)
    {
        var to = SelfUpdater.CurrentVersion.ToString(3);
        ShowWindow();
        var now = When(DateTime.UtcNow);
        Log(from is null ? F("journal.updated.to", now, to) : F("journal.updated", now, from, to), Theme.Ok);
        UploadLog.Append("update", 0, "-", from is null ? $"updated to {to}" : $"updated from {from} to {to}");
        _form?.Flash(F("updated.to", to), 15000);
        _tray.ShowBalloonTip(8000, "Forever Scout", F("updated.to", to), ToolTipIcon.Info);
    }

    private static bool StartNewInstance()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SelfUpdater.CurrentExe) { UseShellExecute = false, Arguments = "--restarted" });
            return p != null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ScoutException) { return false; }
    }

    public bool IsExiting => _exiting;

    /// <summary>Avsluta utan fråga (fältmenyn, Stäng-valet, Windows-avstängning).</summary>
    public void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        _tick.Stop();
        _anim.Stop();
        _sync.Dispose();
        _tray.Visible = false;
        if (_form is { IsDisposed: false }) _form.Close();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tick.Dispose(); _anim.Dispose(); _sync.Dispose(); _tray.Dispose(); _trayMenu.Dispose(); _ui.Dispose();
        }
        base.Dispose(disposing);
    }
}
