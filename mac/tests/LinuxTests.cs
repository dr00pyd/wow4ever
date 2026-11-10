using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ForeverScout.Core;

namespace ForeverScout.Mac.Tests;

/// <summary>Test som bara kan köras på Linux (riktiga rättigheter och sökvägar). Hoppas över annars.</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "kräver Linux";
    }
}

/// <summary>
/// Linux-klienten (client/linux): samma Core som Windows och macOS. Här testas det som är nytt för Linux –
/// signature5 med linux-fälten, autostart-filen, nyckelfilens rättigheter och var spelet söks.
/// </summary>
public class LinuxReleaseTests
{
    private static string Code(Action a) => Assert.ThrowsAny<ScoutException>(a).Code;

    /// <summary>En versionsinfo signerad som scripts/scout-sign.mjs gör det, med en engångsnyckel.</summary>
    private static (JsonObject Json, string Pem, byte[] Tar) Signed()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string Sign(byte[] d) => Convert.ToBase64String(key.SignData(d, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var exe = Encoding.ASCII.GetBytes("MZ windows");
        var tar = new byte[] { 0x1f, 0x8b, 8, 0, 1, 2, 3 };
        var sha = ApiClient.Sha256Hex(exe);
        var lsha = ApiClient.Sha256Hex(tar);
        var n = new JsonObject
        {
            ["v"] = 1, ["issued"] = "2026-11-10T12:00:00.000Z",
            ["client"] = "1.0.11", ["clientFile"] = "ForeverScout.exe", ["sha256"] = sha,
            ["clientSig"] = Sign(ReleaseVerifier.FileMessage(ReleaseKind.Client, "1.0.11", sha)),
            ["linux"] = "1.0.11", ["linuxFile"] = "ForeverScout-x86_64.AppImage", ["linuxSha256"] = lsha,
            ["linuxSig"] = Sign(ReleaseVerifier.FileMessage(ReleaseKind.Linux, "1.0.11", lsha)),
        };
        void SignAs(string name, IReadOnlyList<string> fields)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(n.ToJsonString());
            n[name] = Sign(Encoding.UTF8.GetBytes(ReleaseVerifier.Canonical(doc.RootElement, fields)));
        }
        SignAs("signature", ReleaseVerifier.SignedFields);
        SignAs("signature2", ReleaseVerifier.SignedFields2);
        SignAs("signature3", ReleaseVerifier.SignedFields3);
        SignAs("signature5", ReleaseVerifier.SignedFields5);
        return (n, key.ExportSubjectPublicKeyInfoPem(), tar);
    }

    [Fact]
    public void SignedFields5IsSortedAndCoversEverything()
    {
        var f = ReleaseVerifier.SignedFields5;
        Assert.Equal(f.OrderBy(x => x, StringComparer.Ordinal), f);
        Assert.Equal(f.Length, f.Distinct().Count());
        foreach (var x in ReleaseVerifier.SignedFields3) Assert.Contains(x, f);
        foreach (var x in new[] { "linux", "linuxFile", "linuxSha256", "linuxSig", "dmg", "dmgFile", "dmgSha256", "dmgSig" }) Assert.Contains(x, f);
    }

    [Fact]
    public void LinuxRequiresSignature5AndGetsTheLinuxFile()
    {
        var (n, pem, tar) = Signed();
        var r = ReleaseVerifier.VerifyLinux(n.ToJsonString(), pem);
        Assert.Equal("1.0.11", r.Linux);
        Assert.Equal("ForeverScout-x86_64.AppImage", r.LinuxFile!.FileName);
        ReleaseVerifier.VerifyFile(tar, ReleaseKind.Linux, r.LinuxFile, pem);
        // Filen kan inte utges för en annan typ (typen ingår i det signerade).
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.VerifyFile(tar, ReleaseKind.Mac, r.LinuxFile with { Kind = ReleaseKind.Mac }, pem)));

        n.Remove("signature5");
        Assert.Equal(ErrorCodes.SignatureMissing, Code(() => ReleaseVerifier.VerifyLinux(n.ToJsonString(), pem)));
    }

    [Fact]
    public void ChangedLinuxFieldsStopLinuxOnly()
    {
        var (n, pem, _) = Signed();
        n["linux"] = "9.9.9";
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.VerifyLinux(n.ToJsonString(), pem)));
        // Windows (signature2) och macOS (signature3) täcker inte linux-fälten och ser dem inte.
        var win = ReleaseVerifier.Verify(n.ToJsonString(), pem);
        Assert.Equal("1.0.11", win.Client);
        Assert.Null(win.Linux);
        Assert.Null(win.LinuxFile);
        var mac = ReleaseVerifier.VerifyMac(n.ToJsonString(), pem);
        Assert.Null(mac.Linux);
        Assert.Null(mac.LinuxFile);
    }
}

public class LinuxPathTests
{
    [Fact]
    public void AutostartDesktopFileQuotesThePath()
    {
        var d = SafeFs.LinuxAutostartDesktop("/home/anna/Forever Scout/ForeverScout");
        Assert.StartsWith("[Desktop Entry]\n", d);
        Assert.Contains("\nType=Application\n", d);
        Assert.Contains("\nExec=\"/home/anna/Forever Scout/ForeverScout\" --tray\n", d);
        Assert.Contains("\nTerminal=false\n", d);

        // Specialtecken: " ` $ får \\ framför sig, \ blir \\\\ och % blir %% (Desktop Entry-specifikationen).
        var odd = SafeFs.LinuxAutostartDesktop("/opt/a\"b`c$d\\e%f/ForeverScout");
        Assert.Contains("Exec=\"/opt/a\\\\\"b\\\\`c\\\\$d\\\\\\\\e%%f/ForeverScout\" --tray\n", odd);

        // En radbrytning i sökvägen skulle ge en egen rad i filen – nekas.
        Assert.Throws<SafeFsException>(() => SafeFs.LinuxAutostartDesktop("/opt/a\nExec=/bin/sh"));
    }

    [LinuxFact]
    public void GameIsSearchedInWinePrefixes()
    {
        var roots = SafeFs.StandardWowRoots();
        Assert.All(roots, r => Assert.StartsWith("/", r));
        Assert.Contains(roots, r => r.EndsWith("/.wine/drive_c/Program Files (x86)/World of Warcraft", StringComparison.Ordinal));
        Assert.Contains(roots, r => r.EndsWith("/Games/battlenet/drive_c/Program Files (x86)/World of Warcraft", StringComparison.Ordinal));
        Assert.Equal(roots.Count, roots.Distinct(StringComparer.Ordinal).Count());
    }

    [LinuxFact]
    public void AutostartFileLivesInConfigAutostart()
    {
        Assert.EndsWith("/autostart/se.wow4ever.foreverscout.desktop", SafeFs.LinuxAutostartPath);
    }
}

[Collection("SafeFs")]
public sealed class LinuxFileSystemTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fs-linux-" + Guid.NewGuid().ToString("N"));

    public LinuxFileSystemTests()
    {
        if (OperatingSystem.IsLinux()) SafeFs.UseAppDataDir(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [LinuxFact]
    public void KeyFileIsPrivate()
    {
        var path = Path.Combine(SafeFs.AppDataDir, "contributor-key");
        SafeFs.WritePrivateText(path, "nyckel-123");
        Assert.Equal("nyckel-123", SafeFs.ReadAllText(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(SafeFs.AppDataDir));
        // Bara i appens datakatalog.
        Assert.Throws<SafeFsException>(() => SafeFs.WritePrivateText("/tmp/contributor-key", "x"));
    }
}
