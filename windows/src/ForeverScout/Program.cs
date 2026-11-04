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
        using var instance = SingleInstance.TryAcquire(
            InstanceId,
            () => app?.OnUi(() => app.ShowWindow()),
            args.Contains("--restarted") ? TimeSpan.FromSeconds(10) : null);
        if (instance is null) return 0; // en instans körs redan – den har fått signal att visa sitt fönster

        SelfUpdater.CleanupOld();
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.Dark);

        var settings = Settings.Load();
        Strings.Apply(settings.Language);
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
