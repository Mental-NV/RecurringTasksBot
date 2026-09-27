// OpenRouter streaming response reader. The source bound is enforced
// while accumulating visible content (runes are never split across
// chunks), a provider finish reason of length is terminal, and no partial
// answer is ever returned.
using System.Text;
using System.Text.Json;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Infrastructure.Llm;

public static class OpenRouterStreamReader
{
    public static async Task<LlmResult> ReadStreamAsync(Stream stream, OpenRouterOptions provider,
        int maxSourceScalars, TimeSpan? retryAfter = null, CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var data = new StringBuilder();
        var bound = new AnswerSourceAccumulator(maxSourceScalars);
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
            OpenRouterJson.CheckProviderError(root, retryAfter);
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                (promptTokens, completionTokens) = OpenRouterJson.ExtractUsage(root);
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("index", out var index) && index.GetInt32() != 0) continue;
                OpenRouterJson.CheckProviderError(choice, retryAfter);
                if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
                    finishReason = finish.GetString();
                if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object) continue;
                var increment = OpenRouterJson.ExtractContent(delta);
                try
                {
                    bound.Append(increment);
                }
                catch (PayloadIntegrityException)
                {
                    throw new LlmExecutionException(LlmFailureKind.SourceLimit, "answer_source_limit");
                }
                if (bound.IsOverLimit)
                    throw new LlmExecutionException(LlmFailureKind.SourceLimit, "answer_source_limit");
                foreach (var source in OpenRouterJson.ExtractSources(delta, provider.MaxTotalResults))
                    if (sources.Count < provider.MaxTotalResults && !sources.Any(s => s.Url == source.Url))
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
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new LlmExecutionException(LlmFailureKind.Transient, "Malformed LLM stream.");
        }

        if (!done || finishReason is not ("stop" or "length"))
            throw new LlmExecutionException(LlmFailureKind.Transient, "Incomplete LLM stream.");
        if (finishReason == "length")
            throw new LlmExecutionException(LlmFailureKind.AnswerIncomplete, "answer_incomplete");
        if (!bound.HasContent)
            throw new LlmExecutionException(LlmFailureKind.EmptyResponse, "empty LLM response");
        string canonical;
        try
        {
            canonical = bound.GetCanonical();
        }
        catch (PayloadIntegrityException)
        {
            throw new LlmExecutionException(LlmFailureKind.SourceLimit, "answer_source_limit");
        }
        return new LlmResult(canonical, sources,
            new LlmUsage(promptTokens, completionTokens, sources.Count, sources.Count > 0),
            provider.Provider, provider.Model, sources.Count > 0);
    }
}
