// Shared DeepSeek semantic reader: one interpretation for assembled
// JSON and SSE content blocks. Thinking and tool payloads never enter
// the answer; for search responses only the final answer segment after
// the last server-tool/result block is retained. Source metadata comes
// only from citations on those final text blocks, never from scanning
// answer text or treating every search result as cited.
using System.Text;
using System.Text.Json;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Infrastructure.Llm;

internal sealed class DeepSeekAssembledBlock
{
    public string Type = string.Empty;
    public readonly StringBuilder Text = new();
    public readonly List<(string Title, string Url)> Citations = new();
    public bool IsSearchError;
    public string? SearchErrorCode;
}

internal static class DeepSeekMessageReader
{
    public static LlmResult Finalize(
        IReadOnlyList<DeepSeekAssembledBlock> blocks,
        long promptTokens,
        long completionTokens,
        string? stopReason,
        DeepSeekOptions provider,
        int maxSourceScalars,
        bool deduplicateSources,
        TimeSpan? retryAfter = null)
    {
        foreach (var block in blocks)
            CheckBlockType(block.Type);
        CheckStopReason(stopReason);

        // In-body search errors fail even under HTTP 200: an empty result
        // array is a successful empty search, but an error object is not.
        foreach (var block in blocks)
        {
            if (block is { Type: "web_search_tool_result", IsSearchError: true })
                throw MapSearchError(block.SearchErrorCode, retryAfter);
        }

        // Final answer segment: text after the last server-tool/result
        // block; without tools, all text blocks in order. Concatenated
        // verbatim with no invented separators.
        var boundary = -1;
        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].Type is "server_tool_use" or "web_search_tool_result")
                boundary = i;
        }
        var finalTexts = new List<string>();
        var finalCitations = new List<(string Title, string Url)>();
        for (var i = boundary + 1; i < blocks.Count; i++)
        {
            if (!blocks[i].Type.Equals("text", StringComparison.Ordinal))
                continue;
            finalTexts.Add(blocks[i].Text.ToString());
            finalCitations.AddRange(blocks[i].Citations);
        }

        var bound = new AnswerSourceAccumulator(maxSourceScalars);
        try
        {
            foreach (var text in finalTexts)
                bound.Append(text);
            if (bound.IsOverLimit)
                throw new LlmExecutionException(LlmFailureKind.SourceLimit, "answer_source_limit");
        }
        catch (PayloadIntegrityException)
        {
            throw new LlmExecutionException(LlmFailureKind.SourceLimit, "answer_source_limit");
        }

        string canonical;
        try
        {
            canonical = bound.GetCanonical();
        }
        catch (PayloadIntegrityException)
        {
            throw new LlmExecutionException(LlmFailureKind.SourceLimit, "answer_source_limit");
        }
        if (!bound.HasContent || string.IsNullOrWhiteSpace(canonical))
            throw new LlmExecutionException(LlmFailureKind.EmptyResponse, "empty LLM response");

        var sources = new List<LlmSource>();
        foreach (var (title, url) in finalCitations)
        {
            if (sources.Count >= provider.MaxTotalResults)
                break;
            if (deduplicateSources && sources.Any(s => s.Url == url))
                continue;
            sources.Add(new LlmSource(title, url));
        }

        return new LlmResult(canonical, sources,
            new LlmUsage(promptTokens, completionTokens, sources.Count, sources.Count > 0),
            provider.Provider, provider.Model, sources.Count > 0);
    }

    // Well-formed but unsupported content semantics are terminal: no
    // continuation or client tool execution exists. Malformed transport
    // (bad JSON, truncated stream) is transient and handled by callers.
    private static void CheckBlockType(string type)
    {
        switch (type)
        {
            case "text":
            case "thinking":
            case "redacted_thinking":
            case "server_tool_use":
            case "web_search_tool_result":
                return;
            case "tool_use":
                throw new LlmExecutionException(LlmFailureKind.AnswerIncomplete, "answer_incomplete");
            default:
                throw new LlmExecutionException(LlmFailureKind.AnswerIncomplete, "answer_incomplete");
        }
    }

    private static void CheckStopReason(string? stopReason)
    {
        switch (stopReason)
        {
            case "end_turn":
                return;
            case "max_tokens":
                throw new LlmExecutionException(LlmFailureKind.AnswerIncomplete, "answer_incomplete");
            case null:
                throw new LlmExecutionException(LlmFailureKind.Transient, "Incomplete DeepSeek response.");
            default:
                throw new LlmExecutionException(LlmFailureKind.AnswerIncomplete, "answer_incomplete");
        }
    }

    internal static LlmExecutionException MapSearchError(string? errorCode, TimeSpan? retryAfter) =>
        errorCode switch
        {
            // The provider Retry-After survives on transient search
            // failures so application retries honor the delay instead of
            // retrying immediately.
            "too_many_requests" or "unavailable" => new LlmExecutionException(
                LlmFailureKind.Transient, "LLM search temporarily unavailable.", retryAfter),
            "invalid_tool_input" or "query_too_long" or "request_too_large" => new LlmExecutionException(
                LlmFailureKind.Permanent, "LLM search request was rejected."),
            "max_uses_exceeded" => new LlmExecutionException(
                LlmFailureKind.Permanent, "search_budget_exhausted"),
            _ => new LlmExecutionException(
                LlmFailureKind.Permanent, "unsupported_search_error"),
        };

    internal static LlmExecutionException MapTopLevelError(string? errorType, TimeSpan? retryAfter) =>
        errorType switch
        {
            "rate_limit_error" or "overloaded_error" or "api_error" => new LlmExecutionException(
                LlmFailureKind.Transient, $"LLM provider error (type={errorType}).", retryAfter),
            "authentication_error" or "permission_error" or "invalid_request_error" or
            "not_found_error" or "request_too_large" => new LlmExecutionException(
                LlmFailureKind.Permanent, $"LLM provider error (type={errorType})."),
            _ => new LlmExecutionException(
                LlmFailureKind.Transient, "provider_error_unknown", retryAfter),
        };

    internal static void AddCitation(
        DeepSeekAssembledBlock block, JsonElement citation)
    {
        if (citation.ValueKind != JsonValueKind.Object)
            return;
        var type = citation.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() : null;
        if (!"web_search_result_location".Equals(type, StringComparison.Ordinal))
            return;
        var url = citation.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String
            ? u.GetString() ?? string.Empty : string.Empty;
        var title = citation.TryGetProperty("title", out var ti) && ti.ValueKind == JsonValueKind.String
            ? ti.GetString() ?? string.Empty : string.Empty;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            block.Citations.Add((title, url));
    }

    // A search-result block carries either a results array (successful,
    // possibly empty) or an error object. Only the error case affects
    // control flow; sources come from text citations, never this array.
    // Null, missing, or unrecognized content is an unsupported result
    // shape: it fails terminally instead of passing as a successful
    // search with no evidence.
    internal static void ReadSearchResultContent(
        DeepSeekAssembledBlock block, JsonElement content, bool present)
    {
        if (!present || content.ValueKind == JsonValueKind.Null)
            throw new LlmExecutionException(LlmFailureKind.AnswerIncomplete, "answer_incomplete");
        if (content.ValueKind == JsonValueKind.Array)
            return;
        if (content.ValueKind == JsonValueKind.Object &&
            content.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String &&
            t.GetString() is { } contentType &&
            contentType.Equals("web_search_tool_result_error", StringComparison.Ordinal))
        {
            block.IsSearchError = true;
            block.SearchErrorCode =
                content.TryGetProperty("error_code", out var code) && code.ValueKind == JsonValueKind.String
                    ? code.GetString() : null;
            return;
        }
        throw new LlmExecutionException(LlmFailureKind.AnswerIncomplete, "answer_incomplete");
    }
}
