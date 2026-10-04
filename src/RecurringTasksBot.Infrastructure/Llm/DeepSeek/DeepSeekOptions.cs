// Direct DeepSeek settings: Anthropic-compatible transport plus the
// local source-metadata cap. Shared reasoning/search defaults are bound
// from Llm:ReasoningEffort and Llm:SearchEnabled at configuration time;
// profile-level copies of those shared settings are rejected. DeepSeek
// has no confirmed per-search or total server result-count controls, so
// MaxTotalResults only caps extracted source metadata locally and is
// never sent as a search option.
namespace RecurringTasksBot.Infrastructure.Llm;

public sealed record DeepSeekOptions(
    string Provider,
    string BaseUrl,
    string Model,
    string ReasoningEffort,
    bool SearchEnabled,
    int MaxSearches,
    int MaxTotalResults)
{
    public void Validate()
    {
        if (!Provider.Equals("DeepSeek", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Unsupported LLM provider '{Provider}'. Only 'DeepSeek' is implemented by this adapter.");
        if (string.IsNullOrWhiteSpace(BaseUrl))
            throw new InvalidOperationException("LLM base URL is not configured.");
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("LLM base URL must be an absolute HTTPS URL.");
        if (string.IsNullOrWhiteSpace(Model))
            throw new InvalidOperationException("LLM model is not configured.");
        if (string.IsNullOrWhiteSpace(ReasoningEffort))
            throw new InvalidOperationException("LLM reasoning effort is not configured.");
        if (MaxSearches <= 0 || MaxTotalResults <= 0)
            throw new InvalidOperationException("LLM search limits must be positive.");
    }
}
