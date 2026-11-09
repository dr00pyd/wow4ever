using System.Runtime.InteropServices;
using ForeverScout.Core;

namespace ForeverScout.Tests;

/// <summary>
/// Fönstrets livscykel (1.0.11): fönstret rivs i fältet och byggs upp igen, och inget får nå ett rivet fönster.
/// Körs mot den riktiga ScoutApp/MainForm (utan spelkatalog och utan nyckel – inget nätverk).
/// </summary>
public class WindowLifecycleTests
{
    private const int WM_SYSCOMMAND = 0x0112, SC_MINIMIZE = 0xF020;

    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, nint w, nint l);

    private static void WithApp(Action<ScoutApp> body)
    {
        Exception? err = null;
        var t = new Thread(() =>
        {
            var dir = TestEnv.Temp("fs-window-");
            try
            {
                var settings = Settings.Load(TestEnv.AppData);
                settings.TrayHintShown = true; // ingen bubbla under testet
                using var api = new ApiClient("http://127.0.0.1:9");
                using var app = new ScoutApp(settings, api);
                body(app);
                app.Exit();
            }
            catch (Exception e) { err = e; }
            finally { try { dir.Delete(true); } catch (IOException) { } }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (err != null) throw new Xunit.Sdk.XunitException(err.ToString());
    }

    /// <summary>Kör meddelandekön en stund (BeginInvoke-anrop, Resize m.m.).</summary>
    private static void Pump(int ms = 300)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(5); }
    }

    /// <summary>
    /// Grundorsaken till 1.0.11-kraschen: "Stäng" köade Exit på fönstret som just stängdes (this.BeginInvoke). WinForms
    /// kastar sådana anrop när fönstret disposas, så Exit kördes aldrig: appen levde kvar med ett rivet fönster vars
    /// tabell tömts, och nästa tick kraschade i ShowStats (ArgumentOutOfRangeException).
    /// </summary>
    [Fact]
    public void CloseWithExitDecisionReallyExitsTheApp()
    {
        WithApp(app =>
        {
            app.Settings.DontAskOnClose = true;
            app.Settings.CloseAction = "exit";
            app.ShowWindow();
            var w = app.Window!;
            Pump();
            w.Close();
            Pump();
            Assert.True(w.IsDisposed);
            Assert.True(app.IsExiting, "Exit kördes aldrig efter Stäng");
            Assert.Null(app.Window);
        });
    }

    [Fact]
    public void MinimizeHidesAndReleasesTheWindowAndTheNextOpenBuildsANewOne()
    {
        WithApp(app =>
        {
            app.ShowWindow();
            var first = app.Window!;
            Pump();
            PostMessage(first.Handle, WM_SYSCOMMAND, SC_MINIMIZE, 0);
            Pump();
            Assert.True(first.IsDisposed);
            Assert.Null(app.Window);
            app.RefreshStats(force: true); // som en tick: får inte nå det rivna fönstret
            app.UpdateStatus();
            app.ShowWindow();
            Pump();
            Assert.NotSame(first, app.Window);
            Assert.True(app.Window!.Visible);
            Assert.Equal(FormWindowState.Normal, app.Window.WindowState);
        });
    }

    /// <summary>Öppna igen innan den köade rivningen hunnit köras: tidigare oändlig rekursion (ShowInTaskbar ↔ Resize).</summary>
    [Fact]
    public void ShowingAMinimizedWindowBeforeItIsReleasedDoesNotRecurse()
    {
        WithApp(app =>
        {
            app.ShowWindow();
            var w = app.Window!;
            Pump();
            w.WindowState = FormWindowState.Minimized; // Resize → HideToTray → rivning köad
            app.ShowWindow();                           // … men fönstret öppnas igen direkt
            Pump();
            Assert.Same(w, app.Window);
            Assert.False(w.IsDisposed);
            Assert.True(w.Visible);
            Assert.Equal(FormWindowState.Normal, w.WindowState);
            Assert.True(w.ShowInTaskbar);
        });
    }

    /// <summary>Medan en dialog är öppen göms och rivs fönstret inte (det är dialogens ägare); efteråt rivs det dolda fönstret.</summary>
    [Fact]
    public void WindowIsNeitherHiddenNorReleasedWhileADialogIsOpen()
    {
        WithApp(app =>
        {
            app.ShowWindow();
            var w = app.Window!;
            Pump();
            app.Modal(() =>
            {
                w.WindowState = FormWindowState.Minimized; // t.ex. Win+M medan Inställningar är öppet
                Pump();
                Assert.True(w.Visible);       // bara minimerat, inte gömt
                Assert.True(w.ShowInTaskbar);
                Assert.False(w.IsDisposed);
                return 0;
            });
            Pump();
            Assert.False(w.IsDisposed); // fortfarande synligt (minimerat i aktivitetsfältet) – inget att riva
            w.WindowState = FormWindowState.Normal;
            Pump();
            PostMessage(w.Handle, WM_SYSCOMMAND, SC_MINIMIZE, 0);
            Pump();
            Assert.True(w.IsDisposed);
        });
    }

    [Fact]
    public void AHiddenWindowIsReleasedOnlyWhenTheDialogCloses()
    {
        WithApp(app =>
        {
            app.ShowWindow();
            var w = app.Window!;
            Pump();
            app.Modal(() =>
            {
                w.Hide(); // dolt (som efter HideToTray) medan en dialog från fältmenyn är öppen
                app.OnHiddenToTray();
                Pump();
                Assert.False(w.IsDisposed);
                return 0;
            });
            Pump();
            Assert.True(w.IsDisposed);
            Assert.Null(app.Window);
        });
    }
}
