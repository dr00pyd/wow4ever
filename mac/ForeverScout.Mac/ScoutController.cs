using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using ForeverScout.Core;
using static ForeverScout.Mac.Texts;

namespace ForeverScout.Mac;

/// <summary>
/// Appen bor i menyraden: lägereldsikonen, menyn, filbevakning och uppladdning. Motsvarigheten till ScoutApp på
/// Windows – samma Core (SafeFs, SyncCoordinator, Uploader, ReleaseVerifier, AddonInstaller), samma regler och texter.
/// Huvudfönstret skapas först när användaren öppnar det.
/// </summary>
internal sealed class ScoutController : IDisposable
{
    public Settings Settings { get; }
    public ApiClient Api { get; }
    public ScoutStats Stats { get; private set; } = new();
    public TrayState State { get; private set; } = TrayState.Normal;
    public string StatusText { get; private set; } = "";
    public Color StatusColor { get; private set; } = Look.Muted;
    public bool Uploading => _uploader.IsBusy || _uploadingUi;
    public string? ErrorText { get; private set; }
    public bool HasSavedVariables { get; private set; }
    public IReadOnlyList<(DateTime At, string Text, Color Color)> Journal => _journal;
    public bool IsExiting { get; private set; }

    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly List<(DateTime At, string Text, Color Color)> _journal = new();
    private readonly Uploader _uploader;
    private readonly SyncCoordinator _sync;
    private readonly TrayIcon _tray;
    private readonly NativeMenuItem _miUpload, _miStatus, _miPause;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _anim = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly WindowIcon _iconNormal, _iconError;
    private readonly WindowIcon[] _iconUpload;
    private string? _gameRoot;
    private MainWindow? _window;
    private bool _uploadingUi;
    private int _frame, _humor;
    private string _statsStamp = "";

    public ScoutController(IClassicDesktopStyleApplicationLifetime lifetime, Settings settings, ApiClient api)
    {
        _lifetime = lifetime;
        Settings = settings;
        Api = api;
        _uploader = new Uploader(api, settings);

        // Menyradsikonen: lägerelden som mallbild (följer ljust/mörkt läge), i färg vid uppladdning och fel.
        _iconNormal = Res.Icon("tray-template.png");
        _iconError = Res.Icon("tray-error.png");
        _iconUpload = Enumerable.Range(0, 4).Select(i => Res.Icon($"tray-upload-{i}.png")).ToArray();

        var menu = new NativeMenu();
        NativeMenuItem Item(string text, Action? click, bool enabled = true)
        {
            var i = new NativeMenuItem(text) { IsEnabled = enabled };
            if (click != null) i.Click += (_, _) => click();
            menu.Items.Add(i);
            return i;
        }
        Item(T("menu.open"), ShowWindow);
        _miUpload = Item(T("menu.upload"), () => _ = UploadNow(quietIfNothing: false));
        _miStatus = Item("", null, enabled: false);
        menu.Items.Add(new NativeMenuItemSeparator());
        Item(T("menu.login"), () => _ = LinkWithBrowser(null));
        _miPause = Item(T("menu.pause"), () =>
        {
            Settings.AutoUploadPaused = !Settings.AutoUploadPaused;
            Settings.Save();
            UpdateStatus();
        });
        _miPause.ToggleType = MenuItemToggleType.CheckBox;
        Item(T("menu.payload"), ShowPayload);
        Item(T("menu.update"), () => _ = CheckUpdates(interactive: true));
        Item(T("menu.guide"), () => Links.Open(Api, Links.Guide(Api)));
        Item(T("menu.settings"), () => _ = OpenSettings(pickDir: false));
        Item(T("menu.about"), () => _ = ShowAbout());
        menu.Items.Add(new NativeMenuItemSeparator());
        Item(T("menu.exit"), Exit);
        menu.Opening += (_, _) => UpdateMenu();

        _tray = new TrayIcon { Icon = _iconNormal, ToolTipText = "Forever Scout", Menu = menu, IsVisible = true };
        MacOSProperties.SetIsTemplateIcon(_tray, true);
        TrayIcon.SetIcons(Application.Current!, [_tray]);

        _sync = new SyncCoordinator(
            () => CurrentDir is { } d ? [.. WowLocator.SavedVariableFiles(d), .. WowLocator.AuctionFiles(d).Select(a => a.Path)] : [],
            async ct =>
            {
                if (!AutoUploadActive || string.IsNullOrEmpty(Api.Key)) return;
                var done = new TaskCompletionSource();
                Dispatcher.UIThread.Post(async () => { try { await UploadNow(quietIfNothing: true); } finally { done.TrySetResult(); } });
                await done.Task.WaitAsync(ct);
            });
        _tick.Tick += (_, _) => OnTick();
        _anim.Tick += (_, _) => { _frame = (_frame + 1) % _iconUpload.Length; _tray.Icon = _iconUpload[_frame]; };
    }

