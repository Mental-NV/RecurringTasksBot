// Conservative pre-send source scan. A hit delivers the entire answer as
// literal text; there is no partial stripping or format conversion. The scan
// intentionally also matches examples inside code blocks.
namespace RecurringTasksBot.Application;

public static class TextOnlyCapabilityPolicy
{
    // Exact HTML tag names need a terminator; Telegram tg- tags are blocked
    // by prefix, so <tg-map-view /> and any longer tg- name stay literal.
    private static readonly System.Text.RegularExpressions.Regex BlockedTag = new(
        @"<\s*(?:(img|video|audio|source|iframe|script)(?=[\s/>])|tg-(?:button|map|collage|slideshow|document|emoji|thinking))",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant |
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool RequiresLiteral(string? answer)
    {
        if (string.IsNullOrEmpty(answer))
            return false;
        if (answer.Contains("![", StringComparison.Ordinal))
            return true;
        return BlockedTag.IsMatch(answer);
    }
}
