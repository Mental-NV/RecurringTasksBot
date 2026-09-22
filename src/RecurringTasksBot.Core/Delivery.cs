// Delivery reliability: error classification, retry plan, and the delivery
// activity flow. Runs in the delivery activity (non-orchestration code),
// with long retry waits persisted through Durable timers by the caller.
namespace RecurringTasksBot.Core;

public enum SendFailureKind
{
    Transient,
    Permanent,
}

public sealed record SendFailure(SendFailureKind Kind, string Summary, TimeSpan? RetryAfter = null);

public sealed class TelegramSendException(
    int? httpStatusCode,
    string description,
    TimeSpan? retryAfter = null) : Exception(description)
{
    public int? HttpStatusCode { get; } = httpStatusCode;
    public string Description { get; } = description;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public static class DeliveryPolicy
{
    public const int MaxRetriesAfterInitial = 3;

    public static SendFailure Classify(Exception exception)
    {
        if (exception is TelegramSendException tg)
            return ClassifyTelegram(tg.HttpStatusCode, tg.Description, tg.RetryAfter);
        return new SendFailure(SendFailureKind.Transient, $"send failed: {exception.Message}");
    }

    // Permanent: recipient-side failures (blocked bot, unknown chat).
    // Everything else (network, 5xx, 429) is transient with backoff.
    public static SendFailure ClassifyTelegram(int? httpStatus, string description, TimeSpan? retryAfter = null)
    {
        var desc = description ?? string.Empty;
        if (httpStatus == 403 ||
            desc.Contains("blocked", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("chat not found", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("user not found", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("deactivated", StringComparison.OrdinalIgnoreCase))
            return new SendFailure(SendFailureKind.Permanent, $"permanent recipient failure: {desc}");

        if (httpStatus == 429)
            return new SendFailure(SendFailureKind.Transient,
                $"rate limited: {desc}", retryAfter ?? TimeSpan.FromSeconds(30));

        return new SendFailure(SendFailureKind.Transient, $"transient send failure: {desc}");
    }

    // Increasing delays between attempts; honours Telegram retry_after when
    // it exceeds the default backoff. retryIndex 0 = wait before 1st retry.
    public static TimeSpan RetryDelay(int retryIndex, TimeSpan? retryAfter = null)
    {
        var backoff = retryIndex switch
        {
            0 => TimeSpan.FromSeconds(5),
            1 => TimeSpan.FromSeconds(30),
            _ => TimeSpan.FromMinutes(5),
        };
        if (retryAfter.HasValue && retryAfter.Value > backoff)
            return retryAfter.Value;
        return backoff;
    }
}

public enum DeliveryOutcome
{
    Sent,
    SkippedDeletedOrFailed,
    SkippedDuplicate,
    FailedOccurrenceKeptActive,
    OperationFailed,
}

public enum SingleAttemptOutcome
{
    Sent,
    SkippedStopped,
    SkippedDuplicate,
    NeedRetry,
    OccurrenceFailed,
    OperationFailed,
}

public sealed record SingleAttemptResult(
    SingleAttemptOutcome Outcome,
    int Attempts,
    TimeSpan? RetryIn,
    string? ErrorSummary);

public sealed record DeliveryResult(
    DeliveryOutcome Outcome,
    int Attempts,
    string? ErrorSummary);

public sealed class DeliveryHandler(
    IOperationStore operations,
    IDeliveryReceiptStore deliveries,
    ITelegramSender sender)
{
    // One send attempt for orchestration-driven retries: the caller persists
    // long waits as Durable timers and re-invokes with AttemptIndex + 1.
    // AttemptIndex 0 is the initial attempt; retries are exhausted after
    // DeliveryPolicy.MaxRetriesAfterInitial further attempts.
    public async Task<SingleAttemptResult> AttemptOnceAsync(
        string ownerId, string operationId, DateTime scheduledUtc, int attemptIndex,
        CancellationToken ct = default)
    {
        var existing = await deliveries.GetAsync(ownerId, operationId, scheduledUtc, ct);
        if (existing is { Status: "sent" })
            return new SingleAttemptResult(SingleAttemptOutcome.SkippedDuplicate,
                existing.Attempts, null, null);

        var op = await operations.GetAsync(ownerId, operationId, ct);
        if (op is null || op.Status is OperationStatus.Deleted or OperationStatus.Failed)
            return new SingleAttemptResult(SingleAttemptOutcome.SkippedStopped, 0, null, null);

        var attempts = attemptIndex + 1;
        try
        {
            var messageId = await sender.SendTextAsync(op.ChatId, $"Hi!\n{op.Text}", ct);
            await deliveries.UpsertAsync(new DeliveryReceipt(ownerId, operationId,
                scheduledUtc, "sent", attempts, null, messageId), ct);
            return new SingleAttemptResult(SingleAttemptOutcome.Sent, attempts, null, null);
        }
        catch (Exception ex)
        {
            var failure = DeliveryPolicy.Classify(ex);
            if (failure.Kind == SendFailureKind.Permanent)
            {
                await operations.CompareAndSwapStatusAsync(ownerId, operationId,
                    op.Status, OperationStatus.Failed, failure.Summary, ct);
                await deliveries.UpsertAsync(new DeliveryReceipt(ownerId, operationId,
                    scheduledUtc, "failed", attempts, failure.Summary, null), ct);
                return new SingleAttemptResult(SingleAttemptOutcome.OperationFailed,
                    attempts, null, failure.Summary);
            }

            if (attemptIndex >= DeliveryPolicy.MaxRetriesAfterInitial)
            {
                await deliveries.UpsertAsync(new DeliveryReceipt(ownerId, operationId,
                    scheduledUtc, "failed", attempts, failure.Summary, null), ct);
                return new SingleAttemptResult(SingleAttemptOutcome.OccurrenceFailed,
                    attempts, null, failure.Summary);
            }

            return new SingleAttemptResult(SingleAttemptOutcome.NeedRetry,
                attempts, DeliveryPolicy.RetryDelay(attemptIndex, failure.RetryAfter),
                failure.Summary);
        }
    }

    // Before sending, re-reads the operation by owner+ID and stops recurrence
    // if it is deleted or failed. An already-started send may finish: the
    // status check happens once, just before the first attempt.
    // (operationId, scheduledUtc) suppresses repeated deliveries.
    public async Task<DeliveryResult> DeliverAsync(
        string ownerId, string operationId, DateTime scheduledUtc,
        Func<TimeSpan, Task>? waitAsync = null,
        CancellationToken ct = default)
    {
        var existing = await deliveries.GetAsync(ownerId, operationId, scheduledUtc, ct);
        if (existing is { Status: "sent" })
            return new DeliveryResult(DeliveryOutcome.SkippedDuplicate, existing.Attempts, null);

        var op = await operations.GetAsync(ownerId, operationId, ct);
        if (op is null || op.Status is OperationStatus.Deleted or OperationStatus.Failed)
            return new DeliveryResult(DeliveryOutcome.SkippedDeletedOrFailed, 0, null);

        var text = $"Hi!\n{op.Text}";
        var attempts = 0;
        Exception? lastError = null;
        TimeSpan? lastRetryAfter = null;

        for (var attempt = 0; attempt <= DeliveryPolicy.MaxRetriesAfterInitial; attempt++)
        {
            attempts = attempt + 1;
            try
            {
                var messageId = await sender.SendTextAsync(op.ChatId, text, ct);
                await deliveries.UpsertAsync(new DeliveryReceipt(ownerId, operationId,
                    scheduledUtc, "sent", attempts, null, messageId), ct);
                return new DeliveryResult(DeliveryOutcome.Sent, attempts, null);
            }
            catch (Exception ex)
            {
                lastError = ex;
                var failure = DeliveryPolicy.Classify(ex);
                lastRetryAfter = failure.RetryAfter;

                if (failure.Kind == SendFailureKind.Permanent)
                {
                    await operations.CompareAndSwapStatusAsync(ownerId, operationId,
                        op.Status, OperationStatus.Failed, failure.Summary, ct);
                    await deliveries.UpsertAsync(new DeliveryReceipt(ownerId, operationId,
                        scheduledUtc, "failed", attempts, failure.Summary, null), ct);
                    return new DeliveryResult(DeliveryOutcome.OperationFailed, attempts, failure.Summary);
                }

                if (attempt < DeliveryPolicy.MaxRetriesAfterInitial && waitAsync is not null)
                    await waitAsync(DeliveryPolicy.RetryDelay(attempt, failure.RetryAfter));
                else if (attempt < DeliveryPolicy.MaxRetriesAfterInitial && waitAsync is null)
                {
                    // No durable timer available (unit-test fast path): continue immediately.
                }
            }
        }

        // Transient retries exhausted: record the failed occurrence, keep the
        // operation active, continue future occurrences.
        var summary = DeliveryPolicy.Classify(lastError!).Summary;
        await deliveries.UpsertAsync(new DeliveryReceipt(ownerId, operationId,
            scheduledUtc, "failed", attempts, summary, null), ct);
        return new DeliveryResult(DeliveryOutcome.FailedOccurrenceKeptActive, attempts, summary);
    }
}
