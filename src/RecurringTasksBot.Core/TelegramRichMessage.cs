// sendRichMessage request shape. Validated rich content travels as a
// rich_message object with exactly one content field (html, markdown, or
// blocks), never as a top-level text property.
using System.Text;
using System.Text.Json;

namespace RecurringTasksBot.Core;

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

    public static string BuildPayloadRequestJson(long chatId, TelegramPayload payload) =>
        payload.Kind switch
        {
            TelegramPayloadKind.Markdown => BuildMarkdownRequestJson(chatId, payload.Content),
            TelegramPayloadKind.LiteralRich => BuildLiteralRichRequestJson(chatId, payload.Content),
            TelegramPayloadKind.LegacyHtml => BuildRequestJson(chatId, payload.Content),
            _ => throw new ArgumentOutOfRangeException(nameof(payload)),
        };

    public static string BuildRequestJson(long chatId, string htmlPart)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("chat_id", chatId);
            writer.WriteStartObject("rich_message");
            writer.WriteString("html", htmlPart);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
