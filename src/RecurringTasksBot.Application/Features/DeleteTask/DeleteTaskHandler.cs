// /delete command: the tombstone is persisted before best-effort
// termination, so concurrent work observes the deletion. Repeated deletion
// is a harmless no-op.
namespace RecurringTasksBot.Application;

public sealed class DeleteTaskHandler(
    IOperationStore operations,
    IUpdateReceiptStore receipts,
    IOrchestrationClient orchestrations,
    BotReplySender replies)
{
    public async Task<ProcessResult> HandleDeleteAsync(
        string ownerId, IncomingUpdate update, string text, DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return await CommandReplies.CompleteWithReplyAsync(receipts, replies,
                ownerId, update, text, nowUtc,
                "Usage: /delete <id>.\n\n" + HelpText.Message, ct);
        }

        var id = parts[1];
        var deleted = await DeletionHandler.DeleteAsync(
            operations, orchestrations, ownerId, id, ct);
        var reply = deleted ? $"Reminder {id} deleted." : $"No reminder with ID {id}.";
        return await CommandReplies.CompleteWithReplyAsync(
            receipts, replies, ownerId, update, text, nowUtc, reply, ct);
    }
}
