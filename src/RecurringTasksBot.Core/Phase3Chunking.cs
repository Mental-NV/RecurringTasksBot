// Deterministic literal-text chunking for rejected answers. No Markdown
// structure splitting: every resulting message is recorded individually by
// the plan, and no fallback transition invokes the LLM.
using System.Globalization;
using System.Text;

namespace RecurringTasksBot.Core;

public static class Phase3LiteralChunker
{
    // Literal-rich leaves capped at 32,768 scalars.
    public static IReadOnlyList<string> SplitRich(
        string source, int maxScalars = Phase3Limits.RichTextChars)
    {
        if (maxScalars <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxScalars));
        var runes = source.EnumerateRunes().ToArray();
        if (runes.Length <= maxScalars)
            return [source];
        var boundaries = TextElementBoundaries(source, runes);
        var parts = new List<string>();
        var start = 0;
        while (start < runes.Length)
        {
            var end = Math.Min(start + maxScalars, runes.Length);
            if (end < runes.Length)
                end = PreferBoundary(runes, start, end, boundaries);
            parts.Add(Join(runes, start, end));
            start = end;
        }
        return parts;
    }

    // Conservative compatibility bound: at most 4,096 UTF-16 code units and
    // 4,096 scalars per leaf.
    public static IReadOnlyList<string> SplitConservative(string source)
    {
        const int max = Phase3Limits.PlainFallbackMaxUnits;
        var runes = source.EnumerateRunes().ToArray();
        if (runes.Length <= max && source.Length <= max)
            return [source];
        var boundaries = TextElementBoundaries(source, runes);
        var parts = new List<string>();
        var start = 0;
        while (start < runes.Length)
        {
            var end = start;
            var units = 0;
            while (end < runes.Length &&
                end - start < max &&
                units + runes[end].Utf16SequenceLength <= max)
            {
                units += runes[end].Utf16SequenceLength;
                end++;
            }
            if (end == start)
                end = start + 1;
            if (end < runes.Length)
                end = PreferBoundary(runes, start, end, boundaries);
            parts.Add(Join(runes, start, end));
            start = end;
        }
        return parts;
    }

    private static string Join(System.Text.Rune[] runes, int start, int end)
    {
        var sb = new StringBuilder();
        for (var i = start; i < end; i++)
            sb.Append(runes[i].ToString());
        return sb.ToString();
    }

    // Text-element boundaries from StringInfo: CRLF, Hangul Jamo, ZWJ
    // sequences, regional-indicator pairs, and combining marks stay
    // together. boundaries[pos] is a cut between runes[pos-1] and
    // runes[pos]; index 0 and runes.Length are always boundaries.
    private static bool[] TextElementBoundaries(string source, System.Text.Rune[] runes)
    {
        var boundaries = new bool[runes.Length + 1];
        boundaries[0] = true;
        boundaries[runes.Length] = true;
        var enumerator = StringInfo.GetTextElementEnumerator(source);
        var runeIndex = 0;
        while (enumerator.MoveNext())
        {
            var remaining = enumerator.GetTextElement().Length;
            while (remaining > 0 && runeIndex < runes.Length)
            {
                remaining -= runes[runeIndex].Utf16SequenceLength;
                runeIndex++;
            }
            if (runeIndex <= runes.Length)
                boundaries[runeIndex] = true;
        }
        return boundaries;
    }

    // Prefer a newline within the last 512 scalar positions of the candidate
    // chunk (kept in its original slice); otherwise a text-element boundary
    // in that window; otherwise the scalar boundary. Always makes progress
    // and never splits a UTF-16 surrogate pair (cuts are rune-aligned).
    private static int PreferBoundary(
        System.Text.Rune[] runes, int start, int end, bool[] boundaries,
        int window = Phase3Limits.LiteralNewlineWindow)
    {
        var lower = Math.Max(start + 1, end - window);
        for (var i = end - 1; i >= lower; i--)
        {
            if (runes[i].Value == '\n')
                return i + 1;
        }
        var cut = end;
        while (cut > lower && !boundaries[cut])
            cut--;
        if (cut <= lower && !boundaries[lower])
            return end;
        return cut;
    }
}
