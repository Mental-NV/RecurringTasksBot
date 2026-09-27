using Microsoft.Extensions.Logging;
// Shared attempt plumbing for the ExecuteOccurrence feature slice: outcome
// factories, failure publication, and the combined retry cap. The
// orchestrator bounds the combined loop; every NeedRetry consumes the cap
// (2 generation + 3 delivery), while WaitingForClaim never does.
namespace RecurringTasksBot.Application;

internal static class AttemptResults
{
    public static SingleAttemptResult ClaimWait() =>
        new(SingleAttemptOutcome.WaitingForClaim, 0, OccurrenceExecution.ClaimRecheckDelay, null);

    public static SingleAttemptResult Stopped() =>
        new(SingleAttemptOutcome.SkippedStopped, 0, null, null);

    public static SingleAttemptResult YieldRetry() =>
        new(SingleAttemptOutcome.NeedRetry, 0, TimeSpan.FromSeconds(1), null);

    public static SingleAttemptResult StorageRetry(int attemptIndex, string? summary) =>
        new(SingleAttemptOutcome.NeedRetry, 0, StorageDelay(attemptIndex), summary);

    private static TimeSpan StorageDelay(int attemptIndex) => attemptIndex switch
    {
        0 => TimeSpan.FromSeconds(5),
        1 => TimeSpan.FromSeconds(30),
        _ => TimeSpan.FromMinutes(5),
    };
}

// Failure publication shared by the handler, generator, and plan sender:
// persist the terminal failure and report it without sending more.
internal sealed class AttemptSupport(
    IOccurrenceRepository occurrences,
    TimeProvider clock)
{
    public bool FitsTime(DateTimeOffset workStart, TimeSpan needed) =>
        clock.GetUtcNow() - workStart + needed <= ExecutionLimits.ActivityWorkBudget;

    public async Task<SingleAttemptResult> FailAsync(
        string ownerId, string operationId, DateTime scheduledUtc, string claimId,
        string summary, int attempts, int httpAttempts, CancellationToken ct)
    {
        try
        {
            await occurrences.FailAsync(new FailRequest(ownerId, operationId, scheduledUtc,
                claimId, summary, httpAttempts), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        return new SingleAttemptResult(SingleAttemptOutcome.OccurrenceFailed, attempts, null, summary);
    }

    public async Task<SingleAttemptResult> CapAsync(
        SingleAttemptResult result, string ownerId, string operationId,
        DateTime scheduledUtc, string claimId, int attemptIndex, CancellationToken ct)
    {
        if (result.Outcome != SingleAttemptOutcome.NeedRetry ||
            attemptIndex < OccurrenceExecution.MaxCombinedRetriesAfterInitial)
            return result;
        return await FailAsync(ownerId, operationId, scheduledUtc, claimId,
            result.ErrorSummary ?? "retry budget exhausted", result.Attempts, 0, ct);
    }
}

// Literal-reply canary: emitted at reply build time (not delivery time)
// so the dashboard can answer whether delivered text was LLM-authored
// or a literal fallback, with the persisted and receipt model versions.
// Never logs prompts, answers, or payload contents.
internal static class ReplyCanary
{
    public static void Log(ILogger? logger, string ownerId, string operationId, DateTime scheduledUtc,
        string replySource, string provider, string model, int? receiptSchemaVersion) =>
        logger?.LogInformation(
            "Reply built for {OwnerId}/{OperationId} at {ScheduledUtc:u}: " +
            "replySource={ReplySource} provider={Provider} model={Model} receiptSchema={ReceiptSchema}.",
            ownerId, operationId, scheduledUtc, replySource, provider, model,
            receiptSchemaVersion?.ToString() ?? "legacy");
}
