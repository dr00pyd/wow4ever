namespace ForeverScout.Core;

/// <summary>
/// Publik nyckel (ECDSA P-256, SPKI-PEM) för signerade släpp. Skapas med
/// <c>node scripts/scout-sign.mjs --genkey &lt;katalog&gt;</c>; klistra in utskriften nedan i stället för platshållaren.
/// Release-bygget avbryts (se ForeverScout.Core.csproj, målet ScoutKeyGuard) så länge platshållaren står kvar.
/// Utan riktig nyckel vägrar klienten installera uppdateringar (ingen reserv).
/// </summary>
public static class UpdateKeys
{
    public const string PublicKeyPem = """
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEhi/2w4zEy5kqlaBpgtbmIuhXKsAo
P41EShGig47sfIt1S3TcJLW/fMuUEv9vG50x1nUwaAGfiK4ycufysxyqqw==
-----END PUBLIC KEY-----
""";
}
