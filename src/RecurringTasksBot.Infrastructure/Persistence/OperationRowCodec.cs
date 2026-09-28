// Single operation entity codec, used by both the operation store and
// the occurrence transactions. The accepted prompt is stored as numbered
// properties on the operation entity via the segmented codec; there are
// no separate operation text chunk rows or readers. Rows without
// EntityKind "operation" and SchemaVersion = 1 fail as integrity errors,
// never as defaults or partial text.
using Azure.Data.Tables;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Infrastructure.Persistence;

public static class OperationRowCodec
{
    public const string Kind = "operation";

    public static TableEntity ToEntity(OperationRecord record)
    {
        var props = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["EntityKind"] = Kind,
            ["SchemaVersion"] = StorageLimits.SchemaVersion,
            ["ChatId"] = record.ChatId,
            ["Cron"] = record.CronExpression,
            ["Status"] = OperationStatusNames.ToName(record.Status),
            ["InstanceId"] = record.InstanceId ?? string.Empty,
            ["FailureSummary"] = record.FailureSummary ?? string.Empty,
            ["CreatedUtc"] = record.CreatedUtc,
            ["UpdatedUtc"] = record.UpdatedUtc,
            ["ScheduleTimezone"] = record.ScheduleTimezone,
        };
        SegmentedProperties.Write(props, "Text", record.Text);
        TableStorageLimits.CheckEntityFits(props, "operation publication");
        var entity = new TableEntity(record.OwnerId, TableRowKeys.Operation(record.OperationId));
        foreach (var (key, value) in props)
            entity[key] = value;
        return entity;
    }

    public static OperationRecord FromEntity(string ownerId, string operationId, TableEntity entity)
    {
        var props = new Dictionary<string, object?>(entity, StringComparer.Ordinal);
        TableRow.RequireKind(props, entity.RowKey, Kind);
        var instanceId = TableRow.GetString(props, "InstanceId", entity.RowKey);
        var failure = TableRow.GetString(props, "FailureSummary", entity.RowKey);
        // The zone column postdates V1 rows: absent means UTC.
        var timezone = props.TryGetValue("ScheduleTimezone", out var zoneValue) &&
            zoneValue is string zoneText && zoneText.Length > 0
            ? zoneText : "UTC";
        return new OperationRecord(
            ownerId,
            operationId,
            TableRow.GetInt64(props, "ChatId", entity.RowKey),
            TableRow.GetString(props, "Cron", entity.RowKey),
            SegmentedProperties.Read(props, "Text"),
            OperationStatusNames.Parse(TableRow.GetString(props, "Status", entity.RowKey)),
            string.IsNullOrEmpty(instanceId) ? null : instanceId,
            string.IsNullOrEmpty(failure) ? null : failure,
            TableRow.GetDto(props, "CreatedUtc", entity.RowKey),
            TableRow.GetDto(props, "UpdatedUtc", entity.RowKey),
            timezone);
    }
}
