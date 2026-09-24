using System.Text;
using System.Text.Json;

namespace RecurringTasksBot.Core;

public sealed partial class OpenRouterLlmExecutor
{
    // Buffer only visible content and citations. No partial answer is returned
    // on disconnect, timeout, or provider error; Durable owns the retry.
    public static async Task<LlmResult> ParseStreamAsync(Stream stream, LlmOptions opts,
        TimeSpan? retryAfter = null, CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var data = new StringBuilder();
        var answer = new StringBuilder();
        var sources = new List<LlmSource>();
        long promptTokens = 0, completionTokens = 0;
        string? finishReason = null;

        bool ProcessEvent()
        {
            if (data.Length == 0) return false;
            var payload = data.ToString().TrimEnd('\n');
            data.Clear();
            if (payload == "[DONE]") return true;
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            CheckProviderError(root, retryAfter);
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                (promptTokens, completionTokens) = ExtractUsage(root);
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
                return false; // e.g. server-tool progress events
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("index", out var index) && index.GetInt32() != 0) continue;
                CheckProviderError(choice, retryAfter);
                if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
                    finishReason = finish.GetString();
                if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object) continue;
                answer.Append(ExtractContent(delta));
                foreach (var source in ExtractSources(delta, opts.MaxTotalResults))
                    if (sources.Count < opts.MaxTotalResults && !sources.Any(s => s.Url == source.Url))
                        sources.Add(source);
            }
            return false;
        }

        var done = false;
        try
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Length == 0)
                {
                    if (ProcessEvent()) { done = true; break; }
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    var field = line[5..];
                    data.Append(field.StartsWith(' ') ? field[1..] : field).Append('\n');
                }
                // SSE comments/keep-alives, event names and IDs are not content.
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new LlmExecutionException(LlmFailureKind.Transient, "Malformed LLM stream.");
        }

        if (!done || finishReason is not ("stop" or "length"))
            throw new LlmExecutionException(LlmFailureKind.Transient, "Incomplete LLM stream.");
        if (string.IsNullOrWhiteSpace(answer.ToString()))
            throw new LlmExecutionException(LlmFailureKind.EmptyResponse, "empty LLM response");
        return new LlmResult(answer.ToString().Trim(), sources,
            new LlmUsage(promptTokens, completionTokens, sources.Count, sources.Count > 0),
            opts.Provider, opts.Model, sources.Count > 0);
    }

    private static void CheckProviderError(JsonElement root, TimeSpan? retryAfter)
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
