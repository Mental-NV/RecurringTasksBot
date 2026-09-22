// Webhook update processing: receipts -> commands -> Telegram replies ->
// HTTP status. HTTP acknowledgments and Telegram replies are separate:
// success paths return 200 after best-effort replies; transient store or
// send failures return 503 so Telegram retries; a bad secret returns 403.
// Command completion and reply delivery are tracked separately on the
// update receipt: a redelivered completed command is acknowledged without
// re-execution, and a failed /create confirmation is resent without
// re-executing the command.
namespace RecurringTasksBot.Core;

public sealed record ProcessResult(int StatusCode, IReadOnlyList<string> RepliesSent);

public sealed class UpdateProcessor(
    IOperationStore operations,
    IUpdateReceiptStore receipts,
    IOrchestrationClient orchestrations,
    ITelegramSender sender,
    CreateFlow? createFlow = null)
{
    private CreateFlow Flow => createFlow ?? new CreateFlow(operations, receipts, orchestrations);

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

        try
        {
            return verb switch
            {
                "/create" => await HandleCreateAsync(ownerId, update, text, nowUtc, ct),
                "/list" => await HandleListAsync(ownerId, update, text, nowUtc, ct),
                "/delete" => await HandleDeleteAsync(ownerId, update, text, nowUtc, ct),
                _ => await CompleteWithReplyAsync(ownerId, update, text, nowUtc,
                    ListFormatter.HelpMessage, ct),
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
        // Resend a failed /create confirmation without re-executing anything.
        // Other commands have no stored reply content; acknowledge silently.
        if (!receipt.ReplyDelivered && receipt.OperationId is not null &&
            receipt.Command.TrimStart().StartsWith("/create", StringComparison.OrdinalIgnoreCase))
        {
            var op = await operations.GetAsync(ownerId, receipt.OperationId, ct);
            if (op is not null && op.Status is not (OperationStatus.Deleted or OperationStatus.Failed))
            {
                var reply = CreateConfirmation(op);
                try
                {
                    await sender.SendTextAsync(update.ChatId, reply, ct);
                    await receipts.MarkReplyDeliveredAsync(ownerId, update.UpdateId, ct);
                    return new ProcessResult(200, [reply]);
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    return new ProcessResult(503, []);
                }
            }
        }

        return new ProcessResult(200, []);
    }

    private async Task<ProcessResult> HandleCreateAsync(
        string ownerId, IncomingUpdate update, string text, DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        if (!CreateCommandParser.TryParse(text, nowUtc.UtcDateTime, out var cmd, out var error) ||
            cmd is null)
        {
            var help = $"{error}\n\n{ListFormatter.HelpMessage}";
            return await CompleteWithReplyAsync(ownerId, update, text, nowUtc, help, ct);
        }

        var result = await Flow.HandleCreateAsync(update.UserId, update.ChatId,
            update.UpdateId, text, cmd.CronExpression, cmd.Text, nowUtc, ct);

        if (result.Outcome == CreateOutcome.Duplicate)
        {
            // A concurrent attempt owns execution; acknowledge for retry.
            return new ProcessResult(200, []);
        }

        var op = await operations.GetAsync(ownerId, result.OperationId, ct);
        var reply = op is not null ? CreateConfirmation(op) : $"Reminder {result.OperationId} created.";
        await sender.SendTextAsync(update.ChatId, reply, ct);
        await receipts.MarkReplyDeliveredAsync(ownerId, update.UpdateId, ct);
        return new ProcessResult(200, [reply]);
    }

    private async Task<ProcessResult> HandleListAsync(
        string ownerId, IncomingUpdate update, string text, DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        await EnsureReceiptAsync(ownerId, update, text, nowUtc, ct);
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
            await sender.SendTextAsync(update.ChatId, message, ct);
        await receipts.MarkCompletedAsync(ownerId, update.UpdateId, null, ct);
        await receipts.MarkReplyDeliveredAsync(ownerId, update.UpdateId, ct);
        return new ProcessResult(200, messages);
    }

    private async Task<ProcessResult> HandleDeleteAsync(
        string ownerId, IncomingUpdate update, string text, DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return await CompleteWithReplyAsync(ownerId, update, text, nowUtc,
                "Usage: /delete <id>.\n\n" + ListFormatter.HelpMessage, ct);
        }

        var id = parts[1];
        var deleted = await DeletionHandler.DeleteAsync(
            operations, orchestrations, ownerId, id, ct);
        var reply = deleted ? $"Reminder {id} deleted." : $"No reminder with ID {id}.";
        return await CompleteWithReplyAsync(ownerId, update, text, nowUtc, reply, ct);
    }

    private async Task<ProcessResult> CompleteWithReplyAsync(
        string ownerId, IncomingUpdate update, string commandText, DateTimeOffset nowUtc,
        string reply, CancellationToken ct)
    {
        await EnsureReceiptAsync(ownerId, update, commandText, nowUtc, ct);
        await sender.SendTextAsync(update.ChatId, reply, ct);
        await receipts.MarkCompletedAsync(ownerId, update.UpdateId, null, ct);
        await receipts.MarkReplyDeliveredAsync(ownerId, update.UpdateId, ct);
        return new ProcessResult(200, [reply]);
    }

    private async Task EnsureReceiptAsync(
        string ownerId, IncomingUpdate update, string commandText, DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        try
        {
            await receipts.InsertAsync(new UpdateReceipt(ownerId, update.UpdateId,
                commandText, null, false, false, nowUtc, nowUtc), ct);
        }
        catch (ConcurrencyConflictException)
        {
            // Already claimed by a concurrent attempt; continue idempotently.
        }
    }

    private static string CreateConfirmation(OperationRecord op) =>
        $"Reminder {op.OperationId} created ({OperationStatusNames.ToName(op.Status)}).\n" +
        $"Schedule: {op.CronExpression} (UTC).";

    private static bool IsTransient(Exception ex) =>
        ex is TransientStoreException or TimeoutException or HttpRequestException
        || (ex is TelegramSendException tex &&
            DeliveryPolicy.Classify(tex).Kind == SendFailureKind.Transient);
}
