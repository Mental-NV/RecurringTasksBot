// Phase 5 /create: JSON-only. Legacy positional input returns usage help
// and creates nothing. Success is acknowledged only after orchestration
// startup is durably accepted under the stable instance ID.
using NodaTime;

namespace RecurringTasksBot.Application;

public sealed class TaskCreateHandler(
    ITaskStore tasks,
    IUpdateReceiptStore receipts,
    ITaskOrchestrationClient orchestrations,
    BotReplySender replies)
{
    public async Task<ProcessResult> HandleCreateAsync(
        string ownerId, IncomingUpdate update, string text, DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        if (!TaskCommandParser.TrySplitJsonBody(text, out var jsonBody) || jsonBody is null)
        {
            return await CommandReplies.CompleteWithReplyAsync(receipts, replies,
                ownerId, update, text, nowUtc, TaskHelp.CreateUsage, ct);
        }

        var replyFallback = update.ReplyUserId == update.UserId ? update.ReplyPrompt : null;
        if (!TaskDefinitionParser.TryParseCreate(jsonBody, replyFallback,
                out var definition, out var error) || definition is null)
        {
            return await CommandReplies.CompleteWithReplyAsync(receipts, replies,
                ownerId, update, text, nowUtc,
                TaskHelp.FieldError(error!) + "\n\n" + TaskHelp.CreateUsage, ct);
        }

        TaskTimezones.TryResolve(definition.Timezone, out var zone);
        var commitError = TaskCommitValidator.ValidateForCreate(
            definition, zone!, nowUtc.UtcDateTime);
        if (commitError is not null)
        {
            return await CommandReplies.CompleteWithReplyAsync(receipts, replies,
                ownerId, update, text, nowUtc,
                TaskHelp.FieldError(commitError) + "\n\n" + TaskHelp.CreateUsage, ct);
        }

        var taskId = Ids.DeriveOperationId(update.UserId, update.UpdateId, text);
        var instanceId = Ids.DeriveInstanceId(taskId);
        var receipt = await receipts.GetAsync(ownerId, update.UpdateId, ct);
        if (receipt is { CommandCompleted: true })
            return new ProcessResult(200, []);

        if (receipt is null)
        {
            try
            {
                await receipts.InsertAsync(new UpdateReceipt(ownerId, update.UpdateId,
                    UpdateReceipts.BoundCommand(text), taskId, false, false, nowUtc, nowUtc), ct);
            }
            catch (ConcurrencyConflictException)
            {
                return new ProcessResult(200, []);
            }
        }

        var commitUtc = nowUtc.UtcDateTime;
        var record = new TaskRecord(ownerId, taskId, update.ChatId, instanceId,
            definition, 1, TaskState.Active, null, commitUtc, 0,
            TaskCommitValidator.ResolveExpiresAtUtc(definition.Parameters.ExpiresAt, zone!),
            commitUtc, commitUtc);
        try
        {
            await tasks.InsertAsync(record, ct);
        }
        catch (ConcurrencyConflictException)
        {
            record = await tasks.GetAsync(ownerId, taskId, ct) ?? record;
        }

        // Commit before replying: a crash from here on resumes through the
        // completed receipt without creating a second task or lifecycle.
        await receipts.MarkCompletedAsync(ownerId, update.UpdateId, taskId, ct);
        await orchestrations.StartTaskAsync(instanceId, ownerId, taskId, ct);
        var reply = Confirmation(record, zone!);
        await replies.SendReplyAsync(update.ChatId, reply, ct);
        await receipts.MarkReplyDeliveredAsync(ownerId, update.UpdateId, ct);
        return new ProcessResult(200, [reply]);
    }

    // A redelivered create with an undelivered reply resends the
    // confirmation rebuilt from the committed row: nothing re-executes.
    public async Task<ProcessResult> ResendConfirmationAsync(
        string ownerId, UpdateReceipt receipt, IncomingUpdate update, CancellationToken ct)
    {
        if (receipt.OperationId is null)
            return new ProcessResult(200, []);
        var record = await tasks.GetAsync(ownerId, receipt.OperationId, ct);
        if (record is null || record.Status != TaskState.Active)
            return new ProcessResult(200, []);
        // Startup may never have run if the crash landed before it.
        await orchestrations.StartTaskAsync(record.InstanceId, ownerId, record.TaskId, ct);
        TaskTimezones.TryResolve(record.Definition.Timezone, out var zone);
        var reply = Confirmation(record, zone!);
        await replies.SendReplyAsync(update.ChatId, reply, ct);
        await receipts.MarkReplyDeliveredAsync(ownerId, update.UpdateId, ct);
        return new ProcessResult(200, [reply]);
    }

    private static string Confirmation(TaskRecord record, DateTimeZone zone)
    {
        var lines = new List<string>
        {
            $"Task {record.TaskId} created (revision {record.Revision}).",
            "Overrides: " + DescribeOverrides(record.Definition.Parameters),
            TaskConfirmationPreview.LimitsLine(record),
        };
        lines.AddRange(TaskConfirmationPreview.NextOccurrenceLines(
            record.Definition, zone, record.WaterlineUtc));
        return string.Join('\n', lines);
    }

    private static string DescribeOverrides(TaskParameters parameters)
    {
        var parts = new List<string>();
        if (parameters.MemoryMode is not null)
            parts.Add($"memoryMode={parameters.MemoryMode}");
        if (parameters.ReasoningEffort is not null)
            parts.Add($"reasoningEffort={parameters.ReasoningEffort}");
        if (parameters.WebSearch is { } webSearch)
            parts.Add($"webSearch={webSearch.ToString().ToLowerInvariant()}");
        if (parameters.ExpiresAt is not null)
            parts.Add($"expiresAt={parameters.ExpiresAt}");
        if (parameters.MaxOccurrences is { } maxOccurrences)
            parts.Add($"maxOccurrences={maxOccurrences}");
        return parts.Count == 0 ? "none (inherits global defaults)" : string.Join(", ", parts);
    }
}