    public void Log(string text, Color color)
    {
        _journal.Insert(0, (DateTime.Now, text, color));
        if (_journal.Count > 6) _journal.RemoveAt(_journal.Count - 1);
        _window?.ShowJournal();
    }

    private string Humor() => T("humor." + (1 + (_humor++ % 3)));

    private bool AutoUploadActive => Settings.AutoUpload && !Settings.AutoUploadPaused;

    public GameDir? CurrentDir => _gameRoot is null ? null : new GameDir(_gameRoot, Path.GetFileName(_gameRoot));

    // ------------------------------------------------------------------ start

    public async Task StartAsync(bool showWindow)
    {
        if (showWindow) ShowWindow();
        RefreshAutostartPath();
        await EnsureGameDir();
        ApplyGameDir();
        InstallBundledAddonIfNeeded();
        RefreshStats(force: true);
        UpdateStatus();
        _tick.Start();
        await RefreshMe();
        await CheckUpdates(interactive: false);
        _sync.Notify();
    }

    /// <summary>"Starta vid inloggning" på: skriv om LaunchAgent-filen så att den pekar på paketet som körs nu.</summary>
    private static void RefreshAutostartPath()
    {
        try { if (SafeFs.AutostartEnabled) SafeFs.SetAutostart(true); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SafeFsException) { }
    }

    /// <summary>Godkänner spelkatalogen i SafeFs – bara en som användaren valt eller bekräftat (L11).</summary>
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

    private async Task EnsureGameDir()
    {
        var (step, dir) = WowLocator.NextStep(Settings, WowLocator.IsUsableGameDir, () => WowLocator.Locate());
        if (step == WowLocator.StartStep.Confirm) await ConfirmFoundGameDir(dir!);
        else if (step == WowLocator.StartStep.Pick) await OpenSettings(pickDir: true);
    }

    /// <summary>
    /// L11: en automatiskt hittad (eller av ≤ 1.0.6 sparad) katalog används först när användaren sagt ja – en gång.
    /// Nej: katalogen föreslås inte igen och användaren får välja själv.
    /// </summary>
    private async Task ConfirmFoundGameDir(string dir)
    {
        if (await Dialogs.YesNo(_window, TF("gamedir.confirm", dir)))
        {
            Settings.GameDir = dir;
            Settings.ConfirmGameDir();
            Settings.Save();
            return;
        }
        Settings.GameDirDeclined = dir;
        Settings.GameDir = null;
        Settings.Save();
        await OpenSettings(pickDir: true);
    }

    private void InstallBundledAddonIfNeeded()
    {
        if (CurrentDir is not { } dir) return;
        try
        {
            var bundled = AddonBundle.Version;
            if (Settings.BlockedAddons.Contains(bundled ?? "")) return;
            if (AddonInstaller.NeedsInstall(dir, AddonBundle.Files))
            {
                AddonInstaller.Install(dir, AddonBundle.Files);
                Log(TF("journal.addon", bundled), Look.Ok);
                _window?.Flash(TF("addon.installed", bundled), 8000);
            }
        }
        catch (Exception ex) when (ex is ScoutException or IOException or UnauthorizedAccessException) { SetError(Strings.Error(ex)); }
    }

    // ------------------------------------------------------------------ data

