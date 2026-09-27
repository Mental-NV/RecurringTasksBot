// Delivery reliability: error classification, retry plan, and the delivery
// activity flow. Runs in the delivery activity (non-orchestration code),
// with long retry waits persisted through Durable timers by the caller.
using Microsoft.Extensions.Logging;

namespace RecurringTasksBot.Application;

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

// Occurrence execution policy: check status, generate one complete answer,
// persist the answer and initial delivery plan before any send, then deliver
// unconfirmed plan leaves. Generation retries (at most 2 after the initial
// attempt) stay separate from delivery retries (at most 3 after the initial
// attempt); the orchestrator bounds the combined loop. Delivery retries reuse
// the persisted answer and resume unconfirmed leaves without another LLM
// request. Deletion is rechecked before generation, each generation retry,
// and each message part; an in-flight LLM result is discarded when deleted,
// while an already-started Telegram send may finish.
//
// Outcome/counter table. The overall work-retry cap is five after the initial
// attempt (2 generation + 3 delivery). Every NeedRetry consumes it, including
// storage-conflict and budget-yield retries; WaitingForClaim never does.
//
// | Outcome           | attemptIndex advances | consumes overall cap |
// |-------------------|-----------------------|----------------------|
// | Sent              | terminal              | n/a                  |
// | SkippedStopped    | terminal              | n/a                  |
// | SkippedDuplicate  | terminal              | n/a                  |
// | OccurrenceFailed  | terminal              | n/a                  |
// | OperationFailed   | terminal              | n/a                  |
// | NeedRetry         | +1                    | yes                  |
// | WaitingForClaim   | unchanged             | no                   |
//
// Terminal NeedRetry (attemptIndex has reached the cap of 5) fails the
// occurrence without consuming further work.
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
