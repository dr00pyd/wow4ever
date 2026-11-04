using System.Runtime.CompilerServices;
using ForeverScout.Core;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ForeverScout.Tests;

/// <summary>Testerna rör aldrig den riktiga %APPDATA%\ForeverScout: SafeFs pekas om till en temporär katalog.</summary>
internal static class TestEnv
{
    public static string AppData { get; } = Path.Combine(Path.GetTempPath(), "fs-tests-appdata-" + Environment.ProcessId);

    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(AppData);
        SafeFs.UseAppDataDir(AppData);
    }

    /// <summary>Temporär katalog (utanför appdata) som tas bort efteråt.</summary>
    public static DirectoryInfo Temp(string prefix) => Directory.CreateTempSubdirectory(prefix);
}
