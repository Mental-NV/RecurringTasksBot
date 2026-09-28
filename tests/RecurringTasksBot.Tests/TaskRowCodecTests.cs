using Azure.Data.Tables;
using RecurringTasksBot.Application;
using RecurringTasksBot.Infrastructure.Persistence;

namespace RecurringTasksBot.Tests;

// Phase 5 task row codec: definition fidelity plus scheduling state.
public sealed class TaskRowCodecTests
{
    private static TaskRecord FullRecord()
    {
        Assert.True(TaskDefinitionParser.TryParseCreate(
            """{"prompt": "Review the release checklist", "schedule": {"cron": "0 0 9 5 * *", "once": ["2026-12-08T09:00:00"]}, "timezone": "Europe/Moscow", "parameters": {"reasoningEffort": "high", "maxOccurrences": 5}}""",
            out var definition, out _));
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return new TaskRecord("u1", "task1", 111L, "recurring-task1", definition!, 3,
            TaskState.Active, null, now, 2,
            new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), now, now,
            ActiveClaim: new OccurrenceClaim(
                new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc), 3,
                "Review the release checklist", "None", "high", true, "7", now));
    }

    [Fact]
    public void RoundTrip_PreservesDefinitionAndExecutionState()
    {
        var record = FullRecord();
        var entity = TaskRowCodec.ToEntity(record);
        var parsed = TaskRowCodec.FromEntity("u1", "task1", entity);
        Assert.Equal(record.TaskId, parsed.TaskId);
        Assert.Equal(record.Revision, parsed.Revision);
        Assert.Equal(TaskState.Active, parsed.Status);
        Assert.Equal(record.WaterlineUtc, parsed.WaterlineUtc);
        Assert.Equal(2, parsed.StartedOccurrences);
        Assert.Equal(record.ExpiresAtUtc, parsed.ExpiresAtUtc);
        Assert.Equal("Europe/Moscow", parsed.Definition.Timezone);
        Assert.True(parsed.Definition.TimezoneExplicit);
        Assert.True(parsed.Definition.Schedule.OnceExplicit);
        Assert.Equal("0 0 9 5 * *", parsed.Definition.Schedule.Cron);
        Assert.Equal("2026-12-08T09:00:00", Assert.Single(parsed.Definition.Schedule.Once));
        Assert.Equal("high", parsed.Definition.Parameters.ReasoningEffort);
        Assert.Equal(5, parsed.Definition.Parameters.MaxOccurrences);
        Assert.Null(parsed.Definition.Parameters.MemoryMode);
        Assert.NotNull(parsed.ActiveClaim);
        var claim = parsed.ActiveClaim!;
        Assert.Equal("Review the release checklist", claim.Prompt);
        Assert.Equal("high", claim.ReasoningEffort);
        Assert.Equal("7", claim.DefaultsRevision);
    }

    [Fact]
    public void RoundTrip_PreservesClaimRetryNote()
    {
        var retryAt = new DateTime(2026, 1, 1, 9, 5, 0, DateTimeKind.Utc);
        var record = FullRecord() with
        {
            ActiveClaim = FullRecord().ActiveClaim! with
            {
                FailedAttempts = 2,
                NextRetryUtc = retryAt,
            },
        };
        var parsed = TaskRowCodec.FromEntity("u1", "task1", TaskRowCodec.ToEntity(record));
        Assert.NotNull(parsed.ActiveClaim);
        var claim = parsed.ActiveClaim!;
        Assert.Equal(2, claim.FailedAttempts);
        Assert.Equal(retryAt, claim.NextRetryUtc);
    }

    [Fact]
    public void CommandRow_RoundTrips()
    {
        var command = new TaskAppliedCommand("u1", 7, "task1", 2,
            new DateTime(2026, 1, 1, 0, 5, 0, DateTimeKind.Utc));
        var entity = TaskRowCodec.ToCommandEntity(command);
        Assert.Equal("taskcmd_task1_7", entity.RowKey);
        var parsed = TaskRowCodec.FromCommandEntity("u1", "task1", 7, entity);
        Assert.NotNull(parsed);
        Assert.Equal(command, parsed!);
    }

    [Fact]
    public void RoundTrip_PreservesUpdateIdAndClaimSchedule()
    {
        var record = FullRecord() with
        {
            LastAppliedUpdateId = 42,
            ActiveClaim = FullRecord().ActiveClaim! with
            {
                ScheduleCron = "0 0 9 * * *",
                Timezone = "Europe/Moscow",
                ScheduleOnce = ["2026-12-08T09:00:00"],
            },
        };
        var parsed = TaskRowCodec.FromEntity("u1", "task1", TaskRowCodec.ToEntity(record));
        Assert.Equal(42L, parsed.LastAppliedUpdateId);
        Assert.NotNull(parsed.ActiveClaim);
        var claim = parsed.ActiveClaim!;
        Assert.Equal("0 0 9 * * *", claim.ScheduleCron);
        Assert.Equal("Europe/Moscow", claim.Timezone);
        Assert.Equal(["2026-12-08T09:00:00"], claim.ScheduleOnce);
    }

    [Fact]
    public void OldRow_WithoutUpdateIdOrSchedule_ReadsDefaults()
    {
        var entity = TaskRowCodec.ToEntity(FullRecord());
        entity.Remove("ClaimScheduleCron");
        var old = TaskRowCodec.FromEntity("u1", "task1", entity).ActiveClaim;
        Assert.NotNull(old);
        var claim = old!;
        Assert.Null(claim.ScheduleCron);
        Assert.Null(claim.Timezone);
        Assert.Null(claim.ScheduleOnce);
    }

    [Fact]
    public void OldRow_WithoutRetryColumns_ReadsNoNote()
    {
        var entity = TaskRowCodec.ToEntity(FullRecord());
        entity.Remove("ClaimFailedAttempts");
        entity.Remove("ClaimNextRetryUtc");
        var old = TaskRowCodec.FromEntity("u1", "task1", entity).ActiveClaim;
        Assert.NotNull(old);
        var claim = old!;
        Assert.Equal(0, claim.FailedAttempts);
        Assert.Null(claim.NextRetryUtc);
    }

    [Fact]
    public void RoundTrip_MinimalDefinition_OmitsOptionals()
    {
        var record = TestRecords.Task(owner: "u1", id: "task9");
        var parsed = TaskRowCodec.FromEntity("u1", "task9", TaskRowCodec.ToEntity(record));
        Assert.Equal("UTC", parsed.Definition.Timezone);
        Assert.False(parsed.Definition.TimezoneExplicit);
        Assert.Null(parsed.ExpiresAtUtc);
        Assert.Null(parsed.ActiveClaim);
        Assert.Null(parsed.StopReason);
    }

    [Fact]
    public void StopFields_RoundTrip()
    {
        var record = FullRecord() with
        {
            Status = TaskState.Completed,
            StopReason = "max_occurrences",
            StopReasonAtUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var parsed = TaskRowCodec.FromEntity("u1", "task1", TaskRowCodec.ToEntity(record));
        Assert.Equal(TaskState.Completed, parsed.Status);
        Assert.Equal("max_occurrences", parsed.StopReason);
        Assert.Equal(record.StopReasonAtUtc, parsed.StopReasonAtUtc);
    }

    [Fact]
    public void CorruptDefinition_FailsIntegrity()
    {
        var entity = TaskRowCodec.ToEntity(FullRecord());
        entity["Def_0000"] = "not json";
        Assert.Throws<PayloadIntegrityException>(() => TaskRowCodec.FromEntity("u1", "task1", entity));
    }
}
