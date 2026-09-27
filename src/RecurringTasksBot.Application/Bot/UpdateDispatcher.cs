// Command dispatcher: webhook secret, update receipts, verb routing, and
// acknowledgement/error rules live here exactly once. Feature handlers own
// command logic; shared reply completion lives in CommandReplies.
namespace RecurringTasksBot.Application;

public sealed record ProcessResult(int StatusCode, IReadOnlyList<string> RepliesSent);

public sealed class UpdateDispatcher(
    IOperationStore operations,
    IUpdateReceiptStore receipts,
    IOrchestrationClient orchestrations,
    ITelegramTransport transport)
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

        var create = new CreateTaskHandler(operations, receipts, orchestrations, Replies);
        var list = new ListTasksHandler(operations, receipts, orchestrations, Replies);
        var delete = new DeleteTaskHandler(operations, receipts, orchestrations, Replies);
        var help = new HelpHandler(receipts, Replies);

        try
        {
            return verb switch
            {
                "/create" => await create.HandleCreateAsync(ownerId, update, text, nowUtc, ct),
                "/list" => await list.HandleListAsync(ownerId, update, text, nowUtc, ct),
                "/delete" => await delete.HandleDeleteAsync(ownerId, update, text, nowUtc, ct),
                _ => await help.HandleUnknownAsync(ownerId, update, text, nowUtc, ct),
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
        // anything. Other commands have no stored reply content, so they are
        // acknowledged silently.
        if (!receipt.ReplyDelivered && receipt.OperationId is not null &&
            receipt.Command.TrimStart().StartsWith("/create", StringComparison.OrdinalIgnoreCase))
        {
            var create = new CreateTaskHandler(operations, receipts, orchestrations, Replies);
            return await create.ResendConfirmationAsync(ownerId, receipt, update, ct);
        }

        return new ProcessResult(200, []);
    }

    private static bool IsTransient(Exception ex) =>
        ex is TransientStoreException or TimeoutException or HttpRequestException
        || (ex is TelegramSendException tex &&
            DeliveryPolicy.Classify(tex).Kind == SendFailureKind.Transient);
}
