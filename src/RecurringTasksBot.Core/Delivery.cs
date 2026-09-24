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
    WaitingForClaim,
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

// Phase 2 occurrence execution: check status, generate an answer with the
// LLM, persist the deliverable, then send it. Generation retries (at most 2
// after the initial attempt) stay separate from Telegram retries (at most 3
// after the initial attempt); the orchestrator bounds the combined loop.
// A persisted result is always written before any Telegram send, so
// delivery retries reuse it and resume at the first unconfirmed part.
// Deletion is rechecked before generation, each generation retry, and each
// message part; an in-flight LLM result is discarded when deleted, while an
// already-started Telegram send may finish.

public static class OccurrenceExecution
{
    public const string StatusGenerating = "generating";

    // A worker renews its lease while executing, and releases it before a
    // durable retry wait. Another worker may recover only an expired lease.
    public static readonly TimeSpan StaleClaimAfter = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ClaimRenewalInterval = TimeSpan.FromMinutes(1);

    // Wait before rechecking an unfinished claim owned by someone else.
    public static readonly TimeSpan ClaimRecheckDelay = TimeSpan.FromSeconds(30);

    // Combined orchestrator bound: 2 generation + 3 delivery retries.
    public const int MaxCombinedRetriesAfterInitial =
        GenerationPolicy.MaxRetriesAfterInitial + DeliveryPolicy.MaxRetriesAfterInitial;

    public static bool IsClaimStale(DeliveryReceipt receipt, DateTimeOffset nowUtc) =>
        nowUtc - receipt.UpdatedUtc > StaleClaimAfter;

    public static bool IsTerminal(DeliveryReceipt receipt) => receipt.Status is "sent" or "failed";

    public static bool ShouldRetry(SingleAttemptResult result, int attemptIndex) =>
        result.Outcome == SingleAttemptOutcome.WaitingForClaim ||
        result.Outcome == SingleAttemptOutcome.NeedRetry && attemptIndex < MaxCombinedRetriesAfterInitial;

    public static int NextAttemptIndex(SingleAttemptResult result, int attemptIndex) =>
        result.Outcome == SingleAttemptOutcome.WaitingForClaim ? attemptIndex : attemptIndex + 1;

    public static string MessageIdsToString(IReadOnlyList<long> ids) =>
        string.Join(',', ids);

    public static IReadOnlyList<long> ParseMessageIds(string? stored) =>
        string.IsNullOrWhiteSpace(stored)
            ? []
            : stored.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => long.TryParse(s, out var id) ? id : 0L)
                .Where(id => id > 0)
                .ToList();
}

