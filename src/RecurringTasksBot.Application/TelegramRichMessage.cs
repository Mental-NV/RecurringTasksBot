// sendRichMessage request shape. Validated rich content travels as a
// rich_message object with exactly one content field (html, markdown, or
// blocks), never as a top-level text property.
using System.Text;
using System.Text.Json;

namespace RecurringTasksBot.Application;

public static class TelegramRichMessage
{
    // Native AI answer: the original answer string travels unchanged as
    // rich_message.markdown. No Markdown-to-HTML conversion.
    public static string BuildMarkdownRequestJson(long chatId, string markdown)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("chat_id", chatId);
            writer.WriteStartObject("rich_message");
            writer.WriteString("markdown", markdown);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // Native table (InputRichBlockTable): cell objects with RichText text,
    // header flags, and column spans; compact cells per the list contract.
    // The cells JSON is an array of row arrays of cell objects.
    public static string BuildTableRequestJson(long chatId, string? caption, string cellsJson)
    {
        using var cells = JsonDocument.Parse(cellsJson);
        var root = cells.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            throw new ArgumentOutOfRangeException(nameof(cellsJson), "Table cells must be a JSON array.");
        foreach (var row in root.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array)
                throw new ArgumentOutOfRangeException(nameof(cellsJson), "Each table row must be a JSON array.");
            foreach (var cell in row.EnumerateArray())
            {
                if (cell.ValueKind != JsonValueKind.Object ||
                    !cell.TryGetProperty("text", out var text) ||
                    text.ValueKind != JsonValueKind.String)
                    throw new ArgumentOutOfRangeException(nameof(cellsJson),
                        "Each table cell must be an object with a string \"text\".");
            }
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("chat_id", chatId);
            writer.WriteStartObject("rich_message");
            writer.WriteStartArray("blocks");
            if (!string.IsNullOrEmpty(caption))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "paragraph");
                writer.WriteString("text", caption);
                writer.WriteEndObject();
            }
            writer.WriteStartObject();
            writer.WriteString("type", "table");
            writer.WriteBoolean("is_compact", true);
            writer.WritePropertyName("cells");
            root.WriteTo(writer);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // Literal rich fallback or application text: one paragraph block per
    // message, preserving the original newlines. A plain JSON string is a
    // valid RichText value and does not parse Markdown/HTML.
    public static string BuildLiteralRichRequestJson(long chatId, string text)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("chat_id", chatId);
            writer.WriteStartObject("rich_message");
            writer.WriteStartArray("blocks");
            writer.WriteStartObject();
            writer.WriteString("type", "paragraph");
            writer.WriteString("text", text);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

}
