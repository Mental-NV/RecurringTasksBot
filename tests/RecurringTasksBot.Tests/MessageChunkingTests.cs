// Literal message chunking and the text-only capability policy: rune-aligned cuts, newline/grapheme preferences, blocked tags.
using System.Text;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Tests;

public sealed class MessageChunkingTests
{


    [Theory]
    [InlineData("![cover](https://example.org/cover.png)", true)]
    [InlineData("see <img src=\"https://example.org/x.png\"> here", true)]
    [InlineData("see <IMG SRC=\"https://example.org/x.png\"> here", true)]
    [InlineData("see <  img  src=\"x\"> here", true)]
    [InlineData("<video src=\"x\">", true)]
    [InlineData("<audio src=\"x\">", true)]
    [InlineData("<source src=\"x\">", true)]
    [InlineData("<iframe src=\"x\">", true)]
    [InlineData("<script>alert(1)</script>", true)]
    [InlineData("<tg-button text=\"Go\">", true)]
    [InlineData("<TG-MAP>", true)]
    [InlineData("<tg-emoji id=\"1\">", true)]
    [InlineData("<tg-thinking>", true)]
    [InlineData("<tg-document>", true)]
    [InlineData("<tg-collage>", true)]
    [InlineData("<tg-slideshow>", true)]
    [InlineData("<tg-map-view />", true)]
    [InlineData("<TG-BUTTON-2 text=\"Go\">", true)]
    [InlineData("see <tg-emoji-custom id=\"1\"> here", true)]
    [InlineData("use tg-map wisely", false)]
    [InlineData("```\n<img>\n```", true)] // code examples stay literal too
    [InlineData("[report](https://example.org/report)", false)]
    [InlineData("Review the [report](https://example.org/report) ✅", false)]
    [InlineData("the source of truth <b>bold</b>", false)]
    [InlineData("an image word without markup", false)]
    [InlineData("a < b and c > d", false)]
    [InlineData("description with <details><summary>T</summary>B</details>", false)]
    [InlineData("", false)]
    public void CapabilityGate_MatchesSpec(string answer, bool expected) =>
        Assert.Equal(expected, TextOnlyCapabilityPolicy.RequiresLiteral(answer));



    [Theory]
    [InlineData(32767, 1)]
    [InlineData(32768, 1)]
    [InlineData(32769, 2)]
    public void SplitRich_BoundaryCounts(int scalars, int expectedParts) =>
        Assert.Equal(expectedParts, LiteralMessageChunker.SplitRich(new string('a', scalars)).Count);


    [Fact]
    public void SplitRich_PrefersNewlineAndKeepsItInOriginalSlice()
    {
        var source = new string('a', 32700) + "\n" + new string('b', 1000);
        var parts = LiteralMessageChunker.SplitRich(source);
        Assert.Equal(2, parts.Count);
        Assert.EndsWith("\n", parts[0]);
        Assert.Equal(32701, AnswerSourceBound.CountScalars(parts[0]));
        Assert.Equal(source, string.Concat(parts));
    }


    [Fact]
    public void SplitRich_NeverSplitsSurrogatePairs()
    {
        var source = new string('a', 32767) + "\U0001F600" + new string('b', 100);
        var parts = LiteralMessageChunker.SplitRich(source);
        Assert.Equal(source, string.Concat(parts));
        foreach (var part in parts)
            Assert.DoesNotContain("\uFFFD", Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(part)));
        Assert.StartsWith(new string('a', 32767) + "\U0001F600", parts[0]);
    }


    [Fact]
    public void SplitRich_PrefersGraphemeBoundaryOverSplittingMarks()
    {
        var source = new string('x', 32767) + "e\u0301" + new string('y', 100);
        var parts = LiteralMessageChunker.SplitRich(source);
        Assert.Equal(source, string.Concat(parts));
        Assert.Equal(new string('x', 32767), parts[0]);
        Assert.StartsWith("e\u0301", parts[1]);
    }


    [Theory]
    [InlineData(4096, 1)]
    [InlineData(4097, 2)]
    public void SplitConservative_AsciiDualBound(int chars, int expectedParts) =>
        Assert.Equal(expectedParts, LiteralMessageChunker.SplitConservative(new string('a', chars)).Count);


    [Fact]
    public void SplitConservative_CountsUtf16UnitsForAstralChars()
    {
        Assert.Single(LiteralMessageChunker.SplitConservative(string.Concat(Enumerable.Repeat("\U0001F600", 2048))));
        var parts = LiteralMessageChunker.SplitConservative(string.Concat(Enumerable.Repeat("\U0001F600", 2049)));
        Assert.Equal(2, parts.Count);
        Assert.Equal(4096, parts[0].Length);
        Assert.Equal(2, parts[1].Length);
    }


    [Fact]
    public void SplitConservative_NeverSplitsCrlf()
    {
        var source = new string('a', 4095) + "\r\n" + new string('b', 100);
        var parts = LiteralMessageChunker.SplitConservative(source);
        Assert.Equal(source, string.Concat(parts));
        Assert.Equal(new string('a', 4095), parts[0]);
        Assert.StartsWith("\r\n", parts[1]);
    }


    [Fact]
    public void SplitConservative_KeepsHangulJamoTogether()
    {
        // Choseong + jungseong + jongseong: one text element, three runes.
        const string syllable = "\u1100\u1161\u11A8";
        var source = new string('x', 4094) + syllable + new string('y', 100);
        Assert.Equal(3, syllable.EnumerateRunes().Count());
        var parts = LiteralMessageChunker.SplitConservative(source);
        Assert.Equal(source, string.Concat(parts));
        Assert.Equal(new string('x', 4094), parts[0]);
        Assert.StartsWith(syllable, parts[1]);
    }


    [Fact]
    public void SplitConservative_KeepsZwjSequenceTogether()
    {
        const string family = "👨‍👩‍👧‍👦";
        var source = new string('x', 4094) + family + new string('y', 100);
        var parts = LiteralMessageChunker.SplitConservative(source);
        Assert.Equal(source, string.Concat(parts));
        Assert.Equal(new string('x', 4094), parts[0]);
        Assert.StartsWith(family, parts[1]);
    }
}
