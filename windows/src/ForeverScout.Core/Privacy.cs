using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ForeverScout.Core;

/// <summary>
/// Kontonamnet finns i sökvägen WTF\Account\&lt;konto&gt;\… – det får aldrig skickas, loggas eller visas.
/// Allt som visas eller lagras om filerna går genom de här hjälparna.
/// </summary>
public static partial class Privacy
{
    [GeneratedRegex(@"([\\/]WTF[\\/]Account[\\/])[^\\/]+", RegexOptions.IgnoreCase)]
    private static partial Regex AccountSegment();

    /// <summary>Byter ut kontonamnet i alla sökvägar i en text mot "***".</summary>
    public static string Redact(string? text) => text is null ? "" : AccountSegment().Replace(text, "$1***");

    /// <summary>Stabil, icke-reversibel nyckel för en fil (används i settings.json i stället för sökvägen).</summary>
    public static string FileKey(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant())))[..24];
}
