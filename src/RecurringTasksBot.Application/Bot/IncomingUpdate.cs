// Webhook update DTO: the smallest camel-case subset Telegram delivers for
// the commands this bot handles. Parsing lives with the Telegram adapter;
// command routing consumes only these fields.
namespace RecurringTasksBot.Application;

public enum TelegramUpdateKind
{
    Message,
    Unsupported,
}

public sealed record IncomingUpdate(
    long UpdateId,
    long UserId,
    long ChatId,
    TelegramUpdateKind Kind,
    string? Text,
    // Reply-based creation: the replied-to prompt and its author's user ID.
    string? ReplyPrompt = null,
    long? ReplyUserId = null);
