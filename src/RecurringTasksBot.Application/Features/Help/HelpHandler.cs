// Help handler: unknown commands are answered, never executed.
namespace RecurringTasksBot.Application;

public sealed class HelpHandler(
    IUpdateReceiptStore receipts,
    BotReplySender replies)
{
    public Task<ProcessResult> HandleUnknownAsync(
        string ownerId, IncomingUpdate update, string text, DateTimeOffset nowUtc,
        CancellationToken ct) =>
        CommandReplies.CompleteWithReplyAsync(
            receipts, replies, ownerId, update, text, nowUtc, HelpText.Message, ct);
}
