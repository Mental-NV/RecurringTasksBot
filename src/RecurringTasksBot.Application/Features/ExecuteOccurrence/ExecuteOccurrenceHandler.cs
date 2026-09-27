using Microsoft.Extensions.Logging;
// Single production entry point for one occurrence attempt: the handler
// owns workflow order, the occurrence repository owns atomic state changes,
// and the transport owns exactly one HTTP request per send. Generation,
// plan delivery, and lease renewal live in the collaborators below. All
// collaborators are required constructor parameters.
namespace RecurringTasksBot.Application;

public sealed class ExecuteOccurrenceHandler(
    IOperationStore operations,
    IOccurrenceRepository occurrences,
    ITelegramTransport transport,
    ILlmExecutor generation,
    ExecutionOptions execution,
    string providerName,
    string modelName,
    TimeProvider? clock = null,
    TimeSpan? claimRenewalInterval = null,
    ILogger? logger = null)
{
    private readonly AttemptSupport support = new(occurrences, clock ?? TimeProvider.System);
    private readonly OccurrenceGenerator generator = new(occurrences, generation, execution,
        new(occurrences, clock ?? TimeProvider.System),
        providerName, modelName, logger);
    private readonly DeliveryPlanSender sender =
        new(occurrences, operations, transport, new(occurrences, clock ?? TimeProvider.System));
    private readonly OccurrenceLeaseRunner leases =
        new(occurrences, claimRenewalInterval, logger);

    // One attempt for orchestration-driven retries: the caller persists long
    // waits as Durable timers, advancing AttemptIndex only for work retries.
    public async Task<SingleAttemptResult> ExecuteAttemptAsync(
        string ownerId, string operationId, DateTime scheduledUtc, int attemptIndex,
        CancellationToken ct = default)
    {
        // Transient storage failures on the attempt boundary are work
        // retries, never task killers: the orchestrator re-invokes the
        // activity after a durable wait.
        DeliveryReceipt? existing;
        try
        {
            existing = await occurrences.GetReceiptAsync(ownerId, operationId, scheduledUtc, ct);
        }
        catch (TransientStoreException ex) { return AttemptResults.StorageRetry(attemptIndex, ex.Message); }
        if (existing is { Status: "sent" })
            return new SingleAttemptResult(SingleAttemptOutcome.SkippedDuplicate,
                existing.Attempts, null, null);

        OperationRecord? op;
        try
        {
            op = await operations.GetAsync(ownerId, operationId, ct);
        }
        catch (TransientStoreException ex) { return AttemptResults.StorageRetry(attemptIndex, ex.Message); }
        if (op is null || op.Status is OperationStatus.Deleted or OperationStatus.Failed)
            return new SingleAttemptResult(SingleAttemptOutcome.SkippedStopped, 0, null, null);

        if (existing is { Status: "failed" })
            return new SingleAttemptResult(SingleAttemptOutcome.OccurrenceFailed, existing.Attempts, null, existing.ErrorSummary);

        var workStart = (clock ?? TimeProvider.System).GetUtcNow();
        var invalid = ValidateExecution();
        if (invalid is not null)
        {
            var pre = await occurrences.GetReceiptAsync(ownerId, operationId, scheduledUtc, ct);
            return new SingleAttemptResult(SingleAttemptOutcome.OccurrenceFailed,
                pre?.Attempts ?? 0, null, invalid);
        }

        var claimId = Guid.NewGuid().ToString("N");
        bool claimed;
        try
        {
            claimed = await occurrences.TryClaimAsync(ownerId, operationId, scheduledUtc, claimId, ct);
        }
        catch (TransientStoreException ex) { return AttemptResults.StorageRetry(attemptIndex, ex.Message); }
        if (!claimed)
            return AttemptResults.ClaimWait();

        // Everything below holds the claim, so post-claim checks and the
        // terminal publication run inside the lease: early returns release
        // promptly, and failure publication still owns its claim.
        return await leases.RunAsync(ownerId, operationId, scheduledUtc, claimId, attemptIndex,
            workToken => AttemptOccurrenceAsync(
                op, existing, claimId, scheduledUtc, attemptIndex, workToken, workStart), ct);
    }

    // Single retry-cap point, inside the lease so terminal failure
    // publication still owns its claim.
    private async Task<SingleAttemptResult> AttemptOccurrenceAsync(
        OperationRecord op, DeliveryReceipt? receipt, string claimId, DateTime scheduledUtc,
        int attemptIndex, CancellationToken ct, DateTimeOffset workStart) =>
        await support.CapAsync(
            await AttemptOccurrenceCoreAsync(
                op, receipt, claimId, scheduledUtc, attemptIndex, ct, workStart),
            op.OwnerId, op.OperationId, scheduledUtc, claimId, attemptIndex, ct);

    private async Task<SingleAttemptResult> AttemptOccurrenceCoreAsync(
        OperationRecord op, DeliveryReceipt? receipt, string claimId, DateTime scheduledUtc,
        int attemptIndex, CancellationToken ct, DateTimeOffset workStart)
    {
        var ownerId = op.OwnerId;
        var operationId = op.OperationId;
        var attempts = receipt?.Attempts ?? 0;
        var executionStarted = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;

        // Unknown payload versions fail before either execution path is
        // selected: data is retained and nothing regenerates.
        DeliveryReceipt? postClaim;
        try
        {
            postClaim = await occurrences.GetReceiptAsync(ownerId, operationId, scheduledUtc, ct);
        }
        catch (TransientStoreException ex) { return AttemptResults.StorageRetry(attemptIndex, ex.Message); }
        if (postClaim?.PayloadSchemaVersion is { } schema &&
            schema != StorageLimits.SchemaVersion)
        {
            try
            {
                await occurrences.MarkUnsupportedVersionAsync(ownerId, operationId, scheduledUtc,
                    claimId, postClaim.Attempts, ct);
            }
            catch (TransientStoreException ex) { return AttemptResults.StorageRetry(attemptIndex, ex.Message); }
            return new SingleAttemptResult(SingleAttemptOutcome.OccurrenceFailed,
                postClaim.Attempts, null, OccurrenceFailureCodes.UnsupportedPayloadVersion);
        }

        var (context, initFailure) = await generator.InitializeAsync(
            op, claimId, scheduledUtc, executionStarted, AdminInstruction(), attemptIndex, ct);
        if (initFailure is not null)
            return initFailure;
        if (context is null)
            return await support.FailAsync(ownerId, operationId, scheduledUtc, claimId,
                OccurrenceFailureCodes.PayloadCorrupt, attempts, 0, ct);

        DeliveryReceipt? current;
        try
        {
            current = await occurrences.GetReceiptAsync(ownerId, operationId, scheduledUtc, ct);
        }
        catch (TransientStoreException ex) { return AttemptResults.StorageRetry(attemptIndex, ex.Message); }
        if (current is null || current.ClaimId != claimId)
            return AttemptResults.ClaimWait();
        attempts = current.Attempts;
        // Partial pointers are corruption, not a reason to regenerate: the
        // atomic publication writes both versions together.
        if (current.AnswerVersion is null != (current.PlanVersion is null))
        {
            return await support.FailAsync(ownerId, operationId, scheduledUtc, claimId,
                OccurrenceFailureCodes.PayloadCorrupt, attempts, 0, ct);
        }
        if (current.AnswerVersion is null || current.PlanVersion is null)
        {
            var generated = await generator.GenerateAsync(op, context, claimId, scheduledUtc,
                executionStarted, attemptIndex, attempts, workStart, ct);
            if (generated is not null)
                return generated;
            try
            {
                current = await occurrences.GetReceiptAsync(ownerId, operationId, scheduledUtc, ct);
            }
            catch (TransientStoreException ex) { return AttemptResults.StorageRetry(attemptIndex, ex.Message); }
            if (current is null || current.ClaimId != claimId)
                return AttemptResults.ClaimWait();
            attempts = current.Attempts;
        }

        return await sender.DeliverPlanAsync(
            op, claimId, scheduledUtc, attemptIndex, attempts, workStart, ct);
    }

    // Configuration is rejected before any claim, send, or publication.
    private string? ValidateExecution()
    {
        var options = execution;
        try
        {
            options.Validate();
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
        if (execution.RequestTimeout > ExecutionLimits.MaxLlmTimeout)
            return $"LLM request timeout of {execution.RequestTimeout.TotalSeconds:F0}s " +
                "exceeds the 540-second activity budget.";
        try
        {
            RecurringTaskSystemPrompt.Render(options.TargetAnswerTextChars,
                TelegramLimits.RichTextChars, AdminInstruction());
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
        return null;
    }

    private string? AdminInstruction() =>
        string.IsNullOrWhiteSpace(execution.SystemInstruction) ? null : execution.SystemInstruction;
}
