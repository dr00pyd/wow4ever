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
    /// <summary>Fel att visa: uppladdningens, annars addonens (1.0.12: addonens fel raderas inte av nästa synk).</summary>
    public string? ErrorText => _errorText ?? _addonError;
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
        Live?.ShowJournal();
    }

    private int _humor;
    /// <summary>Ett litet skämt – bara efter att servern bekräftat, aldrig i stället för fel.</summary>
    private string Humor() => S("humor." + (1 + (_humor++ % 3)));

    private readonly Uploader _uploader;
    private readonly SyncCoordinator _sync;
    private string? _gameRoot; // verklig sökväg, godkänd av SafeFs
    private readonly NotifyIcon _tray;
    /// <summary>Senaste bubblan var "Version x finns" (klick → uppdatera).</summary>
    private bool _updateOffered;
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
    /// <summary>1.0.12: addonen kunde inte installeras eller väntar på en signerad version. Lever tills addonen är på plats.</summary>
    private string? _addonError;

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
        _miLogin = Item(S("menu.login"), async (_, _) => await LinkWithBrowser(null));
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
        // Klick på "Version x finns" startar uppdateringen; andra bubblor visar fönstret.
        _tray.BalloonTipClicked += async (_, _) =>
        {
            if (_updateOffered) { _updateOffered = false; await CheckUpdates(interactive: true); }
            else ShowWindow();
        };

        _sync = new SyncCoordinator(
            () => CurrentDir is { } d ? [.. WowLocator.SavedVariableFiles(d), .. WowLocator.AuctionFiles(d).Select(a => a.Path)] : [],
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
    /// <param name="userStarted">Användaren startade appen själv (inte autostart, inte omstart efter uppdatering). Bara då
    /// får appen fråga något i en dialog – aldrig över spelet efter en tyst uppdatering (1.0.12).</param>
    public async Task StartAsync(bool showWindow, bool userStarted = false)
    {
        if (showWindow) ShowWindow();
        RefreshAutostartPath();
        if (userStarted) AskAutostartOnce();
        EnsureGameDir(interactive: showWindow || userStarted);
        ApplyGameDir();
        InstallBundledAddonIfNeeded();
        RefreshStats(force: true);
        UpdateStatus();
        _tick.Start();
        await RefreshMe();
        await CheckUpdates(interactive: false); // signerad addon + spärrlistan (tyst)
        _sync.Notify(); // ny data sedan förra gången? (samma väg som filändringar – inga dubbletter)
        if (Live is not { Visible: true }) MemoryTrim.Trim(); // starten är klar – allt som bara behövdes då släpps
    }

    public bool AutoUploadActive => Settings.AutoUpload && !Settings.AutoUploadPaused;

    /// <summary>
    /// 1.0.12: autostart var av som standard och nämndes bara i Inställningar – den som startade om datorn laddade inte
    /// upp något, utan besked. Nu frågas det på välkomstskärmen, och befintliga användare får frågan en gång.
    /// </summary>
    private void AskAutostartOnce()
    {
        if (Settings.AutostartAsked) return;
        Settings.AutostartAsked = true;
        try { Settings.Save(); } catch { }
        try
        {
            if (Autostart.IsEnabled) return;
            var answer = Message(S("autostart.ask"), S("autostart.ask.title"), MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer == DialogResult.Yes) Autostart.Set(true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or SafeFsException) { }
    }

    /// <summary>"Starta med Windows" på: peka Run-värdet på den exe som körs nu (alltid den senaste filen).</summary>
    private static void RefreshAutostartPath()
    {
        if (BuildInfo.IsStore) return; // startup task pekar alltid på paketets exe
        try { if (SafeFs.AutostartEnabled) SafeFs.SetAutostart(true); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or SafeFsException) { }
    }

    /// <summary>"Om Forever Scout": version, variant och var programmet ligger.</summary>
    public void ShowAbout()
    {
        var text = F("about.text", SelfUpdater.CurrentVersion.ToString(3), S(BuildInfo.IsLite ? "about.variant.lite" : "about.variant.full"), Api.BaseUri.Host);
        Message(text, S("about.title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    public GameDir? CurrentDir => _gameRoot is null ? null : new GameDir(_gameRoot, Path.GetFileName(_gameRoot));

    /// <summary>
    /// Godkänner vald spelkatalog i SafeFs (allt annat nekas) och börjar bevaka WTF\Account. Bara en katalog som
    /// användaren valt eller bekräftat används (L11).
    /// </summary>
    public void ApplyGameDir()
    {
        _gameRoot = null;
        SafeFs.AuctionFilesAllowed = Settings.ShareAuctionPrices; // av som standard: då nekas auktionsaddonernas filer
        try
        {
            if (!string.IsNullOrEmpty(Settings.GameDir) && Settings.IsGameDirConfirmed() && WowLocator.IsUsableGameDir(Settings.GameDir))
                _gameRoot = SafeFs.SetGameDir(Settings.GameDir);
            else SafeFs.SetGameDir(null);
        }
        catch (SafeFsException) { SafeFs.SetGameDir(null); }
        _sync.Watch(CurrentDir?.AccountDir);
    }

    /// <param name="interactive">Användaren startade appen (fönstret visas). Vid autostart (bara ikonen) öppnas ingen
    /// dialog över skrivbordet – en bubbla säger vad som behövs, och fönstret har en knapp för det (1.0.12).</param>
    private void EnsureGameDir(bool interactive)
    {
        WowLocator.StartStep step;
        string? dir;
        try { (step, dir) = WowLocator.NextStep(Settings, WowLocator.IsUsableGameDir, () => WowLocator.Locate()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { (step, dir) = (WowLocator.StartStep.Pick, null); }
        switch (step)
        {
            case WowLocator.StartStep.Confirm:
                ConfirmFoundGameDir(dir!);
                break;
            case WowLocator.StartStep.Pick:
                if (interactive) OnUi(() => OpenSettings(pickDir: true));
                else Toast("Forever Scout", S("gamedir.toast"));
                break;
        }
    }

    /// <summary>
    /// L11: en automatiskt hittad (eller av ≤ 1.0.6 sparad) katalog används först när användaren sagt ja – en gång.
    /// Nej: katalogen föreslås inte igen och användaren får välja själv.
    /// </summary>
    private void ConfirmFoundGameDir(string dir)
    {
        var answer = Message(F("gamedir.confirm", dir), S("gamedir.confirm.title"), MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer == DialogResult.Yes)
        {
            Settings.GameDir = dir;
            Settings.ConfirmGameDir();
            Settings.Save();
            return;
        }
        Settings.GameDirDeclined = dir;
        Settings.GameDir = null;
        Settings.Save();
        OnUi(() => OpenSettings(pickDir: true));
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
        catch (Exception ex) when (ex is ScoutException or IOException or UnauthorizedAccessException) { SetAddonError(Error(ex)); return; }
        if (AddonInstaller.InstalledVersion(dir) != null && _addonError != null && !Settings.BlockedAddons.Contains(AddonInstaller.InstalledVersion(dir) ?? "")) SetAddonError(null);
    }

    private void SetAddonError(string? text)
    {
        _addonError = text;
        UpdateStatus();
    }

    // ------------------------------------------------------------------ data

    public void RefreshStats(bool force)
    {
        if (CurrentDir is not { } dir) { Stats = new ScoutStats(); HasSavedVariables = false; Live?.ShowStats(); return; }
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
        Live?.ShowStats();
    }

    /// <summary>Poängen ändras också när andra bekräftar ens fynd – hämta därför med jämna mellanrum, inte bara efter egna uppladdningar.</summary>
    private static readonly TimeSpan MeEvery = TimeSpan.FromMinutes(10);
    private DateTime _meAt = DateTime.MinValue;

    public async Task RefreshMe()
    {
        if (string.IsNullOrEmpty(Api.Key)) return;
        _meAt = DateTime.UtcNow;
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
            Live?.ShowContribution();
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
        else if (ErrorText is { } errText) { State = TrayState.Error; StatusText = errText; StatusColor = Theme.Err; }
        else if (Settings.NextRetryUtc is { } next) { State = TrayState.Error; StatusText = F("retry", When(next)); StatusColor = Theme.Warn; }
        else
        {
            State = TrayState.Normal;
            StatusText = CurrentDir is null ? S("game.none")
                : Settings.AutoUploadPaused ? S("paused")
                : !Settings.AutoUpload ? S("auto.off")
                : Settings.LastUploadUtc is { } t ? F("last", When(t)) : S("never");
            StatusColor = CurrentDir is null || !AutoUploadActive ? Theme.Warn : Theme.Muted;
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
        Live?.ShowStatus();
    }

    private void UpdateMenu()
    {
        _miUpload.Enabled = !Uploading && CurrentDir != null && !string.IsNullOrEmpty(Api.Key);
        _miStatus.Text = StatusText;
        _miPause.Checked = Settings.AutoUploadPaused;
    }

    // ------------------------------------------------------------------ händelser

    private DateTime _trimAt = DateTime.MinValue;
    /// <summary>I fältet: lämna tillbaka oanvända sidor högst så ofta (billigt – ingen GC, bara arbetsmängden).</summary>
    private static readonly TimeSpan TrimEvery = TimeSpan.FromMinutes(1);

    private async Task OnTick()
    {
        RefreshStats(force: false);
        UpdateStatus();
        if (_uploader.RetryDue && AutoUploadActive) _sync.Notify();
        if (DateTime.UtcNow - _meAt > MeEvery) await RefreshMe();
        if (DateTime.UtcNow - _versionAt > VersionEvery) await CheckUpdates(interactive: false);
        if (Live is not { Visible: true } && !_uploadingUi && DateTime.UtcNow - _trimAt > TrimEvery)
        {
            _trimAt = DateTime.UtcNow;
            MemoryTrim.Release();
        }
    }

    /// <summary>
    /// Laddar upp det som ändrats. Uploader köar körningar (aldrig två samtidigt) och skickar bara filer vars sha256
    /// skiljer sig från förra lyckade uppladdningen – både knappen och filbevakningen går hit.
    /// </summary>
    public async Task UploadNow(bool quietIfNothing = false)
    {
        // 1.0.12: bakgrundssynken (quietIfNothing) öppnar aldrig en dialog – förr dök "Välj WoW-katalog" upp vid varje
        // autostart och vid omförsök. Bara knappen, menyn och F5 öppnar valet av katalog.
        if (CurrentDir is not { } dir) { if (!quietIfNothing) OpenSettings(pickDir: true); return; }
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
        if (report.CreatureCacheStored > 0) Log(F("journal.creaturecache", now, report.CreatureCount), Theme.Ok);
        if (report.AuctionRows > 0) Log(F("journal.auctions", now, report.AuctionRows), Theme.Ok);
        if (report.NothingToDo && !quietIfNothing) Log(F("journal.nothing", now), Theme.Muted);
        if (report.FilesUploaded > 0 || report.HotfixStored > 0)
            Toast(null, F("done", report.FilesUploaded, report.HotfixStored), onlyIfHidden: true);
        else if (report.NothingToDo && !quietIfNothing)
            Live?.Flash(S("nothing"));

        if (report.NewConfirmed.Count > 0)
        {
            var first = report.NewConfirmed[0].Name;
            var what = report.NewConfirmed.Count == 1 ? first : F("toast.more", first, report.NewConfirmed.Count - 1);
            Toast(S("toast.title"), F("toast.body", what));
        }
        if (report.AddonBlocked) await UpdateAddonAfterBlock();

        RefreshStats(force: true);
        Live?.ShowContribution();
        UpdateStatus();
        if (report.FilesUploaded > 0) await RefreshMe();
        // Parsern och uppladdningen lämnar skräp på heapen; i fältet syns det bara som minne. Städa när fönstret är dolt.
        if (Live is not { Visible: true }) MemoryTrim.Trim();
    }

    // ------------------------------------------------------------------ uppdateringar (signerade)

    /// <summary>1.0.10: versionsinfon hämtas vid start och sedan med detta intervall (appen ligger ofta i fältet i dagar).</summary>
    private static readonly TimeSpan VersionEvery = TimeSpan.FromHours(3);
    /// <summary>Efter så många misslyckade självuppdateringar i rad slutar appen försöka själv och erbjuder i stället uppdateringen.</summary>
    private const int AutoUpdateMaxFailures = 3;
    private DateTime _versionAt = DateTime.MinValue;
    private bool _checking;

    /// <summary>
    /// Hämtar signerad versionsinfo; uppdaterar addonen vid behov och klienten. 1.0.10: en nyare signerad klient
    /// installeras utan fråga (tyst kontroll) – aldrig mitt i en uppladdning, och efter <see cref="AutoUpdateMaxFailures"/>
    /// misslyckanden i rad bara som erbjudande (bubbla och meny). Interaktivt (menyn) frågar appen som förut.
    /// </summary>
    public async Task CheckUpdates(bool interactive)
    {
        if (_checking) return;
        _checking = true;
        _versionAt = DateTime.UtcNow;
        try { await CheckUpdatesCore(interactive); if (Live is not { Visible: true }) MemoryTrim.Trim(); }
        finally { _checking = false; }
    }

    private async Task CheckUpdatesCore(bool interactive)
    {
        try
        {
            // L10: signature2 krävs (ReleaseVerifier) och "issued" får inte vara äldre än den senaste vi sett.
            var release = Settings.AcceptRelease(await Api.VersionAsync(UpdateKeys.PublicKeyPem, Settings.Beta), Settings.Beta);
            Settings.BlockedAddons = release.BlockedAddons.ToList();
            Settings.Save();
            await UpdateAddon(release, interactive, announceBlocked: false, alsoBlocked: null);
            // Efter en versionskontroll: försök igen med filer som servern tidigare avvisade (servern kan ha rättats).
            // Oförändrade, redan uppladdade filer skickas inte (sha256), så detta kostar inget annars.
            _sync.Notify();

            // Microsoft Store uppdaterar appen själv (Store-regel; paketkatalogen är skrivskyddad) – bara addonen ovan.
            if (BuildInfo.IsStore)
            {
                if (interactive) Message(F("update.none", SelfUpdater.CurrentVersion.ToString(3)), S("app"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Varje variant uppdaterar sig från sin egen fil: full → clientFile, lite → liteFile.
            var kind = BuildInfo.UpdateKind;
            var newVersion = release.VersionOf(kind);
            if (release.File(kind) is null || !SelfUpdater.IsNewer(newVersion))
            {
                if (interactive) Message(F("update.none", SelfUpdater.CurrentVersion.ToString(3)), S("app"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!interactive && Uploading)
            {
                _versionAt = DateTime.UtcNow - VersionEvery + TimeSpan.FromMinutes(1); // försök igen om en minut
                return;
            }
            if (!interactive && Settings.UpdateFailures >= AutoUpdateMaxFailures)
            {
                // Har misslyckats flera gånger: erbjud i stället (en bubbla per version, menyn visar versionen).
                _miUpdate.Text = F("menu.update.to", newVersion);
                if (!_updateOffered)
                {
                    _updateOffered = true;
                    _tray.ShowBalloonTip(10000, "Forever Scout", F("update.available", newVersion), ToolTipIcon.Info);
                }
                return;
            }
            if (!interactive) Log(F("journal.autoupdate", When(DateTime.UtcNow), newVersion), Theme.Gold);
            byte[] exe;
            try { exe = await Api.DownloadReleaseFileAsync(release, kind, UpdateKeys.PublicKeyPem, 400L * 1024 * 1024); }
            catch (Exception ex) when (ex is ScoutException or IOException or UnauthorizedAccessException) { throw new UpdateStepException(UpdateSteps.Download, ex, null); }
            // Stage och Swap gör blockerande omförsök (upp till ~15 s per steg) – kör dem utanför UI-tråden.
            await Task.Run(() => SelfUpdater.Stage(exe));
            if (!interactive || Message(F("update.ready", newVersion), S("app"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                // Markör för den nya versionen: "Uppdaterad till …" efter omstarten. Fönstret visas bara om det var
                // öppet eller om användaren själv bad om uppdateringen – aldrig ovanpå spelet.
                Settings.JustUpdatedFrom = SelfUpdater.CurrentVersion.ToString(3);
                Settings.QuietUpdate = !interactive && Live is not { Visible: true };
                Settings.Save();
                try { await Task.Run(() => SelfUpdater.Swap()); }
                catch (UpdateStepException) { Settings.JustUpdatedFrom = null; Settings.QuietUpdate = false; Settings.Save(); throw; }
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
                if (Message(msg + "\n\n" + S("update.openpage"), S("app"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    Links.Open(Api, Links.Download(Api));
            }
            else Message(msg, S("app"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex) when (ex is ScoutException or IOException or UnauthorizedAccessException)
        {
            if (interactive) Message(F("update.fail", Error(ex)), S("app"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            // Tyst kontroll: nät-/serverfel visas inte som fel (uppladdningen har egen status).
        }
    }

    /// <summary>
    /// Efter att uppladdningen stoppats för en spärrad addon (spärrlistan eller serverns 422): hämta versionsinfon
    /// och installera en NYARE signerad addon om det finns en. Annars står uppladdningen still och användaren får veta det.
    /// </summary>
    private async Task UpdateAddonAfterBlock()
    {
        try
        {
            // Spärrar som uppladdningen just lärt sig (422) gäller tills nästa versionskontroll, även om versionsinfon inte nämner dem.
            var learned = Settings.BlockedAddons.ToList();
            var release = Settings.AcceptRelease(await Api.VersionAsync(UpdateKeys.PublicKeyPem, Settings.Beta), Settings.Beta);
            Settings.BlockedAddons = release.BlockedAddons.Union(learned).ToList();
            Settings.Save();
            await UpdateAddon(release, interactive: false, announceBlocked: true, alsoBlocked: learned);
        }
        catch (Exception ex) when (ex is ScoutException or IOException or UnauthorizedAccessException)
        {
            SetError(Error(ex));
        }
    }

    /// <summary>
    /// Installerar den signerade addonen om ingen finns eller om den signerade är NYARE (<see cref="AddonInstaller.Decide"/>).
    /// L10: en spärrad addon (osignerad spärrlista eller 422) leder aldrig till en äldre addon – bara till att
    /// uppladdningen står still och ett meddelande. Bara signerade zip-filer installeras.
    /// </summary>
    private async Task UpdateAddon(SignedRelease release, bool interactive, bool announceBlocked, IEnumerable<string>? alsoBlocked)
    {
        if (CurrentDir is not { } dir) return;
        var installed = AddonInstaller.InstalledVersion(dir);
        switch (AddonInstaller.Decide(installed, release, alsoBlocked))
        {
            case AddonInstaller.UpdateAction.None:
                return;
            case AddonInstaller.UpdateAction.BlockedWaiting:
                var wait = F("addon.blocked.wait", installed);
                if (_addonError != wait) Log(F("journal.error", When(DateTime.UtcNow), wait), Theme.Warn);
                SetAddonError(wait);
                if (interactive) Message(wait, S("app"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
        }
        var zip = await Api.DownloadReleaseFileAsync(release, ReleaseKind.Addon, UpdateKeys.PublicKeyPem, 20 * 1024 * 1024);
        AddonInstaller.Install(dir, AddonInstaller.FromZip(zip));
        _errorText = null;
        _addonError = null;
        var msg = announceBlocked ? F("addon.blocked", release.Addon) : F("update.addon", release.Addon);
        if (interactive) Message(msg, S("app"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        else Toast("Forever Scout", msg);
        Live?.ShowStatus();
    }

    // ------------------------------------------------------------------ fönster

    public void ShowWindow()
    {
        if (Live is not { } f)
        {
            f = new MainForm(this);
            // Hur fönstret än rivs (Dispose i fältet, Stäng, Windows-avstängning) pekar _form aldrig på ett rivet fönster.
            f.Disposed += (s, _) => { if (ReferenceEquals(_form, s)) _form = null; };
            _form = f;
        }
        f.ShowFromTray();
        if (DateTime.UtcNow - _meAt > TimeSpan.FromMinutes(1)) _ = RefreshMe(); // färska poäng när fönstret öppnas
    }

    public MainForm? Window => Live;

    /// <summary>Fönstret om det finns och inte är rivet – alla uppdateringar av fönstret går via den här.</summary>
    private MainForm? Live => _form is { IsDisposed: false, Disposing: false } f ? f : null;

    /// <summary>Ägare till dialoger: bara ett levande, synligt fönster (en MessageBox med ett rivet fönster som ägare kastar).</summary>
    private IWin32Window? DialogOwner => Live is { Visible: true } f ? f : null;

    private int _modal;

    /// <summary>Minst en modal dialog (MessageBox, ShowDialog) är öppen. Fönstret rivs inte och göms inte under tiden: det
    /// kan vara dialogens ägare, och koden som visade dialogen fortsätter efteråt.</summary>
    public bool ModalOpen => _modal > 0;

    /// <summary>Visar en modal dialog. Alla modala dialoger i appen går hit (se <see cref="ModalOpen"/>).</summary>
    public T Modal<T>(Func<T> show)
    {
        _modal++;
        try { return show(); }
        finally
        {
            _modal--;
            if (_modal == 0) OnUi(ReleaseHiddenWindow); // fönstret kan ha gömts medan dialogen var öppen
        }
    }

    private DialogResult Message(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon) =>
        Modal(() => MessageBox.Show(DialogOwner, text, caption, buttons, icon));

    private DialogResult Dialog(Form f) => Modal(() => DialogOwner is { } o ? f.ShowDialog(o) : f.ShowDialog());

    public void OpenSettings(bool pickDir)
    {
        var oldLang = Settings.Language;
        var oldAuction = Settings.ShareAuctionPrices;
        var oldBeta = Settings.Beta;
        using var f = new SettingsForm(Settings, pickDir, LinkWithBrowser);
        if (Dialog(f) != DialogResult.OK || _exiting) return;
        if (f.Unlinked || oldLang != Settings.Language) { Restart(); return; }
        ApplyGameDir();
        if (Settings.ShareAuctionPrices && !oldAuction) _sync.Notify(); // nyss påslaget: dela det som redan finns
        InstallBundledAddonIfNeeded();
        RefreshStats(force: true);
        UpdateStatus();
        if (Settings.Beta != oldBeta) _ = CheckUpdates(interactive: false); // 1.0.13: bytt kanal – hämta direkt
    }

    /// <summary>
    /// "Logga in med webbläsaren" från menyerna och Inställningar: kopplar (eller byter koppling) utan omstart. Nyckeln
    /// sparas via <see cref="ContributorStore"/> – samma väg som vid första starten. Null om det avbröts eller misslyckades.
    /// </summary>
    public async Task<Contributor?> LinkWithBrowser(IWin32Window? owner)
    {
        using var f = new BrowserLinkForm(Api, Settings.ContributorName, who => ContributorStore.Save(who, Api, Settings, KeyStore.Save));
        if (owner is Control { IsDisposed: true } or Control { Visible: false }) owner = null;
        owner ??= DialogOwner;
        Modal(() => owner is null ? f.ShowDialog() : f.ShowDialog(owner));
        if (f.Result is not { } linked) return null;
        Log(F("link.ok", linked.Name), Theme.Ok);
        Live?.ShowContribution();
        UpdateStatus();
        await RefreshMe();
        return linked;
    }

    /// <summary>Fönstret har gömts i fältet. Engångstips första gången.</summary>
    public void OnHiddenToTray()
    {
        // Fönstret (kontroller, dubbelbuffrar, logotyp) behövs inte i fältet: släpp det och bygg upp det igen vid
        // nästa öppning (ShowWindow skapar ett nytt). Körs efter att stängningshändelsen är klar (BeginInvoke).
        OnUi(ReleaseHiddenWindow);
        if (Settings.TrayHintShown) return;
        Settings.TrayHintShown = true;
        Settings.Save();
        _tray.ShowBalloonTip(6000, S("tray.hint"), S("tray.hint.body"), ToolTipIcon.Info);
    }

    /// <summary>Rivs det dolda fönstret och trimmar minnet – men aldrig medan en dialog är öppen (tas om när den stängs).</summary>
    private void ReleaseHiddenWindow()
    {
        if (_modal > 0 || _exiting) return;
        if (_form is not { IsDisposed: false, Visible: false } f) return;
        // Nolla referensen FÖRE Dispose: inget som körs under eller efter Dispose får nå det halvt rivna fönstret.
        _form = null;
        f.Dispose();
        MemoryTrim.Trim();
    }

    public void Toast(string? title, string text, bool onlyIfHidden = false)
    {
        if (onlyIfHidden && Live is { Visible: true } f) { f.Flash(text); return; }
        _tray.ShowBalloonTip(8000, title ?? "Forever Scout", text, ToolTipIcon.Info);
    }

    /// <summary>"Visa vad som skickas": exakt innehåll i nästa/senaste uppladdning.</summary>
    public void ShowPayload()
    {
        var text = UploadPreview.Build(Api, CurrentDir, Settings);
        using var f = new PayloadForm(text);
        Dialog(f);
    }

    // ------------------------------------------------------------------ skärmbilder (bara --screenshots)

    internal void SimulateUploading(bool on) { _simUploading = on; UpdateStatus(); Live?.ShowStats(); }
    internal void SimulateError(string? text) { _errorText = text; if (text != null) Log(F("journal.error", When(DateTime.UtcNow), text), Theme.Err); UpdateStatus(); Live?.ShowStats(); }
    internal void SimulateEmpty() { Stats = new ScoutStats(); HasSavedVariables = false; Settings.Points = null; Settings.Rank = null; Settings.AcceptedTotal = null; Settings.LastConfirmed = new(); Settings.LastUploadUtc = null; _journal.Clear(); Live?.ShowStats(); Live?.ShowContribution(); Live?.ShowJournal(); UpdateStatus(); }

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
    public void AnnounceUpdate(string? from, bool showWindow = true)
    {
        var to = SelfUpdater.CurrentVersion.ToString(3);
        if (showWindow) ShowWindow();
        var now = When(DateTime.UtcNow);
        Log(from is null ? F("journal.updated.to", now, to) : F("journal.updated", now, from, to), Theme.Ok);
        UploadLog.Append("update", 0, "-", from is null ? $"updated to {to}" : $"updated from {from} to {to}");
        Live?.Flash(F("updated.to", to), 15000);
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
