using System.Net;
using ForeverScout.Core;

namespace ForeverScout;

/// <summary>
/// --screenshots: sparar scout-v1-*.png i %APPDATA%\ForeverScout\screenshots (för granskning). Lägena uppladdning, fel
/// och tomt tillstånd simuleras i UI:t (ingen falsk serverdata); 150 % DPI simuleras genom att skala fönstret och typsnitten.
/// </summary>
internal static class Screenshots
{
    public static async Task Run(ScoutApp app)
    {
        var dir = Path.Combine(SafeFs.AppDataDir, "screenshots");
        SafeFs.CreateDirectory(dir);
        string P(string name) => Path.Combine(dir, $"scout-v1-{name}.png");

        // ---------- Onboarding ----------
        using (var first = new FirstRunForm(app.Api) { StartPosition = FormStartPosition.CenterScreen })
        {
            first.Show();
            first.SetName("j0n550n");
            await Task.Delay(700);
            MainForm.SaveImage(first, P("onboarding"));
            first.ShowError(new ApiException("name-taken", HttpStatusCode.Conflict) { FromServer = true });
            await Task.Delay(400);
            MainForm.SaveImage(first, P("onboarding-name-taken"));
            first.Hide();
        }

        // ---------- Dashboard ----------
        app.ApplyGameDir();
        app.RefreshStats(force: true);
        await app.RefreshMe();
        app.Log(Strings.F("journal.addon", AddonBundle.Version), Theme.Ok);
        app.Log(Strings.F("journal.uploaded", Strings.When(DateTime.UtcNow), 1) + " – " + Strings.S("humor.2"), Theme.Ok);
        app.UpdateStatus();
        app.ShowWindow();
        var main = app.Window!;
        main.ClientSize = new Size(main.LogicalToDeviceUnits(940), main.LogicalToDeviceUnits(800));
        await Task.Delay(800);
        MainForm.SaveImage(main, P("dashboard"));

        app.SimulateUploading(true);
        await Task.Delay(400);
        MainForm.SaveImage(main, P("uploading"));
        app.SimulateUploading(false);

        app.SimulateError(Strings.Error(new ApiException(ErrorCodes.Network, transient: false)));
        await Task.Delay(400);
        MainForm.SaveImage(main, P("error"));
        app.SimulateError(null);

        main.ClientSize = new Size(main.LogicalToDeviceUnits(640), main.LogicalToDeviceUnits(600));
        await Task.Delay(600);
        MainForm.SaveImage(main, P("small"));

        using (var dlg = new CloseDialog { StartPosition = FormStartPosition.Manual })
        {
            main.ClientSize = new Size(main.LogicalToDeviceUnits(940), main.LogicalToDeviceUnits(800));
            dlg.Show(main);
            dlg.Location = new Point(main.Left + (main.Width - dlg.Width) / 2, main.Top + (main.Height - dlg.Height) / 2);
            await Task.Delay(500);
            MainForm.SaveImage(dlg, P("close-dialog"));
            dlg.Hide();
        }

        var menu = app.TrayMenu;
        menu.Show(new Point(main.Right - 40, main.Top + 120));
        await Task.Delay(500);
        MainForm.SaveImage(menu, P("tray-menu"));
        menu.Close();

        // ---------- 150 % (simulerad: fönster och typsnitt skalas som vid en DPI-ändring) ----------
        using (var big = new MainForm(app) { StartPosition = FormStartPosition.Manual, Location = new Point(10, 10) })
        {
            big.Show();
            ScaleFonts(big, 1.5f);
            big.Scale(new SizeF(1.5f, 1.5f));
            big.ClientSize = new Size(1410, 1170);
            await Task.Delay(900);
            MainForm.SaveImage(big, P("dpi150"));
            big.Hide();
        }

        // ---------- Tomt tillstånd (sist – ändrar bara visningen i minnet, inget sparas) ----------
        app.SimulateEmpty();
        await Task.Delay(500);
        MainForm.SaveImage(main, P("empty"));

        app.Exit();
    }

    private static void ScaleFonts(Control c, float f)
    {
        c.Font = new Font(c.Font.FontFamily, c.Font.Size * f, c.Font.Style);
        if (c is DataGridView g)
        {
            g.ColumnHeadersDefaultCellStyle.Font = Theme.Ui(8.25f * f, FontStyle.Bold);
            g.DefaultCellStyle.Font = Theme.Ui(10f * f);
            for (int i = 1; i < g.Columns.Count; i++) g.Columns[i].DefaultCellStyle.Font = Theme.Ui(10f * f);
            g.Columns[0].DefaultCellStyle.Font = Theme.Ui(10f * f, FontStyle.Bold);
            g.ColumnHeadersHeight = (int)(g.ColumnHeadersHeight * f);
            foreach (DataGridViewRow r in g.Rows) r.Height = (int)(r.Height * f);
        }
        foreach (Control child in c.Controls) ScaleFonts(child, f);
    }
}
