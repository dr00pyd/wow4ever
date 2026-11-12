using Avalonia;
using Avalonia.Controls;
using ForeverScout.Core;

namespace ForeverScout.Mac;

internal static class Program
{
    /// <summary>
    /// Start: fönstret visas (alla skrivbord har inte fältikoner) och appen ligger sedan i systemfältet. Flaggor:
    /// --tray (autostart vid inloggning: inget fönster), --restarted (efter språkbyte eller frånkoppling).
    /// En instans per användare: flock-lås + Unix-socket i ~/.config/ForeverScout (UnixInstance) – startas appen en
    /// gång till visar den körande instansen sitt fönster.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("Forever Scout för Linux – Windows och macOS har egna klienter (client/windows, client/mac).");
            return 1;
        }
        bool restarted = args.Contains("--restarted");
        var settings = Settings.Load();
        Strings.Apply(settings.Language);

        string? message = null;
        // Efter en omstart: vänta på att den gamla processen släpper låset (den avslutar sig själv).
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
        using var api = new ApiClient { Key = message is null ? KeychainStore.Load() : null };
        App.ShowAtStart = !args.Contains("--tray");
        App.Startup = new StartupState(settings, api, instance, restarted, message, Rules112.UserStarted(args));

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
