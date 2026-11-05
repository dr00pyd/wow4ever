namespace ForeverScout.Core;

/// <summary>En spelkatalog (t.ex. "...\World of Warcraft\_forever_").</summary>
public sealed record GameDir(string Path, string Flavor)
{
    public string AddOnsDir => System.IO.Path.Combine(Path, "Interface", "AddOns");
    public string AccountDir => System.IO.Path.Combine(Path, "WTF", "Account");
    public string AdbDir => System.IO.Path.Combine(Path, "Cache", "ADB");
    public override string ToString() => Path;
}

/// <summary>
/// Letar upp Forever-katalogen (<c>_forever_</c>). Får BARA: (1) läsa registervärdet InstallPath under
/// HKLM\SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft, (2) kontrollera om standardsökvägar finns,
/// (3) ta emot användarens eget val. Ingen Battle.net-konfiguration, ingen product.db, inga processer, inget
/// filinnehåll – bara existens och kataloglistning via <see cref="SafeFs"/>.
/// </summary>
public static class WowLocator
{
    public const string ForeverFlavor = "_forever_";

    /// <summary>Alla tänkbara spelkataloger, bäst först (Forever-katalogen överst).</summary>
    public static List<GameDir> Locate(IEnumerable<string>? extraRoots = null)
    {
        var roots = new List<string>();
        if (extraRoots != null) roots.AddRange(extraRoots);
        if (SafeFs.ReadWowInstallPath() is { Length: > 0 } install) roots.Add(install);   // 1. registret
        roots.AddRange(SafeFs.StandardWowRoots());                                        // 2. standardsökvägar
        return FindGameDirs(roots);                                                       // 3. annars väljer användaren
    }

