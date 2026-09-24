// Rich-text handling for Phase 2. Incoming rich messages are normalized
// into a textual prompt preserving reading order, paragraphs, lists,
// tables, code, and link targets (media stays out of scope). Outgoing
// answers use a Telegram HTML subset; unsupported or malformed formatting
// degrades to simple blocks without losing the answer and without another
// LLM call.
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RecurringTasksBot.Core;

public static class RichValidator
{
    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "b", "i", "u", "s", "code", "pre", "a", "br",
    };

    // Rebalance already-HTML content (for message parts split after
    // composition): strip disallowed tags, drop stray closers, auto-close.
    public static string SanitizeHtml(string? html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;
        return Balance(StripDisallowed(html));
    }

    // Strip disallowed tags (keeping inner text), drop non-http(s) link
    // targets, auto-close unclosed inline tags, and drop stray closers so
    // every part stays valid. Never returns empty for non-empty input:
    // falls back to escaped plain text.
    public static string ToSafeHtml(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return string.Empty;
        var html = ConvertMarkdown(markdown);
        var safe = Sanitize(html);
        if (string.IsNullOrWhiteSpace(StripTags(safe)) && markdown.Trim().Length > 0)
            return Escape(markdown.Trim());
        return safe;
    }

    private static string ConvertMarkdown(string text)
    {
        var escaped = Escape(text);
        // Fenced code first so inner content is not reinterpreted.
        escaped = Regex.Replace(escaped, @"```(\w*)\r?\n(.*?)```",
            m => "<pre>" + m.Groups[2].Value.Trim('\n') + "</pre>",
            RegexOptions.Singleline);
        escaped = Regex.Replace(escaped, @"`([^`\n]+)`", "<code>$1</code>");
        // Non-http(s) link targets are dropped, keeping the label text.
        escaped = Regex.Replace(escaped, @"\[([^\]]+)\]\((?!https?://)[^)\s]*\)", "$1");
        escaped = Regex.Replace(escaped, @"\[([^\]]+)\]\((https?://[^)\s]+)\)",
            m => $"<a href=\"{m.Groups[2].Value.Replace("\"", "&quot;")}\">{m.Groups[1].Value}</a>");
        escaped = Regex.Replace(escaped, @"(?m)^\s{0,3}#{1,6}\s+(.+)$", "<b>$1</b>");
        escaped = Regex.Replace(escaped, @"\*\*([^*]+)\*\*", "<b>$1</b>");
        return escaped;
    }

    private static string Sanitize(string html) =>
        Balance(StripDisallowed(html));

    private static string StripDisallowed(string html)
    {
        var withLinks = Regex.Replace(html, @"<a\s+href=""([^""]*)""\s*>",
            m =>
            {
                var href = m.Groups[1].Value;
                return href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    href.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? m.Value
                    : "<a>";
            }, RegexOptions.IgnoreCase);
        withLinks = withLinks.Replace("<a>", string.Empty);
        // Keep bare <a> only when produced above without href: drop them.
        var stripped = Regex.Replace(withLinks, @"</?(?!(?:b|i|u|s|code|pre|a|br)\b)[A-Za-z][^>]*>",
            string.Empty, RegexOptions.IgnoreCase);
        // Normalize tag names to lowercase supported set.
        stripped = Regex.Replace(stripped, @"</?([A-Za-z]+)(\s[^>]*)?>", m =>
        {
            var name = m.Groups[1].Value.ToLowerInvariant();
            if (!AllowedTags.Contains(name))
                return string.Empty;
            if (name == "br")
                return "<br>";
            var isClose = m.Value.StartsWith("</", StringComparison.Ordinal);
            if (name == "a")
                return isClose ? "</a>" : m.Value;
            return isClose ? $"</{name}>" : $"<{name}>";
        });
        return stripped;
    }

    private static string Balance(string html)
    {
        var stack = new Stack<string>();
        var sb = new StringBuilder();
        var token = new Regex(@"(</?(?:b|i|u|s|code|pre)(?:\s[^>]*)?>|</?a(?:\s+href=""[^""]*"")?>|<br>)",
            RegexOptions.IgnoreCase);
        var last = 0;
        foreach (Match m in token.Matches(html))
        {
            sb.Append(html, last, m.Index - last);
            last = m.Index + m.Length;
            var tag = m.Value;
            var name = Regex.Match(tag, @"[A-Za-z]+").Value.ToLowerInvariant();
            if (name == "br")
            {
                sb.Append("<br>");
                continue;
            }

            if (tag.StartsWith("</", StringComparison.Ordinal))
            {
                if (stack.Count > 0 && stack.Peek() == name)
                {
                    stack.Pop();
                    sb.Append(name == "a" ? "</a>" : $"</{name}>");
                }
                // Stray closers are dropped.
            }
            else
            {
                stack.Push(name);
                sb.Append(tag);
            }
        }

        sb.Append(html, last, html.Length - last);
        while (stack.Count > 0)
        {
            var name = stack.Pop();
            sb.Append(name == "a" ? "</a>" : $"</{name}>");
        }

        return sb.ToString();
    }

    private static string StripTags(string html) =>
        Regex.Replace(html, @"<[^>]+>", string.Empty);

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}

public static class AnswerComposer
{
    public const string TruncationMark = "\n\n…[answer truncated to the 32,768-character limit]";

    public static IReadOnlyList<string> Compose(
        string operationId,
        DateTime scheduledUtc,
        DateTime executedUtc,
        string answerText,
        IReadOnlyList<LlmSource> sources,
        int maxChars = TextLimits.MaxAnswerChars)
    {
        var header =
            $"<b>Reminder {Escape(operationId)}</b>\n" +
            $"Scheduled (UTC): {scheduledUtc:u}\n" +
            $"Answered (UTC): {executedUtc:u}";
        var body = RichValidator.ToSafeHtml(answerText.Trim());
        var sourceBlock = FormatSources(sources);

        var full = header + "\n\n" + body;
        if (sourceBlock.Length > 0)
            full += "\n\n" + sourceBlock;

        return RichMessageParts.Split(full, maxChars);
    }

    public static string FailureNotice(string operationId, DateTime scheduledUtc) =>
        $"Reminder {operationId} · scheduled {scheduledUtc:u}: this run failed, " +
        "but future runs remain scheduled.";

    private static string FormatSources(IReadOnlyList<LlmSource> sources)
    {
        var usable = sources
            .Where(s => s.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                s.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .Take(10)
            .ToList();
        if (usable.Count == 0)
            return string.Empty;
        var sb = new StringBuilder("<b>Sources</b>");
        for (var i = 0; i < usable.Count; i++)
        {
            var title = usable[i].Title.Trim();
            if (title.Length == 0)
                title = usable[i].Url;
            if (title.Length > 200)
                title = title[..200];
            sb.Append($"\n{i + 1}. <a href=\"{RichMessageParts.Escape(usable[i].Url)}\">{Escape(title)}</a>");
        }

        return sb.ToString();
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
