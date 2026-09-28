// Phase 5 task row codec: one entity per task carrying the explicit
// definition as segmented JSON plus system-managed scheduling state.
// Required properties are validated on read, never defaulted; absent
// optional deadlines/claims are omitted properties, never zero values.
using Azure.Data.Tables;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Infrastructure.Persistence;

public static class TaskRowCodec
{
    public const string Kind = "task";

    public static TableEntity ToEntity(TaskRecord record)
    {
        var props = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["EntityKind"] = Kind,
            ["SchemaVersion"] = StorageLimits.SchemaVersion,
            ["ChatId"] = record.ChatId,
            ["InstanceId"] = record.InstanceId,
            ["Revision"] = record.Revision,
            ["Status"] = TaskStateNames.ToName(record.Status),
            ["WaterlineUtc"] = ToDto(record.WaterlineUtc),
            ["StartedOccurrences"] = record.StartedOccurrences,
            ["StopReason"] = record.StopReason ?? string.Empty,
            ["CreatedUtc"] = ToDto(record.CreatedAtUtc),
            ["UpdatedUtc"] = ToDto(record.UpdatedAtUtc),
        };
        WriteOptionalDto(props, "ExpiresAtUtc", record.ExpiresAtUtc);
        WriteOptionalDto(props, "StopReasonAtUtc", record.StopReasonAtUtc);
        WriteOptionalDto(props, "PendingActivationUtc", record.PendingActivationUtc);
        WriteOptionalDto(props, "CountClosedAtUtc", record.CountClosedAtUtc);
        if (record.LastAppliedUpdateId is not null)
            props["LastAppliedUpdateId"] = record.LastAppliedUpdateId.Value;
        props["HasClaim"] = record.ActiveClaim is not null;
        if (record.ActiveClaim is not null)
        {
            var claim = record.ActiveClaim;
            props["ClaimScheduledUtc"] = ToDto(claim.ScheduledUtc);
            props["ClaimTaskRevision"] = claim.TaskRevision;
            props["ClaimMemoryMode"] = claim.MemoryMode;
            props["ClaimReasoningEffort"] = claim.ReasoningEffort;
            props["ClaimWebSearch"] = claim.WebSearch;
            props["ClaimDefaultsRevision"] = claim.DefaultsRevision;
            props["ClaimClaimedAtUtc"] = ToDto(claim.ClaimedAtUtc);
            props["ClaimFailedAttempts"] = claim.FailedAttempts;
            WriteOptionalDto(props, "ClaimNextRetryUtc", claim.NextRetryUtc);
            if (claim.ScheduleCron is not null)
                props["ClaimScheduleCron"] = claim.ScheduleCron;
            if (claim.Timezone is not null)
                props["ClaimTimezone"] = claim.Timezone;
            SegmentedProperties.Write(props, "ClaimPrompt", claim.Prompt);
            if (claim.ScheduleOnce is not null && claim.ScheduleOnce.Count > 0)
                SegmentedProperties.Write(props, "ClaimOnce",
                    string.Join("\n", claim.ScheduleOnce));
        }

