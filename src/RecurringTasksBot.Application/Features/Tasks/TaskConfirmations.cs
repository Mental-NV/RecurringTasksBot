// Shared Phase 5 confirmation rendering: task ID/revision, changed
// overrides or inheritance resets, limits/count remaining, and up to three
// eligible local/UTC occurrences. Presentation never feeds back into
// scheduling.
using System.Globalization;

using NodaTime;

namespace RecurringTasksBot.Application;

public static class TaskConfirmationPreview
{
    public const int MaxOccurrences = 3;

    public static IReadOnlyList<string> NextOccurrenceLines(
        TaskDefinition definition, DateTimeZone zone, DateTime fromUtc, int max = MaxOccurrences)
    {
        var lines = new List<string>();
        var cursor = fromUtc;
        for (var i = 0; i < max; i++)
        {
            var next = TaskScheduleResolver.GetNextMergedUtc(
                definition.Schedule.Cron, definition.Schedule.Once, zone, cursor);
            if (next is null)
                break;
            lines.Add(FormatInstant(next.Value, zone, definition.Timezone));
            cursor = next.Value;
        }

        return lines;
    }

    public static string FormatInstant(DateTime instantUtc, DateTimeZone zone, string zoneId)
    {
        var utc = DateTime.SpecifyKind(instantUtc, DateTimeKind.Utc);
        var offset = TaskTimezones.OffsetAt(utc, zone);
        var local = utc + offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var magnitude = offset.Duration();
        return string.Format(CultureInfo.InvariantCulture, "{0:dd MMM HH:mm} {1} ({2}{3:hh\\:mm}) / {4:dd MMM HH:mm}Z",
            local, zoneId, sign, magnitude, utc);
    }

    public static string LimitsLine(TaskRecord record)
    {
        var expires = record.ExpiresAtUtc is null
            ? "no expiration"
            : $"expires {record.ExpiresAtUtc.Value:dd MMM yyyy HH:mm}Z";
        var count = record.Definition.Parameters.MaxOccurrences is null
            ? $"{record.StartedOccurrences} occurrences started (unlimited)"
            : $"{record.StartedOccurrences}/{record.Definition.Parameters.MaxOccurrences.Value} occurrences used";
        return $"Limits: {expires}; {count}.";
    }
}

public static class TaskHelp
{
    public const string CreateUsage =
        "Usage: /create <JSON object>.\n" +
        "Fields: prompt (required), schedule.cron and/or schedule.once (at least one), " +
        "timezone (IANA ID, default UTC, immutable), " +
        "parameters.memoryMode/reasoningEffort/webSearch/expiresAt/maxOccurrences.\n" +
        "Reply to a message with /create {\"schedule\": {...}} to use it as the prompt.";

    public const string GetUsage = "Usage: /get <task-id> [explicit|effective].";
    public const string UpdateUsage = "Usage: /update <task-id> <JSON patch object>.";
    public const string ListUsage = "Usage: /list [page] [compact].";

    public static string FieldError(TaskDefinitionError error) =>
        string.IsNullOrEmpty(error.FieldPath)
            ? $"{error.Code}: {error.Message}"
            : $"{error.Code} at {error.FieldPath}: {error.Message}";

    public const string TaskNotFound = "task_not_found: no such task.";

    public const string Unknown =
        "Unknown command. Tasks are JSON:\n" +
        "/create <JSON object> — prompt, schedule.cron and/or schedule.once, timezone, parameters\n" +
        "/get <task-id> [explicit|effective] — inspect saved or effective settings\n" +
        "/update <task-id> <JSON patch object> — change only supplied fields\n" +
        "/list [page] [compact] — show your tasks\n" +
        "/delete <task-id> — delete a task\n" +
        "Each run executes the stored prompt fresh and sends the answer here. " +
        "Prompts are sent to an external LLM/search service.";
}
