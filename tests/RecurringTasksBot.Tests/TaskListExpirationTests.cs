using System.Text.Json;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Tests;

public sealed class TaskListExpirationTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    private static TaskRecord Limited(int? count = null, DateTime? deadline = null,
        int started = 0, string timezone = "UTC")
    {
        var task = TestRecords.Task(timezone: timezone);
        return task with
        {
            Definition = task.Definition with
            {
                Parameters = task.Definition.Parameters with { MaxOccurrences = count },
            },
            StartedOccurrences = started,
            ExpiresAtUtc = deadline,
        };
    }

    private static DateTime? Expire(TaskRecord task, DateTime? now = null) =>
        Assert.Single(TaskListBuilder.BuildPage([task], null, now ?? Now, 1)).ExpiresUtc;

    [Fact]
    public void NoLimits_HasNoExpiration()
    {
        Assert.Null(Expire(Limited()));
    }

    [Fact]
    public void ExplicitExpiration_IsShownEvenAfterItPasses()
    {
        Assert.Equal(Now.AddDays(2), Expire(Limited(deadline: Now.AddDays(2))));
        Assert.Equal(Now.AddDays(-1), Expire(Limited(10, Now.AddDays(-1))));
    }

    [Fact]
    public void Count_UsesRemainingSlotsAndEarlierLimit()
    {
        var projected = Now.Date.AddDays(1).AddHours(9);
        Assert.Equal(projected, Expire(Limited(5, started: 3)));
        Assert.Equal(Now.AddHours(2), Expire(Limited(5, Now.AddHours(2), 3)));
        Assert.Equal(projected, Expire(Limited(5, Now.AddDays(10), 3)));
        Assert.Equal(projected, Expire(Limited(5, projected, 3)));
    }

    [Fact]
    public void CatchUp_OnlyLatestMissedRunConsumesASlot()
    {
        var now = Now.AddDays(10);
        Assert.Equal(now, Expire(Limited(1), now));
        Assert.Equal(now.Date.AddHours(9), Expire(Limited(2), now));
    }

    [Theory]
    [InlineData(TaskActivityKind.Claimed)]
    [InlineData(TaskActivityKind.RetryWait)]
    [InlineData(TaskActivityKind.RecoveringWithPending)]
    [InlineData(TaskActivityKind.UnknownLease)]
    public void RetainedClaim_DoesNotConsumeAnotherSlot(TaskActivityKind activity)
    {
        var scheduled = Now.Date.AddHours(9);
        var task = Limited(2, started: 1) with
        {
            ActiveClaim = new OccurrenceClaim(scheduled, 1, "Task", "None", "low", false, "1", scheduled),
        };
        Assert.Equal(scheduled.AddDays(1), TaskListBuilder.ComputeExpiration(
            new TaskListItem(task, activity), scheduled.AddMinutes(10)));
    }

    [Fact]
    public void PendingReplacement_ProjectsAfterActivationBoundary()
    {
        var boundary = Now.AddDays(5);
        var task = Limited(2, started: 1) with { PendingActivationUtc = boundary };
        Assert.Equal(boundary.Date.AddHours(9), Expire(task));
    }

    [Fact]
    public void ReachedCount_UsesActualClosureAndEarlierExpiration()
    {
        var task = Limited(2, Now.AddDays(1), started: 2) with
        {
            CountClosedAtUtc = Now.AddHours(-1),
            Status = TaskState.Completed,
        };
        Assert.Equal(Now.AddHours(-1), Expire(task));
        Assert.Equal(Now.AddHours(-2), Expire(task with { ExpiresAtUtc = Now.AddHours(-2) }));
    }

    [Fact]
    public void FiniteSchedule_OnlyProjectsCountWhenEnoughOccurrencesRemain()
    {
        var task = Limited(2);
        task = task with
        {
            Definition = task.Definition with
            {
                Schedule = new TaskSchedule(null, ["2026-01-02T09:00:00"]),
            },
        };
        Assert.Null(Expire(task));
        Assert.Equal(Now.AddDays(3), Expire(task with { ExpiresAtUtc = Now.AddDays(3) }));
    }

    [Theory]
    [InlineData(TaskState.Completed)]
    [InlineData(TaskState.Failed)]
    public void StoppedTasks_DoNotProjectUnstartedOccurrences(TaskState status)
    {
        Assert.Null(Expire(Limited(2) with { Status = status }));
    }

    [Fact]
    public void TableAndFallback_PlaceExpirationBetweenNextAndPromptInLocalTime()
    {
        var deadline = new DateTime(2026, 12, 31, 22, 0, 0, DateTimeKind.Utc);
        var task = Limited(deadline: deadline, timezone: "Europe/Moscow");
        var rows = TaskListBuilder.BuildPage([task], null, Now, 1);
        var (json, stacked) = TaskListFormatter.FormatTable(rows, 1, Now);
        using var doc = JsonDocument.Parse(json);
        var table = doc.RootElement;
        Assert.Equal(5, table[0][0].GetProperty("colspan").GetInt32());
        Assert.Equal(["ID", "Status", "Next", "Expire", "Prompt"],
            table[1].EnumerateArray().Select(cell => cell.GetProperty("text").GetString()));
        Assert.Equal("01 Jan 2027 01:00", table[2][3].GetProperty("text").GetString());
        Assert.Equal("Water the plants", table[2][4].GetProperty("text").GetString());
        Assert.Contains(" · Expire 01 Jan 2027 01:00 · Water the plants", stacked);
        Assert.Contains(" | 01 Jan 2027 01:00 | Water the plants",
            TaskListFormatter.FormatPage(rows, 1, Now, compact: false));
        var unlimited = TaskListBuilder.BuildPage([Limited()], null, Now, 1);
        Assert.Contains("Expire —", TaskListFormatter.FormatPage(unlimited, 1, Now, compact: true));
    }
}