        SegmentedProperties.Write(props, "Def", TaskJsonRenderer.RenderExplicit(record.Definition));
        TableStorageLimits.CheckEntityFits(props, "task publication");
        var entity = new TableEntity(record.OwnerId, TableRowKeys.Task(record.TaskId));
        foreach (var (key, value) in props)
            entity[key] = value;
        return entity;
    }

    public static TaskRecord FromEntity(string ownerId, string taskId, TableEntity entity)
    {
        var props = new Dictionary<string, object?>(entity, StringComparer.Ordinal);
        TableRow.RequireKind(props, entity.RowKey, Kind);
        var definitionJson = SegmentedProperties.Read(props, "Def");
        if (!TaskDefinitionParser.TryParseCreate(definitionJson,
                out var definition, out _) || definition is null)
            throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt,
                $"{entity.RowKey} has an unusable task definition");
        return new TaskRecord(
            ownerId,
            taskId,
            TableRow.GetInt64(props, "ChatId", entity.RowKey),
            TableRow.GetString(props, "InstanceId", entity.RowKey),
            definition,
            TableRow.GetInt(props, "Revision", entity.RowKey),
            TaskStateNames.Parse(TableRow.GetString(props, "Status", entity.RowKey)),
            TableRow.NullIfEmpty(TableRow.GetString(props, "StopReason", entity.RowKey)),
            TableRow.GetDto(props, "WaterlineUtc", entity.RowKey).UtcDateTime,
            TableRow.GetInt(props, "StartedOccurrences", entity.RowKey),
            ReadOptionalDto(props, "ExpiresAtUtc"),
            TableRow.GetDto(props, "CreatedUtc", entity.RowKey).UtcDateTime,
            TableRow.GetDto(props, "UpdatedUtc", entity.RowKey).UtcDateTime,
            ReadOptionalDto(props, "StopReasonAtUtc"),
            ReadOptionalDto(props, "PendingActivationUtc"),
            ReadClaim(props, entity.RowKey),
            ReadOptionalInt64(props, "LastAppliedUpdateId"),
            ReadOptionalDto(props, "CountClosedAtUtc"));
    }

    private static OccurrenceClaim? ReadClaim(
        Dictionary<string, object?> props, string rowKey)
    {
        if (!props.TryGetValue("HasClaim", out var flag) || flag is not true)
            return null;
        // Retry-note columns postdate the claim row: absent means no note.
        var attempts = props.TryGetValue("ClaimFailedAttempts", out var attemptsValue) &&
            SegmentedProperties.TryToInt(attemptsValue, out var attemptsParsed)
            ? attemptsParsed : 0;
        return new OccurrenceClaim(
            TableRow.GetDto(props, "ClaimScheduledUtc", rowKey).UtcDateTime,
            TableRow.GetInt(props, "ClaimTaskRevision", rowKey),
            SegmentedProperties.Read(props, "ClaimPrompt"),
            TableRow.GetString(props, "ClaimMemoryMode", rowKey),
            TableRow.GetString(props, "ClaimReasoningEffort", rowKey),
            props.TryGetValue("ClaimWebSearch", out var web) && web is true,
            TableRow.GetString(props, "ClaimDefaultsRevision", rowKey),
            TableRow.GetDto(props, "ClaimClaimedAtUtc", rowKey).UtcDateTime,
            attempts,
            ReadOptionalDto(props, "ClaimNextRetryUtc"),
            props.TryGetValue("ClaimScheduleCron", out var cronValue) &&
                cronValue is string cronText && cronText.Length > 0 ? cronText : null,
            props.TryGetValue("ClaimTimezone", out var zoneValue) &&
                zoneValue is string zoneText && zoneText.Length > 0 ? zoneText : null,
            ReadClaimOnce(props));
    }

    private static DateTimeOffset ToDto(DateTime utc) =>
        new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    private static void WriteOptionalDto(
        IDictionary<string, object?> props, string name, DateTime? utc)
    {
        if (utc is not null)
            props[name] = ToDto(utc.Value);
    }

    private static IReadOnlyList<string>? ReadClaimOnce(IReadOnlyDictionary<string, object?> props)
    {
        if (!props.TryGetValue("ClaimOnce_Count", out _))
            return null;
        var joined = SegmentedProperties.Read(props, "ClaimOnce");
        return string.IsNullOrEmpty(joined)
            ? []
            : joined.Split('\n');
    }

    public static TableEntity ToCommandEntity(TaskAppliedCommand command) =>
        new(
            command.OwnerId,
            TableRowKeys.TaskCommand(command.TaskId, command.UpdateId))
        {
            ["TaskId"] = command.TaskId,
            ["UpdateId"] = command.UpdateId,
            ["ResultRevision"] = command.ResultRevision,
            ["AppliedAtUtc"] = new DateTimeOffset(
                DateTime.SpecifyKind(command.AppliedAtUtc, DateTimeKind.Utc)),
            ["SchemaVersion"] = StorageLimits.SchemaVersion,
        };

    public static TaskAppliedCommand? FromCommandEntity(
        string ownerId, string taskId, long updateId, TableEntity entity)
    {
        var props = new Dictionary<string, object?>(entity, StringComparer.Ordinal);
        return new TaskAppliedCommand(
            ownerId,
            updateId,
            TableRow.GetString(props, "TaskId", entity.RowKey),
            TableRow.GetInt(props, "ResultRevision", entity.RowKey),
            TableRow.GetDto(props, "AppliedAtUtc", entity.RowKey).UtcDateTime);
    }

    private static long? ReadOptionalInt64(IDictionary<string, object?> props, string name)
    {
        if (!props.TryGetValue(name, out var value) || value is null)
            return null;
        if (value is long l)
            return l;
        if (value is int i)
            return i;
        throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt,
            $"optional '{name}' is not an integer");
    }

    private static DateTime? ReadOptionalDto(IDictionary<string, object?> props, string name)
    {
        if (!props.TryGetValue(name, out var value))
            return null;
        if (value is DateTimeOffset dto)
            return dto.UtcDateTime;
        if (value is DateTime dt)
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt,
            $"property '{name}' is not a date-time");
    }
}
