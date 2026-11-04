namespace ForeverScout.Core;

/// <summary>
/// En instans per användare: en mutex, plus en namngiven händelse som en andra start signalerar
/// så att den första instansen tar fram sitt fönster.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly RegisteredWaitHandle? _wait;
    private bool _owned;

    private SingleInstance(Mutex mutex, EventWaitHandle activate, Action onActivate)
    {
        _mutex = mutex;
        _activate = activate;
        _owned = true;
        _wait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public static string MutexName(string id) => $@"Local\{id}.SingleInstance";
    public static string EventName(string id) => $@"Local\{id}.Activate";

    /// <summary>
    /// Försöker bli den enda instansen. Lyckas det returneras en instans som anropar <paramref name="onActivate"/>
    /// när någon annan startar programmet. Annars signaleras den befintliga instansen och null returneras.
    /// <paramref name="waitForPrevious"/> används efter en uppdatering (den gamla processen håller på att avslutas).
    /// </summary>
    public static SingleInstance? TryAcquire(string id, Action onActivate, TimeSpan? waitForPrevious = null)
    {
        var mutex = new Mutex(false, MutexName(id));
        bool got;
        try { got = mutex.WaitOne(waitForPrevious ?? TimeSpan.Zero); }
        catch (AbandonedMutexException) { got = true; }
        if (!got)
        {
            mutex.Dispose();
            SignalExisting(id);
            return null;
        }
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, EventName(id));
        return new SingleInstance(mutex, ev, onActivate);
    }

    /// <summary>Ber den körande instansen att visa sitt fönster. Returnerar false om ingen lyssnar.</summary>
    public static bool SignalExisting(string id)
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(EventName(id));
            return ev.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _activate.Dispose();
        if (_owned) { try { _mutex.ReleaseMutex(); } catch (ApplicationException) { } _owned = false; }
        _mutex.Dispose();
    }
}

/// <summary>Varför fönstret stängs (motsvarar WinForms CloseReason, men utan WinForms-beroende).</summary>
public enum CloseCause { UserClosing, WindowsShutDown, TaskManagerClosing, ApplicationExit, Other }

public enum CloseDecision
{
    /// <summary>Låt stängningen ske (avsluta appen).</summary>
    Exit,
    /// <summary>Avbryt stängningen och göm fönstret i fältet.</summary>
    Tray,
    /// <summary>Visa frågan "Vill du verkligen stänga …?".</summary>
    Ask,
}

/// <summary>Vad krysset (X) ska göra. Windows-avstängning/utloggning och Aktivitetshanteraren stoppas aldrig.</summary>
public static class CloseBehavior
{
    public static CloseDecision Decide(CloseCause cause, bool exitRequested, Settings settings)
    {
        if (exitRequested) return CloseDecision.Exit;                    // "Avsluta" i menyn: fråga inte
        if (cause != CloseCause.UserClosing) return CloseDecision.Exit;  // avstängning, utloggning, Aktivitetshanteraren …
        if (!settings.DontAskOnClose) return CloseDecision.Ask;
        return settings.CloseAction == "exit" ? CloseDecision.Exit : CloseDecision.Tray;
    }

    /// <summary>Sparar svaret från frågan (om "Fråga inte igen" kryssats).</summary>
    public static void Remember(Settings settings, CloseDecision choice, bool dontAskAgain)
    {
        if (!dontAskAgain || choice == CloseDecision.Ask) return;
        settings.DontAskOnClose = true;
        settings.CloseAction = choice == CloseDecision.Exit ? "exit" : "tray";
    }
}
