using Avalonia;
using Avalonia.Controls;
using ForeverScout.Core;

namespace ForeverScout.Mac;

internal static class Program
{
    /// <summary>
    /// Start: appen går direkt till menyraden (inget fönster, ingen Dock-ikon – LSUIElement), utom vid allra första
    /// starten då onboarding visas. Flaggor: --tray (inloggning, samma sak), --restarted (efter uppdatering/språkbyte).
    /// En instans per användare: flock-lås + Unix-socket i ~/Library/Application Support/ForeverScout (UnixInstance).
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine("Forever Scout för macOS – Windows har en egen klient (client/windows).");
            return 1;
        }
        bool restarted = args.Contains("--restarted");
        var settings = Settings.Load();
        Strings.Apply(settings.Language);
        _ = SafeFs.OwnBundle; // läses en gång, före ett eventuellt paketbyte

        string? message = null;
        // Efter en uppdatering/omstart: vänta på att den gamla processen släpper låset (den avslutar sig själv).
        var instance = UnixInstance.TryAcquire(restarted ? TimeSpan.FromSeconds(30) : null);
        if (instance is null)
        {
            var me = new TakeoverRequest(BuildInfo.Version.ToString(3), Environment.ProcessPath ?? "");
            switch (UnixInstance.Request(me, TimeSpan.FromSeconds(3)))
            {
                case TakeoverOutcome.TakeOver:
                    instance = UnixInstance.TryAcquire(TimeSpan.FromSeconds(15));
                    if (instance is null) message = Texts.T("takeover.old");
                    break;
                case TakeoverOutcome.Shown:
                    return 0; // den körande instansen visar sitt fönster
                default:
                    message = Texts.T("takeover.old");
                    break;
            }
        }

        using var _instance = instance;
        IoRetry.OnRetry = (step, attempt, ex) => UploadLog.Append("update", 0, "-", $"retry step={step} attempt={attempt} {ex.GetType().Name} 0x{ex.HResult:X8}");
        if (instance != null) MacBundleUpdater.CleanupOld();
        using var api = new ApiClient { Key = message is null ? KeychainStore.Load() : null };
        App.Startup = new StartupState(settings, api, instance, restarted, message, Rules112.UserStarted(args));

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    /// <summary>Menyradsapp: ingen Dock-ikon (Info.plist har också LSUIElement=true).</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .LogToTrace();
}
