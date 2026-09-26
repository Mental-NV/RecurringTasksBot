// Provider-neutral prompt-execution contract. Only web search is exposed to
// the model: prompts carry the user's text plus scheduling context, never
// credentials, other users' data, or internal operational data.
namespace RecurringTasksBot.Core;

public sealed record LlmOptions(
    string Provider,
    string BaseUrl,
    string Model,
    string ReasoningEffort,
    TimeSpan RequestTimeout,
    int CompletionTokenBudget,
    int MaxStoredAnswerChars,
    int GenerationRetries,
    bool SearchEnabled,
    string SearchEngine,
    int MaxSearches,
    int MaxResultsPerSearch,
    int MaxTotalResults,
    string SystemInstruction)
{
    public void Validate()
    {
        if (!Provider.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Unsupported LLM provider '{Provider}'. Only 'OpenRouter' is implemented; add an adapter before changing providers.");
        if (string.IsNullOrWhiteSpace(BaseUrl))
            throw new InvalidOperationException("LLM base URL is not configured.");
        if (string.IsNullOrWhiteSpace(Model))
            throw new InvalidOperationException("LLM model is not configured.");
        if (string.IsNullOrWhiteSpace(ReasoningEffort))
            throw new InvalidOperationException("LLM reasoning effort is not configured.");
        if (RequestTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("LLM request timeout must be positive.");
        if (CompletionTokenBudget <= 0)
            throw new InvalidOperationException("LLM completion token budget must be positive.");
        if (GenerationRetries < 0)
            throw new InvalidOperationException("LLM generation retries must not be negative.");
        if (!SearchEnabled)
            throw new InvalidOperationException(
                "LLM web search is disabled by configuration. Phase 2 requires search; refusing to silently run without it.");
        if (string.IsNullOrWhiteSpace(SearchEngine))
            throw new InvalidOperationException("LLM search engine is not configured.");
        if (MaxSearches <= 0 || MaxResultsPerSearch <= 0 || MaxTotalResults <= 0)
            throw new InvalidOperationException("LLM search limits must be positive.");
    }
}

public sealed record LlmSource(string Title, string Url);

public sealed record LlmUsage(
    long PromptTokens,
    long CompletionTokens,
    int SearchResults,
    bool SearchUsed);

public sealed record LlmPrompt(string PromptText, DateTime ScheduledUtc, DateTime ExecutionUtc);

public sealed record LlmResult(
    string AnswerText,
    IReadOnlyList<LlmSource> Sources,
    LlmUsage Usage,
    string Provider,
    string Model,
    bool SearchUsed);

public interface ILlmPromptExecutor
{
    // One request per call; transport streaming is internal to the adapter.
    // Returns only a complete answer. The caller (Durable activity) owns retries.
    Task<LlmResult> ExecuteAsync(LlmPrompt prompt, CancellationToken ct = default);
}

// Phase 3 prompt executor: frozen instruction plus an optional archived
// assistant turn and the current envelope. Enforces the answer source bound
// while accumulating streamed content and on the final response.
public interface IPhase3LlmExecutor
{
    Task<LlmResult> ExecuteMessagesAsync(
        IReadOnlyList<ChatMessage> messages, int maxSourceScalars, CancellationToken ct = default);
}

// Configuration binding shared by the host and tests: JSON profiles
// (common + environment-specific, environment variables applied over them)
// feed one lookup; code defaults apply only when neither defines a value.
// Secrets never come from JSON.
public static class LlmConfig
{
    public static LlmOptions Read(Func<string, string?> get)
    {
        string Str(string key, string fallback)
        {
            var value = get(key);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        int Int(string key, int fallback) =>
            int.TryParse(get(key), out var value) ? value : fallback;
        bool Bool(string key, bool fallback) =>
            bool.TryParse(get(key), out var value) ? value : fallback;
        return new LlmOptions(
            Provider: Str("RecurringTasksBot:Llm:Provider", "OpenRouter"),
            BaseUrl: Str("RecurringTasksBot:Llm:BaseUrl", "https://openrouter.ai/api/v1"),
            Model: Str("RecurringTasksBot:Llm:Model", "deepseek/deepseek-v4.1-flash"),
            ReasoningEffort: Str("RecurringTasksBot:Llm:ReasoningEffort", "Maximum"),
            RequestTimeout: TimeSpan.FromSeconds(Int("RecurringTasksBot:Llm:RequestTimeoutSeconds", 480)),
            CompletionTokenBudget: Int("RecurringTasksBot:Llm:CompletionTokenBudget", 131072),
            MaxStoredAnswerChars: Int("RecurringTasksBot:Llm:MaxStoredAnswerChars", 32768),
            GenerationRetries: Int("RecurringTasksBot:Llm:GenerationRetries", 2),
            SearchEnabled: Bool("RecurringTasksBot:Llm:SearchEnabled", true),
            SearchEngine: Str("RecurringTasksBot:Llm:SearchEngine", "exa"),
            MaxSearches: Int("RecurringTasksBot:Llm:MaxSearches", 8),
            MaxResultsPerSearch: Int("RecurringTasksBot:Llm:MaxResultsPerSearch", 5),
            MaxTotalResults: Int("RecurringTasksBot:Llm:MaxTotalResults", 40),
            SystemInstruction: Str("RecurringTasksBot:Llm:SystemInstruction", string.Empty));
    }
}

public enum LlmFailureKind
{
    Transient,
    Permanent,
    EmptyResponse,
    // Phase 3 terminal generation failures: never retried, never delivered
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
