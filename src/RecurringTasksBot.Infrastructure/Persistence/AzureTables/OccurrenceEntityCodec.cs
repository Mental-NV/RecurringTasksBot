// Occurrence entity codec: property maps and row parsers for the
// memory, context, answer, and plan entities. Strict reads fail corrupt
// or unknown-schema rows as integrity errors, never defaults.
using Azure.Data.Tables;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Infrastructure.Persistence;

public static class OccurrenceEntityCodec
{
    public sealed record StoredAnswer(AnswerArtifact Answer, DateTime SourceExecutedUtc);

    public const string KindMemory = "memory";
    public const string KindContext = "context";
    public const string KindAnswer = "answer";
    public const string KindPlan = "plan";

    public static Dictionary<string, object?> ContextProps(FrozenContextRecord c)
    {
        var props = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["EntityKind"] = KindContext,
            ["SchemaVersion"] = StorageLimits.SchemaVersion,
            ["OwnerId"] = c.OwnerId,
            ["OperationId"] = c.OperationId,
            ["ScheduledUtc"] = new DateTimeOffset(UtcTime.Utc(c.ScheduledUtc)),
            ["ContextVersion"] = c.ContextVersion,
            ["CreatedUtc"] = c.CreatedUtc,
            ["ScheduleCron"] = c.ScheduleCron,
            ["ScheduleTimezone"] = c.ScheduleTimezone,
            ["OccurrenceId"] = c.OccurrenceId,
            ["ScheduledAtUtc"] = c.ScheduledAtUtc,
            ["ExecutionStartedAtUtc"] = c.ExecutionStartedAtUtc,
            ["PreviousReplyPresent"] = c.PreviousReplyPresent,
            ["PreviousReplyScheduledAtUtc"] = c.PreviousReplyScheduledAtUtc ?? string.Empty,
            ["PreviousReplyExecutedAtUtc"] = c.PreviousReplyExecutedAtUtc ?? string.Empty,
            ["NoMemoryReason"] = c.NoMemoryReason ?? string.Empty,
            ["TargetAnswerTextChars"] = c.TargetAnswerTextChars,
            ["MaxRichMessageChars"] = c.MaxRichMessageChars,
            ["MemoryMode"] = c.MemoryMode,
            ["EnabledCapabilities"] = c.EnabledCapabilities,
            ["InstructionVersion"] = c.InstructionVersion,
        };
        SegmentedProperties.Write(props, "Task", c.TaskText);
        SegmentedProperties.Write(props, "Instruction", c.EffectiveSystemInstruction);
        if (c.PreviousReplyAnswer is not null)
            SegmentedProperties.Write(props, "Previous", c.PreviousReplyAnswer);
        return props;
    }

    public static FrozenContextRecord ParseContext(TableEntity entity)
    {
        var props = new Dictionary<string, object?>(entity, StringComparer.Ordinal);
        TableRow.RequireKind(props, entity.RowKey, KindContext);
        var present = props.TryGetValue("PreviousReplyPresent", out var p) && p is true;
        return new FrozenContextRecord(
            TableRow.GetString(props, "ContextVersion", entity.RowKey),
            TableRow.GetString(props, "OwnerId", entity.RowKey),
            TableRow.GetString(props, "OperationId", entity.RowKey),
            TableRow.GetDto(props, "ScheduledUtc", entity.RowKey).UtcDateTime,
            SegmentedProperties.Read(props, "Task"),
            TableRow.GetString(props, "ScheduleCron", entity.RowKey),
            props.TryGetValue("ScheduleTimezone", out var zoneValue) &&
                zoneValue is string zoneText && zoneText.Length > 0
                ? zoneText : "UTC",
            TableRow.GetString(props, "OccurrenceId", entity.RowKey),
            TableRow.GetString(props, "ScheduledAtUtc", entity.RowKey),
            TableRow.GetString(props, "ExecutionStartedAtUtc", entity.RowKey),
            present,
            TableRow.NullIfEmpty(TableRow.GetString(props, "PreviousReplyScheduledAtUtc", entity.RowKey)),
            TableRow.NullIfEmpty(TableRow.GetString(props, "PreviousReplyExecutedAtUtc", entity.RowKey)),
            present ? SegmentedProperties.Read(props, "Previous") : null,
            TableRow.NullIfEmpty(TableRow.GetString(props, "NoMemoryReason", entity.RowKey)),
            SegmentedProperties.Read(props, "Instruction"),
            TableRow.GetInt(props, "TargetAnswerTextChars", entity.RowKey),
            TableRow.GetInt(props, "MaxRichMessageChars", entity.RowKey),
            TableRow.GetString(props, "MemoryMode", entity.RowKey),
            TableRow.GetString(props, "EnabledCapabilities", entity.RowKey),
            TableRow.GetString(props, "InstructionVersion", entity.RowKey),
            TableRow.GetDto(props, "CreatedUtc", entity.RowKey));
    }

    public static Dictionary<string, object?> AnswerProps(
        AnswerArtifact answer, DateTime scheduledUtc, DateTime executedUtc, DateTimeOffset createdUtc)
    {
        var props = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["EntityKind"] = KindAnswer,
            ["SchemaVersion"] = StorageLimits.SchemaVersion,
            ["AnswerVersion"] = answer.AnswerVersion,
            ["Kind"] = answer.Kind == AnswerKind.Answer ? "answer" : "failure_notice",
            ["ScheduledUtc"] = new DateTimeOffset(UtcTime.Utc(scheduledUtc)),
            ["SourceExecutedUtc"] = new DateTimeOffset(UtcTime.Utc(executedUtc)),
            ["CreatedUtc"] = createdUtc,
        };
        SegmentedProperties.Write(props, "Answer", answer.Text);
        return props;
    }

    public static StoredAnswer ParseAnswer(TableEntity entity)
    {
        var props = new Dictionary<string, object?>(entity, StringComparer.Ordinal);
        TableRow.RequireKind(props, entity.RowKey, KindAnswer);
        var kind = TableRow.GetString(props, "Kind", entity.RowKey) switch
        {
            "answer" => AnswerKind.Answer,
            "failure_notice" => AnswerKind.FailureNotice,
            var other => throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt,
                $"{entity.RowKey} has unknown answer kind '{other}'"),
        };
        var text = SegmentedProperties.Read(props, "Answer");
        var executed = TableRow.GetDto(props, "SourceExecutedUtc", entity.RowKey).UtcDateTime;
        return new StoredAnswer(
            new AnswerArtifact(
                TableRow.GetString(props, "AnswerVersion", entity.RowKey), kind, text,
                DeliveryPlan.HashSource(text), AnswerSourceBound.CountScalars(text)),
            executed);
    }

    public static Dictionary<string, object?> PlanProps(string planVersion, DeliveryPlanDoc plan)
    {
        var props = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["EntityKind"] = KindPlan,
            ["SchemaVersion"] = StorageLimits.SchemaVersion,
            ["PlanVersion"] = planVersion,
            ["AnswerVersion"] = plan.AnswerVersion,
            ["Revision"] = plan.Revision,
            ["SourceSha256"] = plan.SourceSha256,
        };
        SegmentedProperties.Write(props, "Plan", PlanRowCodec.Serialize(plan));
        return props;
    }

    public static DeliveryPlanDoc ParsePlan(TableEntity entity, string source)
    {
        var props = new Dictionary<string, object?>(entity, StringComparer.Ordinal);
        TableRow.RequireKind(props, entity.RowKey, KindPlan);
        var plan = PlanRowCodec.Deserialize(SegmentedProperties.Read(props, "Plan"), source);
        if (plan.AnswerVersion != TableRow.GetString(props, "AnswerVersion", entity.RowKey) ||
            plan.Revision != TableRow.GetInt(props, "Revision", entity.RowKey) ||
            plan.SourceSha256 != TableRow.GetString(props, "SourceSha256", entity.RowKey))
            throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt,
                $"{entity.RowKey} plan metadata disagrees with its leaves");
        return plan;
    }

    public static Dictionary<string, object?> MemoryProps(MemoryRecord memory)
    {
        var props = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["EntityKind"] = KindMemory,
            ["SchemaVersion"] = StorageLimits.SchemaVersion,
            ["OwnerId"] = memory.OwnerId,
            ["OperationId"] = memory.OperationId,
            ["AnswerVersion"] = memory.AnswerVersion,
            ["SourceScheduledUtc"] = new DateTimeOffset(UtcTime.Utc(memory.SourceScheduledUtc)),
            ["SourceExecutedUtc"] = new DateTimeOffset(UtcTime.Utc(memory.SourceExecutedUtc)),
            ["PublishedUtc"] = memory.PublishedUtc,
        };
        SegmentedProperties.Write(props, "Answer", memory.Answer);
        return props;
    }

    public static MemoryRecord ParseMemory(TableEntity entity)
    {
        var props = new Dictionary<string, object?>(entity, StringComparer.Ordinal);
        TableRow.RequireKind(props, entity.RowKey, KindMemory);
        var answer = SegmentedProperties.Read(props, "Answer");
        return new MemoryRecord(
            TableRow.GetString(props, "OwnerId", entity.RowKey),
            TableRow.GetString(props, "OperationId", entity.RowKey),
            TableRow.GetDto(props, "SourceScheduledUtc", entity.RowKey).UtcDateTime,
            TableRow.GetDto(props, "SourceExecutedUtc", entity.RowKey).UtcDateTime,
            TableRow.GetDto(props, "PublishedUtc", entity.RowKey),
            TableRow.GetString(props, "AnswerVersion", entity.RowKey),
            answer,
            DeliveryPlan.HashSource(answer),
            AnswerSourceBound.CountScalars(answer));
    }
}