public sealed class DeliveryHandler(
    IOperationStore operations,
    IDeliveryReceiptStore deliveries,
    IOccurrencePayloadStore payloads,
    ITelegramSender sender,
    ILlmPromptExecutor llm,
    LlmOptions llmOptions,
    TimeSpan? claimRenewalInterval = null)
{
    // One attempt for orchestration-driven retries: the caller persists long
    // waits as Durable timers, advancing AttemptIndex only for work retries.
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

        if (existing is { Status: "failed" })
            return new SingleAttemptResult(SingleAttemptOutcome.OccurrenceFailed, existing.Attempts, null, existing.ErrorSummary);

        var claimId = Guid.NewGuid().ToString("N");
        if (!await deliveries.TryClaimAsync(ownerId, operationId, scheduledUtc, claimId, ct))
            return ClaimWait();

        using var work = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stopHeartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = RenewWhileRunningAsync(ownerId, operationId, scheduledUtc, claimId, work, stopHeartbeat.Token);
        try
        {
            var receipt = (await deliveries.GetAsync(ownerId, operationId, scheduledUtc, work.Token))!;
            if (receipt.ClaimId != claimId) throw new ClaimLostException();
            if (!HasPersistedPayload(receipt))
            {
                var generated = await GenerateOnceAsync(op, receipt, work.Token);
                if (generated is not null)
                    return await CapAttemptAsync(generated, receipt, attemptIndex, work.Token);
                receipt = (await deliveries.GetAsync(ownerId, operationId, scheduledUtc, work.Token))!;
                if (receipt.ClaimId != claimId) throw new ClaimLostException();
            }

            return await CapAttemptAsync(await DeliverPersistedAsync(op, receipt, work.Token),
                receipt, attemptIndex, work.Token);
        }
        catch (ClaimLostException) { return ClaimWait(); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return ClaimWait(); }
        finally
        {
            await stopHeartbeat.CancelAsync();
            await heartbeat;
            await deliveries.ReleaseClaimAsync(ownerId, operationId, scheduledUtc, claimId, CancellationToken.None);
        }
    }

    private static SingleAttemptResult ClaimWait() =>
        new(SingleAttemptOutcome.WaitingForClaim, 0, OccurrenceExecution.ClaimRecheckDelay, null);

    private async Task RenewWhileRunningAsync(string ownerId, string operationId, DateTime scheduledUtc,
        string claimId, CancellationTokenSource work, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(claimRenewalInterval ?? OccurrenceExecution.ClaimRenewalInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
                if (!await deliveries.RenewClaimAsync(ownerId, operationId, scheduledUtc, claimId, stop))
                {
                    await work.CancelAsync();
                    return;
                }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception)
        {
            // Stop external work if renewal cannot be confirmed. A future
            // invocation can resume persisted progress after lease recovery.
            await work.CancelAsync();
        }
    }

    private async Task EnsureOwnershipAsync(DeliveryReceipt receipt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (receipt.ClaimId is null || !await deliveries.RenewClaimAsync(receipt.OwnerId,
            receipt.OperationId, receipt.ScheduledUtc.UtcDateTime, receipt.ClaimId, ct))
            throw new ClaimLostException();
    }

    // Before sending, re-reads the operation by owner+ID and stops recurrence
    // if it is deleted or failed. (operationId, scheduledUtc) suppresses
    // repeated deliveries.
    public async Task<DeliveryResult> DeliverAsync(
        string ownerId, string operationId, DateTime scheduledUtc,
        Func<TimeSpan, Task>? waitAsync = null,
        CancellationToken ct = default)
    {
        SingleAttemptResult attempt =
            new(SingleAttemptOutcome.NeedRetry, 0, TimeSpan.Zero, null);
        for (var index = 0; ; index = OccurrenceExecution.NextAttemptIndex(attempt, index))
        {
            attempt = await AttemptOnceAsync(ownerId, operationId, scheduledUtc, index, ct);
            if (!OccurrenceExecution.ShouldRetry(attempt, index))
                break;
            if (attempt.Outcome == SingleAttemptOutcome.WaitingForClaim && waitAsync is null)
                throw new InvalidOperationException("A durable wait is required while another worker owns the occurrence.");
            if (waitAsync is not null && attempt.RetryIn.HasValue)
                await waitAsync(attempt.RetryIn.Value);
        }

        return attempt.Outcome switch
        {
            SingleAttemptOutcome.Sent =>
                new DeliveryResult(DeliveryOutcome.Sent, attempt.Attempts, null),
            SingleAttemptOutcome.SkippedDuplicate =>
                new DeliveryResult(DeliveryOutcome.SkippedDuplicate, attempt.Attempts, null),
            SingleAttemptOutcome.SkippedStopped =>
                new DeliveryResult(DeliveryOutcome.SkippedDeletedOrFailed, 0, null),
            SingleAttemptOutcome.OperationFailed =>
                new DeliveryResult(DeliveryOutcome.OperationFailed, attempt.Attempts, attempt.ErrorSummary),
            _ => new DeliveryResult(
                DeliveryOutcome.FailedOccurrenceKeptActive, attempt.Attempts, attempt.ErrorSummary),
        };
    }

    // Returns null when the payload is now persisted and delivery follows.
    private async Task<SingleAttemptResult?> GenerateOnceAsync(
        OperationRecord op, DeliveryReceipt receipt, CancellationToken ct)
    {
        // Recheck deletion before generation.
        var fresh = await operations.GetAsync(op.OwnerId, op.OperationId, ct);
        if (fresh is null || fresh.Status is OperationStatus.Deleted or OperationStatus.Failed)
            return new SingleAttemptResult(SingleAttemptOutcome.SkippedStopped, 0, null, null);

        var scheduledUtc = receipt.ScheduledUtc.UtcDateTime;
        var executionUtc = DateTime.UtcNow;
        LlmResult result;
        try
        {
            result = await llm.ExecuteAsync(
                new LlmPrompt(fresh.Text, scheduledUtc, executionUtc), ct);
        }
        catch (LlmExecutionException ex)
        {
            return await HandleGenerationFailureAsync(fresh, receipt, ex.Kind,
                GenerationPolicy.Sanitize(ex.Summary), ex.RetryAfter, ct);
        }

        if (string.IsNullOrWhiteSpace(result.AnswerText))
        {
            return await HandleGenerationFailureAsync(fresh, receipt, LlmFailureKind.EmptyResponse,
                "empty LLM response", null, ct);
        }

        await EnsureOwnershipAsync(receipt, ct);
        var afterGeneration = await operations.GetAsync(fresh.OwnerId, fresh.OperationId, ct);
        if (afterGeneration is null || afterGeneration.Status is OperationStatus.Deleted or OperationStatus.Failed)
            return new SingleAttemptResult(SingleAttemptOutcome.SkippedStopped, 0, null, null);

        var truncated = TruncateAnswer(result.AnswerText);
        var parts = AnswerComposer.Compose(fresh.OperationId,
            scheduledUtc, executionUtc, truncated, result.Sources);
        await payloads.PersistAsync(fresh.OwnerId, fresh.OperationId, scheduledUtc, parts, ct, version: receipt.ClaimId);
        await deliveries.UpsertAsync(receipt with
        {
            Status = OccurrenceExecution.StatusGenerating,
            GenerationAttempts = receipt.GenerationAttempts + 1,
            ExecutionStatus = "generated",
            Provider = result.Provider,
            ModelName = result.Model,
            PromptTokens = result.Usage.PromptTokens,
            CompletionTokens = result.Usage.CompletionTokens,
            SearchResults = result.Usage.SearchResults,
            SearchUsed = result.Usage.SearchUsed,
            SentParts = 0,
            TotalParts = parts.Count,
            PayloadVersion = receipt.ClaimId,
            MessageIds = string.Empty,
            ErrorSummary = null,
            UpdatedUtc = DateTimeOffset.UtcNow,
        }, ct);

        // An in-flight LLM request may finish after deletion: discard it.
        var after = await operations.GetAsync(fresh.OwnerId, fresh.OperationId, ct);
        if (after is null || after.Status is OperationStatus.Deleted or OperationStatus.Failed)
            return new SingleAttemptResult(SingleAttemptOutcome.SkippedStopped, 0, null, null);

        return null;
    }

    // Returns null when the failure notice is persisted and the caller
    // should proceed to deliver it through the Telegram retry path.
    private async Task<SingleAttemptResult?> HandleGenerationFailureAsync(
        OperationRecord op, DeliveryReceipt receipt, LlmFailureKind kind,
        string sanitizedSummary, TimeSpan? retryAfter, CancellationToken ct)
    {
        await EnsureOwnershipAsync(receipt, ct);
        var attempts = receipt.GenerationAttempts + 1;
        if (kind != LlmFailureKind.Permanent && attempts <= llmOptions.GenerationRetries)
        {
            await deliveries.UpsertAsync(receipt with
            {
                Status = OccurrenceExecution.StatusGenerating,
                GenerationAttempts = attempts,
                ExecutionStatus = "generating",
                ErrorSummary = sanitizedSummary,
                UpdatedUtc = DateTimeOffset.UtcNow,
            }, ct);
            var retryIndex = attempts - 1;
            return new SingleAttemptResult(SingleAttemptOutcome.NeedRetry, attempts,
                GenerationPolicy.RetryDelay(retryIndex, retryAfter), sanitizedSummary);
        }

        // Retries exhausted (or permanent): persist the failure notice and
        // deliver it through the normal Telegram retry path. The notice
        // never exposes provider errors or credentials.
        var notice = AnswerComposer.FailureNotice(op.OperationId, receipt.ScheduledUtc.UtcDateTime);
        var parts = AnswerComposer.Compose(op.OperationId,
            receipt.ScheduledUtc.UtcDateTime, DateTime.UtcNow, notice, []);
        await payloads.PersistAsync(op.OwnerId, op.OperationId,
            receipt.ScheduledUtc.UtcDateTime, parts, ct, version: receipt.ClaimId);
        await deliveries.UpsertAsync(receipt with
        {
            Status = OccurrenceExecution.StatusGenerating,
            GenerationAttempts = attempts,
            ExecutionStatus = "failed",
            FailureNotice = notice,
            Provider = llmOptions.Provider,
            ModelName = llmOptions.Model,
            SentParts = 0,
            TotalParts = parts.Count,
            PayloadVersion = receipt.ClaimId,
            MessageIds = string.Empty,
            ErrorSummary = sanitizedSummary,
            UpdatedUtc = DateTimeOffset.UtcNow,
        }, ct);
        return null;
    }

    private async Task<SingleAttemptResult> DeliverPersistedAsync(
        OperationRecord op, DeliveryReceipt receipt, CancellationToken ct)
    {
        // Re-read the generated answer or failure notice under the same lease.
        var current = await deliveries.GetAsync(op.OwnerId, op.OperationId,
            receipt.ScheduledUtc.UtcDateTime, ct) ?? receipt;
        if (current.ClaimId != receipt.ClaimId) throw new ClaimLostException();
        if (!HasPersistedPayload(current))
        {
            // Generation path returned a terminal/retry result; re-derive it
            // from counters to stay consistent (defensive: unreachable).
            return new SingleAttemptResult(SingleAttemptOutcome.NeedRetry,
                current.Attempts, TimeSpan.FromSeconds(10), current.ErrorSummary);
        }

        var parts = await payloads.LoadAsync(op.OwnerId, op.OperationId,
            current.ScheduledUtc.UtcDateTime, ct, version: current.PayloadVersion);
        if (parts is null || parts.Count == 0)
        {
            await deliveries.UpsertAsync(current with
            {
                Status = "failed",
                ErrorSummary = "persisted payload missing",
                UpdatedUtc = DateTimeOffset.UtcNow,
            }, ct);
            return new SingleAttemptResult(SingleAttemptOutcome.OccurrenceFailed,
                current.Attempts, null, "persisted payload missing");
        }

        var sentIds = OccurrenceExecution.ParseMessageIds(current.MessageIds).ToList();
        var attempts = current.Attempts;
        for (var i = current.SentParts; i < parts.Count; i++)
        {
            // Recheck deletion before each message part.
            var live = await operations.GetAsync(op.OwnerId, op.OperationId, ct);
            if (live is null || live.Status is OperationStatus.Deleted or OperationStatus.Failed)
                return new SingleAttemptResult(SingleAttemptOutcome.SkippedStopped, attempts, null, null);

            await EnsureOwnershipAsync(current, ct);
            attempts++;
            try
            {
                var messageId = await sender.SendRichTextAsync(live.ChatId, parts[i], ct);
                sentIds.Add(messageId);
                current = current with
                {
                    Status = OccurrenceExecution.StatusGenerating,
                    Attempts = attempts,
                    SentParts = i + 1,
                    MessageIds = OccurrenceExecution.MessageIdsToString(sentIds),
                    TelegramMessageId = messageId,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                };
                await deliveries.UpsertAsync(current, ct);
            }
            catch (Exception ex) when (ex is not ClaimLostException and not OperationCanceledException)
            {
                var failure = DeliveryPolicy.Classify(ex);
                if (failure.Kind == SendFailureKind.Permanent)
                {
                    await operations.CompareAndSwapStatusAsync(op.OwnerId, op.OperationId,
                        live.Status, OperationStatus.Failed, failure.Summary, ct);
                    await deliveries.UpsertAsync(current with
                    {
                        Status = "failed",
                        Attempts = attempts,
                        SentParts = i,
                        MessageIds = OccurrenceExecution.MessageIdsToString(sentIds),
                        ErrorSummary = failure.Summary,
                        UpdatedUtc = DateTimeOffset.UtcNow,
                    }, ct);
                    return new SingleAttemptResult(SingleAttemptOutcome.OperationFailed,
                        attempts, null, failure.Summary);
                }

                await deliveries.UpsertAsync(current with
                {
                    Status = OccurrenceExecution.StatusGenerating,
                    Attempts = attempts,
                    SentParts = i,
                    MessageIds = OccurrenceExecution.MessageIdsToString(sentIds),
                    ErrorSummary = failure.Summary,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                }, ct);
                return new SingleAttemptResult(SingleAttemptOutcome.NeedRetry,
                    attempts, DeliveryPolicy.RetryDelay(attempts - 1, failure.RetryAfter),
                    failure.Summary);
            }
        }

        await deliveries.UpsertAsync(current with
        {
            Status = "sent",
            UpdatedUtc = DateTimeOffset.UtcNow,
        }, ct);
        return new SingleAttemptResult(SingleAttemptOutcome.Sent, attempts, null, null);
    }

    private async Task<SingleAttemptResult> CapAttemptAsync(SingleAttemptResult result,
        DeliveryReceipt owned, int attemptIndex, CancellationToken ct)
    {
        if (result.Outcome != SingleAttemptOutcome.NeedRetry ||
            attemptIndex < OccurrenceExecution.MaxCombinedRetriesAfterInitial)
            return result;

        var receipt = await deliveries.GetAsync(owned.OwnerId, owned.OperationId,
            owned.ScheduledUtc.UtcDateTime, ct);
        if (receipt is null || receipt.ClaimId != owned.ClaimId) throw new ClaimLostException();
        await deliveries.UpsertAsync(receipt with
        {
            Status = "failed",
            ErrorSummary = result.ErrorSummary ?? receipt.ErrorSummary,
            UpdatedUtc = DateTimeOffset.UtcNow,
        }, ct);
        return result with { Outcome = SingleAttemptOutcome.OccurrenceFailed };
    }

    private static bool HasPersistedPayload(DeliveryReceipt receipt) =>
        receipt.TotalParts > 0 &&
        (receipt.ExecutionStatus == "generated" || receipt.ExecutionStatus == "failed");

    private string TruncateAnswer(string answer)
    {
        if (TextLimits.CountChars(answer) <= llmOptions.MaxStoredAnswerChars)
            return answer;
        var runes = answer.EnumerateRunes().ToArray();
        return string.Concat(runes[..llmOptions.MaxStoredAnswerChars].Select(r => r.ToString())) +
            "\n\n…[answer truncated to the 32,768-character limit]";
    }
}
