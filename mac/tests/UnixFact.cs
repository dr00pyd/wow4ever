namespace ForeverScout.Mac.Tests;

/// <summary>Test som bara kan köras på macOS (riktiga symlänkar, 0700, ditto, codesign). Hoppas över på Windows.</summary>
public sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "kräver macOS";
    }
}
