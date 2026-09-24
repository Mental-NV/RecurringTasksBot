using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace RecurringTasksBot.Core;

// Split rendered Unicode text, not serialized HTML. Every part closes and
// reopens its formatting, and entities/attributes are never split.
public static class RichMessageParts
{
    public static IReadOnlyList<string> Split(string html, int maxChars = TextLimits.MaxAnswerChars)
    {
        if (maxChars <= 0) throw new ArgumentOutOfRangeException(nameof(maxChars));
        XElement root;
        try
        {
            root = XElement.Parse("<root>" + html.Replace("<br>", "<br/>") + "</root>", LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            // Malformed formatting becomes literal text, preserving content.
            return TextLimits.SplitByChars(html, maxChars).Select(Escape).ToArray();
        }

        var parts = new List<string>();
        var current = new StringBuilder();
        var opened = new List<(string Open, string Close)>();
        var count = 0;
        void Flush()
        {
            if (count == 0) return;
            for (var i = opened.Count - 1; i >= 0; i--) current.Append(opened[i].Close);
            parts.Add(current.ToString());
            current.Clear();
            foreach (var tag in opened) current.Append(tag.Open);
            count = 0;
        }
        void AppendText(string text)
        {
            foreach (var rune in text.EnumerateRunes())
            {
                if (count == maxChars) Flush();
                current.Append(Escape(rune.ToString()));
                count++;
            }
        }
        void Visit(XNode node)
        {
            if (node is XText text) { AppendText(text.Value); return; }
            if (node is not XElement element) return;
            if (element.Name.LocalName == "br") { AppendText("\n"); return; }
            // Beyond Telegram's nesting limit, flatten only formatting.
            if (opened.Count >= 16)
            {
                foreach (var child in element.Nodes()) Visit(child);
                return;
            }
            var name = element.Name.LocalName;
            var open = "<" + name + string.Concat(element.Attributes().Select(a =>
                " " + a.Name.LocalName + "=\"" + Escape(a.Value) + "\"")) + ">";
            var close = "</" + name + ">";
            current.Append(open);
            opened.Add((open, close));
            foreach (var child in element.Nodes()) Visit(child);
            opened.RemoveAt(opened.Count - 1);
            current.Append(close);
        }
        foreach (var node in root.Nodes()) Visit(node);
        Flush();
        return parts;
    }

    public static int CountRenderedChars(string html) => TextLimits.CountChars(ToPlainText(html));

    public static string ToPlainText(string html) => WebUtility.HtmlDecode(
        Regex.Replace(html.Replace("<br>", "\n").Replace("<br/>", "\n"), "<[^>]+>", string.Empty));

    public static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;")
        .Replace(">", "&gt;").Replace("\"", "&quot;");
}
