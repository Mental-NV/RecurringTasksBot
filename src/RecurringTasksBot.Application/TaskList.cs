// Phase 5 compact /list (docs/Spec.Phase5.md section 1): owner-scoped,
// ten per page, creation-descending, grouped by timezone. Next is a schedule
// preview from the merged schedule, waterline, and stop limits.
using System.Globalization;
using System.Text;
using System.Text.Json;

using NodaTime;

namespace RecurringTasksBot.Application;

public enum TaskActivityKind
{
    Idle,
    Claimed,
    RetryWait,
    RecoveringWithPending,
    UnknownLease,
    OrchestrationFailed,
}

public sealed record TaskListItem(
    TaskRecord Record,
    TaskActivityKind Activity = TaskActivityKind.Idle,
    DateTime? ClaimedUtc = null,
    DateTime? ActivationBoundaryUtc = null);

public sealed record TaskListRow(
    string IdPrefix,
    string Status,
    DateTime? NextUtc,
    DateTime? ExpiresUtc,
    string PromptPreview,
    string Timezone);

public static class TaskListBuilder
{
    public const int PageSize = 10;
    public const int MaxPreviewWords = 3;
    public const int MaxPreviewChars = 18;

    public static IReadOnlyList<TaskListRow> BuildPage(
        IReadOnlyList<TaskRecord> ownedTasks,
        IReadOnlyDictionary<string, TaskListItem>? activity,
        DateTime nowUtc, int page)
    {
        var visible = ownedTasks
            .Where(t => t.Status != TaskState.Deleted)
            .OrderByDescending(t => t.CreatedAtUtc)
            .ThenBy(t => t.TaskId, StringComparer.Ordinal)
            .ToList();
        var prefixes = ComputePrefixes(visible);
        return visible
            .Skip((page - 1) * PageSize)
            .Take(PageSize)
            .Select(record =>
            {
                var item = new TaskListItem(record);
                if (activity is not null && activity.TryGetValue(record.TaskId, out var listed) &&
                    listed is not null)
                    item = listed;
                var next = ComputeNext(item, nowUtc);
                return new TaskListRow(
                    prefixes[record.TaskId],
                    DisplayStatus(item, next),
                    next,
                    ComputeExpiration(item, nowUtc),
                    PromptPreview(record.Definition.Prompt ?? string.Empty),
                    record.Definition.Timezone);
            })
            .ToList();
    }

    public static Dictionary<string, string> ComputePrefixes(IReadOnlyList<TaskRecord> visible)
    {
        var ids = visible.Select(t => t.TaskId).ToList();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var length = Math.Min(TaskCommandParser.MinIdPrefixLength, id.Length);
            while (length < id.Length &&
                ids.Any(other => !other.Equals(id, StringComparison.Ordinal) &&
                    other.StartsWith(id[..length], StringComparison.Ordinal)))
                length++;
            result[id] = id[..length];
        }

