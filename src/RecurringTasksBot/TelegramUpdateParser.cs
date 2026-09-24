// Minimal Telegram update parsing: private-chat messages carry commands;
// everything else (edits, callbacks, channel posts, ...) is unsupported.
// Phase 2 also parses rich messages (message.rich_message) and the
// replied-to message (message.reply_to_message) for reply-based creation.
// Rich content is normalized to prompt text preserving reading order,
// paragraphs, lists, tables, code, and link targets.
using System.Text.Json;
using RecurringTasksBot.Core;

namespace RecurringTasksBot;

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
                var text = ExtractPromptText(message);

                string? replyPrompt = null;
                long? replyUserId = null;
                if (message.TryGetProperty("reply_to_message", out var reply) &&
                    reply.ValueKind == JsonValueKind.Object)
                {
                    if (reply.TryGetProperty("from", out var replyFrom) &&
                        replyFrom.TryGetProperty("id", out var rid) && rid.TryGetInt64(out var r))
                        replyUserId = r;
                    replyPrompt = ExtractPromptText(reply);
                }

                return new IncomingUpdate(updateId, userId, chatId,
                    TelegramUpdateKind.Message, text, replyPrompt, replyUserId);
            }

            return new IncomingUpdate(updateId, 0, 0, TelegramUpdateKind.Unsupported, null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractPromptText(JsonElement message)
    {
        if (message.TryGetProperty("text", out var textProp) &&
            textProp.ValueKind == JsonValueKind.String)
            return textProp.GetString();
        if (message.TryGetProperty("caption", out var caption) &&
            caption.ValueKind == JsonValueKind.String)
            return caption.GetString();
        if (message.TryGetProperty("rich_message", out var rich))
            return RichNormalizer.NormalizePrompt(null, rich.GetRawText());
        return null;
    }
}
