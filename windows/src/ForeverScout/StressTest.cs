#if DEBUG
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using ForeverScout.Core;

namespace ForeverScout;

/// <summary>
/// <c>--stress[=minuter]</c> – finns bara i Debug-byggen. Kör en slumpad sekvens mot den riktiga appen i snabb takt:
/// öppna fönstret, minimera, stäng till fältet (med och utan frågan), öppna igen direkt, F5, Ladda upp, Sök efter
/// uppdatering (fältmenyn och kugghjulet), Inställningar, Visa vad som skickas, Om, fältmenyn, storleksändring, med
/// korta tick-intervall och tvingad /jag- och versionskontroll i varje tick. Dialoger stängs automatiskt efter en slumpad
/// stund – aldrig med "Avsluta", och inloggning, länkar och Avsluta i menyerna används aldrig. Filändringar (synk) och
/// minnesmätningen görs utifrån (<c>tools/stress.ps1</c>). Resultat i <c>&lt;datakatalog&gt;\stress.log</c>; exit-kod
/// 0 = inga ohanterade undantag och inga kvarlevande fönster.
/// </summary>
internal static class StressTest
{
    private const BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>0 = klart utan ohanterade undantag och utan kvarlevande fönster; 3 = fel; 1 = avbrutet.</summary>
    public static int ExitCode => !_finished && Scenario is null ? 1 : _errors == 0 && _windowsOk ? 0 : 3;

    /// <summary>
    /// FOREVERSCOUT_STRESS_SCENARIO: i stället för den slumpade sekvensen. "close-exit" = öppna fönstret, X och välj
    /// Stäng i frågan; "close-exit-remembered" = samma med "Fråga inte igen" sparat som Stäng. Appen ska då avsluta sig
    /// själv; lever den efter 30 s loggas det och processen avslutas med kod 4.
    /// </summary>
    private static readonly string? Scenario = Environment.GetEnvironmentVariable("FOREVERSCOUT_STRESS_SCENARIO") is { Length: > 0 } sc ? sc : null;

