using System.Runtime.Versioning;

namespace ForeverScout.Core;

/// <summary>
/// macOS: byter ut hela <c>Forever Scout.app</c> mot en ny version. Motsvarigheten till <see cref="SelfUpdater"/>.
/// <list type="number">
/// <item>Zip-filen (macFile) har redan kontrollerats mot den signerade versionsinfon (signature3): SHA-256 och vår
/// ECDSA-filsignatur (<see cref="ApiClient.DownloadReleaseFileAsync"/>). Apples kodsignatur och notarisering ligger
/// under vår signatur – vi signerar den färdiga, notariserade och staplade zip:en.</item>
/// <item><see cref="Stage"/>: zip:en packas upp med <c>ditto</c> i <c>.ForeverScout-update/new/</c> bredvid paketet
/// (samma volym). Är den körande appen kodsignerad måste den nya ha giltig kodsignatur med samma Team ID och samma
/// paket-id (<c>codesign --verify --deep --strict -R=…</c>).</item>
/// <item><see cref="Swap"/>: det körande paketet döps om till <c>.ForeverScout-update/old.app</c> (tillåtet medan
/// appen kör) och det nya flyttas in på samma plats. Misslyckas andra steget flyttas det gamla tillbaka.</item>
/// <item>Anroparen startar den nya binären med <c>--restarted</c> och avslutar sig; nästa start städar bort
/// <c>.ForeverScout-update</c> (<see cref="CleanupOld"/>).</item>
/// </list>
/// Ingen kod laddas ner och körs utanför det signerade paketet, inga skript, ingen admin. Kräver att appen ligger
/// där användaren får skriva (Program/Applications för en administratör, eller ~/Applications).
/// </summary>
[UnsupportedOSPlatform("windows")]
public static class MacBundleUpdater
{
    /// <summary>Det körande paketet (…/Forever Scout.app).</summary>
    public static string Bundle => SafeFs.OwnBundle ?? throw new ScoutException(ErrorCodes.NoBundle);

    public static string WorkDir => SafeFs.BundleUpdateDir ?? throw new ScoutException(ErrorCodes.NoBundle);

    /// <summary>Binären som startas efter bytet (universell startare eller själva appen).</summary>
    public static string ExecutableIn(string bundle) => bundle + "/Contents/MacOS/ForeverScout";

    /// <summary>
    /// Kör appen från en tillfällig, skrivskyddad kopia (Gatekeepers "App Translocation", t.ex. direkt ur
    /// Hämtade filer)? Då går paketet inte att byta – användaren måste flytta appen till Program först.
    /// </summary>
    public static bool IsTranslocated(string bundle) => bundle.Contains("/AppTranslocation/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Apples krav på den nya versionens kodsignatur: samma paket-id och samma utvecklare (Team ID).</summary>
    public static string Requirement(string teamId) =>
        $"identifier \"{SafeFs.MacBundleId}\" and anchor apple generic and certificate leaf[subject.OU] = \"{teamId}\"";

    /// <summary>Tar bort rester från ett tidigare byte (bästa försök).</summary>
    public static void CleanupOld()
    {
        try
        {
            if (SafeFs.BundleUpdateDir is { } d) SafeFs.DeleteDirectory(d);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SafeFsException) { }
    }

    private static UpdateStepException Fail(string step, Exception ex, string path) => new(step, ex, Path.GetFileName(path));

    /// <summary>Packar upp och kontrollerar den nya versionen. Returnerar sökvägen till det nya paketet.</summary>
    public static string Stage(byte[] zip)
    {
        var bundle = Bundle;
        if (IsTranslocated(bundle)) throw new ScoutException(ErrorCodes.AppTranslocated);
        var work = WorkDir;
        string staged;
        try
        {
            IoRetry.Run(UpdateSteps.Stage, () => SafeFs.DeleteDirectory(work));
            SafeFs.CreateDirectory(work);
            var zipPath = work + "/update.zip";
            SafeFs.WriteAllBytes(zipPath, zip);
            var newDir = work + "/new";
            SafeFs.CreateDirectory(newDir);
            SafeFs.MacExtractZip(zipPath, newDir);
            SafeFs.DeleteFile(zipPath);
            var apps = SafeFs.GetDirectories(newDir).Where(d => d.EndsWith(".app", StringComparison.OrdinalIgnoreCase)).ToList();
            if (apps.Count != 1) throw new ScoutException(ErrorCodes.BadResponse, "app-count");
            staged = apps[0];
            if (!SafeFs.FileExists(ExecutableIn(staged)) || !SafeFs.FileExists(staged + "/Contents/Info.plist"))
                throw new ScoutException(ErrorCodes.BadResponse, "app-layout");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw Fail(UpdateSteps.Stage, ex, bundle); }

        // Kodsignaturen: är den körande appen signerad (Developer ID) måste den nya vara det med samma Team ID.
        if (SafeFs.MacCodesignTeam(bundle) is { } team && !SafeFs.MacVerifyCodeSignature(staged, Requirement(team)))
            throw new ScoutException(ErrorCodes.CodeSignatureInvalid);
        return staged;
    }

    /// <summary>Byter paketen (med omförsök och återställning). Anroparen startar sedan den nya binären.</summary>
    public static void Swap(string staged)
    {
        var bundle = Bundle;
        var old = WorkDir + "/old.app";
        try { SafeFs.DeleteDirectory(old); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw Fail(UpdateSteps.SwapDelete, ex, old); }
        try { IoRetry.Run(UpdateSteps.SwapMove1, () => SafeFs.MoveDirectory(bundle, old)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw Fail(UpdateSteps.SwapMove1, ex, bundle); }
        try { IoRetry.Run(UpdateSteps.SwapMove2, () => SafeFs.MoveDirectory(staged, bundle)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { IoRetry.Run(UpdateSteps.SwapMove2, () => SafeFs.MoveDirectory(old, bundle)); }
            catch (Exception rb) when (rb is IOException or UnauthorizedAccessException) { /* rapporteras via det första felet */ }
            throw Fail(UpdateSteps.SwapMove2, ex, staged);
        }
    }
}
