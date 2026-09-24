// Phase 2 character accounting. Telegram counts characters, not UTF-8
// bytes or .NET UTF-16 code units: one "character" is one Unicode scalar
// value, so limits and splits are measured in System.Text.Rune units.
// Splits never separate a surrogate pair and prefer newline boundaries so
// emoji and other multi-unit characters stay intact.
using System.Text;

namespace RecurringTasksBot.Core;

public static class TextLimits
{
    public const int MaxPromptChars = 32768;
    public const int MaxAnswerChars = 32768;
    public const int MaxListMessageChars = 32768;

    // Azure Table string properties hold ~64KB; stay well under it so one
    // chunk entity property never overflows, even for 4-byte characters.
    public const int StorageChunkChars = 16000;

    public static int CountChars(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
            count++;
        return count;
    }

    public static IReadOnlyList<string> SplitByChars(string text, int maxChars)
    {
        if (maxChars <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxChars));
        if (CountChars(text) <= maxChars)
            return [text];

        var runes = text.EnumerateRunes().ToArray();
        var parts = new List<string>();
        var start = 0;
        while (start < runes.Length)
        {
            var end = Math.Min(start + maxChars, runes.Length);
            if (end < runes.Length)
            {
                // Prefer a newline boundary, but never emit an empty part.
                var cut = end;
                for (var i = end - 1; i > start && i > end - 2048; i--)
                {
                    if (runes[i].Value == '\n')
                    {
                        cut = i + 1;
                        break;
                    }
                }

                if (cut == start)
                    cut = end;
                end = cut;
            }

            parts.Add(string.Concat(runes[start..end].Select(r => r.ToString())));
            start = end;
        }

        return parts;
    }

    // Bounded storage segmentation for prompts/answers whose markup or link
    // targets exceed a single table property.
    public static IReadOnlyList<string> ToStorageChunks(string text) =>
        SplitByChars(text, StorageChunkChars);

    public static string JoinStorageChunks(IEnumerable<string> chunks) =>
        string.Concat(chunks);
}
