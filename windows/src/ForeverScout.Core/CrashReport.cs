using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ForeverScout.Core;

/// <summary>
/// Oväntade undantag (1.0.11): en kort beskrivning för uploads.log. Bara typ, HResult och metodnamnen i stacken –
/// aldrig undantagets meddelande (ett IO-fel nämner sökvägen och därmed kontonamnet) och aldrig filnamn eller radnummer.
/// </summary>
public static class CrashReport
{
    /// <summary>"ArgumentOutOfRangeException 0x80131502 at ForeverScout.MainForm.ShowStats &lt; ForeverScout.ScoutApp.RefreshStats …".
    /// Inre undantag följer efter " | inner: ".</summary>
    public static string Describe(Exception ex, int maxFrames = 12)
    {
        var sb = new StringBuilder();
        int depth = 0;
        for (var e = ex; e != null && depth < 4; e = e.InnerException, depth++)
        {
            if (depth > 0) sb.Append(" | inner: ");
            sb.Append(e.GetType().Name).Append(" 0x").Append(e.HResult.ToString("X8", CultureInfo.InvariantCulture));
            var frames = Frames(e, maxFrames);
            if (frames.Count > 0) sb.Append(" at ").Append(string.Join(" < ", frames));
        }
        return sb.ToString();
    }

    /// <summary>Kort nyckel för samma fel (typ + innersta egna metod): används för att inte fylla journalen med upprepningar.</summary>
    public static string Signature(Exception ex) => ex.GetType().Name + "@" + (Frames(ex, 1).FirstOrDefault() ?? "?");

    /// <summary>Metodnamn ur stacken, innerst först. Kompilatorgenererade namn (async/lambda) görs läsbara.</summary>
    public static List<string> Frames(Exception ex, int max)
    {
        var list = new List<string>();
        StackFrame[] frames;
        try { frames = new StackTrace(ex, fNeedFileInfo: false).GetFrames(); }
        catch (Exception) { return list; }
        foreach (var f in frames)
        {
            if (list.Count >= max) break;
            if (f.GetMethod() is not { } m) continue;
            var type = m.DeclaringType;
            string name = m.Name;
            // Kompilatorgenererat: <OnTick>d__57.MoveNext → OnTick, <>c.<.ctor>b__27_1 → .ctor(lambda),
            // <>c.<<.ctor>b__27_1>d.MoveNext → .ctor(lambda).
            bool lambda = name.Contains("b__", StringComparison.Ordinal);
            while (type is { DeclaringType: { } outer } && type.Name.StartsWith('<'))
            {
                lambda |= type.Name.Contains("b__", StringComparison.Ordinal);
                if (name == "MoveNext" && Generated(type.Name) is { } sm) name = sm;
                type = outer;
            }
            if (Generated(name) is { } plain) name = plain;
            if (lambda) name += "(lambda)";
            var full = (type?.FullName ?? "?") + "." + name;
            if (list.Count == 0 || list[^1] != full) list.Add(full);
        }
        return list;
    }

    /// <summary>"&lt;OnTick&gt;d__57" → "OnTick", "&lt;&lt;.ctor&gt;b__27_1&gt;d" → ".ctor"; null om namnet inte är kompilatorgenererat.</summary>
    private static string? Generated(string s)
    {
        if (!s.StartsWith('<')) return null;
        var t = s.TrimStart('<');
        int b = t.IndexOf('>');
        return b > 0 ? t[..b] : null;
    }
}

/// <summary>
/// Samma fel högst en gång per fönster (t.ex. en minut) i journalen: ett fel i en tick som upprepas var 15:e sekund
/// ska synas, men inte trycka undan allt annat. Antalet bortfiltrerade rapporteras med nästa rad.
/// </summary>
public sealed class ErrorThrottle(TimeSpan window)
{
    private readonly Dictionary<string, (DateTime At, int Suppressed)> _seen = new(StringComparer.Ordinal);

    /// <summary>True om felet ska visas nu; <paramref name="suppressed"/> = hur många gånger det filtrerats bort sedan sist.</summary>
    public bool ShouldReport(string signature, DateTime now, out int suppressed)
    {
        if (_seen.TryGetValue(signature, out var s) && now - s.At < window)
        {
            _seen[signature] = (s.At, s.Suppressed + 1);
            suppressed = 0;
            return false;
        }
        if (_seen.Count > 100) _seen.Clear(); // aldrig obegränsat
        suppressed = s.Suppressed;
        _seen[signature] = (now, 0);
        return true;
    }
}
