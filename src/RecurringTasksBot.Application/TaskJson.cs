// Phase 5 get modes (docs/Spec.Phase5.md section 1): both return the
// task-settings JSON structure only. Explicit preserves omissions; effective
// fills timezone, once, and all parameters from current global defaults.
// No envelope, IDs, revisions, state, or source labels inside the JSON.
using System.Text.Json;

namespace RecurringTasksBot.Application;

public static class TaskJsonRenderer
{
    public static string RenderExplicit(TaskDefinition definition)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("prompt", definition.Prompt ?? string.Empty);
            writer.WriteStartObject("schedule");
            if (definition.Schedule.Cron is not null)
                writer.WriteString("cron", definition.Schedule.Cron);
            if (definition.Schedule.OnceExplicit)
            {
                writer.WriteStartArray("once");
                foreach (var date in definition.Schedule.Once ?? [])
                    writer.WriteStringValue(date);
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
            if (definition.TimezoneExplicit)
                writer.WriteString("timezone", definition.Timezone);
            WriteParameters(writer, definition.Parameters, explicitOnly: true);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string RenderEffective(TaskDefinition definition, TaskDefaults globals)
    {
        var effective = TaskDefinitionParser.ResolveEffective(definition, globals);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("prompt", effective.Prompt);
            writer.WriteStartObject("schedule");
            if (effective.Cron is not null)
                writer.WriteString("cron", effective.Cron);
            writer.WriteStartArray("once");
            foreach (var date in effective.Once)
                writer.WriteStringValue(date);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteString("timezone", effective.Timezone);
            writer.WriteStartObject("parameters");
            writer.WriteString("memoryMode", effective.MemoryMode);
            writer.WriteString("reasoningEffort", effective.ReasoningEffort);
            writer.WriteBoolean("webSearch", effective.WebSearch);
            if (effective.ExpiresAt is null)
                writer.WriteNull("expiresAt");
            else
                writer.WriteString("expiresAt", effective.ExpiresAt);
            if (effective.MaxOccurrences is null)
                writer.WriteNull("maxOccurrences");
            else
                writer.WriteNumber("maxOccurrences", effective.MaxOccurrences.Value);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteParameters(Utf8JsonWriter writer, TaskParameters parameters, bool explicitOnly)
    {
        var hasAny = parameters.MemoryMode is not null || parameters.ReasoningEffort is not null ||
            parameters.WebSearch is not null || parameters.ExpiresAt is not null ||
            parameters.MaxOccurrences is not null;
        if (explicitOnly && !hasAny)
            return;
        writer.WriteStartObject("parameters");
        WriteOptionalString(writer, "memoryMode", parameters.MemoryMode);
        WriteOptionalString(writer, "reasoningEffort", parameters.ReasoningEffort);
        if (parameters.WebSearch is not null)
            writer.WriteBoolean("webSearch", parameters.WebSearch.Value);
        WriteOptionalString(writer, "expiresAt", parameters.ExpiresAt);
        if (parameters.MaxOccurrences is not null)
            writer.WriteNumber("maxOccurrences", parameters.MaxOccurrences.Value);
        writer.WriteEndObject();
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
            writer.WriteString(name, value);
    }
}