    public void RefreshStats(bool force)
    {
        if (CurrentDir is not { } dir) { Stats = new ScoutStats(); HasSavedVariables = false; _window?.ShowStats(); return; }
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
        _window?.ShowStats();
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
            Settings.ConfirmedTotal = me.Confirmed;
            if (me.Recent is { Count: > 0 } recent && Settings.LastConfirmed.Count == 0)
                Settings.LastConfirmed = recent.Select(ToConfirmed).OfType<ConfirmedItem>().Take(5).ToList();
            Settings.Save();
            _window?.ShowContribution();
            UpdateStatus();
        }
        catch (ApiException) { }
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
        ErrorText = text;
        UpdateStatus();
    }

    public void UpdateStatus()
    {
        if (Uploading) { State = TrayState.Uploading; StatusText = T("uploading"); StatusColor = Look.Gold; }
        else if (ErrorText != null) { State = TrayState.Error; StatusText = ErrorText; StatusColor = Look.Err; }
        else if (Settings.NextRetryUtc is { } next) { State = TrayState.Error; StatusText = TF("retry", Strings.When(next)); StatusColor = Look.Warn; }
        else
        {
            State = TrayState.Normal;
            StatusText = CurrentDir is null ? T("game.none")
                : Settings.AutoUploadPaused ? T("paused")
                : Settings.LastUploadUtc is { } t ? TF("last", Strings.When(t)) : T("never");
            StatusColor = CurrentDir is null || Settings.AutoUploadPaused ? Look.Warn : Look.Muted;
        }

        if (State == TrayState.Uploading)
        {
            MacOSProperties.SetIsTemplateIcon(_tray, false);
            if (!_anim.IsEnabled) _anim.Start();
        }
        else
        {
            _anim.Stop();
            bool normal = State == TrayState.Normal;
            MacOSProperties.SetIsTemplateIcon(_tray, normal);
            _tray.Icon = normal ? _iconNormal : _iconError;
        }
        var tip = "Forever Scout";
        if (Settings.Points is int p) tip += " · " + TF("tray.tooltip.points", p.ToString("N0", Strings.Culture));
        _tray.ToolTipText = tip + "\n" + StatusText;
        UpdateMenu();
        _window?.ShowStatus();
    }

    private void UpdateMenu()
    {
        _miUpload.IsEnabled = !Uploading && CurrentDir != null && !string.IsNullOrEmpty(Api.Key);
        _miStatus.Header = StatusText.Length > 80 ? StatusText[..79] + "…" : StatusText;
        _miPause.IsChecked = Settings.AutoUploadPaused;
    }

    private void OnTick()
    {
        RefreshStats(force: false);
        UpdateStatus();
        if (_uploader.RetryDue && AutoUploadActive) _sync.Notify();
        if (DateTime.UtcNow - _meAt > MeEvery) _ = RefreshMe();
        if (DateTime.UtcNow - _versionAt > VersionEvery) _ = CheckUpdates(interactive: false);
    }

    // ------------------------------------------------------------------ uppladdning

    public async Task UploadNow(bool quietIfNothing = false)
    {
        if (CurrentDir is not { } dir) { await OpenSettings(pickDir: true); return; }
        if (string.IsNullOrEmpty(Api.Key)) return;
        _uploadingUi = true;
        UpdateStatus();
        SyncReport report;
        try { report = await Task.Run(() => _uploader.SyncAsync(dir, force: false)); }
        finally { _uploadingUi = false; }

        ErrorText = report.Error is { } err && !report.WillRetry ? Strings.Error(err) : null;
        var now = Strings.When(DateTime.UtcNow);
        if (report.Error is { } e2) Log(TF("journal.error", now, Strings.Error(e2)), report.WillRetry ? Look.Warn : Look.Err);
        if (report.FilesUploaded > 0) Log(TF("journal.uploaded", now, report.FilesUploaded) + " – " + Humor(), Look.Ok);
        if (report.HotfixStored > 0) Log(TF("journal.hotfix", now), Look.Ok);
        if (report.AuctionRows > 0) Log(TF("journal.auctions", now, report.AuctionRows), Look.Ok);
        if (report.NothingToDo && !quietIfNothing) Log(TF("journal.nothing", now), Look.Muted);
        if (report.FilesUploaded > 0 || report.HotfixStored > 0) _window?.Flash(TF("done", report.FilesUploaded, report.HotfixStored));
        else if (report.NothingToDo && !quietIfNothing) _window?.Flash(T("nothing"));
        if (report.NewConfirmed.Count > 0)
        {
            var first = report.NewConfirmed[0].Name;
            var what = report.NewConfirmed.Count == 1 ? first : TF("toast.more", first, report.NewConfirmed.Count - 1);
            Log(T("toast.title") + " " + TF("toast.body", what), Look.Ok);
        }
        if (report.AddonBlocked) await UpdateAddonAfterBlock();

        RefreshStats(force: true);
        _window?.ShowContribution();
        UpdateStatus();
        if (report.FilesUploaded > 0) await RefreshMe();
    }

    // ------------------------------------------------------------------ uppdateringar (signerade)

    /// <summary>1.0.10: versionsinfon hämtas vid start och sedan med detta intervall (appen ligger ofta i menyraden i dagar).</summary>
    private static readonly TimeSpan VersionEvery = TimeSpan.FromHours(3);
    /// <summary>Efter så många misslyckade självuppdateringar i rad slutar appen försöka själv (menyn fungerar som förut).</summary>
    private const int AutoUpdateMaxFailures = 3;
    private DateTime _versionAt = DateTime.MinValue;
    private bool _checking;
    private string? _offered;

    /// <summary>
    /// Addonen och spärrlistan: samma signerade versionsinfo som Windows (signature2). Appen själv:
    /// mac-fälten verifierade med signature3, zip:en mot vår filsignatur, sedan Apples kodsignatur (MacBundleUpdater).
    /// 1.0.10: en nyare app installeras utan fråga vid den tysta kontrollen – aldrig mitt i en uppladdning, och efter
    /// <see cref="AutoUpdateMaxFailures"/> misslyckanden i rad bara som en rad i lägerjournalen. Menyn frågar som förut.
    /// </summary>
    public async Task CheckUpdates(bool interactive)
    {
        if (_checking) return;
        _checking = true;
        _versionAt = DateTime.UtcNow;
        try { await CheckUpdatesCore(interactive); }
        finally { _checking = false; }
    }

    private async Task CheckUpdatesCore(bool interactive)
    {
        try
        {
            // L10: signature2 krävs (ReleaseVerifier) och "issued" får inte vara äldre än den senaste vi sett.
            var release = Settings.AcceptRelease(await Api.VersionAsync(UpdateKeys.PublicKeyPem));
            Settings.BlockedAddons = release.BlockedAddons.ToList();
            Settings.Save();
            await UpdateAddon(release, interactive, announceBlocked: false, alsoBlocked: null);
            _sync.Notify();

            SignedRelease mac;
            try { mac = Settings.AcceptRelease(await Api.MacVersionAsync(UpdateKeys.PublicKeyPem)); }
            catch (ApiException ex) when (ex.Code == ErrorCodes.SignatureMissing) { mac = new SignedRelease(); } // inget Mac-släpp än
            var newVersion = mac.Mac;
            if (mac.MacFile is null || !SelfUpdater.IsNewer(newVersion))
            {
                if (interactive) await Dialogs.Info(_window, TF("update.none", SelfUpdater.CurrentVersion.ToString(3)));
                return;
            }
            if (!interactive && Uploading)
            {
                _versionAt = DateTime.UtcNow - VersionEvery + TimeSpan.FromMinutes(1); // försök igen om en minut
                return;
            }
            if (!interactive && (Settings.UpdateFailures >= AutoUpdateMaxFailures || MacBundleUpdater.IsTranslocated(MacBundleUpdater.Bundle)))
            {
                // Har misslyckats flera gånger (eller körs direkt ur Hämtade filer): säg till en gång per version.
                if (_offered != newVersion) { _offered = newVersion; Log(TF("update.available", newVersion), Look.Warn); }
                return;
            }
            if (!interactive) Log(TF("journal.autoupdate", Strings.When(DateTime.UtcNow), newVersion), Look.Gold);
            if (MacBundleUpdater.IsTranslocated(MacBundleUpdater.Bundle)) throw new ScoutException(ErrorCodes.AppTranslocated);
            byte[] zip;
            try { zip = await Api.DownloadReleaseFileAsync(mac, ReleaseKind.Mac, UpdateKeys.PublicKeyPem, 600L * 1024 * 1024); }
            catch (Exception ex) when (ex is ScoutException or IOException or UnauthorizedAccessException) { throw new UpdateStepException(UpdateSteps.Download, ex, null); }
            var staged = await Task.Run(() => MacBundleUpdater.Stage(zip));
            if (!interactive || await Dialogs.YesNo(_window, TF("update.ready", newVersion)))
            {
                Settings.JustUpdatedFrom = SelfUpdater.CurrentVersion.ToString(3);
                // Fönstret visas efter omstarten bara om det var öppet eller om användaren själv bad om uppdateringen.
                Settings.QuietUpdate = !interactive && _window is not { IsVisible: true };
                Settings.Save();
                try { await Task.Run(() => MacBundleUpdater.Swap(staged)); }
                catch (UpdateStepException) { Settings.JustUpdatedFrom = null; Settings.QuietUpdate = false; Settings.Save(); throw; }
                UploadLog.Append("update", zip.Length, ApiClient.Sha256Hex(zip), $"ok swapped to {newVersion}");
                Settings.UpdateFailures = 0;
                Settings.Save();
                if (!await StartNewInstanceVerifiedAsync())
                    throw new UpdateStepException(UpdateSteps.Restart, new InvalidOperationException("new process did not start"), "ForeverScout");
                Exit();
            }
            else MacBundleUpdater.CleanupOld();
        }
        catch (UpdateStepException ux)
        {
            UploadLog.Append("update", 0, "-", "fail " + ux.Diagnostic);
            Log(TF("journal.updatefail", Strings.When(DateTime.UtcNow), ux.Diagnostic), Look.Err);
            Settings.UpdateFailures++;
            Settings.Save();
            if (!interactive) return;
            var msg = ux.Step switch
            {
                UpdateSteps.Download => TF("update.download", Strings.Error(ux.InnerException!)),
                UpdateSteps.Restart => T("update.notstarted"),
                _ when ux.FileInUse => T("update.inuse"),
                _ => TF("update.fail", ux.Diagnostic),
            };
            if (Settings.UpdateFailures >= 2)
            {
                if (await Dialogs.YesNo(_window, msg + "\n\n" + T("update.openpage"))) Links.Open(Api, Links.Download(Api));
            }
            else await Dialogs.Info(_window, msg);
        }
        catch (Exception ex) when (ex is ScoutException or IOException or UnauthorizedAccessException)
        {
            if (interactive) await Dialogs.Info(_window, TF("update.fail", Strings.Error(ex)));
        }
    }

    /// <summary>
    /// Efter att uppladdningen stoppats för en spärrad addon (spärrlistan eller serverns 422): installera en NYARE
    /// signerad addon om det finns en, annars står uppladdningen still och användaren får veta det (L10).
    /// </summary>
    private async Task UpdateAddonAfterBlock()
    {
        try
        {
            // Spärrar som uppladdningen just lärt sig (422) gäller tills nästa versionskontroll.
            var learned = Settings.BlockedAddons.ToList();
            var release = Settings.AcceptRelease(await Api.VersionAsync(UpdateKeys.PublicKeyPem));
            Settings.BlockedAddons = release.BlockedAddons.Union(learned).ToList();
            Settings.Save();
            await UpdateAddon(release, interactive: false, announceBlocked: true, alsoBlocked: learned);
        }
        catch (Exception ex) when (ex is ScoutException or IOException or UnauthorizedAccessException) { SetError(Strings.Error(ex)); }
    }

    /// <summary>
    /// Installerar den signerade addonen om ingen finns eller om den signerade är NYARE (<see cref="AddonInstaller.Decide"/>).
    /// L10: en spärrad addon leder aldrig till en äldre addon – bara till att uppladdningen står still och ett meddelande.
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
                var wait = TF("addon.blocked.wait", installed);
                if (ErrorText != wait) Log(TF("journal.error", Strings.When(DateTime.UtcNow), wait), Look.Warn);
                SetError(wait);
                if (interactive) await Dialogs.Info(_window, wait);
                return;
        }
        var zip = await Api.DownloadReleaseFileAsync(release, ReleaseKind.Addon, UpdateKeys.PublicKeyPem, 20 * 1024 * 1024);
        AddonInstaller.Install(dir, AddonInstaller.FromZip(zip));
        ErrorText = null;
        var msg = announceBlocked ? TF("addon.blocked", release.Addon) : TF("update.addon", release.Addon);
        if (interactive) await Dialogs.Info(_window, msg);
        else Log(msg, Look.Ok);
        _window?.ShowStatus();
    }

    /// <summary>Startar den nya binären (efter bytet) och kontrollerar att den lever efter ~2 s.</summary>
    private static async Task<bool> StartNewInstanceVerifiedAsync()
    {
        Process? p;
        try { p = Process.Start(new ProcessStartInfo(MacBundleUpdater.ExecutableIn(MacBundleUpdater.Bundle)) { UseShellExecute = false, Arguments = "--restarted" }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ScoutException) { return false; }
        if (p is null) return false;
        using (p)
        {
            bool exited = await Task.Run(() => p.WaitForExit(2000));
            if (exited) UploadLog.Append("update", 0, "-", $"fail step=restart new process exited code={p.ExitCode}");
            return !exited;
        }
    }

    /// <summary>Efter en uppdatering: fönstret fram, "Uppdaterad till …" och en rad i lägerjournalen och uploads.log.</summary>
    public void AnnounceUpdate(string? from, bool showWindow = true)
    {
        var to = SelfUpdater.CurrentVersion.ToString(3);
        if (showWindow) ShowWindow();
        var now = Strings.When(DateTime.UtcNow);
        Log(from is null ? TF("journal.updated.to", now, to) : TF("journal.updated", now, from, to), Look.Ok);
        UploadLog.Append("update", 0, "-", from is null ? $"updated to {to}" : $"updated from {from} to {to}");
        _window?.Flash(TF("updated.to", to), 15000);
    }

    // ------------------------------------------------------------------ fönster

    public void ShowWindow()
    {
        _window ??= new MainWindow(this);
        _window.ShowFromMenuBar();
        if (DateTime.UtcNow - _meAt > TimeSpan.FromMinutes(1)) _ = RefreshMe(); // färska poäng när fönstret öppnas
    }

    public async Task ShowAbout()
    {
        var text = TF("about.text", SelfUpdater.CurrentVersion.ToString(3), T("about.variant.full"), Api.BaseUri.Host);
        await Dialogs.Info(_window, text, T("about.title"));
    }

    public void ShowPayload()
    {
        var w = new PayloadWindow(UploadPreview.Build(Api, CurrentDir, Settings));
        if (_window is { IsVisible: true }) w.Show(_window); else { w.Show(); w.Activate(); }
    }

    private bool _settingsOpen;

    public async Task OpenSettings(bool pickDir)
    {
        if (_settingsOpen) return;
        _settingsOpen = true;
        try
        {
            var oldLang = Settings.Language;
            var oldAuction = Settings.ShareAuctionPrices;
            var f = new SettingsWindow(Settings, pickDir, LinkWithBrowser);
            var closed = new TaskCompletionSource();
            f.Closed += (_, _) => closed.TrySetResult();
            if (_window is { IsVisible: true }) await f.ShowDialog(_window);
            else { f.Show(); f.Activate(); await closed.Task; }
            if (!f.Saved) return;
            if (f.Unlinked || oldLang != Settings.Language) { Restart(); return; }
            ApplyGameDir();
            if (Settings.ShareAuctionPrices && !oldAuction) _sync.Notify(); // nyss påslaget: dela det som redan finns
            InstallBundledAddonIfNeeded();
            RefreshStats(force: true);
            UpdateStatus();
        }
        finally { _settingsOpen = false; }
    }

    /// <summary>
    /// "Logga in med webbläsaren" från menyerna och Inställningar: kopplar (eller byter koppling) utan omstart. Nyckeln
    /// sparas via <see cref="ContributorStore"/> i nyckelringen – samma väg som vid första starten. Null om det avbröts.
    /// </summary>
    public async Task<Contributor?> LinkWithBrowser(Window? owner)
    {
        owner ??= _window is { IsVisible: true } ? _window : null;
        var linked = await BrowserLinkWindow.Run(Api, owner, Settings.ContributorName,
            who => ContributorStore.Save(who, Api, Settings, KeychainStore.Save));
        if (linked is null) return null;
        Log(TF("link.ok", linked.Name), Look.Ok);
        _window?.ShowContribution();
        UpdateStatus();
        await RefreshMe();
        return linked;
    }

    /// <summary>Första gången fönstret göms: tala om att appen finns kvar i menyraden.</summary>
    public void OnHiddenToMenuBar()
    {
        if (Settings.TrayHintShown) return;
        Settings.TrayHintShown = true;
        Settings.Save();
        _ = Dialogs.Info(null, T("tray.hint.body"), T("tray.hint"));
    }

    /// <summary>Startar appen igen (språkbyte, koppla från). Den nya processen väntar på låset tills den här avslutats.</summary>
    public void Restart()
    {
        try
        {
            var exe = SafeFs.OwnBundle is { } b ? MacBundleUpdater.ExecutableIn(b) : Environment.ProcessPath;
            if (exe != null) using (Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, Arguments = "--restarted" })) { }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        Exit();
    }

    /// <summary>Avsluta utan fråga (menyn, Stäng-valet, utloggning).</summary>
    public void Exit()
    {
        if (IsExiting) return;
        IsExiting = true;
        _tick.Stop();
        _anim.Stop();
        _sync.Dispose();
        _tray.IsVisible = false;
        _window?.Close();
        _lifetime.Shutdown();
    }

    public void Dispose()
    {
        _tick.Stop();
        _anim.Stop();
        _sync.Dispose();
        _tray.Dispose();
    }
}
