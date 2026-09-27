// /list command: caller-owned operations with Durable status
// reconciliation, split across messages when long.
namespace RecurringTasksBot.Application;

public sealed class ListTasksHandler(
    IOperationStore operations,
    IUpdateReceiptStore receipts,
    IOrchestrationClient orchestrations,
    BotReplySender replies)
{
    public async Task<ProcessResult> HandleListAsync(
        string ownerId, IncomingUpdate update, string text, DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        await CommandReplies.EnsureReceiptAsync(receipts, ownerId, update, text, nowUtc, ct);
        var ops = await operations.ListOwnedAsync(ownerId, ct);
        var summaries = new List<OperationSummary>();
        foreach (var op in ops)
        {
            string? runtime = null;
            if (op is { Status: OperationStatus.Active or OperationStatus.Starting } &&
                op.InstanceId is not null)
            {
                try
                {
                    runtime = await orchestrations.GetRuntimeStatusAsync(op.InstanceId, ct);
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    runtime = null;
                }
            }

            var (listStatus, _) = FailureReporter.ResolveListStatus(op, runtime);
            summaries.Add(new OperationSummary(op.OperationId, op.CronExpression,
                op.Text, listStatus));
        }

        var messages = summaries.Count == 0
            ? new[] { ListFormatter.EmptyListMessage }
            : ListFormatter.Split(summaries).ToArray();
        foreach (var message in messages)
            await replies.SendReplyAsync(update.ChatId, message, ct);
        await receipts.MarkCompletedAsync(ownerId, update.UpdateId, null, ct);
        await receipts.MarkReplyDeliveredAsync(ownerId, update.UpdateId, ct);
        return new ProcessResult(200, messages);
    }

    private static bool IsTransient(Exception ex) =>
        ex is TransientStoreException or TimeoutException or HttpRequestException
        || (ex is TelegramSendException tex &&
            DeliveryPolicy.Classify(tex).Kind == SendFailureKind.Transient);
}
