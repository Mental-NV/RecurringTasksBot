using RecurringTasksBot.Core;

namespace RecurringTasksBot.Tests;

public sealed class NcrontabTests
{
    private static DateTime Dt(int y, int mo, int d, int h, int mi, int s = 0) =>
        new(y, mo, d, h, mi, s, DateTimeKind.Utc);

    [Fact]
    public void DailyNineAm_BeforeTime_SameDay()
    {
        var s = NcrontabSchedule.Parse("0 0 9 * * *");
        Assert.Equal(Dt(2026, 1, 5, 9, 0), s.GetNextOccurrence(Dt(2026, 1, 5, 8, 0)));
    }

    [Fact]
    public void Occurrence_IsStrictlyAfterReference()
    {
        var s = NcrontabSchedule.Parse("0 0 9 * * *");
        // Reference exactly at an occurrence: next is tomorrow, never itself.
        Assert.Equal(Dt(2026, 1, 6, 9, 0), s.GetNextOccurrence(Dt(2026, 1, 5, 9, 0)));
    }

    [Fact]
    public void FirstAfterActivation_IsStrictlyAfterActivation()
    {
        var s = NcrontabSchedule.Parse("0 0 9 * * *");
        Assert.Equal(Dt(2026, 1, 6, 9, 0),
            Scheduler.FirstAfterActivation(s, Dt(2026, 1, 5, 9, 0)));
        Assert.Equal(Dt(2026, 1, 5, 9, 0),
            Scheduler.FirstAfterActivation(s, Dt(2026, 1, 5, 8, 59, 59)));
    }

    [Fact]
    public void WeeklyMonday()
    {
        var s = NcrontabSchedule.Parse("0 30 8 * * MON");
        // 2026-01-05 is a Monday.
        Assert.Equal(Dt(2026, 1, 5, 8, 30), s.GetNextOccurrence(Dt(2026, 1, 4, 0, 0)));
        Assert.Equal(Dt(2026, 1, 12, 8, 30), s.GetNextOccurrence(Dt(2026, 1, 5, 8, 30)));
    }

    [Fact]
    public void StepsListsRanges()
    {
        var s = NcrontabSchedule.Parse("0 */30 9-17 * * *");
        Assert.Equal(Dt(2026, 1, 5, 9, 30), s.GetNextOccurrence(Dt(2026, 1, 5, 9, 5)));
        Assert.Equal(Dt(2026, 1, 6, 9, 0), s.GetNextOccurrence(Dt(2026, 1, 5, 17, 30)));
    }

    [Fact]
    public void MonthNames()
    {
        var s = NcrontabSchedule.Parse("0 0 12 1 JAN,JUL *");
        Assert.Equal(Dt(2026, 7, 1, 12, 0), s.GetNextOccurrence(Dt(2026, 1, 2, 0, 0)));
    }

    [Fact]
    public void DayOfMonth_And_DayOfWeek_Restricted_MatchEither()
    {
        // 13th of month OR any Friday at noon.
        var s = NcrontabSchedule.Parse("0 0 12 13 * FRI");
        // 2026-02-13 is a Friday and the 13th; 2026-02-07 is a Saturday.
        Assert.Equal(Dt(2026, 2, 13, 12, 0), s.GetNextOccurrence(Dt(2026, 2, 7, 0, 0)));
        // Next Friday the 6th of March is a Friday but not the 13th -> matches via dow.
        Assert.Equal(Dt(2026, 3, 6, 12, 0), s.GetNextOccurrence(Dt(2026, 3, 1, 0, 0)));
    }

    [Fact]
    public void SundaySeven_Equals_SundayZero()
    {
        var seven = NcrontabSchedule.Parse("0 0 9 * * 7");
        var zero = NcrontabSchedule.Parse("0 0 9 * * 0");
        var after = Dt(2026, 1, 5, 10, 0); // Monday
        Assert.Equal(zero.GetNextOccurrence(after), seven.GetNextOccurrence(after));
        Assert.Equal(DayOfWeek.Sunday, seven.GetNextOccurrence(after)!.Value.DayOfWeek);
    }

    [Fact]
    public void ImpossibleSchedule_ReturnsNull()
    {
        var s = NcrontabSchedule.Parse("0 0 9 30 2 *");
        Assert.Null(s.GetNextOccurrence(Dt(2026, 1, 1, 0, 0)));
    }

    [Theory]
    [InlineData("0 0 9 * *")]          // five fields
    [InlineData("0 0 9 * * * *")]      // seven fields
    [InlineData("0 60 9 * * *")]       // minute out of range
    [InlineData("0 0 24 * * *")]       // hour out of range
    [InlineData("0 0 9 0 * *")]        // dom 0
    [InlineData("0 0 9 * 0 *")]        // month 0
    [InlineData("0 0 9 * * FOO")]      // bad name
    [InlineData("0 0 9 * * 1-")]       // bad range
    [InlineData("0 0 9 * * 5-1")]      // reversed range
    [InlineData("0 0 9 * * */0")]      // bad step
    [InlineData("")]                   // empty
    public void InvalidExpressions_Throw(string expr)
    {
        Assert.False(NcrontabSchedule.TryParse(expr, out _));
        Assert.Throws<NcrontabParseException>(() => NcrontabSchedule.Parse(expr));
    }
}
