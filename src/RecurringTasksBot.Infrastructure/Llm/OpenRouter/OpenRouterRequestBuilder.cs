// OpenRouter chat-completions request construction: the frozen effective
// system instruction plus an optional archived assistant turn. Messages
// are already fully constructed; they are serialized verbatim with no
// interpolation or delimiter tricks.
using System.Text;
using System.Text.Json;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Infrastructure.Llm;

public static class OpenRouterRequestBuilder
{
    public static string BuildRequestJson(
        OpenRouterOptions provider, ExecutionOptions execution, IReadOnlyList<ChatMessage> messages,
        LlmRequest? request = null)
    {
        provider.Validate();
        execution.Validate();
        if (messages is null || messages.Count == 0)
            throw new ArgumentOutOfRangeException(nameof(messages));
        var effort = OpenRouterLlmExecutor.MapReasoningEffort(
            request?.ReasoningEffort ?? provider.ReasoningEffort);
        var searchEnabled = request?.SearchEnabled ?? provider.SearchEnabled;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", provider.Model);
            writer.WriteStartArray("messages");
            foreach (var message in messages)
            {
                if (string.IsNullOrEmpty(message.Role) || message.Content is null)
                    throw new ArgumentOutOfRangeException(nameof(messages));
                writer.WriteStartObject();
                writer.WriteString("role", message.Role);
                writer.WriteString("content", message.Content);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartObject("reasoning");
            writer.WriteString("effort", effort);
            writer.WriteBoolean("exclude", true);
            writer.WriteEndObject();
            writer.WriteNumber("max_completion_tokens", execution.CompletionTokenBudget);
            writer.WriteBoolean("stream", true);
            if (searchEnabled)
            {
                writer.WriteNumber("max_tool_calls", provider.MaxSearches);
                writer.WriteStartArray("tools");
                writer.WriteStartObject();
                writer.WriteString("type", OpenRouterLlmExecutor.ServerToolType);
                writer.WriteStartObject("parameters");
                writer.WriteString("engine", provider.SearchEngine);
                writer.WriteString("mode", provider.SearchMode);
                writer.WriteNumber("max_results", provider.MaxResultsPerSearch);
                writer.WriteNumber("max_total_results", provider.MaxTotalResults);
                writer.WriteNumber("max_uses", provider.MaxSearches);
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
