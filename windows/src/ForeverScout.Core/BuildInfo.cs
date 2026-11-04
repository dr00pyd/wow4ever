namespace ForeverScout.Core;

/// <summary>
/// Vilken variant som byggts. Sätts med <c>-p:ScoutVariant=lite|full</c> (se client/windows/Directory.Build.props):
/// <list type="bullet">
/// <item><b>full</b> – fristående (self-contained) exe, inget att installera. Uppdateras från clientFile.</item>
/// <item><b>lite</b> – ramverksberoende exe (≈ 2 MB) som kräver .NET 10 Desktop Runtime. Uppdateras från liteFile.</item>
/// </list>
/// </summary>
public static class BuildInfo
{
#if SCOUT_LITE
    public const string Variant = "lite";
#else
    public const string Variant = "full";
#endif

    public static bool IsLite => Variant == "lite";

    /// <summary>Vilken fil i den signerade versionsinfon den här varianten uppdaterar sig från.</summary>
    public static ReleaseKind UpdateKind => IsLite ? ReleaseKind.Lite : ReleaseKind.Client;

    public static Version Version =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(1, 0, 0);
}
