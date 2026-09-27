// ContinueAsNew regression tests: RecurrenceState must survive a JSON
// round-trip byte-identically so persisted scheduling state — not a
// recomputed first occurrence — drives subsequent orchestrator invocations.
using System.Text.Json;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Tests;

public sealed class RecurrenceStateTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static DateTime Dt(int y, int mo, int d, int h, int mi, int s = 0) =>
        new(y, mo, d, h, mi, s, DateTimeKind.Utc);

    [Fact]
    public void RoundTrip_PreservesEveryField()
    {
        var state = new RecurrenceState("recurring-abc", "op-1", "42",
            "0 0 9 * * *", Dt(2026, 1, 5, 9, 0), 3);

        var restored = JsonSerializer.Deserialize<RecurrenceState>(
            JsonSerializer.Serialize(state, Json), Json);

        Assert.Equal(state, restored);
        Assert.Equal(DateTimeKind.Utc, restored!.NextScheduledUtc!.Value.Kind);
    }

    [Fact]
    public void RoundTrip_PreservesUninitializedNewOperation()
    {
        // The initial ContinueAsNew-agnostic input: no due time yet.
        var state = new RecurrenceState("recurring-abc", "op-1", "42",
            string.Empty, null, 0);

        var restored = JsonSerializer.Deserialize<RecurrenceState>(
            JsonSerializer.Serialize(state, Json), Json);

        Assert.Equal(state, restored);
        Assert.Null(restored!.NextScheduledUtc);
        Assert.False(Scheduler.IsDue(restored, Dt(2026, 9, 25, 9, 0)));
        Assert.True(Scheduler.IsDue(
            restored with { NextScheduledUtc = Dt(2026, 9, 25, 9, 0) },
            Dt(2026, 9, 25, 9, 0)));
    }

    [Fact]
    public void RoundTrip_PreservesAdvancedState()
    {
        var schedule = NcrontabSchedule.Parse("0 0 9 * * *");
        var state = new RecurrenceState("recurring-abc", "op-1", "42",
            "0 0 9 * * *", Dt(2026, 1, 5, 9, 0), 3);
        var advanced = Scheduler.AdvanceAfterOccurrence(
            state, schedule, Dt(2026, 1, 5, 9, 0), Dt(2026, 1, 5, 9, 0, 1));

        var restored = JsonSerializer.Deserialize<RecurrenceState>(
            JsonSerializer.Serialize(advanced, Json), Json);

        Assert.Equal(advanced, restored);
        Assert.Equal("recurring-abc", restored!.InstanceId);
        Assert.Equal(4, restored.OccurrenceIndex);
        Assert.True(restored.NextScheduledUtc > Dt(2026, 1, 5, 9, 0));
    }
}
