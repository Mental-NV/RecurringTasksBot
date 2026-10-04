// DeepSeek non-streaming response reader over the Anthropic-compatible
// message shape. No partial answer is ever returned: empty, over-bound,
// and length-truncated responses are terminal failures, and in-body
// search errors fail even under HTTP 200.
using System.Text.Json;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Infrastructure.Llm;

public static class DeepSeekResponseReader
{
    public static LlmResult ReadResponse(
        string body, DeepSeekOptions provider, int maxSourceScalars, TimeSpan? retryAfter = null)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new LlmExecutionException(LlmFailureKind.EmptyResponse, "unusable DeepSeek response");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new LlmExecutionException(LlmFailureKind.EmptyResponse, "unusable DeepSeek response");
            if (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                type.GetString() == "error")
            {
                var errorType = root.TryGetProperty("error", out var error) &&
                    error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("type", out var nested) && nested.ValueKind == JsonValueKind.String
                    ? nested.GetString() : null;
                throw DeepSeekMessageReader.MapTopLevelError(errorType, retryAfter);
            }
            if (!root.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
                throw new LlmExecutionException(LlmFailureKind.EmptyResponse, "unusable DeepSeek response");

            var blocks = new List<DeepSeekAssembledBlock>();
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new LlmExecutionException(LlmFailureKind.Transient, "Incomplete DeepSeek response.");
                blocks.Add(ReadBlock(item));
            }

            var stop = root.TryGetProperty("stop_reason", out var stopReason) &&
                stopReason.ValueKind == JsonValueKind.String ? stopReason.GetString() : null;
            var (promptTokens, completionTokens) = ExtractUsage(root);
            // The JSON helper retains duplicate citation URLs, matching the
            // existing OpenRouter JSON behavior for this response mode.
            return DeepSeekMessageReader.Finalize(
                blocks, promptTokens, completionTokens, stop, provider, maxSourceScalars,
                deduplicateSources: false, retryAfter: retryAfter);
        }
    }

    private static DeepSeekAssembledBlock ReadBlock(JsonElement item)
    {
        var block = new DeepSeekAssembledBlock
        {
            Type = item.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? string.Empty : string.Empty,
        };
        if (block.Type.Equals("text", StringComparison.Ordinal))
        {
            if (item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                block.Text.Append(text.GetString());
            if (item.TryGetProperty("citations", out var citations) &&
                citations.ValueKind == JsonValueKind.Array)
            {
                foreach (var citation in citations.EnumerateArray())
                    DeepSeekMessageReader.AddCitation(block, citation);
            }
        }
        else if (block.Type.Equals("web_search_tool_result", StringComparison.Ordinal))
        {
            var present = item.TryGetProperty("content", out var content);
            DeepSeekMessageReader.ReadSearchResultContent(block, content, present);
        }
        return block;
    }

    private static (long PromptTokens, long CompletionTokens) ExtractUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return (0, 0);
        var prompt = usage.TryGetProperty("input_tokens", out var p) && p.TryGetInt64(out var pv) ? pv : 0;
        var completion = usage.TryGetProperty("output_tokens", out var c) && c.TryGetInt64(out var cv) ? cv : 0;
        return (prompt, completion);
    }
}