    /// <summary>Ur rot- eller spelkataloger: hitta alla spelkataloger (unika), sorterade efter trolighet.</summary>
    public static List<GameDir> FindGameDirs(IEnumerable<string> roots)
    {
        var found = new Dictionary<string, GameDir>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in roots)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string root;
            try { root = SafeFs.Normalize(raw.Trim().Trim('"')); } catch (SafeFsException) { continue; }
            // Registret pekar ofta på en flavor-katalog (…\_retail_\): sök då i föräldern.
            var probe = IsFlavorName(System.IO.Path.GetFileName(root)) && System.IO.Path.GetDirectoryName(root) is { } parent ? parent : root;
            var real = SafeFs.AllowProbeRoot(probe);
            if (real is null || !SafeFs.DirectoryExists(real)) continue;
            foreach (var dir in FlavorDirs(real))
                found.TryAdd(dir, new GameDir(dir, System.IO.Path.GetFileName(dir)));
        }
        return found.Values.OrderBy(Rank).ThenBy(d => d.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Underkataloger vars namn börjar eller slutar med '_' och som innehåller Interface eller WTF.</summary>
    public static IEnumerable<string> FlavorDirs(string root)
    {
        foreach (var d in SafeFs.GetDirectories(root))
            if (IsFlavorName(System.IO.Path.GetFileName(d)) && LooksLikeGameDir(d)) yield return d;
    }

    public static bool IsFlavorName(string name) => name.Length > 1 && (name.StartsWith('_') || name.EndsWith('_'));

    /// <summary>Innehåller Interface eller WTF – eller är en (ännu tom) _forever_ bredvid rotens .build.info.</summary>
    public static bool LooksLikeGameDir(string dir)
    {
        if (SafeFs.DirectoryExists(System.IO.Path.Combine(dir, "Interface")) || SafeFs.DirectoryExists(System.IO.Path.Combine(dir, "WTF"))) return true;
        if (!System.IO.Path.GetFileName(dir).Equals(ForeverFlavor, StringComparison.OrdinalIgnoreCase)) return false;
        return System.IO.Path.GetDirectoryName(dir) is { } root && SafeFs.FileExists(System.IO.Path.Combine(root, ".build.info"));
    }

    /// <summary>Låter användarens eget val (eller en sparad katalog) prövas: registrerar den och föräldern som sökrötter.</summary>
    public static bool IsUsableGameDir(string dir)
    {
        try
        {
            var n = SafeFs.Normalize(dir);
            SafeFs.AllowProbeRoot(n);
            if (System.IO.Path.GetDirectoryName(n) is { } parent) SafeFs.AllowProbeRoot(parent);
            return SafeFs.DirectoryExists(n) && LooksLikeGameDir(n);
        }
        catch (SafeFsException) { return false; }
    }

    /// <summary>Vad appen ska göra med spelkatalogen vid start (<see cref="NextStep"/>).</summary>
    public enum StartStep
    {
        /// <summary>Den sparade katalogen är vald/bekräftad av användaren – använd den.</summary>
        Ready,
        /// <summary>Fråga användaren om katalogen (en gång): den hittades automatiskt eller är sparad av ≤ 1.0.6.</summary>
        Confirm,
        /// <summary>Ingen (eller flera) kandidater och ingen katalog sparad – öppna väljaren.</summary>
        Pick,
        /// <summary>Inget att göra nu (t.ex. sparad katalog finns inte just nu – felet visas i status).</summary>
        Nothing,
    }

    /// <summary>
    /// L11: en automatiskt hittad katalog används aldrig utan att användaren bekräftat den en gång – en annan lokal
    /// användare med skrivrätt i spelets mapp skulle annars kunna plantera en <c>_forever_</c> och få offrets nyckel att
    /// ladda upp sin data. Ren funktion (testas); appen visar frågan och sparar svaret.
    /// </summary>
    /// <param name="usable">Finns katalogen och ser den ut som en spelkatalog? (<see cref="IsUsableGameDir"/>)</param>
    /// <param name="locate">Automatisk sökning (<see cref="Locate"/>) – anropas bara när den behövs.</param>
    public static (StartStep Step, string? Dir) NextStep(Settings s, Func<string, bool> usable, Func<List<GameDir>> locate)
    {
        if (!string.IsNullOrEmpty(s.GameDir) && usable(s.GameDir))
            return s.IsGameDirConfirmed() ? (StartStep.Ready, s.GameDir) : (StartStep.Confirm, s.GameDir);
        var best = locate().Where(d => Rank(d) == 0 && !string.Equals(d.Path, s.GameDirDeclined, StringComparison.OrdinalIgnoreCase)).ToList();
        if (best.Count == 1) return (StartStep.Confirm, best[0].Path);
        // Flera eller ingen: fråga bara om ingen katalog någonsin valts (annars visas felet i status).
        return string.IsNullOrEmpty(s.GameDir) ? (StartStep.Pick, null) : (StartStep.Nothing, null);
    }

    /// <summary>Lägre = bättre: _forever_ först, sedan allt med "forever", sedan övriga.</summary>
    public static int Rank(GameDir d)
    {
        var f = d.Flavor.ToLowerInvariant();
        if (f == ForeverFlavor) return 0;
        if (f.Contains("forever")) return 1;
        if (f is "_retail_" or "_ptr_" or "_beta_" or "_xptr_" or "_classic_" or "_classic_era_") return 3;
        return 2;
    }

    /// <summary>Alla ForeverScout.lua under WTF\Account\*\SavedVariables. Kontonamnet i sökvägen visas/skickas aldrig.</summary>
    public static List<string> SavedVariableFiles(GameDir dir)
    {
        var list = new List<string>();
        try
        {
            foreach (var acc in SafeFs.GetDirectories(dir.AccountDir))
            {
                var f = System.IO.Path.Combine(acc, "SavedVariables", "ForeverScout.lua");
                if (SafeFs.FileExists(f)) list.Add(f);
            }
        }
        catch (SafeFsException) { }
        return list;
    }

    /// <summary>Alla Cache\ADB\&lt;locale&gt;\DBCache.bin.</summary>
    public static List<string> HotfixFiles(GameDir dir)
    {
        var list = new List<string>();
        try
        {
            foreach (var loc in SafeFs.GetDirectories(dir.AdbDir))
            {
                var f = System.IO.Path.Combine(loc, "DBCache.bin");
                if (SafeFs.FileExists(f)) list.Add(f);
            }
        }
        catch (SafeFsException) { }
        return list;
    }
}
