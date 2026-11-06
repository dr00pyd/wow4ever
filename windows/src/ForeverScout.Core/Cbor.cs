using System.Buffers.Binary;
using System.Text;

namespace ForeverScout.Core;

/// <summary>Trasig eller för stor CBOR-data.</summary>
public sealed class CborException(string reason) : Exception(reason);

/// <summary>
/// Minimal, begränsad CBOR-avkodare (RFC 8949) för data som spelet själv har serialiserat
/// (<c>C_EncodingUtil.SerializeCBOR</c> eller LibCBOR), t.ex. Auctionators prisdatabas per realm.
/// Kör aldrig kod. Avkodar till samma typer som <see cref="LuaParser"/>: map/array → <see cref="LuaTable"/>
/// (array-element får nycklarna 1..n som i Lua), heltal → long, flyttal → double, text → string, byte-sträng →
/// string (Latin-1, 1 tecken = 1 byte), true/false → bool; null/undefined hoppas över. Taggar ignoreras (innehållet
/// avkodas). Tak: djup, antal värden och strängstorlek – en trasig eller fientlig fil kan inte fylla minnet.
/// </summary>
public static class Cbor
{
    public const int MaxDepth = 32;
    public const int MaxItems = 5_000_000;
    public const int MaxString = 1 << 20;

    public static object? Decode(byte[] data)
    {
        var r = new Reader(data);
        var v = r.Value(0);
        if (r.Pos != data.Length) throw new CborException("trailing-bytes");
        return v;
    }

    private sealed class Reader(byte[] d)
    {
        public int Pos;
        private int _items;

        private byte Byte()
        {
            if (Pos >= d.Length) throw new CborException("truncated");
            return d[Pos++];
        }

        private ReadOnlySpan<byte> Take(long n)
        {
            if (n < 0 || n > d.Length - Pos) throw new CborException("truncated");
            var s = d.AsSpan(Pos, (int)n);
            Pos += (int)n;
            return s;
        }

        /// <summary>Argumentet efter huvudbyten (additional info 0–27). 31 = obestämd längd → -1.</summary>
        private long Arg(int info)
        {
            if (info < 24) return info;
            switch (info)
            {
                case 24: return Byte();
                case 25: return BinaryPrimitives.ReadUInt16BigEndian(Take(2));
                case 26: return BinaryPrimitives.ReadUInt32BigEndian(Take(4));
                case 27:
                {
                    var v = BinaryPrimitives.ReadUInt64BigEndian(Take(8));
                    if (v > long.MaxValue) throw new CborException("too-large");
                    return (long)v;
                }
                case 31: return -1;
                default: throw new CborException("bad-info");
            }
        }

        private void Count()
        {
            if (++_items > MaxItems) throw new CborException("too-many-items");
        }

        private bool AtBreak()
        {
            if (Pos >= d.Length) throw new CborException("truncated");
            if (d[Pos] != 0xFF) return false;
            Pos++;
            return true;
        }

        private string Str(int major, long len)
        {
            if (len >= 0)
            {
                if (len > MaxString) throw new CborException("string-too-large");
                var bytes = Take(len);
                return major == 3 ? Encoding.UTF8.GetString(bytes) : Encoding.Latin1.GetString(bytes);
            }
            // Obestämd längd: delar av samma typ fram till 0xFF.
            var sb = new StringBuilder();
            while (!AtBreak())
            {
                var h = Byte();
                if (h >> 5 != major) throw new CborException("bad-chunk");
                var n = Arg(h & 0x1F);
                if (n < 0) throw new CborException("bad-chunk");
                sb.Append(Str(major, n));
                if (sb.Length > MaxString) throw new CborException("string-too-large");
            }
            return sb.ToString();
        }

        public object? Value(int depth)
        {
            if (depth > MaxDepth) throw new CborException("too-deep");
            Count();
            var h = Byte();
            int major = h >> 5, info = h & 0x1F;
            switch (major)
            {
                case 0: return CheckedInt(Arg(info), info);
                case 1: return -1 - CheckedInt(Arg(info), info);
                case 2:
                case 3: return Str(major, Arg(info));
                case 4:
                {
                    var t = new LuaTable();
                    long n = Arg(info), i = 1;
                    if (n > MaxItems) throw new CborException("too-many-items");
                    for (long k = 0; n < 0 ? !AtBreak() : k < n; k++, i++)
                    {
                        var v = Value(depth + 1);
                        if (v is not null) t[i] = v;
                    }
                    return t;
                }
                case 5:
                {
                    var t = new LuaTable();
                    long n = Arg(info);
                    if (n > MaxItems) throw new CborException("too-many-items");
                    for (long k = 0; n < 0 ? !AtBreak() : k < n; k++)
                    {
                        var key = Value(depth + 1);
                        var v = Value(depth + 1);
                        if (key is null || key is LuaTable) throw new CborException("bad-key");
                        if (key is double dk && double.IsNaN(dk)) throw new CborException("bad-key");
                        if (v is not null) t[key] = v;
                    }
                    return t;
                }
                case 6:
                    Arg(info);
                    return Value(depth + 1); // tagg: innehållet räknas
                default:
                    return info switch
                    {
                        20 => false,
                        21 => true,
                        22 or 23 => null,
                        25 => (double)BinaryPrimitives.ReadHalfBigEndian(Take(2)),
                        26 => (double)BinaryPrimitives.ReadSingleBigEndian(Take(4)),
                        27 => BinaryPrimitives.ReadDoubleBigEndian(Take(8)),
                        _ => throw new CborException("bad-simple"),
                    };
            }
        }

        private static long CheckedInt(long v, int info)
        {
            if (info == 31) throw new CborException("bad-int");
            return v;
        }
    }
}
