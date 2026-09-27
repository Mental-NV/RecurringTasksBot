// Shared OpenRouter response JSON helpers: content/source/usage
// extraction and provider error mapping. Provider messages and raw
// metadata can echo user data, so only whitelisted diagnostics surface.
using System.Text.Json;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Infrastructure.Llm;

internal static class OpenRouterJson
{
    internal static string ExtractContent(JsonElement message)
    {
        if (message.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String)
                return content.GetString() ?? string.Empty;
            if (content.ValueKind == JsonValueKind.Array)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object)
                        continue;
                    var type = part.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type == "text" && part.TryGetProperty("text", out var text) &&
                        text.ValueKind == JsonValueKind.String)
                    {
                        if (sb.Length > 0)
                            sb.Append('\n');
                        sb.Append(text.GetString());
                    }
                }

                return sb.ToString();
            }
        }

        return string.Empty;
    }

    internal static IReadOnlyList<LlmSource> ExtractSources(JsonElement message, int maxTotal)
    {
        var sources = new List<LlmSource>();
        if (!message.TryGetProperty("annotations", out var annotations) ||
            annotations.ValueKind != JsonValueKind.Array)
            return sources;
        foreach (var annotation in annotations.EnumerateArray())
        {
            if (sources.Count >= maxTotal)
                break;
            if (annotation.ValueKind != JsonValueKind.Object)
                continue;
            var type = annotation.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (!"url_citation".Equals(type, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!annotation.TryGetProperty("url_citation", out var citation) ||
                citation.ValueKind != JsonValueKind.Object)
                continue;
            var url = citation.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
            var title = citation.TryGetProperty("title", out var ti) ? ti.GetString() ?? string.Empty : string.Empty;
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                sources.Add(new LlmSource(title, url));
        }

        return sources;
    }

    internal static (long PromptTokens, long CompletionTokens) ExtractUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return (0, 0);
        var prompt = usage.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt64(out var pv) ? pv : 0;
        var completion = usage.TryGetProperty("completion_tokens", out var c) && c.TryGetInt64(out var cv) ? cv : 0;
        return (prompt, completion);
    }

    internal static void CheckProviderError(JsonElement root, TimeSpan? retryAfter)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new LlmExecutionException(LlmFailureKind.EmptyResponse, "unusable LLM response");
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            int? status = null;
            if (error.TryGetProperty("code", out var code))
            {
                if (code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var number)) status = number;
                else if (code.ValueKind == JsonValueKind.String && int.TryParse(code.GetString(), out number)) status = number;
            }
            var type = error.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object &&
                metadata.TryGetProperty("error_type", out var kind) && kind.ValueKind == JsonValueKind.String
                ? kind.GetString() : null;
            // Whitelist diagnostics: provider messages/raw metadata can echo user data.
            var safeType = type switch
            {
                "timeout" or "provider_unavailable" or "provider_overloaded" or "rate_limit_exceeded" or
                "server" or "authentication" or "payment_required" or "permission_denied" or
                "invalid_request" or "invalid_prompt" or "not_found" or "context_length_exceeded" or
                "max_tokens_exceeded" or "token_limit_exceeded" or "content_policy_violation" => type,
                _ => "unknown",
            };
            var failure = safeType is "authentication" or "payment_required" or "permission_denied" or
                "invalid_request" or "invalid_prompt" or "not_found" or "context_length_exceeded" or
                "content_policy_violation" ? LlmFailureKind.Permanent
                : status.HasValue ? GenerationPolicy.ClassifyHttpStatus(status.Value) : LlmFailureKind.Transient;
            throw new LlmExecutionException(failure,
                $"LLM provider error (code={status?.ToString() ?? "unknown"}; type={safeType}).",
                failure == LlmFailureKind.Transient ? retryAfter : null);
        }
        if (root.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String &&
            finish.GetString() == "error")
            throw new LlmExecutionException(LlmFailureKind.Transient, "LLM provider ended with an error.", retryAfter);
    }
}
