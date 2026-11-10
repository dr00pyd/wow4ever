using ForeverScout.Core;

namespace ForeverScout.Mac;

/// <summary>
/// Linux: nyckeln sparas i filen <c>~/.config/ForeverScout/contributor-key</c> med rättigheterna 0600, i en katalog
/// med 0700 – bara användaren själv kan läsa den. Motsvarigheten till nyckelringen på macOS och DPAPI på Windows.
/// Linux har ingen nyckelring som finns på alla skrivbord (Steam Deck i spelläge, fönsterhanterare utan Secret
/// Service), och en nyckel som inte går att läsa vid inloggning stoppar uppladdningen i tysthet.
/// Samma klassnamn som på macOS, så att de delade källfilerna (App, ScoutController, SettingsWindow) är oförändrade.
/// All filåtkomst går genom SafeFs.
/// </summary>
internal static class KeychainStore
{
    private static string KeyPath => Path.Combine(SafeFs.AppDataDir, "contributor-key");

    public static string? Load()
    {
        try
        {
            if (!SafeFs.FileExists(KeyPath)) return null;
            var key = SafeFs.ReadAllText(KeyPath).Trim();
            return key.Length is > 0 and <= 512 ? key : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SafeFsException) { return null; }
    }

    public static void Save(string key)
    {
        try { SafeFs.WritePrivateText(KeyPath, key); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SafeFsException) { throw new IOException("key file", ex); }
    }

    public static void Delete()
    {
        try { SafeFs.DeleteFile(KeyPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SafeFsException) { }
    }
}
