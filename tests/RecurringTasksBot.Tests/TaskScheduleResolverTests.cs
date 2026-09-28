using NodaTime;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Tests;

// Spec section 2 acceptance: month/year boundaries, leap years, DST
// gaps/folds, fractional-hour offsets, ordered unique UTC results,
// latest-only catch-up, collision dedup, boundary equality.
public sealed class TaskScheduleResolverTests
{
    private static DateTimeZone Zone(string id)
    {
        Assert.True(TaskTimezones.TryResolve(id, out var zone));
        return zone!;
    }

    private static DateTime Next(string cron, string zoneId, DateTime afterUtc)
    {
        var result = TaskScheduleResolver.GetNextCronUtc(
            NcrontabSchedule.Parse(cron), Zone(zoneId), afterUtc);
        Assert.NotNull(result);
        return result.Value;
    }

    [Fact]
    public void MoscowMonthlyFirst_StaysOnLocalDateAcrossUtcYearBoundary()
    {
        // Jan 1 00:30 MSK (+03:00) executes Dec 31 21:30Z: still January locally.
        var january = Next("0 30 0 1 * *", "Europe/Moscow",
            new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 12, 31, 21, 30, 0, DateTimeKind.Utc), january);
        // Mar 1 00:30 MSK executes Feb 28 21:30Z: still March locally.
        var march = Next("0 30 0 1 * *", "Europe/Moscow",
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 2, 28, 21, 30, 0, DateTimeKind.Utc), march);
    }

    [Fact]
    public void BerlinMonthlyFifth_KeepsLocalHourAsUtcOffsetChanges()
    {
        var zone = "Europe/Berlin";
        Assert.Equal(new DateTime(2026, 1, 5, 8, 0, 0, DateTimeKind.Utc),
            Next("0 0 9 5 * *", zone, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 7, 5, 7, 0, 0, DateTimeKind.Utc),
            Next("0 0 9 5 * *", zone, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void BerlinDstGap_SkipsOccurrence()
    {
        // 2026-03-29 02:30 does not exist in Berlin; the rule skips to Mar 30.
        var next = Next("0 30 2 * * *", "Europe/Berlin",
            new DateTime(2026, 3, 28, 2, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 3, 30, 0, 30, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void BerlinDstFold_RunsOnceAtEarlierUtcInstant()
    {
        // 2026-10-25 02:30 occurs twice; consume it at 00:30Z (UTC+2 side).
        var next = Next("0 30 2 * * *", "Europe/Berlin",
            new DateTime(2026, 10, 24, 1, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void KathmanduFractionalOffset_ResolvesThroughTzData()
    {
        var next = Next("0 0 9 * * *", "Asia/Kathmandu",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 1, 1, 3, 15, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Feb29_SkipsNonLeapYears()
    {
        var next = Next("0 0 9 29 2 *", "UTC", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2028, 2, 29, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void DayOfMonthOrWeekday_UsesOrSemantics()
    {
        // 2026-01-02 is a Friday: matches via weekday without the 13th.
        var next = Next("0 0 12 13 * 5", "UTC", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void NextResult_IsStrictlyAfterBoundary()
    {
        var exact = new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc);
        var next = Next("0 0 9 * * *", "UTC", exact);
        Assert.Equal(new DateTime(2026, 1, 6, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void LatestDue_SelectsLatestAboveWaterline_ExcludingWaterlineItself()
    {
        var latest = TaskScheduleResolver.GetLatestDueCronUtc(
            NcrontabSchedule.Parse("0 0 * * * *"), Zone("UTC"),
            new DateTime(2026, 1, 1, 10, 20, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc), latest);
    }

    [Fact]
    public void MergedSchedule_CoincidentInstantsProduceOneOccurrence()
    {
        var zone = Zone("UTC");
        var latest = TaskScheduleResolver.GetLatestDueMergedUtc(
            "0 0 * * * *", ["2026-01-01T09:00:00"], zone,
            new DateTime(2026, 1, 1, 10, 20, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc), latest);

        var next = TaskScheduleResolver.GetNextMergedUtc(
            "0 0 * * * *", ["2026-01-01T09:00:00"], zone,
            new DateTime(2026, 1, 1, 8, 30, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void MergedSchedule_ExplicitOnly_WorksWithoutCron()
    {
        var next = TaskScheduleResolver.GetNextMergedUtc(
            null, ["2026-12-08T09:00:00"], Zone("Europe/Moscow"),
            new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 12, 8, 6, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void HorizonGate_RejectsImpossibleCronEvenWithExplicitDates()
    {
        Assert.False(TaskScheduleResolver.HasFutureCronOccurrence(
            "0 0 9 30 2 *", Zone("UTC"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.True(TaskScheduleResolver.HasFutureCronOccurrence(
            "0 0 9 * * *", Zone("UTC"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void UtcZone_UsesSameResolver()
    {
        var next = Next("0 0 9 5 * *", "UTC", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc), next);
    }
}
