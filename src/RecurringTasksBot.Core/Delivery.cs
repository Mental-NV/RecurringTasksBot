// Delivery reliability: error classification, retry plan, and the delivery
// activity flow. Runs in the delivery activity (non-orchestration code),
// with long retry waits persisted through Durable timers by the caller.
using Microsoft.Extensions.Logging;

namespace RecurringTasksBot.Core;

public enum SendFailureKind
{
    Transient,
    Permanent,
}

public sealed record SendFailure(SendFailureKind Kind, string Summary, TimeSpan? RetryAfter = null);

public class TelegramSendException(
    int? httpStatusCode,
    string description,
    TimeSpan? retryAfter = null,
    int? apiErrorCode = null) : Exception(description)
{
    public int? HttpStatusCode { get; } = httpStatusCode;
    public int? ApiErrorCode { get; } = apiErrorCode;
    public string Description { get; } = description;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

// The rich endpoint itself is unavailable: the delivery service must switch
// to regular literal messages, never retry the rich format. Derives from
// TelegramSendException so legacy catch sites keep working.
public sealed class TelegramUnknownMethodException(
    int? httpStatusCode,
    int? apiErrorCode,
    string description) : TelegramSendException(httpStatusCode, description, null, apiErrorCode);

public static class DeliveryPolicy
{
    public const int MaxRetriesAfterInitial = 3;

    public static SendFailure Classify(Exception exception)
    {
        if (exception is TelegramSendException tg)
            return ClassifyTelegram(tg.HttpStatusCode, tg.Description, tg.RetryAfter, tg.ApiErrorCode);
        return new SendFailure(SendFailureKind.Transient, $"send failed: {exception.Message}");
    }

    // Permanent: recipient-side failures (blocked bot, unknown chat).
    // Everything else (network, 5xx, 429) is transient with backoff.
    public static SendFailure ClassifyTelegram(
        int? httpStatus, string description, TimeSpan? retryAfter = null, int? apiErrorCode = null)
    {
        var desc = description ?? string.Empty;
        if (httpStatus == 403 || apiErrorCode == 403 ||
            desc.Contains("blocked", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("chat not found", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("user not found", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("deactivated", StringComparison.OrdinalIgnoreCase))
            return new SendFailure(SendFailureKind.Permanent, $"permanent recipient failure: {desc}");

        if (httpStatus == 429 || apiErrorCode == 429)
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
    TimeSpan? claimRenewalInterval = null,
    IOccurrenceRepository? occurrences = null,
    Phase3Options? phase3Options = null,
    IPhase3LlmExecutor? phase3Llm = null,
    IPhase3Clock? clock = null,
    ILogger? logger = null)
{
    private IPhase3Clock ActiveClock => clock ?? SystemClock.Instance;

    private Phase3Options ActivePhase3 => phase3Options ?? Phase3Config.Read(_ => null);

    private IPhase3LlmExecutor ActivePhase3Llm => phase3Llm ??
        throw new InvalidOperationException("Phase 3 delivery requires an IPhase3LlmExecutor.");

    // A receipt carries the new flow once it declares schema 3. Anything
    // older with a persisted deliverable stays on the legacy transport.
    private static bool UseV3(DeliveryReceipt receipt) =>
        receipt.PayloadSchemaVersion == 3 || !HasPersistedPayload(receipt);

    // Literal-reply canary: emitted at reply build time (not delivery time)
    // so the dashboard can answer whether delivered text was LLM-authored
    // or a literal fallback, with the persisted and receipt model versions.
    // Never logs prompts, answers, or payload contents.
    private void LogReplyCanary(string ownerId, string operationId, DateTime scheduledUtc,
        string replySource, string provider, string model, int? receiptSchemaVersion) =>
        logger?.LogInformation(
            "Reply built for {OwnerId}/{OperationId} at {ScheduledUtc:u}: " +
            "replySource={ReplySource} provider={Provider} model={Model} receiptSchema={ReceiptSchema}.",
            ownerId, operationId, scheduledUtc, replySource, provider, model,
            receiptSchemaVersion?.ToString() ?? "legacy");

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

        var workStart = ActiveClock.UtcNow;
        if (occurrences is not null)
        {
            var invalid = ValidatePhase3Config();
            if (invalid is not null)
            {
                var pre = await deliveries.GetAsync(ownerId, operationId, scheduledUtc, ct);
                return new SingleAttemptResult(SingleAttemptOutcome.OccurrenceFailed,
                    pre?.Attempts ?? 0, null, invalid);
            }
        }

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
            // Unknown payload versions fail before either execution path is
            // selected: data is retained and nothing regenerates.
            if (receipt.PayloadSchemaVersion is { } schema &&
                schema != Phase3Limits.SchemaVersion)
            {
                var unsupported = receipt with
                {
                    Status = "failed",
                    ErrorSummary = Phase3FailureCodes.UnsupportedPayloadVersion,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                };
                await deliveries.UpsertAsync(unsupported, work.Token);
                return new SingleAttemptResult(SingleAttemptOutcome.OccurrenceFailed,
                    receipt.Attempts, null, Phase3FailureCodes.UnsupportedPayloadVersion);
            }
            if (occurrences is not null && UseV3(receipt))
                return await CapV3Async(
                    await AttemptV3Async(op, receipt, claimId, scheduledUtc, attemptIndex, work.Token, workStart),
                    ownerId, operationId, scheduledUtc, claimId, attemptIndex, work.Token);
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
        LogReplyCanary(fresh.OwnerId, fresh.OperationId, scheduledUtc, "llm-authored",
            result.Provider, result.Model, receipt.PayloadSchemaVersion);

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
        LogReplyCanary(op.OwnerId, op.OperationId, receipt.ScheduledUtc.UtcDateTime,
            "failure-notice-literal", llmOptions.Provider, llmOptions.Model,
            receipt.PayloadSchemaVersion);
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
                var messageId = await sender.SendPayloadAsync(
                    live.ChatId, new TelegramPayload(TelegramPayloadKind.LegacyHtml, parts[i]), ct);
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
            catch (TelegramUnknownMethodException)
            {
                // Rich endpoint unavailable: replace the unsent part with
                // bounded plain parts, persist them before any send, and keep
                // the confirmed prefix untouched.
                var fallback = await PersistLegacyFallbackAsync(op, current, parts, i,
                    PlainFallbackParts(parts[i]), ct);
                if (fallback.Failure is not null)
                    return fallback.Failure;
                (parts, current) = (fallback.Parts, fallback.Receipt);
                i--; // Re-send from the first replacement part after the loop increment.
                continue;
            }
            catch (TelegramSendException ex) when (TelegramErrorClassifier.ClassifyException(ex).Disposition
                is TelegramDisposition.ContentRejection)
            {
                // Definitely rejected unsent part: persist the literal
                // fallback text and its plan before sending; confirmed parts
                // keep their IDs and the legacy originals stay stored.
                var fallback = await PersistLegacyFallbackAsync(op, current, parts, i,
                    PlainFallbackParts(parts[i]), ct);
                if (fallback.Failure is not null)
                    return fallback.Failure;
                (parts, current) = (fallback.Parts, fallback.Receipt);
                i--; // Re-send from the first replacement part after the loop increment.
                continue;
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

    // Legacy HTML part converted to literal text, split for the
    // conservative 4,096-unit plain transport. An empty conversion drops
    // the part instead of sending a blank message.
    private static IReadOnlyList<string> PlainFallbackParts(string htmlPart)
    {
        var plain = RichMessageParts.ToPlainText(htmlPart);
        if (string.IsNullOrWhiteSpace(plain))
            return [];
        return Phase3LiteralChunker.SplitConservative(plain);
    }

    // Persists bounded replacement parts for one definitely-rejected,
    // unsent legacy part and repoints the receipt before any send: the
    // persisted list is the compatibility plan. The confirmed prefix
    // (SentParts/MessageIds) is preserved, and the legacy originals stay
    // stored under the previous payload version. A part that was already
    // replaced once and rejects again fails the occurrence instead of
    // rewriting versions forever.
    private async Task<(IReadOnlyList<string> Parts, DeliveryReceipt Receipt, SingleAttemptResult? Failure)>
        PersistLegacyFallbackAsync(
            OperationRecord op, DeliveryReceipt current, IReadOnlyList<string> parts, int index,
            IReadOnlyList<string> replacement, CancellationToken ct)
    {
        var marker = $"-compat-{index}";
        if (current.PayloadVersion?.EndsWith(marker, StringComparison.Ordinal) == true)
        {
            var failed = current with
            {
                Status = "failed",
                ErrorSummary = "content_rejected",
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
            await deliveries.UpsertAsync(failed, ct);
            return ([], failed, new SingleAttemptResult(SingleAttemptOutcome.OccurrenceFailed,
                current.Attempts, null, "content_rejected"));
        }
        var updated = parts.Take(index).Concat(replacement).Concat(parts.Skip(index + 1)).ToList();
        var version = $"{current.PayloadVersion ?? current.ClaimId}{marker}";
        var scheduledUtc = current.ScheduledUtc.UtcDateTime;
        try
        {
            await payloads.PersistAsync(op.OwnerId, op.OperationId,
                scheduledUtc, updated, ct, version: version);
        }
        catch (TransientStoreException ex)
        {
            return ([], current, new SingleAttemptResult(SingleAttemptOutcome.NeedRetry,
                current.Attempts, TimeSpan.FromSeconds(10), ex.Message));
        }
        catch (ClaimLostException)
        {
            return ([], current, ClaimWait());
        }
        var receipt = current with
        {
            PayloadVersion = version,
            TotalParts = updated.Count,
            SentParts = index,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        try
        {
            await deliveries.UpsertAsync(receipt, ct);
        }
        catch (TransientStoreException ex)
        {
            return ([], current, new SingleAttemptResult(SingleAttemptOutcome.NeedRetry,
                current.Attempts, TimeSpan.FromSeconds(10), ex.Message));
        }
        catch (ClaimLostException)
        {
            return ([], current, ClaimWait());
        }
        return (updated, receipt, null);
    }

    // Phase 3 configuration is rejected before any claim, send, or
    // publication when the repository is wired; legacy callers are unaffected.
    private string? ValidatePhase3Config()
    {
        if (phase3Llm is null)
            return "Phase 3 delivery requires an IPhase3LlmExecutor.";
        var options = ActivePhase3;
        try
        {
            options.Validate();
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
        if (llmOptions.RequestTimeout > Phase3Limits.MaxLlmTimeout)
            return $"LLM request timeout of {llmOptions.RequestTimeout.TotalSeconds:F0}s " +
                "exceeds the 540-second activity budget.";
        try
        {
            Phase3SystemTemplate.Render(options.TargetAnswerTextChars,
                Phase3Limits.RichTextChars, AdminInstruction());
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
        return null;
    }

    private string? AdminInstruction() =>
        string.IsNullOrWhiteSpace(llmOptions.SystemInstruction) ? null : llmOptions.SystemInstruction;

    private static TimeSpan StorageDelay(int attemptIndex) => attemptIndex switch
    {
        0 => TimeSpan.FromSeconds(5),
        1 => TimeSpan.FromSeconds(30),
        _ => TimeSpan.FromMinutes(5),
    };

    private bool FitsTime(DateTimeOffset workStart, TimeSpan needed) =>
        ActiveClock.UtcNow - workStart + needed <= Phase3Limits.ActivityWorkBudget;

    private static SingleAttemptResult StorageRetry(int attemptIndex, string? summary) =>
        new(SingleAttemptOutcome.NeedRetry, 0, StorageDelay(attemptIndex), summary);

    private static SingleAttemptResult YieldRetry() =>
        new(SingleAttemptOutcome.NeedRetry, 0, TimeSpan.FromSeconds(1), null);

    private static SingleAttemptResult Stopped() =>
        new(SingleAttemptOutcome.SkippedStopped, 0, null, null);

    private async Task<SingleAttemptResult> FailV3Async(
        string ownerId, string operationId, DateTime scheduledUtc, string claimId,
        string summary, int attempts, int httpAttempts, CancellationToken ct)
    {
        try
        {
            await occurrences!.FailAsync(new FailRequest(ownerId, operationId, scheduledUtc,
                claimId, summary, httpAttempts), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        return new SingleAttemptResult(SingleAttemptOutcome.OccurrenceFailed, attempts, null, summary);
    }

    private async Task<SingleAttemptResult> CapV3Async(
        SingleAttemptResult result, string ownerId, string operationId,
        DateTime scheduledUtc, string claimId, int attemptIndex, CancellationToken ct)
    {
        if (result.Outcome != SingleAttemptOutcome.NeedRetry ||
            attemptIndex < OccurrenceExecution.MaxCombinedRetriesAfterInitial)
            return result;
        return await FailV3Async(ownerId, operationId, scheduledUtc, claimId,
            result.ErrorSummary ?? "retry budget exhausted", result.Attempts, 0, ct);
    }

    private async Task<SingleAttemptResult> AttemptV3Async(
        OperationRecord op, DeliveryReceipt receipt, string claimId, DateTime scheduledUtc,
        int attemptIndex, CancellationToken ct, DateTimeOffset workStart)
    {
        var ownerId = op.OwnerId;
        var operationId = op.OperationId;
        var phase3 = ActivePhase3;
        var attempts = receipt.Attempts;
        var executionStarted = ActiveClock.UtcNow.UtcDateTime;

        FrozenContextRecord context;
        try
        {
            var instruction = Phase3SystemTemplate.Render(phase3.TargetAnswerTextChars,
                Phase3Limits.RichTextChars, AdminInstruction());
            var init = await occurrences!.InitializeContextAsync(new FrozenContextRequest(
                ownerId, operationId, scheduledUtc, claimId,
                new FrozenContextInputs(instruction, phase3.TargetAnswerTextChars,
                    Phase3Limits.RichTextChars, phase3.MemoryMode, executionStarted)), ct);
            context = init.Context;
        }
        catch (TransientStoreException ex) { return StorageRetry(attemptIndex, ex.Message); }
        catch (ClaimLostException) { return ClaimWait(); }
        catch (Phase3OperationStoppedException) { return Stopped(); }
        catch (Phase3PayloadException ex)
        {
            return await FailV3Async(ownerId, operationId, scheduledUtc, claimId,
                ex.Code, attempts, 0, ct);
        }

        var current = await deliveries.GetAsync(ownerId, operationId, scheduledUtc, ct);
        if (current is null || current.ClaimId != claimId)
            return ClaimWait();
        attempts = current.Attempts;
        if (current.AnswerVersion is null || current.PlanVersion is null)
        {
            var generated = await GenerateV3Async(op, context, phase3, claimId, scheduledUtc,
                executionStarted, attemptIndex, attempts, ct, workStart);
            if (generated is not null)
                return await CapV3Async(generated, ownerId, operationId, scheduledUtc,
                    claimId, attemptIndex, ct);
            current = await deliveries.GetAsync(ownerId, operationId, scheduledUtc, ct);
            if (current is null || current.ClaimId != claimId)
                return ClaimWait();
            attempts = current.Attempts;
        }

        return await CapV3Async(
            await DeliverV3PlanAsync(op, claimId, scheduledUtc, attemptIndex, attempts, ct, workStart),
            ownerId, operationId, scheduledUtc, claimId, attemptIndex, ct);
    }

    // Returns null when generation is persisted and delivery follows.
    private async Task<SingleAttemptResult?> GenerateV3Async(
        OperationRecord op, FrozenContextRecord context, Phase3Options phase3,
        string claimId, DateTime scheduledUtc, DateTime executionStarted,
        int attemptIndex, int attempts, CancellationToken ct, DateTimeOffset workStart)
    {
        var ownerId = op.OwnerId;
        var operationId = op.OperationId;
        string? previous = context.PreviousReplyPresent &&
            context.MemoryMode.Equals(Phase3Options.PreviousSuccessfulReply, StringComparison.Ordinal)
            ? context.PreviousReplyAnswer
            : null;
        var snapshot = previous is null && context.PreviousReplyPresent
            ? context.ToSnapshot() with
            {
                PreviousReplyPresent = false,
                PreviousReplyScheduledAtUtc = null,
                PreviousReplyExecutedAtUtc = null,
            }
            : context.ToSnapshot();
        var messages = Phase3ContextEnvelope.BuildMessages(
            context.EffectiveSystemInstruction, snapshot, previous);
        if (!Phase3ContextBudget.FitsBudget(messages, phase3, llmOptions.CompletionTokenBudget))
            return await TerminalNoticeAsync(op, claimId, scheduledUtc, executionStarted,
                attemptIndex, attempts, Phase3FailureCodes.ContextBudgetExceeded, ct, workStart);

        if (!FitsTime(workStart, llmOptions.RequestTimeout + TimeSpan.FromSeconds(30)))
            return YieldRetry();

        LlmResult result;
        try
        {
            result = await ActivePhase3Llm.ExecuteMessagesAsync(
                messages, phase3.MaxAnswerSourceChars, ct);
        }
        catch (LlmExecutionException ex) when (ex.Kind is LlmFailureKind.Transient or LlmFailureKind.EmptyResponse)
        {
            var (failure, count) = await TrackGenerationV3Async(ownerId, operationId, scheduledUtc,
                claimId, GenerationPolicy.Sanitize(ex.Summary), attemptIndex, ct);
            if (failure is not null)
                return failure;
            if (count > llmOptions.GenerationRetries)
                return await TerminalNoticeAsync(op, claimId, scheduledUtc, executionStarted,
                    attemptIndex, attempts, GenerationPolicy.Sanitize(ex.Summary), ct, workStart);
            return new SingleAttemptResult(SingleAttemptOutcome.NeedRetry, attempts,
                GenerationPolicy.RetryDelay(count - 1, ex.RetryAfter),
                GenerationPolicy.Sanitize(ex.Summary));
        }
        catch (LlmExecutionException ex)
        {
            return await TerminalNoticeAsync(op, claimId, scheduledUtc, executionStarted,
                attemptIndex, attempts, GenerationPolicy.Sanitize(ex.Summary), ct, workStart);
        }

        if (string.IsNullOrWhiteSpace(result.AnswerText))
        {
            var (failure, count) = await TrackGenerationV3Async(ownerId, operationId, scheduledUtc,
                claimId, "empty LLM response", attemptIndex, ct);
            if (failure is not null)
                return failure;
            if (count > llmOptions.GenerationRetries)
                return await TerminalNoticeAsync(op, claimId, scheduledUtc, executionStarted,
                    attemptIndex, attempts, "empty LLM response", ct, workStart);
            return new SingleAttemptResult(SingleAttemptOutcome.NeedRetry, attempts,
                GenerationPolicy.RetryDelay(count - 1), "empty LLM response");
        }

        string canonical;
        try
        {
            canonical = AnswerSourceBound.RequireWithinBound(
                AnswerSourceBound.Canonicalize(result.AnswerText), phase3.MaxAnswerSourceChars);
        }
        catch (Phase3PayloadException)
        {
            return await TerminalNoticeAsync(op, claimId, scheduledUtc, executionStarted,
                attemptIndex, attempts, Phase3FailureCodes.AnswerSourceLimit, ct, workStart);
        }

        var answer = AnswerArtifact.Create(Phase3Versions.New(), AnswerKind.Answer, canonical);
        var literalRequired = Phase3CapabilityGate.RequiresLiteral(canonical);
        var initialPlan = literalRequired
            ? DeliveryPlan.CreateInitialLiteral(answer.AnswerVersion, canonical)
            : DeliveryPlan.CreateInitial(answer.AnswerVersion, canonical);
        LogReplyCanary(ownerId, operationId, scheduledUtc,
            literalRequired ? "literal" : "llm-authored",
            result.Provider, result.Model, 3);
        try
        {
            await occurrences!.PersistGenerationAsync(new PersistGenerationRequest(
                ownerId, operationId, scheduledUtc, claimId, answer, initialPlan,
                executionStarted,
                new ReceiptUsage(result.Provider, result.Model, result.Usage.PromptTokens,
                    result.Usage.CompletionTokens, result.Usage.SearchResults, result.Usage.SearchUsed)), ct);
        }
        catch (TransientStoreException ex) { return StorageRetry(attemptIndex, ex.Message); }
        catch (ClaimLostException) { return ClaimWait(); }
        catch (Phase3OperationStoppedException) { return Stopped(); }
        catch (Phase3PayloadException ex)
        {
            return await FailV3Async(ownerId, operationId, scheduledUtc, claimId,
                ex.Code, attempts, 0, ct);
        }
        return null;
    }

    // Tracks one generation failure. Returns a failure result for
    // storage/claim/corruption outcomes, otherwise null with the new
    // attempt count for the caller's retry decision.
    private async Task<(SingleAttemptResult? Failure, int Count)> TrackGenerationV3Async(
        string ownerId, string operationId, DateTime scheduledUtc, string claimId,
        string summary, int attemptIndex, CancellationToken ct)
    {
        try
        {
            var count = await occurrences!.TrackGenerationAsync(
                new TrackGenerationRequest(ownerId, operationId, scheduledUtc, claimId, summary), ct);
            return (null, count);
        }
        catch (TransientStoreException ex) { return (StorageRetry(attemptIndex, ex.Message), 0); }
        catch (ClaimLostException) { return (ClaimWait(), 0); }
        catch (Phase3PayloadException ex)
        {
            return (await FailV3Async(ownerId, operationId, scheduledUtc, claimId,
                ex.Code, 0, 0, ct), 0);
        }
    }

    // Terminal pre-delivery failures deliver the persisted standard failure
    // notice through the normal plan path when storage remains usable.
    private async Task<SingleAttemptResult> TerminalNoticeAsync(
        OperationRecord op, string claimId, DateTime scheduledUtc, DateTime executionStarted,
        int attemptIndex, int attempts, string code, CancellationToken ct, DateTimeOffset workStart)
    {
        var ownerId = op.OwnerId;
        var operationId = op.OperationId;
        var notice = AnswerArtifact.Create(
            Phase3Versions.New(), AnswerKind.FailureNotice, Phase3DeliveryText.FailureNotice);
        var plan = DeliveryPlan.CreateInitial(notice.AnswerVersion, notice.Text);
        LogReplyCanary(ownerId, operationId, scheduledUtc, "failure-notice-literal",
            llmOptions.Provider, llmOptions.Model, 3);
        try
        {
            await occurrences!.PersistGenerationAsync(new PersistGenerationRequest(
                ownerId, operationId, scheduledUtc, claimId, notice, plan, executionStarted,
                new ReceiptUsage(llmOptions.Provider, llmOptions.Model, 0, 0, 0, false), code), ct);
        }
        catch (TransientStoreException ex) { return StorageRetry(attemptIndex, ex.Message); }
        catch (ClaimLostException) { return ClaimWait(); }
        catch (Phase3OperationStoppedException) { return Stopped(); }
        catch (Phase3PayloadException ex)
        {
            return await FailV3Async(ownerId, operationId, scheduledUtc, claimId,
                ex.Code, attempts, 0, ct);
        }
        return await CapV3Async(
            await DeliverV3PlanAsync(op, claimId, scheduledUtc, attemptIndex, attempts, ct, workStart),
            ownerId, operationId, scheduledUtc, claimId, attemptIndex, ct);
    }

    private async Task<SingleAttemptResult> DeliverV3PlanAsync(
        OperationRecord op, string claimId, DateTime scheduledUtc,
        int attemptIndex, int attempts, CancellationToken ct, DateTimeOffset workStart)
    {
        var ownerId = op.OwnerId;
        var operationId = op.OperationId;
        ProgressRead progress;
        try
        {
            progress = await occurrences!.ReadProgressAsync(ownerId, operationId, scheduledUtc, claimId, ct);
        }
        catch (TransientStoreException ex) { return StorageRetry(attemptIndex, ex.Message); }
        catch (ClaimLostException) { return ClaimWait(); }
        catch (Phase3ConsistencyException ex)
        {
            return await FailV3Async(ownerId, operationId, scheduledUtc, claimId,
                ex.Message, attempts, 0, ct);
        }
        catch (Phase3PayloadException ex)
        {
            return await FailV3Async(ownerId, operationId, scheduledUtc, claimId,
                ex.Code, attempts, 0, ct);
        }

        var plan = progress.Plan;
        var source = progress.Answer.Text;
        while (true)
        {
            var leaf = plan.Leaves.FirstOrDefault(l => !l.Confirmed);
            if (leaf is null)
                break;
            if (!FitsTime(workStart, Phase3Limits.TelegramProgressReserve))
                return YieldRetry();

            var live = await operations.GetAsync(ownerId, operationId, ct);
            if (live is null || live.Status is OperationStatus.Deleted or OperationStatus.Failed)
                return Stopped();

            if (!await deliveries.RenewClaimAsync(ownerId, operationId, scheduledUtc, claimId, ct))
                return ClaimWait();
            ct.ThrowIfCancellationRequested();

            TelegramPayload payload;
            try
            {
                payload = ToPayload(leaf, source);
            }
            catch (Phase3ConsistencyException ex)
            {
                return await FailV3Async(ownerId, operationId, scheduledUtc, claimId,
                    ex.Message, attempts, 0, ct);
            }

            long messageId;
            try
            {
                attempts++;
                messageId = await sender.SendPayloadAsync(live.ChatId, payload, ct);
            }
            catch (TelegramUnknownMethodException)
            {
                if (leaf.Kind == PlanLeafKind.LiteralPlain)
                {
                    return await FailV3Async(ownerId, operationId, scheduledUtc, claimId,
                        "unknown_method", attempts, 1, ct);
                }
                var replaced = await ReplaceV3Async(ownerId, operationId, scheduledUtc, claimId,
                    leaf.Id, PlanFallbackKind.ToPlain, attemptIndex, attempts, ct);
                if (replaced.Failure is not null)
                    return replaced.Failure;
                if (replaced.Plan is null)
                    return StorageRetry(attemptIndex, "plan replacement did not persist");
                plan = replaced.Plan;
                continue;
            }
            catch (TelegramSendException ex)
            {
                var classification = TelegramErrorClassifier.ClassifyException(ex);
                switch (classification.Disposition)
                {
                    case TelegramDisposition.ContentRejection:
                    {
                        var fallback = DecideFallback(leaf, ex.Description);
                        if (fallback is null)
                        {
                            return await FailV3Async(ownerId, operationId, scheduledUtc, claimId,
                                "content_rejected", attempts, 1, ct);
                        }
                        var replaced = await ReplaceV3Async(ownerId, operationId, scheduledUtc, claimId,
                            leaf.Id, fallback.Value, attemptIndex, attempts, ct);
                        if (replaced.Failure is not null)
                            return replaced.Failure;
                        if (replaced.Plan is null)
                            return StorageRetry(attemptIndex, "plan replacement did not persist");
                        plan = replaced.Plan;
                        continue;
                    }
                    case TelegramDisposition.UnknownMethod:
                    {
                        if (leaf.Kind == PlanLeafKind.LiteralPlain)
                        {
                            return await FailV3Async(ownerId, operationId, scheduledUtc, claimId,
                                "unknown_method", attempts, 1, ct);
                        }
                        var replaced = await ReplaceV3Async(ownerId, operationId, scheduledUtc, claimId,
                            leaf.Id, PlanFallbackKind.ToPlain, attemptIndex, attempts, ct);
                        if (replaced.Failure is not null)
                            return replaced.Failure;
                        if (replaced.Plan is null)
                            return StorageRetry(attemptIndex, "plan replacement did not persist");
                        plan = replaced.Plan;
                        continue;
                    }
                    case TelegramDisposition.PermanentRecipient:
                    {
                        var current = await operations.GetAsync(ownerId, operationId, ct);
                        if (current is not null)
                            await operations.CompareAndSwapStatusAsync(ownerId, operationId,
                                current.Status, OperationStatus.Failed, "permanent recipient failure", ct);
                        try
                        {
                            await occurrences!.FailAsync(new FailRequest(ownerId, operationId,
                                scheduledUtc, claimId, "permanent recipient failure", 1), ct);
                        }
                        catch (Exception failEx) when (failEx is not OperationCanceledException) { }
                        return new SingleAttemptResult(SingleAttemptOutcome.OperationFailed,
                            attempts, null, "permanent recipient failure");
                    }
                    case TelegramDisposition.RateLimited:
                    case TelegramDisposition.Transient:
                    {
                        var category = classification.Disposition == TelegramDisposition.RateLimited
                            ? "rate_limited" : "transient_delivery_failure";
                        int total;
                        try
                        {
                            total = await occurrences!.TrackDeliveryAsync(
                                new TrackDeliveryRequest(ownerId, operationId, scheduledUtc,
                                    claimId, category), ct);
                            attempts++;
                        }
                        catch (TransientStoreException storeEx)
                        {
                            return StorageRetry(attemptIndex, storeEx.Message);
                        }
                        catch (ClaimLostException) { return ClaimWait(); }
                        catch (Phase3PayloadException payloadEx)
                        {
                            return await FailV3Async(ownerId, operationId, scheduledUtc, claimId,
                                payloadEx.Code, attempts, 0, ct);
                        }
                        if (total > DeliveryPolicy.MaxRetriesAfterInitial)
                        {
                            return await FailV3Async(ownerId, operationId, scheduledUtc, claimId,
                                category, attempts, 0, ct);
                        }
                        return new SingleAttemptResult(SingleAttemptOutcome.NeedRetry, attempts,
                            DeliveryPolicy.RetryDelay(total - 1, ex.RetryAfter), category);
                    }
                    default:
                    {
                        var code = ex.ApiErrorCode == 401 || ex.HttpStatusCode == 401
                            ? "telegram_unauthorized" : "delivery_rejected";
                        return await FailV3Async(ownerId, operationId, scheduledUtc, claimId,
                            code, attempts, 1, ct);
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return ClaimWait();
            }
            catch (Exception)
            {
                int total;
                try
                {
                    total = await occurrences!.TrackDeliveryAsync(
                        new TrackDeliveryRequest(ownerId, operationId, scheduledUtc,
                            claimId, "transient_delivery_failure"), ct);
                    attempts++;
                }
                catch (TransientStoreException storeEx) { return StorageRetry(attemptIndex, storeEx.Message); }
                catch (ClaimLostException) { return ClaimWait(); }
                catch (Phase3PayloadException payloadEx)
                {
                    return await FailV3Async(ownerId, operationId, scheduledUtc, claimId,
                        payloadEx.Code, attempts, 0, ct);
                }
                if (total > DeliveryPolicy.MaxRetriesAfterInitial)
                {
                    return await FailV3Async(ownerId, operationId, scheduledUtc, claimId,
                        "transient_delivery_failure", attempts, 0, ct);
                }
                return new SingleAttemptResult(SingleAttemptOutcome.NeedRetry, attempts,
                    DeliveryPolicy.RetryDelay(total - 1), "transient_delivery_failure");
            }

            try
            {
                plan = await occurrences!.ConfirmLeafAsync(new ConfirmLeafRequest(
                    ownerId, operationId, scheduledUtc, claimId, leaf.Id, messageId), ct);
            }
            catch (TransientStoreException ex) { return StorageRetry(attemptIndex, ex.Message); }
            catch (ClaimLostException) { return ClaimWait(); }
            catch (Phase3ConsistencyException ex)
            {
                return await FailV3Async(ownerId, operationId, scheduledUtc, claimId,
                    ex.Message, attempts, 0, ct);
            }
            catch (Phase3PayloadException ex)
            {
                return await FailV3Async(ownerId, operationId, scheduledUtc, claimId,
                    ex.Code, attempts, 0, ct);
            }
        }

        try
        {
            await occurrences!.CompleteAsync(
                new CompleteRequest(ownerId, operationId, scheduledUtc, claimId), ct);
            return new SingleAttemptResult(SingleAttemptOutcome.Sent, attempts, null, null);
        }
        catch (TransientStoreException ex) { return StorageRetry(attemptIndex, ex.Message); }
        catch (ClaimLostException) { return ClaimWait(); }
        catch (Phase3OperationStoppedException) { return Stopped(); }
        catch (Phase3ConsistencyException ex)
        {
            return await FailV3Async(ownerId, operationId, scheduledUtc, claimId,
                ex.Message, attempts, 0, ct);
        }
        catch (Phase3PayloadException ex)
        {
            return await FailV3Async(ownerId, operationId, scheduledUtc, claimId,
                ex.Code, attempts, 0, ct);
        }
    }

    // Replaces the rejected unsent leaf and persists the replacement before
    // any new send. Storage conflicts surface as a retryable failure; claim
    // loss propagates to the activity's claim handling.
    private async Task<(DeliveryPlanDoc? Plan, SingleAttemptResult? Failure)> ReplaceV3Async(
        string ownerId, string operationId, DateTime scheduledUtc, string claimId,
        string leafId, PlanFallbackKind fallback, int attemptIndex, int attempts, CancellationToken ct)
    {
        try
        {
            var replaced = await occurrences!.ReplaceLeafAsync(new ReplaceLeafRequest(
                ownerId, operationId, scheduledUtc, claimId, leafId, fallback), ct);
            return (replaced.Plan, null);
        }
        catch (TransientStoreException ex) { return (null, StorageRetry(attemptIndex, ex.Message)); }
        catch (Phase3ConsistencyException ex)
        {
            return (null, await FailV3Async(ownerId, operationId, scheduledUtc, claimId,
                ex.Message, attempts, 0, ct));
        }
        catch (Phase3PayloadException ex)
        {
            return (null, await FailV3Async(ownerId, operationId, scheduledUtc, claimId,
                ex.Code, attempts, 0, ct));
        }
    }

    private static PlanFallbackKind? DecideFallback(PlanLeaf leaf, string description) =>
        (leaf.Kind, leaf.FallbackStage) switch
        {
            (PlanLeafKind.Markdown, PlanFallbackStage.Native) => PlanFallbackKind.ToLiteralRich,
            (PlanLeafKind.LiteralRich, PlanFallbackStage.RichFull) =>
                TelegramErrorClassifier.IsSizeRejection(description)
                    ? PlanFallbackKind.ToSmallLiteral
                    : PlanFallbackKind.ToPlain,
            (PlanLeafKind.LiteralRich, PlanFallbackStage.RichSmall) => PlanFallbackKind.ToPlain,
            _ => null,
        };

    private static TelegramPayload ToPayload(PlanLeaf leaf, string source)
    {
        if (leaf.StartUtf16 < 0 || leaf.LengthUtf16 <= 0 ||
            leaf.StartUtf16 + leaf.LengthUtf16 > source.Length)
            throw new Phase3ConsistencyException("plan leaf range is out of bounds");
        var text = source.Substring(leaf.StartUtf16, leaf.LengthUtf16);
        return leaf.Kind switch
        {
            PlanLeafKind.Markdown => new TelegramPayload(TelegramPayloadKind.Markdown, text),
            PlanLeafKind.LiteralRich => new TelegramPayload(TelegramPayloadKind.LiteralRich, text),
            PlanLeafKind.LiteralPlain => new TelegramPayload(TelegramPayloadKind.LiteralPlain, text),
            _ => throw new Phase3ConsistencyException($"unexpected leaf kind {leaf.Kind}"),
        };
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

    private sealed class SystemClock : IPhase3Clock
    {
        public static readonly SystemClock Instance = new();
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private string TruncateAnswer(string answer)
    {
        if (TextLimits.CountChars(answer) <= llmOptions.MaxStoredAnswerChars)
            return answer;
        var runes = answer.EnumerateRunes().ToArray();
        return string.Concat(runes[..llmOptions.MaxStoredAnswerChars].Select(r => r.ToString())) +
            "\n\n…[answer truncated to the 32,768-character limit]";
    }
}
