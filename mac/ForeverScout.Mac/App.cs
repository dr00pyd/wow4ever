using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using ForeverScout.Core;
using static ForeverScout.Mac.Texts;

namespace ForeverScout.Mac;

/// <summary>Avalonia-appen (ingen XAML): mörkt Fluent-tema, sedan onboarding vid första starten och menyradsappen.</summary>
internal sealed class App : Application
{
    /// <summary>Satt av Program innan Avalonia startar.</summary>
    internal static StartupState? Startup { get; set; }

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
        Name = "Forever Scout";
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Startup is { } s)
            Dispatcher.UIThread.Post(async () => await Run(desktop, s));
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task Run(IClassicDesktopStyleApplicationLifetime desktop, StartupState s)
    {
        // Bara ett meddelande (t.ex. en annan instans körs redan) – visa det och avsluta.
        if (s.Message is { } msg)
        {
            await Dialogs.Info(null, msg);
            desktop.Shutdown();
            return;
        }

        bool firstRun = false;
        if (string.IsNullOrEmpty(s.Api.Key))
        {
            var first = new OnboardingWindow(s.Api);
            var closed = new TaskCompletionSource();
            first.Closed += (_, _) => closed.TrySetResult();
            first.Show();
            first.Activate();
            await closed.Task;
            if (first.Result is not { } who) { desktop.Shutdown(); return; }
            // Samma väg för namn, kod och webbläsaren. Nyckelringen kan vägra – då körs appen ändå (nyckeln i minnet).
            bool keyFailed = false;
            ContributorStore.Save(who, s.Api, s.Settings, key =>
            {
                try { KeychainStore.Save(key); }
                catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) { keyFailed = true; }
            });
            if (keyFailed) await Dialogs.Info(null, Strings.S("err.io"));
            firstRun = true;
        }

        var scout = new ScoutController(desktop, s.Settings, s.Api);
        desktop.Exit += (_, _) => scout.Dispose();
        // Lämna över till en nyare (eller annan) kopia som startas medan vi kör.
        s.Instance?.Listen(
            req => Takeover.Decide(BuildInfo.Version, Environment.ProcessPath ?? "", req),
            decision => Dispatcher.UIThread.Post(() => { if (decision == TakeoverDecision.Exit) scout.Exit(); else scout.ShowWindow(); }));

        var current = SelfUpdater.CurrentVersion.ToString(3);
        var (updated, updatedFrom) = SelfUpdater.DetectUpdate(s.Settings.JustUpdatedFrom, s.Settings.LastRunVersion, current, s.Restarted);
        s.Settings.JustUpdatedFrom = null;
        s.Settings.LastRunVersion = current;
        s.Settings.Save();
        if (updated) scout.AnnounceUpdate(updatedFrom);
        await scout.StartAsync(showWindow: firstRun || s.Restarted || updated);
    }
}

/// <summary>Det Program har förberett: inställningar, API-klienten, instanslåset och startflaggor.</summary>
internal sealed record StartupState(Settings Settings, ApiClient Api, UnixInstance? Instance, bool Restarted, string? Message);
