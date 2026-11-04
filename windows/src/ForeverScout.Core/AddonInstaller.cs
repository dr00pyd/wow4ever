using System.IO.Compression;

namespace ForeverScout.Core;

/// <summary>Installerar/uppdaterar addonen i &lt;spel&gt;\Interface\AddOns\ForeverScout.</summary>
public static class AddonInstaller
{
    public const string AddonName = "ForeverScout";

    /// <summary>Filtyper som får finnas i addonen. Inget körbart.</summary>
    private static readonly HashSet<string> AllowedExt = new(StringComparer.OrdinalIgnoreCase)
        { ".lua", ".toc", ".xml", ".md", ".txt", ".tga", ".blp" };

    public sealed record AddonFile(string RelativePath, byte[] Data);

    public static string TargetDir(GameDir dir) => Path.Combine(dir.AddOnsDir, AddonName);

    /// <summary>"## Version: 1.0.0" ur .toc-filen.</summary>
    public static string? TocVersion(string tocText)
    {
        foreach (var line in tocText.Split('\n'))
        {
            var l = line.Trim();
            if (l.StartsWith("## Version:", StringComparison.OrdinalIgnoreCase)) return l[11..].Trim();
        }
        return null;
    }

    public static string? InstalledVersion(GameDir dir)
    {
        try
        {
            var toc = SafeFs.DirectoryExists(TargetDir(dir)) ? SafeFs.GetFiles(TargetDir(dir), "*.toc").OrderBy(f => f.Length).FirstOrDefault() : null;
            return toc is null ? null : TocVersion(SafeFs.ReadAllText(toc));
        }
        catch (Exception ex) when (ex is SafeFsException or IOException or UnauthorizedAccessException) { return null; }
    }

    public static string? BundledVersion(IReadOnlyList<AddonFile> files)
    {
        var toc = files.FirstOrDefault(f => f.RelativePath.EndsWith(".toc", StringComparison.OrdinalIgnoreCase) && !f.RelativePath.Contains('/'));
        return toc is null ? null : TocVersion(System.Text.Encoding.UTF8.GetString(toc.Data));
    }

    /// <summary>Jämför versioner "1.2.3" (saknad/ogiltig räknas som äldst).</summary>
    public static int CompareVersions(string? a, string? b)
    {
        static Version P(string? s) => Version.TryParse((s ?? "").Split('-', '+')[0], out var v) ? v : new Version(0, 0);
        return P(a).CompareTo(P(b));
    }

    /// <summary>Behöver addonen installeras eller uppdateras?</summary>
    public static bool NeedsInstall(GameDir dir, IReadOnlyList<AddonFile> files) =>
        files.Count > 0 && (InstalledVersion(dir) is not { } inst || CompareVersions(BundledVersion(files), inst) > 0);

    /// <summary>Skriver filerna till en temporär katalog bredvid och byter sedan ut den gamla (atomärt så långt det går).</summary>
    public static void Install(GameDir dir, IReadOnlyList<AddonFile> files)
    {
        if (files.Count == 0) throw new ScoutException(ErrorCodes.AddonMissing);
        SafeFs.CreateDirectory(dir.AddOnsDir);
        var target = TargetDir(dir);
        var staging = target + ".new";
        var old = target + ".old";
        SafeFs.DeleteDirectory(staging);
        SafeFs.CreateDirectory(staging);
        var stagingFull = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        foreach (var f in files)
        {
            var rel = f.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            if (!AllowedExt.Contains(Path.GetExtension(rel))) continue;
            var dest = Path.GetFullPath(Path.Combine(staging, rel));
            if (!dest.StartsWith(stagingFull, StringComparison.OrdinalIgnoreCase)) continue; // ingen ../
            SafeFs.CreateDirectory(Path.GetDirectoryName(dest)!);
            SafeFs.WriteAllBytes(dest, f.Data);
        }
        SafeFs.DeleteDirectory(old);
        if (SafeFs.DirectoryExists(target)) SafeFs.MoveDirectory(target, old);
        SafeFs.MoveDirectory(staging, target);
        try { SafeFs.DeleteDirectory(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Läser en addon-zip (från servern, redan sha256-kontrollerad). Mappen "ForeverScout/" i zip:en tas bort ur sökvägen.</summary>
    public static List<AddonFile> FromZip(byte[] zip)
    {
        try { return ReadZip(zip); }
        catch (InvalidDataException ex) { throw new ScoutException(ErrorCodes.AddonInvalid, inner: ex); }
    }

    private static List<AddonFile> ReadZip(byte[] zip)
    {
        var list = new List<AddonFile>();
        using var za = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        long total = 0;
        foreach (var e in za.Entries)
        {
            if (string.IsNullOrEmpty(e.Name)) continue;
            var rel = e.FullName.Replace('\\', '/');
            if (rel.StartsWith(AddonName + "/", StringComparison.OrdinalIgnoreCase)) rel = rel[(AddonName.Length + 1)..];
            if (rel.Contains("..") || rel.StartsWith('/')) continue;
            if (!AllowedExt.Contains(Path.GetExtension(rel))) continue;
            total += e.Length;
            if (total > 20 * 1024 * 1024) throw new ScoutException(ErrorCodes.AddonTooLarge);
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            list.Add(new AddonFile(rel, ms.ToArray()));
        }
        if (BundledVersion(list) is null) throw new ScoutException(ErrorCodes.AddonInvalid);
        return list;
    }
}
