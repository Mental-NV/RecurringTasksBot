// Phase 5 occurrence execution bridge: a task's frozen claim runs through
// the production occurrence pipeline (frozen context, generation, plan
// delivery, leases, memory) with the task ID as the occurrence key, so all
// receipts and memory live under the task. Status reads are always fresh so
// deletion stops delivery; prompt and settings come from the frozen claim,
// never a newer definition.
using Microsoft.Extensions.Logging;

namespace RecurringTasksBot.Application;

public sealed class TaskOperationSource(
    ITaskStore tasks,
    string ownerId,
    string taskId,
    long chatId,
    OccurrenceClaim claim) : IOperationStore
{
    public async Task<OperationRecord?> GetAsync(string owner, string operationId,
        CancellationToken ct = default)
    {
        if (!owner.Equals(ownerId, StringComparison.Ordinal) ||
            !operationId.Equals(taskId, StringComparison.Ordinal))
            return null;
        var record = await tasks.GetAsync(ownerId, taskId, ct);
        if (record is null)
            return null;
        return TaskOperationSynthesis.ToOperationRecord(
            ownerId, taskId, chatId, record.InstanceId, record, claim.ScheduledUtc);
    }

    public Task InsertStartingAsync(OperationRecord record, CancellationToken ct = default) =>
        throw new NotSupportedException("Tasks are created through the task store.");

    public async Task<bool> CompareAndSwapStatusAsync(string owner, string operationId,
        OperationStatus expected, OperationStatus next, string? failureSummary = null,
        CancellationToken ct = default)
    {
        if (!owner.Equals(ownerId, StringComparison.Ordinal) ||
            !operationId.Equals(taskId, StringComparison.Ordinal))
            return false;
        var record = await tasks.GetAsync(ownerId, taskId, ct);
        if (record is null || TaskOperationSynthesis.MapStatus(record.Status) != expected)
            return false;
        if (next == expected)
            return true;
        // The pipeline only ever fails operations; a failed task stays
        // inspectable with its receipts and memory intact.
        if (next == OperationStatus.Failed)
            return await tasks.TryMarkTaskFailedAsync(ownerId, taskId, DateTime.UtcNow, ct);
        return false;
    }

    public Task<IReadOnlyList<OperationRecord>> ListOwnedAsync(string owner,
        CancellationToken ct = default) =>
        throw new NotSupportedException("Tasks list through the task store.");

}

public interface ITaskOccurrenceRunner
{
    Task<SingleAttemptResult> RunAsync(string ownerId, string taskId, OccurrenceClaim claim,
        int attemptIndex, CancellationToken ct = default);
}

public sealed class TaskOccurrenceRunner(
    ITaskStore tasks,
    IOccurrenceRepository occurrences,
    ITelegramTransport transport,
    ILlmExecutor generation,
    ExecutionOptions execution,
    string providerName,
    string modelName,
    TimeProvider? clock = null,
    TimeSpan? claimRenewalInterval = null,
    ILogger<TaskOccurrenceRunner>? logger = null) : ITaskOccurrenceRunner
{
    public async Task<SingleAttemptResult> RunAsync(string ownerId, string taskId,
        OccurrenceClaim claim, int attemptIndex, CancellationToken ct = default)
    {
        var record = await tasks.GetAsync(ownerId, taskId, ct);
        if (record is null || record.Status != TaskState.Active)
            return new SingleAttemptResult(SingleAttemptOutcome.SkippedStopped, 0, null, null);
        if (record.ActiveClaim?.ScheduledUtc != claim.ScheduledUtc)
            return AttemptResults.ClaimWait();
        var source = new TaskOperationSource(tasks, ownerId, taskId, record.ChatId, claim);
        var handler = new ExecuteOccurrenceHandler(source, occurrences, transport, generation,
            execution, providerName, modelName, clock, claimRenewalInterval, logger);
        var overrides = new OccurrenceGenerationOverrides(
            claim.MemoryMode, claim.ReasoningEffort, claim.WebSearch);
        var result = await handler.ExecuteAttemptAsync(
            ownerId, taskId, claim.ScheduledUtc, attemptIndex, ct, overrides);
        await NoteRetryAsync(ownerId, taskId, claim, attemptIndex, result, ct);
        return result;
    }

    // Best-effort durable retry visibility for /list. A failed attempt with
    // a scheduled retry notes when to look again; any other outcome after
    // a retry started clears the note. The store skips identical notes, so
    // steady states cost no writes. Visibility must never fail delivery.
    private async Task NoteRetryAsync(string ownerId, string taskId, OccurrenceClaim claim,
        int attemptIndex, SingleAttemptResult result, CancellationToken ct)
    {
        try
        {
            var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
            if (result.Outcome == SingleAttemptOutcome.NeedRetry && result.RetryIn.HasValue)
            {
                await tasks.TryUpdateClaimRetryAsync(ownerId, taskId, claim.ScheduledUtc,
                    result.Attempts, now + result.RetryIn.Value, now, ct);
            }
            else if (attemptIndex > 0)
            {
                await tasks.TryUpdateClaimRetryAsync(ownerId, taskId, claim.ScheduledUtc,
                    0, null, now, ct);
            }
        }
        catch (TransientStoreException)
        {
        }
    }
}
