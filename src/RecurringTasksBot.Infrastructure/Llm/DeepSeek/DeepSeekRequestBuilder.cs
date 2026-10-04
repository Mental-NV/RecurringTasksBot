// Direct DeepSeek request construction for the Anthropic-compatible
// messages endpoint. The application's leading system message moves to
// top-level "system"; remaining messages keep their roles, order, and
// text content. Only server-side web search is exposed; a returned
// client tool is never executed.
using System.Text;
using System.Text.Json;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Infrastructure.Llm;

public static class DeepSeekRequestBuilder
{
    public const string SearchToolType = "web_search_20250305";
    public const string SearchToolName = "web_search";
    public const string AnthropicVersion = "2023-06-01";

    public static string MessagesEndpoint(DeepSeekOptions provider)
    {
        provider.Validate();
        return provider.BaseUrl.TrimEnd('/') + "/v1/messages";
    }

    public static string BuildRequestJson(
        DeepSeekOptions provider, ExecutionOptions execution, IReadOnlyList<ChatMessage> messages,
        LlmRequest? request = null, bool stream = true)
    {
        provider.Validate();
        execution.Validate();
        if (messages is null || messages.Count == 0)
            throw new ArgumentOutOfRangeException(nameof(messages));
        var effort = DeepSeekLlmExecutor.MapReasoningEffort(
            request?.ReasoningEffort ?? provider.ReasoningEffort);
        var searchEnabled = request?.SearchEnabled ?? provider.SearchEnabled;
        var (system, remaining) = SplitSystemMessage(messages);
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("model", provider.Model);
            writer.WriteNumber("max_tokens", execution.CompletionTokenBudget);
            writer.WriteBoolean("stream", stream);
            writer.WriteString("system", system);
            writer.WriteStartObject("thinking");
            writer.WriteString("type", "enabled");
            writer.WriteEndObject();
            writer.WriteStartObject("output_config");
            writer.WriteString("effort", effort);
            writer.WriteEndObject();
            writer.WriteStartArray("messages");
            foreach (var message in remaining)
            {
                writer.WriteStartObject();
                writer.WriteString("role", message.Role);
                writer.WriteStartArray("content");
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", message.Content);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (searchEnabled)
            {
                writer.WriteStartArray("tools");
                writer.WriteStartObject();
                writer.WriteString("type", SearchToolType);
                writer.WriteString("name", SearchToolName);
                writer.WriteNumber("max_uses", provider.MaxSearches);
                writer.WriteEndObject();
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // Only the current system,user and system,user,assistant,user shapes
    // are supported. Anything else is rejected rather than dropping
    // content or adding another system instruction.
    private static (string System, IReadOnlyList<ChatMessage> Remaining) SplitSystemMessage(
        IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0 || !messages[0].Role.Equals("system", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "DeepSeek requests require a leading system message.");
        string[] restRoles = messages.Count switch
        {
            2 => ["user"],
            4 => ["user", "assistant", "user"],
            _ => throw new InvalidOperationException(
                $"Unsupported DeepSeek message shape with {messages.Count} messages."),
        };
        for (var i = 0; i < restRoles.Length; i++)
        {
            var message = messages[i + 1];
            if (!message.Role.Equals(restRoles[i], StringComparison.Ordinal) ||
                message.Content is null)
                throw new InvalidOperationException(
                    $"Unsupported DeepSeek message shape: expected '{restRoles[i]}' at position {i + 1}.");
        }
        return (messages[0].Content, messages.Skip(1).ToList());
    }
}
