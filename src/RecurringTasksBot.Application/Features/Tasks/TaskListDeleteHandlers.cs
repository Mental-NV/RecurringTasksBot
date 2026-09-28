// Phase 5 /list and /delete for tasks. List pages, groups by timezone,
// and honors the compact stacked fallback; delete tombstones before
// requesting orchestration shutdown.
namespace RecurringTasksBot.Application;

public sealed class TaskListHandler(
    ITaskStore tasks,
    IUpdateReceiptStore receipts,
    BotReplySender replies,
    IOccurrenceRepository occurrences,
    IOrchestrationClient orchestrations)
{
    public async Task<ProcessResult> HandleListAsync(
        string ownerId, IncomingUpdate update, string text, DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        if (!TaskCommandParser.TryParseListArgs(text, out var args, out var usageError) ||
            args is null)
        {
            return await CommandReplies.CompleteWithReplyAsync(receipts, replies,
                ownerId, update, text, nowUtc, usageError + "\n\n" + TaskHelp.ListUsage, ct);
        }

        // Running-awareness checks every active task against durable
        // runtime health, including claim-less ones whose stored status
        // would otherwise read active for a dead orchestration. A
        // confirmed runtime failure shows failed; unreadable or
        // non-running health shows unknown; only a live instance falls
        // through to claim/lease resolution. One status read per active
        // task; non-active rows keep their stored status.
        var owned = await tasks.ListOwnedAsync(ownerId, ct);
        var activity = new Dictionary<string, TaskListItem>();
        foreach (var t in owned.Where(t => t.Status == TaskState.Active))
        {
            activity[t.TaskId] = await ResolveItemAsync(ownerId, t, nowUtc, ct);
        }
        var rows = TaskListBuilder.BuildPage(owned, activity, nowUtc.UtcDateTime, args.Page);
        if (args.Compact || rows.Count == 0)
        {
            var message = TaskListFormatter.FormatPage(rows, args.Page, nowUtc.UtcDateTime, args.Compact);
            return await CommandReplies.CompleteWithReplyAsync(
                receipts, replies, ownerId, update, text, nowUtc, message, ct);
        }

        // Native table first; the stacked text below doubles as the
        // delivery fallback and the reply record.
        var (cellsJson, stacked) = TaskListFormatter.FormatTable(rows, args.Page, nowUtc.UtcDateTime);
        return await CommandReplies.CompleteWithTableAsync(
            receipts, replies, ownerId, update, text, nowUtc, null, cellsJson, stacked, ct);
    }

    private async Task<TaskListItem> ResolveItemAsync(
        string ownerId, TaskRecord record, DateTimeOffset nowUtc, CancellationToken ct)
    {
        string? runtime;
        try
        {
            runtime = await orchestrations.GetRuntimeStatusAsync(record.InstanceId, ct);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            runtime = null;
        }
        TaskActivityKind kind;
        if (string.Equals(runtime, "Failed", StringComparison.Ordinal))
            kind = TaskActivityKind.OrchestrationFailed;
        else if (!string.Equals(runtime, "Running", StringComparison.Ordinal))
            kind = TaskActivityKind.UnknownLease;
        else
            kind = await ResolveActivityAsync(ownerId, record, nowUtc, ct);
        return new TaskListItem(record, kind,
            record.ActiveClaim?.ScheduledUtc, record.PendingActivationUtc);
    }

    private async Task<TaskActivityKind> ResolveActivityAsync(
        string ownerId, TaskRecord record, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var claim = record.ActiveClaim;
        if (claim is null)
            return TaskActivityKind.Idle;
        DeliveryReceipt? receipt = null;
        try
        {
            receipt = await occurrences.GetReceiptAsync(
                ownerId, record.TaskId, claim.ScheduledUtc, ct);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
        }
        return TaskListBuilder.ResolveActivity(record, receipt, nowUtc.UtcDateTime);
    }

    private static bool IsTransient(Exception ex) =>
        ex is TransientStoreException or TimeoutException or HttpRequestException;
}

public sealed class TaskDeleteHandler(
    ITaskStore tasks,
    IUpdateReceiptStore receipts,
    IOrchestrationClient orchestrations,
    BotReplySender replies)
{
    public async Task<ProcessResult> HandleDeleteAsync(
        string ownerId, IncomingUpdate update, string text, DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return await CommandReplies.CompleteWithReplyAsync(receipts, replies,
                ownerId, update, text, nowUtc,
                "Usage: /delete <task-id>.\n\n" + TaskHelp.ListUsage, ct);
        }

        var owned = await tasks.ListOwnedAsync(ownerId, ct);
        var resolution = TaskCommandParser.ResolveTaskRef(owned, parts[1], out var record);
        if (resolution != TaskIdResolution.Found || record is null)
        {
            var reply = resolution == TaskIdResolution.Ambiguous
                ? $"Ambiguous task prefix '{parts[1]}'. Use a longer prefix."
                : TaskHelp.TaskNotFound;
            return await CommandReplies.CompleteWithReplyAsync(
                receipts, replies, ownerId, update, text, nowUtc, reply, ct);
        }

        // A failed conditional write leaves the task active: never
        // acknowledge deletion (or terminate its live orchestration) until
        // the tombstone is confirmed. An already-tombstoned or missing row
        // is idempotent success; anything else is a 503 for redelivery.
        if (!await tasks.TryMarkDeletedAsync(ownerId, record.TaskId, nowUtc.UtcDateTime, ct))
        {
            var current = await tasks.GetAsync(ownerId, record.TaskId, ct);
            if (current is not null && current.Status != TaskState.Deleted)
                return new ProcessResult(503, []);
        }
        var deleted = $"Task {record.TaskId} deleted.";
        // Commit before replying: redelivery acknowledges without a second
        // tombstone or a misleading not-found.
        await CommandReplies.EnsureReceiptAsync(receipts, ownerId, update, text, nowUtc, ct);
        await receipts.MarkCompletedAsync(ownerId, update.UpdateId, record.TaskId, ct);
        try
        {
            await orchestrations.TerminateAsync(record.InstanceId, ct);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            // Tombstone already stops admission; termination retries later.
        }

        await replies.SendReplyAsync(update.ChatId, deleted, ct);
        await receipts.MarkReplyDeliveredAsync(ownerId, update.UpdateId, ct);
        return new ProcessResult(200, [deleted]);
    }

    private static bool IsTransient(Exception ex) =>
        ex is TransientStoreException or TimeoutException or HttpRequestException;
}
