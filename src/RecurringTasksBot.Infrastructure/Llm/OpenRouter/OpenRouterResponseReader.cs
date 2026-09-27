// OpenRouter non-streaming response reader. No partial answer is ever
// returned: empty, over-bound, and length-truncated responses are
// terminal failures.
using System.Text.Json;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Infrastructure.Llm;

public static class OpenRouterResponseReader
{
    public static LlmResult ReadResponse(
        string body, OpenRouterOptions provider, int maxSourceScalars, TimeSpan? retryAfter = null)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new LlmExecutionException(LlmFailureKind.EmptyResponse, "unusable LLM response");
        }

        using (doc)
        {
            var root = doc.RootElement;
            OpenRouterJson.CheckProviderError(root, retryAfter);
            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                throw new LlmExecutionException(LlmFailureKind.EmptyResponse, "unusable LLM response");
            OpenRouterJson.CheckProviderError(choices[0], retryAfter);
            if (choices[0].TryGetProperty("finish_reason", out var finish) &&
                finish.ValueKind == JsonValueKind.String && finish.GetString() == "length")
                throw new LlmExecutionException(LlmFailureKind.AnswerIncomplete, "answer_incomplete");
            var message = choices[0].TryGetProperty("message", out var m) ? m : default;
            if (message.ValueKind != JsonValueKind.Object)
                throw new LlmExecutionException(LlmFailureKind.EmptyResponse, "unusable LLM response");

            var answer = OpenRouterJson.ExtractContent(message);
            var sources = OpenRouterJson.ExtractSources(message, provider.MaxTotalResults);
            var (promptTokens, completionTokens) = OpenRouterJson.ExtractUsage(root);
            if (string.IsNullOrWhiteSpace(answer))
                throw new LlmExecutionException(LlmFailureKind.EmptyResponse, "empty LLM response");
            try
            {
                AnswerSourceBound.RequireWithinBound(answer.Trim(), maxSourceScalars);
            }
            catch (PayloadIntegrityException)
            {
                throw new LlmExecutionException(LlmFailureKind.SourceLimit, "answer_source_limit");
            }
            return new LlmResult(answer.Trim(), sources,
                new LlmUsage(promptTokens, completionTokens, sources.Count, sources.Count > 0),
                provider.Provider, provider.Model, sources.Count > 0);
        }
    }
}