    /// <summary>Minuter från <c>--stress</c> / <c>--stress=N</c>, 0 om flaggan saknas.</summary>
    public static double Minutes(string[] args)
    {
        foreach (var a in args)
        {
            if (a == "--stress") return 10;
            if (a.StartsWith("--stress=", StringComparison.Ordinal) &&
                double.TryParse(a["--stress=".Length..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) && m > 0)
                return m;
        }
        return 0;
    }

    private static ScoutApp _app = null!;
    private static Random _rnd = null!;
    private static DateTime _start, _end;
    private static readonly System.Windows.Forms.Timer Driver = new() { Interval = 50 };
    private static readonly System.Windows.Forms.Timer Closer = new() { Interval = 40 };
    private static readonly System.Windows.Forms.Timer Reporter = new() { Interval = 10_000 };
    private static readonly SortedDictionary<string, int> Counts = new(StringComparer.Ordinal);
    private static readonly List<WeakReference<MainForm>> Windows = new();
    private static readonly Dictionary<object, DateTime> Due = new();
    private static int _depth, _steps, _maxDepth, _errors;
    private static bool _finished, _windowsOk = true;

    public static void Start(ScoutApp app, double minutes)
    {
        _app = app;
        int seed = Environment.GetEnvironmentVariable("FOREVERSCOUT_STRESS_SEED") is { } s && int.TryParse(s, out var v) ? v : Environment.TickCount;
        _rnd = new Random(seed);
        _start = DateTime.UtcNow;
        _end = _start + TimeSpan.FromMinutes(minutes);
        HookErrors();
        Log($"start seed={seed} minutes={minutes.ToString(System.Globalization.CultureInfo.InvariantCulture)} pid={Environment.ProcessId} scenario={Scenario ?? "-"}");
        Driver.Tick += (_, _) => Step();
        Closer.Tick += (_, _) => CloseDialogs();
        Reporter.Tick += (_, _) => Report();
        if (Scenario != null) { RunScenario(); return; }
        Driver.Start();
        Closer.Start();
        Reporter.Start();
    }

    /// <summary>Fel räknas via appens egen hantering (CrashGuard, 1.0.11+) – finns den inte installeras en här.</summary>
    private static void HookErrors()
    {
        void Seen(Exception ex)
        {
            _errors++;
            Log("EXCEPTION " + ex);
        }
        var guard = typeof(StressTest).Assembly.GetType("ForeverScout.CrashGuard");
        var observer = guard?.GetField("Observer", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        if (observer != null) observer.SetValue(null, (Action<Exception>)Seen);
        else Application.ThreadException += (_, e) => Seen(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("FATAL " + e.ExceptionObject);
    }

    private static void RunScenario()
    {
        var step = new System.Windows.Forms.Timer { Interval = 3000 };
        int n = 0;
        step.Tick += (_, _) =>
        {
            switch (n++)
            {
                case 0:
                    _app.ShowWindow();
                    _app.Settings.DontAskOnClose = Scenario == "close-exit-remembered";
                    _app.Settings.CloseAction = "exit";
                    Log("scenario: fönstret öppet, skickar X (SC_CLOSE)");
                    Post(Live(), WM_SYSCOMMAND, SC_CLOSE);
                    if (Scenario == "close-exit") Closer.Start(); // väljer Stäng i frågan
                    break;
                case 10: // 30 s: två ordinarie ticks (15 s) har passerat
                    Log($"scenario: LEVER FORTFARANDE 30 s efter Stäng – Exit kördes aldrig (fel: {_errors}, fönster: {Application.OpenForms.Count})");
                    Environment.Exit(4);
                    break;
            }
        };
        step.Start();
    }

    // ------------------------------------------------------------------ åtgärder

    private static void Step()
    {
        Track();
        if (DateTime.UtcNow >= _end)
        {
            if (_depth == 0 && !AnyDialog()) Finish();
            return;
        }
        Driver.Interval = _rnd.Next(15, 260);
        if (_depth >= 3) return; // redan tre modala loopar djupt: vänta tills någon stängts
        _depth++;
        _maxDepth = Math.Max(_maxDepth, _depth);
        try { Act(); }
        finally { _depth--; }
    }

    private static readonly (int Weight, string Name, Action Run)[] Actions =
    [
        (14, "open", () => Click("_miOpen")),
        (11, "minimize", () => Post(Live(), WM_SYSCOMMAND, SC_MINIMIZE)),
        (9, "close-x", () =>
        {
            _app.Settings.CloseAction = "tray";
            _app.Settings.DontAskOnClose = _rnd.Next(3) > 0; // ibland frågan (CloseDialog)
            Post(Live(), WM_SYSCOMMAND, SC_CLOSE);
        }),
        (5, "minimize+open", () =>
        {
            if (Live() is { } f) f.WindowState = FormWindowState.Minimized; // Resize → HideToTray (rivning köad)
            _app.ShowWindow(); // … och öppna igen innan den köade rivningen hunnit köras
        }),
        (4, "minimize-posted+open", () => { Post(Live(), WM_SYSCOMMAND, SC_MINIMIZE); _app.OnUi(_app.ShowWindow); }),
        (8, "f5", () => Post(Live(), WM_KEYDOWN, VK_F5)),
        (4, "upload-button", () => Field<Button>(Live(), "_upload")?.PerformClick()),
        (5, "tray-upload", () => { _ = _app.TrayMenu; Click("_miUpload"); }),
        (5, "tray-update", () => Click("_miUpdate")),
        (3, "gear-update", () => Gear(2)),
        (4, "tray-settings", () => Click("_miSettings")),
        (2, "gear-settings", () => Gear(0)),
        (3, "tray-payload", () => Click("_miPayload")),
        (2, "gear-payload", () => Gear(1)),
        (2, "about", () => Click("_miAbout")),
        (3, "tray-menu", () => { var m = _app.TrayMenu; m.Show(new Point(_rnd.Next(100, 600), _rnd.Next(100, 500))); }),
        (2, "gear-menu", () =>
        {
            if (Live() is { Visible: true } f && Field<Button>(f, "_gear") is { } g) g.PerformClick();
        }),
        (4, "resize", () =>
        {
            if (Live() is not { Visible: true } f) return;
            if (_rnd.Next(6) == 0) { f.WindowState = _rnd.Next(2) == 0 ? FormWindowState.Maximized : FormWindowState.Normal; return; }
            f.ClientSize = new Size(_rnd.Next(600, 1100), _rnd.Next(540, 900));
        }),
        (2, "pause-toggle", () => Click("_miPause")),
        (6, "force-me+version", () => { SetField("_meAt", DateTime.MinValue); SetField("_versionAt", DateTime.MinValue); }),
        (3, "tick-interval", () => { if (AppField<System.Windows.Forms.Timer>("_tick") is { } t) t.Interval = _rnd.Next(20, 400); }),
        (3, "sync-notify", () => AppField<SyncCoordinator>("_sync")?.Notify()),
        (1, "gc", () => GC.Collect()),
    ];

    /// <summary>FOREVERSCOUT_STRESS_SKIP=namn,namn: åtgärder som hoppas över (t.ex. för att jaga ett annat fel i äldre kod).</summary>
    private static readonly HashSet<string> Skip = new((Environment.GetEnvironmentVariable("FOREVERSCOUT_STRESS_SKIP") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static readonly int TotalWeight = Actions.Where(a => !Skip.Contains(a.Name)).Sum(a => a.Weight);

    private static void Act()
    {
        int r = _rnd.Next(TotalWeight);
        foreach (var (w, name, run) in Actions)
        {
            if (Skip.Contains(name) || (r -= w) >= 0) continue;
            _steps++;
            Count(name);
            run();
            return;
        }
    }

    /// <summary>Fönstret som appen visar just nu (inte ett rivet).</summary>
    private static MainForm? Live() => Application.OpenForms.OfType<MainForm>().FirstOrDefault(f => !f.IsDisposed && f.Visible);

    private static void Click(string field) => AppField<ToolStripMenuItem>(field)?.PerformClick();

    private static void Gear(int index)
    {
        if (Live() is { Visible: true } f && Field<ContextMenuStrip>(f, "_gearMenu") is { } m && index < m.Items.Count) m.Items[index].PerformClick();
    }

    private static T? AppField<T>(string name) where T : class => Field<T>(_app, name);

    private static T? Field<T>(object? owner, string name) where T : class =>
        owner?.GetType().GetField(name, Priv)?.GetValue(owner) as T;

    private static void SetField(string name, object value) => _app.GetType().GetField(name, Priv)?.SetValue(_app, value);

    private static void Post(Control? c, int msg, int wparam)
    {
        if (c is { IsDisposed: false, IsHandleCreated: true }) PostMessage(c.Handle, msg, wparam, 0);
    }

    // ------------------------------------------------------------------ dialoger stängs automatiskt

    private static bool AnyDialog() =>
        Application.OpenForms.Cast<Form>().Any(f => f is not MainForm && f.Visible) || MessageBoxes().Count > 0;

    private static void CloseDialogs()
    {
        var now = DateTime.UtcNow;
        bool Ready(object key)
        {
            if (!Due.TryGetValue(key, out var at)) { Due[key] = now + TimeSpan.FromMilliseconds(_rnd.Next(20, 1200)); return false; }
            if (now < at) return false;
            Due.Remove(key);
            return true;
        }

        foreach (var f in Application.OpenForms.Cast<Form>().ToList())
        {
            if (f is MainForm || f.IsDisposed || !f.Visible || !Ready(f)) continue;
            Count("dialog:" + f.GetType().Name);
            if (f is CloseDialog cd)
            {
                // Aldrig "Stäng" (avsluta): minimera till fältet eller avbryt – utom i scenariot close-exit.
                var choice = Scenario == "close-exit" ? CloseDecision.Exit : _rnd.Next(10) < 7 ? CloseDecision.Tray : CloseDecision.Ask;
                typeof(CloseDialog).GetMethod("Done", Priv)!.Invoke(cd, [choice]);
            }
            else if (f.Modal) f.DialogResult = DialogResult.Cancel;
            else f.Close();
        }

        foreach (var h in MessageBoxes())
        {
            if (!Ready(h)) continue;
            Count("dialog:MessageBox");
            // Nej > Avbryt > OK – aldrig Ja (t.ex. "Installera uppdateringen nu?").
            foreach (var id in new[] { IDNO, IDCANCEL, IDOK })
            {
                var b = GetDlgItem(h, id);
                if (b == IntPtr.Zero) continue;
                PostMessage(h, WM_COMMAND, id, b);
                break;
            }
        }

        foreach (var m in new[] { _app.TrayMenu }.Concat(Application.OpenForms.OfType<MainForm>().Select(f => Field<ContextMenuStrip>(f, "_gearMenu")).OfType<ContextMenuStrip>()))
            if (m is { IsDisposed: false, Visible: true } && Ready(m)) m.Close();

        foreach (var k in Due.Keys.OfType<Control>().Where(c => c.IsDisposed).ToList()) Due.Remove(k);
        foreach (var k in Due.Keys.OfType<IntPtr>().Where(h => !IsWindow(h)).ToList()) Due.Remove(k);
    }

    private static List<IntPtr> MessageBoxes()
    {
        var list = new List<IntPtr>();
        var cls = new StringBuilder(64);
        EnumThreadWindows(GetCurrentThreadId(), (h, _) =>
        {
            cls.Clear();
            if (IsWindowVisible(h) && GetClassName(h, cls, cls.Capacity) > 0 && cls.ToString() == "#32770") list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // ------------------------------------------------------------------ rapport

    private static void Track()
    {
        foreach (var f in Application.OpenForms.OfType<MainForm>())
            if (!Windows.Any(w => w.TryGetTarget(out var t) && ReferenceEquals(t, f))) { Windows.Add(new WeakReference<MainForm>(f)); Count("window-created"); }
    }

    private static int AliveWindows() => Windows.Count(w => w.TryGetTarget(out _));

    private static void Report()
    {
        Windows.RemoveAll(w => !w.TryGetTarget(out _));
        var p = System.Diagnostics.Process.GetCurrentProcess();
        Log(FormattableString.Invariant($"t={(DateTime.UtcNow - _start).TotalSeconds:0} steps={_steps} maxDepth={_maxDepth} errors={_errors} windowsCreated={Get("window-created")} windowsAlive={AliveWindows()} ") +
            FormattableString.Invariant($"ws={p.WorkingSet64 / 1048576.0:0.0}MB private={p.PrivateMemorySize64 / 1048576.0:0.0}MB managed={GC.GetTotalMemory(false) / 1048576.0:0.0}MB gen2={GC.CollectionCount(2)} handles={p.HandleCount} gdi={GetGuiResources(p.Handle, 0)} user={GetGuiResources(p.Handle, 1)}"));
    }

    private static void Finish()
    {
        if (_finished) return;
        _finished = true;
        Driver.Stop(); Closer.Stop(); Reporter.Stop();
        Report();
        // Kvarlevande fönster: bara det som visas (eller är dolt men ännu inte rivet) får finnas kvar efter en full GC.
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        var current = Application.OpenForms.OfType<MainForm>().Count(f => !f.IsDisposed);
        int alive = AliveWindows();
        Log("counts " + string.Join(" ", Counts.Select(kv => kv.Key + "=" + kv.Value)));
        Log($"DONE seconds={(DateTime.UtcNow - _start).TotalSeconds:0} steps={_steps} errors={_errors} windowsCreated={Get("window-created")} windowsAliveAfterGc={alive} windowsOpen={current}");
        _windowsOk = alive <= Math.Max(current, 1);
        // Ibland avslutas appen medan en dialog fortfarande är öppen (som Avsluta i fältmenyn mitt i Inställningar).
        if (_rnd.Next(2) == 0 && Application.OpenForms.OfType<MainForm>().FirstOrDefault() is { IsDisposed: false })
        {
            Log("exit while open: Inställningar + Avsluta");
            _app.OnUi(() => { Click("_miSettings"); });
            var t = new System.Windows.Forms.Timer { Interval = 300 };
            t.Tick += (_, _) => { t.Stop(); Click("_miExit"); };
            t.Start();
            return;
        }
        _app.Exit();
    }

    private static int Get(string k) => Counts.TryGetValue(k, out var n) ? n : 0;

    private static void Count(string k) => Counts[k] = Get(k) + 1;

    public static void Log(string line)
    {
        try { SafeFs.AppendText(Path.Combine(SafeFs.AppDataDir, "stress.log"), DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine); }
        catch (Exception ex) when (ex is IOException or SafeFsException or UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ Win32

    private const int WM_SYSCOMMAND = 0x0112, SC_MINIMIZE = 0xF020, SC_CLOSE = 0xF060, WM_KEYDOWN = 0x0100, VK_F5 = 0x74, WM_COMMAND = 0x0111;
    private const int IDOK = 1, IDCANCEL = 2, IDNO = 7;

    private delegate bool EnumProc(IntPtr hwnd, IntPtr l);

    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, nint w, nint l);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumProc cb, IntPtr l);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);
}
#endif
