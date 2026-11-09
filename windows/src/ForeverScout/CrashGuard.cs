using ForeverScout.Core;
using static ForeverScout.Core.Strings;

namespace ForeverScout;

/// <summary>
/// Oväntade undantag (1.0.11). Ett fel på UI-tråden – i en tick, en händelse eller en async-fortsättning – avslutar inte
/// appen och visar inte .NET:s feldialog ("Unhandled exception … Continue/Quit"). Det döljs inte heller: varje fel
/// skrivs i uploads.log ("error", <see cref="CrashReport.Describe"/>: typ, HResult och metodnamn, aldrig meddelandet
/// som kan innehålla sökvägar) och som en rad i lägerjournalen (samma fel högst en gång per minut). Fel på andra
/// trådar kan inte stoppas – processen avslutas – men loggas först ("crash"). Obevakade Task-fel loggas ("error").
/// </summary>
internal static class CrashGuard
{
    private static Func<ScoutApp?> _app = () => null;
    private static readonly ErrorThrottle Journal = new(TimeSpan.FromMinutes(1));
    private static bool _inHandler;

    /// <summary>Antal hanterade fel på UI-tråden under den här körningen.</summary>
    public static int Count { get; private set; }

#if DEBUG
    /// <summary>Bara Debug: stresstestet räknar och loggar varje fel med hela stacken.</summary>
    internal static Action<Exception>? Observer;
#endif

    /// <summary>Anropas först i Main, innan något fönster skapats (krav för SetUnhandledExceptionMode).</summary>
    public static void Install(Func<ScoutApp?> app)
    {
        _app = app;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => OnUiException(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) UploadLog.Append("crash", 0, "-", CrashReport.Describe(ex));
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            UploadLog.Append("error", 0, "-", "unobserved task: " + CrashReport.Describe(e.Exception));
            e.SetObserved();
        };
    }

    private static void OnUiException(Exception ex)
    {
        Count++;
#if DEBUG
        Observer?.Invoke(ex);
#endif
        UploadLog.Append("error", 0, "-", CrashReport.Describe(ex));
        if (_inHandler) return; // fel i själva journalraden: bara loggen
        _inHandler = true;
        try
        {
            if (_app() is { IsExiting: false } app && Journal.ShouldReport(CrashReport.Signature(ex), DateTime.UtcNow, out int more))
                app.Log(F("journal.internal", When(DateTime.UtcNow), ex.GetType().Name + (more > 0 ? $" ×{more + 1}" : "")), Theme.Err);
        }
        catch (Exception inner)
        {
            UploadLog.Append("error", 0, "-", "while reporting: " + CrashReport.Describe(inner));
        }
        finally { _inHandler = false; }
    }
}
