using System.Text.Json;
using RecurringTasksBot.Core;

namespace RecurringTasksBot;

// Minimal Telegram update parsing: private-chat messages carry commands;
// everything else (edits, callbacks, channel posts, ...) is unsupported.
public static class TelegramUpdateParser
{
    public static IncomingUpdate? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("update_id", out var updateIdProp) ||
                !updateIdProp.TryGetInt64(out var updateId))
                return null;

            if (root.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.Object)
            {
                var userId = message.TryGetProperty("from", out var from) &&
                    from.TryGetProperty("id", out var uid) && uid.TryGetInt64(out var u)
                    ? u : 0L;
                var chatId = message.TryGetProperty("chat", out var chat) &&
                    chat.TryGetProperty("id", out var cid) && cid.TryGetInt64(out var c)
                    ? c : 0L;
                var text = message.TryGetProperty("text", out var textProp) &&
                    textProp.ValueKind == JsonValueKind.String
                    ? textProp.GetString() : null;
                return new IncomingUpdate(updateId, userId, chatId,
                    TelegramUpdateKind.Message, text);
            }

            return new IncomingUpdate(updateId, 0, 0, TelegramUpdateKind.Unsupported, null);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
