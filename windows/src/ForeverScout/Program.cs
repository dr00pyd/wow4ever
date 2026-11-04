using ForeverScout.Core;

namespace ForeverScout;

internal static class Program
{
    private const string InstanceId = "ForeverScout";

    /// <summary>
    /// Start: appen går direkt till fältet (ingen fönster), utom vid allra första starten då första-start-fönstret visas.
    /// Flaggor: --tray / --minimized (autostart, samma sak), --restarted (efter uppdatering/språkbyte),
    /// --screenshots (sparar skärmbilder i %APPDATA%\ForeverScout\screenshots och avslutar).
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        bool shots = args.Contains("--screenshots");

        ScoutApp? app = null;
        void Activate() => app?.OnUi(() => app.ShowWindow());
        var instance = SingleInstance.TryAcquire(InstanceId, Activate, args.Contains("--restarted") ? TimeSpan.FromSeconds(10) : null);

        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.Dark);
        var settings = Settings.Load();
        Strings.Apply(settings.Language);

        if (instance is null)
        {
            // En instans körs redan. Fråga den (1.0.3+) via pipen: är vi nyare eller en annan exe tar vi över.
            var me = new TakeoverRequest(BuildInfo.Version.ToString(3), Environment.ProcessPath ?? "");
            switch (Takeover.Request(InstanceId, me, TimeSpan.FromSeconds(3)))
            {
                case TakeoverOutcome.TakeOver:
                    instance = SingleInstance.TryAcquire(InstanceId, Activate, TimeSpan.FromSeconds(15));
                    if (instance is null) { MessageBox.Show(Strings.S("takeover.old"), "Forever Scout", MessageBoxButtons.OK, MessageBoxIcon.Information); return 0; }
                    break;
                case TakeoverOutcome.Shown:
                    return 0; // den körande instansen visar sitt fönster
                default:
                    // Ingen svarar: en äldre version (1.0.0–1.0.2) som inte kan lämna över. Säg det tydligt.
                    MessageBox.Show(Strings.S("takeover.old"), "Forever Scout", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
            }
        }
        using var _instance = instance;

        SelfUpdater.CleanupOld();
        using var api = new ApiClient { Key = KeyStore.Load() };

        bool firstRun = false;
        if (string.IsNullOrEmpty(api.Key))
        {
            using var first = new FirstRunForm(api);
            if (first.ShowDialog() != DialogResult.OK || first.Result is not { } who) return 0;
            KeyStore.Save(who.Key);
            api.Key = who.Key;
            settings.ContributorId = who.Id;
            settings.ContributorName = who.Name;
            settings.Save();
            firstRun = true;
        }

        using var scout = new ScoutApp(settings, api);
        app = scout;
        // Lämna över till en nyare (eller annan) exe som startas medan vi kör.
        using var takeover = Takeover.Listen(
            InstanceId,
            req => Takeover.Decide(BuildInfo.Version, Environment.ProcessPath ?? "", req),
            decision => scout.OnUi(() => { if (decision == TakeoverDecision.Exit) scout.Exit(); else scout.ShowWindow(); }));
        bool showWindow = firstRun || shots; // annars: bara ikonen i fältet
        scout.OnUi(async () =>
        {
            if (shots) { await Screenshots.Run(scout); return; }
            await scout.StartAsync(showWindow);
        });
        Application.Run(scout);
        return 0;
    }
}
