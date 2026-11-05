using System.Runtime.CompilerServices;
using ForeverScout.Core;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ForeverScout.Tests;

/// <summary>Testerna rör aldrig den riktiga %APPDATA%\ForeverScout: SafeFs pekas om till en temporär katalog.</summary>
internal static class TestEnv
{
    // macOS: /tmp i stället för TMPDIR (/var/folders/…/T/ är så lång att instance.sock spräcker gränsen på 104 byte).
    public static string AppData { get; } = Path.Combine(OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp", "fs-tests-appdata-" + Environment.ProcessId);

    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(AppData);
        SafeFs.UseAppDataDir(AppData);
    }

    /// <summary>Temporär katalog (utanför appdata) som tas bort efteråt.</summary>
    public static DirectoryInfo Temp(string prefix) => Directory.CreateTempSubdirectory(prefix);

#if DEBUG
    internal const bool IsDebug = true;
#else
    internal const bool IsDebug = false;
#endif
    internal const string ReleaseSkip = "kräver Debug: i Release ignorerar ApiClient baseUrl och pratar bara med produktionen";
}

/// <summary>
/// Tester mot en lokal server (127.0.0.1). Bara i Debug – i Release pekar ApiClient alltid på guide.wow4ever.se,
/// och testet skulle då nå den riktiga sajten (build.sh på macOS kör testerna i Release).
/// </summary>
public sealed class DebugFactAttribute : FactAttribute
{
    public DebugFactAttribute() { if (!TestEnv.IsDebug) Skip = TestEnv.ReleaseSkip; }
}

public sealed class DebugTheoryAttribute : TheoryAttribute
{
    public DebugTheoryAttribute() { if (!TestEnv.IsDebug) Skip = TestEnv.ReleaseSkip; }
}
