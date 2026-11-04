using System.Globalization;
using System.Text;

namespace ForeverScout.Core;

/// <summary>
/// En Lua-tabell som data. Nycklar normaliseras: heltal blir <see cref="long"/>, övriga tal <see cref="double"/>,
/// strängar <see cref="string"/>, booleaner <see cref="bool"/>. Värden är long/double/string/bool/LuaTable (nil hoppas över).
/// </summary>
public sealed class LuaTable
{
    private readonly Dictionary<object, object> _map = new();
    private readonly List<object> _order = new();

    /// <summary>Källtextens längd (tecken) per fält – bara ifylld för tabeller på nivå 1 (rot-tabellen i en global).</summary>
    public Dictionary<object, int> SourceLength { get; } = new();

    public int Count => _map.Count;
    public IEnumerable<object> Keys => _order;
    public IEnumerable<KeyValuePair<object, object>> Pairs => _order.Select(k => new KeyValuePair<object, object>(k, _map[k]));

    public object? this[object key]
    {
        get => _map.TryGetValue(LuaParser.NormalizeKey(key), out var v) ? v : null;
        set
        {
            var k = LuaParser.NormalizeKey(key);
            if (value is null) { if (_map.Remove(k)) _order.Remove(k); return; }
            if (!_map.ContainsKey(k)) _order.Add(k);
            _map[k] = value;
        }
    }

    public LuaTable? Table(object key) => this[key] as LuaTable;
    public string? Str(object key) => this[key] as string;
    public long? Int(object key) => this[key] switch { long l => l, double d when d == Math.Floor(d) => (long)d, _ => null };
    public double? Num(object key) => this[key] switch { long l => l, double d => d, _ => null };

    /// <summary>Positionella element 1..n (Lua-sekvens).</summary>
    public IEnumerable<object> Array()
    {
        for (long i = 1; _map.TryGetValue(i, out var v); i++) yield return v;
    }
}

/// <summary>Parsfel. <see cref="Reason"/> är en språkneutral kod (t.ex. "expected-value"), Detail = radnummer.</summary>
public sealed class LuaParseException(string reason, int line) : ScoutException(ErrorCodes.LuaParse, line.ToString(System.Globalization.CultureInfo.InvariantCulture))
{
    public string Reason { get; } = reason;
    public int Line { get; } = line;
}

/// <summary>
/// Säker parser för SavedVariables: förstår bara tilldelningar <c>Namn = literal</c> där literal är
/// tabell, sträng, tal, boolean eller nil. Kör aldrig kod; funktionsanrop, operatorer m.m. ger fel.
/// </summary>
public sealed class LuaParser
{
    public const int MaxDepth = 64;

    private readonly string _s;
    private int _p;
    private int _line = 1;

    private readonly bool _bytes;

    private LuaParser(string source, bool bytes = false) { _s = source; _bytes = bytes; }

    /// <summary>
    /// Parsar filens råa byte: strängar i SavedVariables är UTF-8-byte (även via \ddd-escapes),
    /// så vi läser som Latin-1 (1 byte = 1 tecken) och avkodar varje sträng som UTF-8 efteråt.
    /// </summary>
    public static Dictionary<string, object?> ParseGlobals(byte[] bytes)
    {
        int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        var text = Encoding.Latin1.GetString(bytes, start, bytes.Length - start);
        return ParseGlobalsCore(new LuaParser(text, bytes: true));
    }

