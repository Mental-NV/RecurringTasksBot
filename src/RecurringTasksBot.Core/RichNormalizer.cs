using System.Text;
using System.Text.Json;

namespace RecurringTasksBot.Core;

// Telegram RichText is a string, an array of inline RichText values, or a
// typed wrapper. Only block containers introduce paragraph separators.
public static class RichNormalizer
{
    public static string? NormalizePrompt(string? text, string? richMessageJson)
    {
        if (!string.IsNullOrWhiteSpace(text)) return text;
        if (string.IsNullOrWhiteSpace(richMessageJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(richMessageJson);
            var result = Render(doc.RootElement).Trim();
            return result.Length == 0 ? null : result;
        }
        catch (JsonException) { return null; }
    }

    private static string Render(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.String) return node.GetString() ?? string.Empty;
        if (node.ValueKind == JsonValueKind.Array)
            return string.Concat(node.EnumerateArray().Select(Render));
        if (node.ValueKind != JsonValueKind.Object) return string.Empty;

        var type = node.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() : null;
        if (type is "photo" or "video" or "audio" or "document" or "animation" or "voice_note"
            or "map" or "collage" or "slideshow") return string.Empty;

        if (node.TryGetProperty("url", out var url) || node.TryGetProperty("href", out url))
        {
            var label = Content(node, "text", "caption");
            var target = url.ValueKind == JsonValueKind.String ? url.GetString() : null;
            return string.IsNullOrEmpty(target) ? label
                : label.Length == 0 || label == target ? target : $"{label} ({target})";
        }

        if (type == "pre" || node.TryGetProperty("language", out _))
            return "```" + Content(node, "language") + "\n" + Content(node, "text", "code") + "\n```";

        // The API calls this field 'cells'; 'rows' is accepted for older inputs.
        if (node.TryGetProperty("cells", out var rows) || node.TryGetProperty("rows", out rows))
        {
            var result = new StringBuilder();
            if (rows.ValueKind == JsonValueKind.Array)
                foreach (var row in rows.EnumerateArray())
                    if (row.ValueKind == JsonValueKind.Array)
                        result.Append("| ").AppendJoin(" | ", row.EnumerateArray().Select(Render)).Append(" |\n");
            var caption = Content(node, "caption");
            if (caption.Length > 0) result.Append(caption);
            return result.ToString().TrimEnd('\n');
        }

        if (node.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            var result = new List<string>();
            var ordered = node.TryGetProperty("ordered", out var o) && o.ValueKind == JsonValueKind.True;
            foreach (var item in items.EnumerateArray())
            {
                var label = item.ValueKind == JsonValueKind.Object ? Content(item, "label") : string.Empty;
                result.Add((label.Length > 0 ? label + " " : ordered ? $"{result.Count + 1}. " : "- ") + Render(item));
            }
            return string.Join('\n', result);
        }

        if (node.TryGetProperty("blocks", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
            return string.Join("\n\n", blocks.EnumerateArray().Select(Render).Where(s => s.Length > 0));

        // Formatting wrappers and table cells all contain RichText in 'text'.
        // The type discriminator and unrelated metadata never become content.
        return Content(node, "text", "segments", "spans", "children", "content", "caption", "source", "formula");
    }

    private static string Content(JsonElement node, params string[] names)
    {
        foreach (var name in names)
            if (node.TryGetProperty(name, out var value)) return Render(value);
        return string.Empty;
    }
}
