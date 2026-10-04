// Owned OpenRouter settings: provider transport and search-tool knobs
// consumed only by the adapter. Generation time/retry/token budgets live
// in ExecutionOptions (Application); the host composes both.
namespace RecurringTasksBot.Infrastructure.Llm;

public sealed record OpenRouterOptions(
    string Provider,
    string BaseUrl,
    string Model,
    string ReasoningEffort,
    bool SearchEnabled,
    string SearchEngine,
    string SearchMode,
    int MaxSearches,
    int MaxResultsPerSearch,
    int MaxTotalResults)
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
        // Search is a default capability, not a mandatory global setting:
        // disabled stays disabled for requests without an override, while
        // an explicit task override can still enable search per occurrence.
        if (string.IsNullOrWhiteSpace(SearchEngine))
            throw new InvalidOperationException("LLM search engine is not configured.");
        if (string.IsNullOrWhiteSpace(SearchMode))
            throw new InvalidOperationException("LLM search mode is not configured.");
        if (MaxSearches <= 0 || MaxResultsPerSearch <= 0 || MaxTotalResults <= 0)
            throw new InvalidOperationException("LLM search limits must be positive.");
    }
}
