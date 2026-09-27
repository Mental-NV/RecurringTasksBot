// /create command: schedule-plus-prompt parsing, creation recovery, and
// confirmation replies. A failed confirmation is resent without restarting
// the task.
namespace RecurringTasksBot.Application;

public sealed class CreateTaskHandler(
    IOperationStore operations,
    IUpdateReceiptStore receipts,
    IOrchestrationClient orchestrations,
    BotReplySender replies,
    CreateFlow? createFlow = null)
{
    private CreateFlow Flow => createFlow ?? new CreateFlow(operations, receipts, orchestrations);

    public async Task<ProcessResult> HandleCreateAsync(
        string ownerId, IncomingUpdate update, string text, DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        // Reply-based creation: `/create <schedule>` as a reply uses the
        // replied-to text or rich message from the same user as the prompt.
        var effectiveText = text;
        if (!HasInlinePrompt(text) && update.ReplyPrompt is { Length: > 0 } reply &&
            update.ReplyUserId == update.UserId)
            effectiveText = text.TrimEnd() + "\n" + reply.Trim();

        if (!CreateCommandParser.TryParse(effectiveText, nowUtc.UtcDateTime, out var cmd, out var error) ||
            cmd is null)
        {
            var help = $"{error}\n\n{HelpText.Message}";
            return await CommandReplies.CompleteWithReplyAsync(
                receipts, replies, ownerId, update, effectiveText, nowUtc, help, ct);
        }

        var result = await Flow.HandleCreateAsync(update.UserId, update.ChatId,
            update.UpdateId, effectiveText, cmd.CronExpression, cmd.Text, nowUtc, ct);

        if (result.Outcome == CreateOutcome.Duplicate)
        {
            // A concurrent attempt owns execution; acknowledge for retry.
            return new ProcessResult(200, []);
        }

        var op = await operations.GetAsync(ownerId, result.OperationId, ct);
        var replyText = op is not null ? CreateConfirmation(op) : $"Reminder {result.OperationId} created.";
        await replies.SendReplyAsync(update.ChatId, replyText, ct);
        await receipts.MarkReplyDeliveredAsync(ownerId, update.UpdateId, ct);
        return new ProcessResult(200, [replyText]);
    }

    public async Task<ProcessResult> ResendConfirmationAsync(
        string ownerId, UpdateReceipt receipt, IncomingUpdate update, CancellationToken ct)
    {
        var op = await operations.GetAsync(ownerId, receipt.OperationId!, ct);
        if (op is not null && op.Status is not (OperationStatus.Deleted or OperationStatus.Failed))
        {
            var reply = CreateConfirmation(op);
            await replies.SendReplyAsync(update.ChatId, reply, ct);
            await receipts.MarkReplyDeliveredAsync(ownerId, update.UpdateId, ct);
            return new ProcessResult(200, [reply]);
        }

        return new ProcessResult(200, []);
    }

    // Inline text beyond the verb plus six schedule fields counts as the
    // prompt; a bare schedule relies on the replied-to message.
    private static bool HasInlinePrompt(string text)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length > 7;
    }

    private static string CreateConfirmation(OperationRecord op) =>
        $"Reminder {op.OperationId} created ({OperationStatusNames.ToName(op.Status)}).\n" +
        $"Schedule: {op.CronExpression} (UTC).\n" +
        "I will run this prompt at each occurrence and send the answer here.";
}
