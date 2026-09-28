// Command dispatcher: webhook secret, update receipts, verb routing, and
// acknowledgement/error rules live here exactly once. Phase 5 JSON task
// commands own every verb; legacy positional input is answered with help.
namespace RecurringTasksBot.Application;

public sealed record ProcessResult(int StatusCode, IReadOnlyList<string> RepliesSent);

public sealed class UpdateDispatcher(
    ITaskStore tasks,
    IUpdateReceiptStore receipts,
    ITaskOrchestrationClient orchestrations,
    ITelegramTransport transport,
    TaskDefaults taskDefaults,
    IOccurrenceRepository occurrences)
{
    private BotReplySender Replies => new(transport);

    public async Task<ProcessResult> ProcessAsync(
        bool secretValid, IncomingUpdate? update, DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        if (!secretValid)
            return new ProcessResult(403, []);

        if (update is null || update.Kind != TelegramUpdateKind.Message)
            return new ProcessResult(200, []);

        var ownerId = update.UserId.ToString();
        UpdateReceipt? receipt;
        try
        {
            receipt = await receipts.GetAsync(ownerId, update.UpdateId, ct);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            return new ProcessResult(503, []);
        }

        if (receipt is { CommandCompleted: true })
            return await HandleCompletedRedeliveryAsync(ownerId, receipt, update, ct);

        var text = (update.Text ?? string.Empty).Trim();
        var verb = text.Length == 0 ? string.Empty : text.Split((char[]?)null, 2,
            StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();

        var create = new TaskCreateHandler(tasks, receipts, orchestrations, Replies);
        var get = new TaskGetHandler(tasks, receipts, Replies, taskDefaults);
        var upkeep = new TaskUpdateHandler(tasks, receipts, orchestrations, Replies);
        var list = new TaskListHandler(tasks, receipts, Replies, occurrences, orchestrations);
        var delete = new TaskDeleteHandler(tasks, receipts, orchestrations, Replies);

        try
        {
            return verb switch
            {
                "/create" => await create.HandleCreateAsync(ownerId, update, text, nowUtc, ct),
                "/get" => await get.HandleGetAsync(ownerId, update, text, nowUtc, ct),
                "/update" => await upkeep.HandleUpdateAsync(ownerId, update, text, nowUtc, ct),
                "/list" => await list.HandleListAsync(ownerId, update, text, nowUtc, ct),
                "/delete" => await delete.HandleDeleteAsync(ownerId, update, text, nowUtc, ct),
                _ => await CommandReplies.CompleteWithReplyAsync(receipts, Replies,
                    ownerId, update, text, nowUtc, TaskHelp.Unknown, ct),
            };
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            return new ProcessResult(503, []);
        }
        catch (TelegramSendException)
        {
            // Permanent reply failure (e.g. blocked bot): the update itself
            // was processed; there is nobody to reply to.
            return new ProcessResult(200, []);
        }
    }

    private async Task<ProcessResult> HandleCompletedRedeliveryAsync(
        string ownerId, UpdateReceipt receipt, IncomingUpdate update, CancellationToken ct)
    {
        // A failed /create confirmation is resent without re-executing
        // anything. Other completed commands are acknowledged silently:
        // update/delete mutations must never apply twice, and reads have no
        // stored reply content worth resending.
        if (!receipt.ReplyDelivered && receipt.OperationId is not null &&
            receipt.Command.TrimStart().StartsWith("/create", StringComparison.OrdinalIgnoreCase))
        {
            var create = new TaskCreateHandler(tasks, receipts, orchestrations, Replies);
            return await create.ResendConfirmationAsync(ownerId, receipt, update, ct);
        }

        return new ProcessResult(200, []);
    }

    private static bool IsTransient(Exception ex) =>
        ex is TransientStoreException or TimeoutException or HttpRequestException
        || (ex is TelegramSendException tex &&
            DeliveryPolicy.Classify(tex).Kind == SendFailureKind.Transient);
}
