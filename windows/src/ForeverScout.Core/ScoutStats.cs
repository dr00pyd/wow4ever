namespace ForeverScout.Core;

public enum DataKind { Npcs, Objects, Quests, Trainers, Vendors, Loot, Hotfix }

public sealed record KindStats(DataKind Kind, int Count, long Bytes, int Uploaded);

/// <summary>Summering av en eller flera ForeverScout.lua-filer (allt räknat lokalt, ingen kod körs).</summary>
public sealed class ScoutStats
{
    public static readonly (DataKind Kind, string Key)[] Sections =
    [
        (DataKind.Npcs, "npcs"),
        (DataKind.Objects, "objects"),
        (DataKind.Quests, "quests"),
        (DataKind.Trainers, "trainers"),
        (DataKind.Vendors, "vendors"),
        (DataKind.Loot, "loot"),
    ];

    public Dictionary<DataKind, KindStats> ByKind { get; } = new();
    public long MaxSeq { get; private set; }
    public string? Locale { get; private set; }
    public string? Build { get; private set; }
    public string? AddonVersion { get; private set; }
    public List<string> Errors { get; } = new();

    public KindStats this[DataKind k] => ByKind.TryGetValue(k, out var s) ? s : new KindStats(k, 0, 0, 0);

    /// <summary>Räknar en parsad <c>ForeverScoutDB</c>. <paramref name="uploadedSeq"/> = senast uppladdade seq för filen.</summary>
    public void Add(LuaTable db, long uploadedSeq)
    {
        Locale ??= db.Str("locale");
        Build ??= db.Str("build");
        AddonVersion ??= db.Str("addon");
        MaxSeq = Math.Max(MaxSeq, db.Int("seq") ?? 0);
        foreach (var (kind, key) in Sections)
        {
            var t = db.Table(key);
            if (t is null) continue;
            int count = 0, uploaded = 0;
            foreach (var (_, v) in t.Pairs)
            {
                if (v is not LuaTable entry) continue;
                count++;
                if (uploadedSeq > 0 && EntrySeq(entry) is long q && q <= uploadedSeq) uploaded++;
            }
            long bytes = db.SourceLength.TryGetValue(key, out var len) ? len : 0;
            var prev = this[kind];
            ByKind[kind] = new KindStats(kind, prev.Count + count, prev.Bytes + bytes, prev.Uploaded + uploaded);
        }
    }

    public void AddHotfix(long bytes, bool uploaded)
    {
        var prev = this[DataKind.Hotfix];
        ByKind[DataKind.Hotfix] = new KindStats(DataKind.Hotfix, prev.Count + 1, prev.Bytes + bytes, prev.Uploaded + (uploaded ? 1 : 0));
    }

    /// <summary>Postens seq: fältet q, annars högsta q bland dess spots.</summary>
    public static long? EntrySeq(LuaTable entry)
    {
        long? best = entry.Int("q");
        if (entry.Table("spots") is { } spots)
            foreach (var s in spots.Array())
                if (s is LuaTable st && st.Int("q") is long q && (best is null || q > best)) best = q;
        return best;
    }

    /// <summary>Läser och räknar en fil. Fel samlas som koder i <see cref="Errors"/> (aldrig sökvägar – de innehåller kontonamnet).</summary>
    public void AddFile(string path, long uploadedSeq, long maxBytes = 20 * 1024 * 1024)
    {
        try
        {
            if (SafeFs.Stat(path) is not { } st) return;
            if (st.Length > maxBytes) { Errors.Add(ErrorCodes.FileTooLarge); return; }
            var bytes = ReadShared(path);
            var globals = LuaParser.ParseGlobals(bytes);
            if (globals.TryGetValue("ForeverScoutDB", out var v) && v is LuaTable db) Add(db, uploadedSeq);
        }
        catch (LuaParseException ex)
        {
            Errors.Add($"{ErrorCodes.LuaParse}:{ex.Line}:{ex.Reason}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SafeFsException)
        {
            Errors.Add(ex is SafeFsException ? ErrorCodes.AccessDenied : ErrorCodes.Io);
        }
    }

    /// <summary>Läser en fil via SafeFs (FileShare.ReadWrite – spelet kan ha den öppen).</summary>
    public static byte[] ReadShared(string path) => SafeFs.ReadAllBytes(path);
}
