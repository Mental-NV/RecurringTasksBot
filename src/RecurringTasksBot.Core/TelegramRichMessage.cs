// sendRichMessage request shape. Validated rich content travels as a
// rich_message object with exactly one content field (html, markdown, or
// blocks), never as a top-level text property.
using System.Text;
using System.Text.Json;

namespace RecurringTasksBot.Core;

public static class TelegramRichMessage
{
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
