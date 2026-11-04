using System.Text;
using ForeverScout.Core;

namespace ForeverScout.Tests;

public class LuaParserTests
{
    internal const string Sample = """
        ForeverScoutDB = {
          ["v"] = 1,
          ["addon"] = "1.0.0",
          ["build"] = "1.60.1.70755",
          ["locale"] = "enUS",
          ["install"] = "a1b2c3d4",
          ["seq"] = 417,
          ["npcs"] = {
            [198] = {
              ["n"] = "Khelden Bremen", ["s"] = "Mage Trainer", ["l"] = 5,
              ["r"] = "friendly", ["pf"] = "Alliance",
              ["roles"] = { ["trainer"] = true, ["vendor"] = false },
              ["spots"] = { { ["m"] = 1429, ["x"] = 49.71, ["y"] = 39.42, ["t"] = 1793750000, ["q"] = 412 } },
            },
            [823] = { n = "Deputy Willem", spots = { { m = 1429, x = 48.2, y = 41.9, t = 0, q = 414 }, }, },
          },
          ["trainers"] = { [198] = { q = 412, t = 1793750000, kind = "class",
            services = { { spell = 1459, name = "Arcane Intellect", rank = "Rank 1", cost = 10, lvl = 1, skill = nil } } } },
          ["vendors"] = { [1234] = { q = 413, t = 1793750000, items = { { id = 2320, price = 10, qty = 1, limited = -1 } } } },
          ["quests"] = { [783] = { q = 414, title = "A Threat Within", objectives = { "Speak to \"Marshal\" McBride" } } },
          ["objects"] = { [1731] = { n = "Copper Vein", spots = { { m = 1429, x = 1, y = 2, t = 0, q = 415 } } } },
          ["loot"] = { { q = 416, t = 0, src = "npc", id = 299, items = { { id = 2589, c = 2 } }, money = 15 },
                       { q = 417, t = 0, src = "object", id = 1731, items = {} } },
          ["flights"] = { [2] = { n = "Stormwind, Elwynn", m = 1453, x = 66.3, y = 62.1, q = 417 } },
        }
        """;

    [Fact]
    public void ParsesSampleSavedVariables()
    {
        var g = LuaParser.ParseGlobals(Sample);
        var db = Assert.IsType<LuaTable>(g["ForeverScoutDB"]);
        Assert.Equal(1L, db["v"]);
        Assert.Equal("1.60.1.70755", db.Str("build"));
        var npc = db.Table("npcs")!.Table(198)!;
        Assert.Equal("Khelden Bremen", npc.Str("n"));
        Assert.Equal(true, npc.Table("roles")!["trainer"]);
        Assert.Equal(false, npc.Table("roles")!["vendor"]);
        var spot = Assert.IsType<LuaTable>(npc.Table("spots")!.Array().Single());
        Assert.Equal(49.71, spot.Num("x"));
        Assert.Equal(-1L, db.Table("vendors")!.Table(1234)!.Table("items")!.Table(1)!["limited"]);
        Assert.Equal("Speak to \"Marshal\" McBride", db.Table("quests")!.Table(783)!.Table("objectives")!.Array().Single());
        Assert.Equal(2, db.Table("loot")!.Count);
    }

    [Theory]
    [InlineData("1", 1L)]
    [InlineData("-42", -42L)]
    [InlineData("0x1F", 31L)]
    [InlineData("1e3", 1000.0)]
    [InlineData("3.25", 3.25)]
    [InlineData("-0.5", -0.5)]
    public void ParsesNumbers(string src, object expected) => Assert.Equal(expected, LuaParser.ParseValue(src));

    [Theory]
    [InlineData("\"a\\nb\"", "a\nb")]
    [InlineData("'it''s'", null)]
    [InlineData("\"\\65\\066\"", "AB")]
    [InlineData("\"\\x41\"", "A")]
    [InlineData("[[long\nstring]]", "long\nstring")]
    [InlineData("[==[with ]] inside]==]", "with ]] inside")]
    [InlineData("\"tab\\tq\\\"\"", "tab\tq\"")]
    public void ParsesStrings(string src, string? expected)
    {
        if (expected is null) { Assert.Throws<LuaParseException>(() => LuaParser.ParseValue(src)); return; }
        Assert.Equal(expected, LuaParser.ParseValue(src));
    }

    [Fact]
    public void DecodesUtf8BytesIncludingEscapes()
    {
        // "Åsa" som rå UTF-8 och som \ddd-escapes (WoW kan skriva båda).
        var src = "X = { a = \"Åsa\", b = \"\\195\\133sa\" }";
        var g = LuaParser.ParseGlobals(Encoding.UTF8.GetBytes(src));
        var t = (LuaTable)g["X"]!;
        Assert.Equal("Åsa", t.Str("a"));
        Assert.Equal("Åsa", t.Str("b"));
    }

    [Fact]
    public void HandlesCommentsSemicolonsAndMultipleGlobals()
    {
        var g = LuaParser.ParseGlobals("-- header\nA = { 1; 2, 3 } --[[ block\ncomment ]]\nB = \"x\";\nC = nil\n");
        Assert.Equal([1L, 2L, 3L], ((LuaTable)g["A"]!).Array().ToArray());
        Assert.Equal("x", g["B"]);
        Assert.Null(g["C"]);
    }

    [Fact]
    public void BomIsSkipped()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("A = true")).ToArray();
        Assert.Equal(true, LuaParser.ParseGlobals(bytes)["A"]);
    }

    [Theory]
    [InlineData("A = os.execute(\"calc\")")]
    [InlineData("A = loadstring(\"x\")()")]
    [InlineData("A = function() end")]
    [InlineData("A = 1 + 2")]
    [InlineData("A = { [\"k\"] = print }")]
    [InlineData("A = { x = 1 ")]
    [InlineData("A = \"unterminated")]
    [InlineData("A = B")]
    public void RejectsAnythingButLiterals(string src) =>
        Assert.Throws<LuaParseException>(() => LuaParser.ParseGlobals(src));

    [Fact]
    public void RejectsExcessiveNesting()
    {
        var src = "A = " + new string('{', LuaParser.MaxDepth + 2) + new string('}', LuaParser.MaxDepth + 2);
        Assert.Throws<LuaParseException>(() => LuaParser.ParseGlobals(src));
    }

    [Fact]
    public void IntegralFloatKeysNormalizeToIntegers()
    {
        var t = (LuaTable)LuaParser.ParseValue("{ [1.0] = \"a\", [2] = \"b\" }")!;
        Assert.Equal(["a", "b"], t.Array().ToArray());
    }

    [Fact]
    public void ErrorReportsLine()
    {
        var ex = Assert.Throws<LuaParseException>(() => LuaParser.ParseGlobals("A = {\n x = 1,\n y = @\n}"));
        Assert.Equal(3, ex.Line);
    }
}
