// Shared acknowledgement and reply rules for command handlers: exactly one
// copy. Receipt completion and reply delivery are tracked separately, so a
// redelivered completed command never re-executes.
namespace RecurringTasksBot.Application;

internal static class CommandReplies
{
    internal static async Task<ProcessResult> CompleteWithReplyAsync(
        IUpdateReceiptStore receipts, BotReplySender replies,
        string ownerId, IncomingUpdate update, string commandText, DateTimeOffset nowUtc,
        string reply, CancellationToken ct)
    {
        await EnsureReceiptAsync(receipts, ownerId, update, commandText, nowUtc, ct);
        await replies.SendReplyAsync(update.ChatId, reply, ct);
        await receipts.MarkCompletedAsync(ownerId, update.UpdateId, null, ct);
        await receipts.MarkReplyDeliveredAsync(ownerId, update.UpdateId, ct);
        return new ProcessResult(200, [reply]);
    }

    internal static async Task EnsureReceiptAsync(
        IUpdateReceiptStore receipts,
        string ownerId, IncomingUpdate update, string commandText, DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        try
        {
            await receipts.InsertAsync(new UpdateReceipt(ownerId, update.UpdateId,
                UpdateReceipts.BoundCommand(commandText), null, false, false, nowUtc, nowUtc), ct);
        }
        catch (ConcurrencyConflictException)
        {
            // Already claimed by a concurrent attempt; continue idempotently.
        }
    }
}
