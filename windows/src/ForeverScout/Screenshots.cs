using ForeverScout.Core;

namespace ForeverScout;

/// <summary>--screenshots: sparar scout-*.png i %APPDATA%\ForeverScout\screenshots (för granskning).</summary>
internal static class Screenshots
{
    public static async Task Run(ScoutApp app)
    {
        var dir = Path.Combine(SafeFs.AppDataDir, "screenshots");
        SafeFs.CreateDirectory(dir);
        app.ApplyGameDir();
        app.RefreshStats(force: true);
        await app.RefreshMe();
        app.UpdateStatus();
        app.ShowWindow();
        var main = app.Window!;
        await Task.Delay(700);
        MainForm.SaveImage(main, Path.Combine(dir, "scout-client.png"));

        using (var dlg = new CloseDialog { StartPosition = FormStartPosition.Manual })
        {
            dlg.Show(main);
            dlg.Location = new Point(main.Left + (main.Width - dlg.Width) / 2, main.Top + (main.Height - dlg.Height) / 2);
            await Task.Delay(500);
            MainForm.SaveImage(dlg, Path.Combine(dir, "scout-close-dialog.png"));
            dlg.Hide();
        }

        using (var pf = new PayloadForm(UploadPreview.Build(app.Api, app.CurrentDir, app.Settings)) { StartPosition = FormStartPosition.Manual })
        {
            pf.Show(main);
            pf.Location = new Point(main.Left + 30, main.Top + 30);
            await Task.Delay(500);
            MainForm.SaveImage(pf, Path.Combine(dir, "scout-payload.png"));
            pf.Hide();
        }

        var menu = app.TrayMenu;
        menu.Show(new Point(main.Right - 40, main.Top + 120));
        await Task.Delay(500);
        MainForm.SaveImage(menu, Path.Combine(dir, "scout-tray-menu.png"));
        menu.Close();

        app.Exit();
    }
}