        return result;
    }

    public static DateTime? ComputeNext(TaskListItem item, DateTime nowUtc)
    {
        var record = item.Record;
        if (record.Status is TaskState.Completed or TaskState.Failed)
            return null;
        if (record.Definition.Timezone is null ||
            !TaskTimezones.TryResolve(record.Definition.Timezone, out var zone) || zone is null)
            return null;

        // Stop limits close admission before any preview.
        if (record.Definition.Parameters.MaxOccurrences is not null &&
            record.StartedOccurrences >= record.Definition.Parameters.MaxOccurrences.Value)
            return null;
        if (record.ExpiresAtUtc is not null && nowUtc >= record.ExpiresAtUtc.Value)
            return null;

        var lower = record.WaterlineUtc;
        if (item.Activity == TaskActivityKind.Claimed && item.ClaimedUtc is not null)
            lower = Max(lower, item.ClaimedUtc.Value);
        if (item.ActivationBoundaryUtc is not null)
            lower = Max(lower, item.ActivationBoundaryUtc.Value);

        var latestDue = TaskScheduleResolver.GetLatestDueMergedUtc(
            record.Definition.Schedule.Cron, record.Definition.Schedule.Once, zone, nowUtc, lower);
        DateTime? candidate = null;
        if (latestDue is not null &&
            (record.ExpiresAtUtc is null || latestDue.Value < record.ExpiresAtUtc.Value))
            candidate = latestDue;
        else
        {
            var boundary = Max(lower, nowUtc);
            var next = TaskScheduleResolver.GetNextMergedUtc(
                record.Definition.Schedule.Cron, record.Definition.Schedule.Once, zone, boundary);
            if (next is not null &&
                (record.ExpiresAtUtc is null || next.Value < record.ExpiresAtUtc.Value))
                candidate = next;
        }

        return candidate;
    }

    // Count limits reserve a slot at claim time. Project only the remaining
    // slots, excluding retained work even when its lease is retrying or stale.
    // Like Next, this is a schedule preview; future delays can move it.
    public static DateTime? ComputeExpiration(TaskListItem item, DateTime nowUtc)
    {
        var record = item.Record;
        var deadline = record.ExpiresAtUtc;
        var max = record.Definition.Parameters.MaxOccurrences;
        if (max is null)
            return deadline;
        var remaining = max.Value - record.StartedOccurrences;
        if (remaining <= 0)
        {
            var closed = record.CountClosedAtUtc ?? record.StopReasonAtUtc ??
                record.ActiveClaim?.ClaimedAtUtc ?? record.UpdatedAtUtc;
            return deadline is null || closed < deadline.Value ? closed : deadline;
        }
        if (record.Status != TaskState.Active || (deadline is not null && deadline <= nowUtc) ||
            !TaskTimezones.TryResolve(record.Definition.Timezone, out var zone) || zone is null)
            return deadline;

        var lower = record.WaterlineUtc;
        if (record.ActiveClaim is not null)
            lower = Max(lower, record.ActiveClaim.ScheduledUtc);
        if (item.ClaimedUtc is not null)
            lower = Max(lower, item.ClaimedUtc.Value);
        if (record.PendingActivationUtc is not null)
            lower = Max(lower, record.PendingActivationUtc.Value);
        if (item.ActivationBoundaryUtc is not null)
            lower = Max(lower, item.ActivationBoundaryUtc.Value);

        var schedule = record.Definition.Schedule;
        var due = TaskScheduleResolver.GetLatestDueMergedUtc(
            schedule.Cron, schedule.Once, zone, nowUtc, lower);
        if (due is not null && (deadline is null || due < deadline))
        {
            // Only the latest missed occurrence consumes a slot, at admission now.
            if (--remaining == 0)
                return nowUtc;
        }
        var projected = TaskScheduleResolver.GetNthMergedUtc(
            schedule.Cron, schedule.Once, zone, Max(lower, nowUtc), remaining, deadline);
        return projected ?? deadline;
    }

    // Durable running-awareness for one claimed task, derived from
    // occurrence/lease state — never Durable's generic running status,
    // which stays "Running" through timer waits. Only a confirmed live
    // lease reads executing: a claim over finished or terminally recorded
    // work is unknown, a future retry note is retrying, and anything else
    // with pending work is recovering.
    public static TaskActivityKind ResolveActivity(
        TaskRecord record, DeliveryReceipt? receipt, DateTime nowUtc)
    {
        var claim = record.ActiveClaim;
        if (claim is null)
            return TaskActivityKind.Idle;
        if (claim.ScheduledUtc <= record.WaterlineUtc)
            return TaskActivityKind.UnknownLease;
        if (claim.NextRetryUtc is not null && claim.NextRetryUtc.Value > nowUtc)
            return TaskActivityKind.RetryWait;
        if (receipt is not null && receipt.ClaimId is not null &&
            new DateTimeOffset(nowUtc, TimeSpan.Zero) - receipt.UpdatedUtc <=
                OccurrenceExecution.StaleClaimAfter)
            return TaskActivityKind.Claimed;
        if (receipt is not null && OccurrenceExecution.IsTerminal(receipt))
            return TaskActivityKind.UnknownLease;
        return TaskActivityKind.RecoveringWithPending;
    }

    public static string DisplayStatus(TaskListItem item, DateTime? next)
    {
        if (item.Record.Status == TaskState.Completed)
            return TaskStateNames.Completed;
        if (item.Record.Status == TaskState.Failed)
            return TaskStateNames.Failed;
        return item.Activity switch
        {
            TaskActivityKind.Claimed => "executing",
            TaskActivityKind.RetryWait => "retrying",
            TaskActivityKind.RecoveringWithPending => "recovering",
            TaskActivityKind.UnknownLease => "unknown",
            TaskActivityKind.OrchestrationFailed => TaskStateNames.Failed,
            _ => TaskStateNames.Active,
        };
    }

    public static string PromptPreview(string prompt)
    {
        var normalized = string.Join(' ',
            prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var words = normalized.Split(' ');
        var head = string.Join(' ', words.Take(MaxPreviewWords));
        var truncated = words.Length > MaxPreviewWords || head.Length < normalized.Length;
        var runes = head.EnumerateRunes().ToArray();
        if (runes.Length > MaxPreviewChars)
            return string.Concat(runes[..MaxPreviewChars].Select(r => r.ToString())) + "…";
        return truncated ? head + "…" : head;
    }

    private static DateTime Max(DateTime a, DateTime b) => a >= b ? a : b;
}

