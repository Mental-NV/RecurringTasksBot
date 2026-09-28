// Provider-neutral prompt-execution contract. Only web search is exposed to
// the model: prompts carry the user's text plus scheduling context, never
// credentials, other users' data, or internal operational data.
namespace RecurringTasksBot.Application;


public sealed record LlmSource(string Title, string Url);

public sealed record LlmUsage(
    long PromptTokens,
    long CompletionTokens,
    int SearchResults,
    bool SearchUsed);

public sealed record LlmResult(
    string AnswerText,
    IReadOnlyList<LlmSource> Sources,
    LlmUsage Usage,
    string Provider,
    string Model,
    bool SearchUsed);

// Single generation contract: the application constructs the complete
// frozen-context message list; the adapter serializes it verbatim without
// injecting a second system prompt. The answer-source bound travels with
// the request so streaming readers enforce it while accumulating content.
// Optional per-request overrides carry a frozen occurrence's effective
// settings; null means the provider default. The adapter must not override
// them again from provider configuration.
public sealed record LlmRequest(
    IReadOnlyList<ChatMessage> Messages,
    int MaxSourceScalars,
    string? ReasoningEffort = null,
    bool? SearchEnabled = null);

// Per-occurrence generation overrides resolved from a frozen claim snapshot.
public sealed record OccurrenceGenerationOverrides(
    string? MemoryMode = null,
    string? ReasoningEffort = null,
    bool? SearchEnabled = null);

public interface ILlmExecutor
{
    Task<LlmResult> ExecuteAsync(LlmRequest request, CancellationToken ct = default);
}

public enum LlmFailureKind
{
    Transient,
    Permanent,
    EmptyResponse,
    // Terminal generation failures: never retried, never delivered
    // partially. The exception summary carries the sanitized occurrence code
    // (answer_incomplete, answer_source_limit).
    AnswerIncomplete,
    SourceLimit,
}

public sealed class LlmExecutionException(
    LlmFailureKind kind,
    string summary,
    TimeSpan? retryAfter = null) : Exception(summary)
{
    public LlmFailureKind Kind { get; } = kind;
    public string Summary { get; } = summary;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public static class GenerationPolicy
{
    public const int MaxRetriesAfterInitial = 2;

    public static bool ShouldRetry(LlmFailureKind kind, int attemptsSoFar) =>
        kind is LlmFailureKind.Transient or LlmFailureKind.EmptyResponse &&
        attemptsSoFar <= MaxRetriesAfterInitial;

    // Increasing Durable waits; honors Retry-After when it exceeds backoff.
    public static TimeSpan RetryDelay(int retryIndex, TimeSpan? retryAfter = null)
    {
        var backoff = retryIndex switch
        {
            0 => TimeSpan.FromSeconds(10),
            _ => TimeSpan.FromSeconds(60),
        };
        if (retryAfter.HasValue && retryAfter.Value > backoff)
            return retryAfter.Value;
        return backoff;
    }

    // Authentication, credit, and invalid-request errors must not be
    // retried. Everything else network/server-side is transient.
    public static LlmFailureKind ClassifyHttpStatus(int statusCode) => statusCode switch
    {
        408 or 425 or 429 => LlmFailureKind.Transient,
        >= 500 => LlmFailureKind.Transient,
        401 or 402 or 403 or 400 or 404 or 422 => LlmFailureKind.Permanent,
        _ => LlmFailureKind.Transient,
    };

    // Sanitized summary for storage: trimmed, bounded, with key-like
    // material redacted. Never carries prompts, answers, or raw payloads.
    public static string Sanitize(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return "generation failed";
        var text = detail.Trim();
        text = System.Text.RegularExpressions.Regex.Replace(text, @"sk-[A-Za-z0-9\-_]{8,}", "sk-***");
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"AccountKey=[^;'\s]+", "AccountKey=***", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        const int max = 280;
        return text.Length <= max ? text : text[..max];
    }

    public static TimeSpan? ParseRetryAfter(string? headerValue, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
            return null;
        if (int.TryParse(headerValue.Trim(), out var seconds) && seconds >= 0)
            return TimeSpan.FromSeconds(seconds);
        if (DateTimeOffset.TryParse(headerValue.Trim(), out var date) && date > nowUtc)
            return date - nowUtc;
        return null;
    }
}
