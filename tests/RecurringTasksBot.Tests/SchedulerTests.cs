using RecurringTasksBot.Core;

namespace RecurringTasksBot.Tests;

public sealed class SchedulerTests
{
    private static DateTime Dt(int y, int mo, int d, int h, int mi, int s = 0) =>
        new(y, mo, d, h, mi, s, DateTimeKind.Utc);

    [Fact]
    public void NoMissedOccurrences_NoCatchUp()
    {
        var s = NcrontabSchedule.Parse("0 0 9 * * *");
        Assert.Null(Scheduler.SingleCatchUp(s, Dt(2026, 1, 5, 9, 0), Dt(2026, 1, 5, 10, 0)));
    }

    [Fact]
    public void SingleMissedOccurrence_CatchesUpOnce()
    {
        var s = NcrontabSchedule.Parse("0 0 9 * * *");
        Assert.Equal(Dt(2026, 1, 6, 9, 0),
            Scheduler.SingleCatchUp(s, Dt(2026, 1, 5, 9, 0), Dt(2026, 1, 6, 10, 0)));
    }

    [Fact]
    public void ManyMissedOccurrences_AtMostOneLate()
    {
        var s = NcrontabSchedule.Parse("0 0 * * * *"); // hourly
        var previous = Dt(2026, 1, 1, 0, 0);
        var now = Dt(2026, 1, 10, 0, 0); // ~216 missed
        var catchUp = Scheduler.SingleCatchUp(s, previous, now);

        Assert.NotNull(catchUp);
        Assert.True(catchUp <= now);
        // The single late send is the latest missed occurrence...
        Assert.Equal(Dt(2026, 1, 10, 0, 0), catchUp);
        // ...and advancing from it resumes strictly-future scheduling.
        var state = new RecurrenceState("inst", "op", "u", "0 0 * * * *", previous, 0);
        var advanced = Scheduler.AdvanceAfterOccurrence(state, s, catchUp.Value, now);
        Assert.True(advanced.NextScheduledUtc > now);
    }

    [Fact]
    public void Advance_RetainsInstanceId_AndIncrementsIndex()
    {
        var s = NcrontabSchedule.Parse("0 0 9 * * *");
        var state = new RecurrenceState("recurring-abc", "abc", "u", "0 0 9 * * *",
            Dt(2026, 1, 5, 9, 0), 3);
        var next = Scheduler.AdvanceAfterOccurrence(state, s,
            Dt(2026, 1, 5, 9, 0), Dt(2026, 1, 5, 9, 0, 1));

        Assert.Equal("recurring-abc", next.InstanceId); // ContinueAsNew keeps the ID
        Assert.Equal("abc", next.OperationId);
        Assert.Equal(4, next.OccurrenceIndex);
        Assert.Equal(Dt(2026, 1, 6, 9, 0), next.NextScheduledUtc);
    }

    [Fact]
    public void Advance_FromBehindNow_SkipsToFuture()
    {
        var s = NcrontabSchedule.Parse("0 0 * * * *");
        var state = new RecurrenceState("inst", "op", "u", "0 0 * * * *",
            Dt(2026, 1, 1, 0, 0), 0);
        var next = Scheduler.AdvanceAfterOccurrence(state, s,
            Dt(2026, 1, 1, 0, 0), Dt(2026, 1, 5, 12, 30));
        Assert.Equal(Dt(2026, 1, 5, 13, 0), next.NextScheduledUtc);
    }

    [Fact]
    public void ChainedAdvances_StayStrictlyIncreasing()
    {
        var s = NcrontabSchedule.Parse("0 */15 * * * *");
        var state = new RecurrenceState("inst", "op", "u", "0 */15 * * * *",
            Dt(2026, 1, 5, 9, 0), 0);
        var now = Dt(2026, 1, 5, 9, 0, 1);
        for (var i = 0; i < 10; i++)
        {
            var prev = state.NextScheduledUtc;
            state = Scheduler.AdvanceAfterOccurrence(state, s, state.NextScheduledUtc, now);
            Assert.True(state.NextScheduledUtc > prev);
            now = state.NextScheduledUtc.AddSeconds(1);
        }

        Assert.Equal(10, state.OccurrenceIndex);
    }

    [Fact]
    public void IsDue_TriggersAtScheduledTime()
    {
        var state = new RecurrenceState("i", "o", "u", "c", Dt(2026, 1, 5, 9, 0), 0);
        Assert.False(Scheduler.IsDue(state, Dt(2026, 1, 5, 8, 59, 59)));
        Assert.True(Scheduler.IsDue(state, Dt(2026, 1, 5, 9, 0)));
    }
}