public static class TaskListFormatter
{
    // Native-table cells: one spanning label row plus a repeated header
    // per timezone group, then the data rows. The stacked (compact) text
    // stays the delivery fallback and the reply record.
    public static (string CellsJson, string Stacked) FormatTable(
        IReadOnlyList<TaskListRow> rows, int page, DateTime nowUtc)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var group in rows.GroupBy(r => r.Timezone))
            {
                var currentYear = group.Any() ? CurrentLocalYear(group.Key, nowUtc) : nowUtc.Year;
                writer.WriteStartArray();
                WriteCell(writer, $"{group.Key} ({currentYear})", colspan: 5);
                writer.WriteEndArray();
                writer.WriteStartArray();
                WriteCell(writer, "ID", isHeader: true);
                WriteCell(writer, "Status", isHeader: true);
                WriteCell(writer, "Next", isHeader: true);
                WriteCell(writer, "Expire", isHeader: true);
                WriteCell(writer, "Prompt", isHeader: true);
                writer.WriteEndArray();
                foreach (var row in group)
                {
                    var next = row.NextUtc is null
                        ? "—"
                        : FormatInstant(row.NextUtc.Value, row.Timezone, currentYear);
                    writer.WriteStartArray();
                    WriteCell(writer, row.IdPrefix);
                    WriteCell(writer, row.Status);
                    WriteCell(writer, next);
                    WriteCell(writer, row.ExpiresUtc is null
                        ? "—"
                        : FormatInstant(row.ExpiresUtc.Value, row.Timezone, currentYear));
                    WriteCell(writer, row.PromptPreview);
                    writer.WriteEndArray();
                }
            }
            writer.WriteEndArray();
        }

        return (Encoding.UTF8.GetString(stream.ToArray()),
            FormatPage(rows, page, nowUtc, compact: true));
    }

    private static void WriteCell(Utf8JsonWriter writer, string text,
        bool isHeader = false, int colspan = 1)
    {
        writer.WriteStartObject();
        writer.WriteString("text", text);
        if (isHeader)
            writer.WriteBoolean("is_header", true);
        if (colspan > 1)
            writer.WriteNumber("colspan", colspan);
        writer.WriteString("align", "left");
        writer.WriteString("valign", "top");
        writer.WriteEndObject();
    }

    public static string FormatPage(IReadOnlyList<TaskListRow> rows, int page, DateTime nowUtc, bool compact)
    {
        if (rows.Count == 0)
            return page <= 1
                ? "No recurring tasks yet. Create one with /create <JSON object>."
                : $"No tasks on page {page}.";

        var output = new StringBuilder();
        foreach (var group in rows.GroupBy(r => r.Timezone))
        {
            var currentYear = group.Any() ? CurrentLocalYear(group.Key, nowUtc) : nowUtc.Year;
            output.AppendLine($"{group.Key} ({currentYear})");
            if (!compact)
                output.AppendLine("ID | Status | Next | Expire | Prompt");
            foreach (var row in group)
            {
                var next = row.NextUtc is null
                    ? "—"
                    : FormatInstant(row.NextUtc.Value, row.Timezone, currentYear);
                var expire = row.ExpiresUtc is null
                    ? "—"
                    : FormatInstant(row.ExpiresUtc.Value, row.Timezone, currentYear);
                if (compact)
                    output.AppendLine($"{row.IdPrefix} · {row.Status}")
                        .AppendLine($"Next {next} · Expire {expire} · {row.PromptPreview}");
                else
                    output.AppendLine($"{row.IdPrefix} | {row.Status} | {next} | {expire} | {row.PromptPreview}");
            }
        }

        return output.ToString().TrimEnd();
    }

    private static int CurrentLocalYear(string timezoneId, DateTime nowUtc)
    {
        if (TaskTimezones.TryResolve(timezoneId, out var zone) && zone is not null)
            return TaskTimezones.ToLocal(
                DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone).Year;
        return nowUtc.Year;
    }

    private static string FormatInstant(DateTime instantUtc, string timezoneId, int currentYear)
    {
        if (TaskTimezones.TryResolve(timezoneId, out var zone) && zone is not null)
        {
            var local = TaskTimezones.ToLocal(
                DateTime.SpecifyKind(instantUtc, DateTimeKind.Utc), zone);
            return local.Year == currentYear
                ? local.ToString("dd MMM HH:mm", CultureInfo.InvariantCulture)
                : local.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture);
        }

        return instantUtc.ToString("dd MMM HH:mm", CultureInfo.InvariantCulture);
    }
}