    private string Finish(string s)
    {
        if (!_bytes) return s;
        foreach (var ch in s) if (ch > 0x7F) return Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(s));
        return s;
    }

    /// <summary>Parsar en hel SavedVariables-fil och returnerar dess globaler.</summary>
    public static Dictionary<string, object?> ParseGlobals(string source) => ParseGlobalsCore(new LuaParser(source));

    private static Dictionary<string, object?> ParseGlobalsCore(LuaParser p)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        p.SkipWs();
        if (p.Peek() == '﻿') { p._p++; p.SkipWs(); }
        while (!p.Eof)
        {
            var name = p.ReadName() ?? throw p.Error("expected-global-name");
            p.SkipWs();
            p.Expect('=');
            var value = p.ParseValue(0);
            result[name] = value;
            p.SkipWs();
            if (p.Peek() == ';') { p._p++; p.SkipWs(); }
        }
        return result;
    }

    /// <summary>Parsar ett enskilt värde (t.ex. "{ a = 1 }").</summary>
    public static object? ParseValue(string source)
    {
        var p = new LuaParser(source);
        var v = p.ParseValue(0);
        p.SkipWs();
        if (!p.Eof) throw p.Error("trailing-text");
        return v;
    }

    internal static object NormalizeKey(object key) => key switch
    {
        int i => (long)i,
        double d when d == Math.Floor(d) && Math.Abs(d) < 9.0e15 => (long)d,
        _ => key,
    };

    private bool Eof => _p >= _s.Length;
    private char Peek(int o = 0) => _p + o < _s.Length ? _s[_p + o] : '\0';
    private LuaParseException Error(string msg) => new(msg, _line);

    private void Expect(char c)
    {
        SkipWs();
        if (Peek() != c) throw Error("expected-" + c);
        _p++;
    }

    private void SkipWs()
    {
        while (!Eof)
        {
            char c = _s[_p];
            if (c == '\n') { _line++; _p++; }
            else if (char.IsWhiteSpace(c)) _p++;
            else if (c == '-' && Peek(1) == '-')
            {
                _p += 2;
                int level = LongBracketLevel();
                if (level >= 0) ReadLongBracket(level);
                else while (!Eof && _s[_p] != '\n') _p++;
            }
            else break;
        }
    }

    private string? ReadName()
    {
        if (Eof) return null;
        char c = _s[_p];
        if (!(char.IsAsciiLetter(c) || c == '_')) return null;
        int start = _p;
        while (!Eof && (char.IsAsciiLetterOrDigit(_s[_p]) || _s[_p] == '_')) _p++;
        return _s[start.._p];
    }

    private object? ParseValue(int depth)
    {
        SkipWs();
        if (Eof) throw Error("expected-value");
        char c = Peek();
        if (c == '{') return ParseTable(depth + 1);
        if (c == '"' || c == '\'') return ReadQuoted();
        if (c == '[')
        {
            int level = LongBracketLevel();
            if (level < 0) throw Error("bad-long-string");
            return Finish(ReadLongBracket(level));
        }
        if (c == '-' || c == '.' || char.IsAsciiDigit(c)) return ReadNumber();
        var name = ReadName();
        return name switch
        {
            "true" => true,
            "false" => false,
            "nil" => null,
            null => throw Error("unexpected-char"),
            _ => throw Error("not-a-literal"),
        };
    }

    private LuaTable ParseTable(int depth)
    {
        if (depth > MaxDepth) throw Error("too-deep");
        Expect('{');
        var t = new LuaTable();
        long arrayIndex = 1;
        while (true)
        {
            SkipWs();
            if (Peek() == '}') { _p++; return t; }
            int fieldStart = _p;
            object? key;
            object? value;
            if (Peek() == '[' && Peek(1) != '[' && Peek(1) != '=')
            {
                _p++;
                key = ParseValue(depth) ?? throw Error("nil-key");
                Expect(']');
                Expect('=');
                value = ParseValue(depth);
            }
            else
            {
                int save = _p, saveLine = _line;
                var name = ReadName();
                SkipWs();
                if (name != null && name is not ("true" or "false" or "nil") && Peek() == '=' && Peek(1) != '=')
                {
                    _p++;
                    key = name;
                    value = ParseValue(depth);
                }
                else
                {
                    _p = save; _line = saveLine;
                    key = arrayIndex++;
                    value = ParseValue(depth);
                }
            }
            if (value is not null)
            {
                t[key] = value;
                if (depth == 1) t.SourceLength[NormalizeKey(key)] = _p - fieldStart;
            }
            SkipWs();
            char sep = Peek();
            if (sep == ',' || sep == ';') { _p++; continue; }
            if (sep == '}') { _p++; return t; }
            throw Error("expected-separator");
        }
    }

    private object ReadNumber()
    {
        int start = _p;
        bool neg = false;
        if (Peek() == '-') { neg = true; _p++; SkipWs(); }
        if (Peek() == '0' && (Peek(1) == 'x' || Peek(1) == 'X'))
        {
            _p += 2;
            int hs = _p;
            while (!Eof && char.IsAsciiHexDigit(_s[_p])) _p++;
            if (_p == hs) throw Error("bad-hex");
            var hv = long.Parse(_s[hs.._p], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return neg ? -hv : hv;
        }
        int ns = _p;
        while (!Eof && (char.IsAsciiDigit(_s[_p]) || _s[_p] == '.')) _p++;
        if (!Eof && (_s[_p] == 'e' || _s[_p] == 'E'))
        {
            _p++;
            if (Peek() == '+' || Peek() == '-') _p++;
            while (!Eof && char.IsAsciiDigit(_s[_p])) _p++;
        }
        var text = _s[ns.._p];
        if (text.Length == 0) { _p = start; throw Error("bad-number"); }
        if (text is "inf" or "nan") throw Error("bad-number");
        if (!text.Contains('.') && !text.Contains('e') && !text.Contains('E')
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var l))
            return neg ? -l : l;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) throw Error("bad-number");
        return neg ? -d : d;
    }

    private string ReadQuoted()
    {
        char q = _s[_p++];
        var sb = new StringBuilder();
        while (true)
        {
            if (Eof) throw Error("unterminated-string");
            char c = _s[_p++];
            if (c == q) return Finish(sb.ToString());
            if (c == '\n') throw Error("newline-in-string");
            if (c != '\\') { sb.Append(c); continue; }
            if (Eof) throw Error("unterminated-string");
            char e = _s[_p++];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'a': sb.Append('\a'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'v': sb.Append('\v'); break;
                case '\\': sb.Append('\\'); break;
                case '"': sb.Append('"'); break;
                case '\'': sb.Append('\''); break;
                case '\n': sb.Append('\n'); _line++; break;
                case '\r': sb.Append('\n'); if (Peek() == '\n') _p++; _line++; break;
                case 'x':
                {
                    if (!char.IsAsciiHexDigit(Peek()) || !char.IsAsciiHexDigit(Peek(1))) throw Error("bad-x-escape");
                    sb.Append((char)Convert.ToInt32(_s.Substring(_p, 2), 16));
                    _p += 2;
                    break;
                }
                case 'z':
                    while (!Eof && char.IsWhiteSpace(_s[_p])) { if (_s[_p] == '\n') _line++; _p++; }
                    break;
                case 'u':
                {
                    if (Peek() != '{') throw Error("bad-u-escape");
                    int end = _s.IndexOf('}', _p);
                    if (end < 0) throw Error("bad-u-escape");
                    var cp = Convert.ToInt32(_s.Substring(_p + 1, end - _p - 1), 16);
                    sb.Append(char.ConvertFromUtf32(cp));
                    _p = end + 1;
                    break;
                }
                default:
                    if (char.IsAsciiDigit(e))
                    {
                        int v = e - '0', n = 1;
                        while (n < 3 && char.IsAsciiDigit(Peek())) { v = v * 10 + (_s[_p++] - '0'); n++; }
                        if (v > 255) throw Error("bad-decimal-escape");
                        sb.Append((char)v);
                    }
                    else throw Error("bad-escape");
                    break;
            }
        }
    }

    /// <summary>Om vi står på "[[" eller "[==[" returneras nivån (antal '='), annars -1. Flyttar inte positionen.</summary>
    private int LongBracketLevel()
    {
        if (Peek() != '[') return -1;
        int i = 1;
        while (Peek(i) == '=') i++;
        return Peek(i) == '[' ? i - 1 : -1;
    }

    private string ReadLongBracket(int level)
    {
        _p += level + 2;
        if (Peek() == '\r') _p++;
        if (Peek() == '\n') { _p++; _line++; }
        var close = "]" + new string('=', level) + "]";
        int end = _s.IndexOf(close, _p, StringComparison.Ordinal);
        if (end < 0) throw Error("unterminated-long-bracket");
        var text = _s[_p..end];
        foreach (var ch in text) if (ch == '\n') _line++;
        _p = end + close.Length;
        return text;
    }
}
