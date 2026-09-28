// /list running-awareness: only a confirmed live lease reads executing.
// Stale or missing leases with pending work read recovering; finished or
// terminally recorded work reads unknown. Durable's generic running status
// (live through timer waits) never decides.
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Tests;

public sealed class TaskListActivityTests
{
    private static readonly DateTime Water = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Due = new(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 1, 1, 9, 30, 0, DateTimeKind.Utc);

    private static TaskRecord Claimed(OccurrenceClaim? claim) =>
        TestRecords.Task(owner: "u1", id: "task1") with
        {
            ActiveClaim = claim,
            WaterlineUtc = Water,
        };

    private static OccurrenceClaim Claim(DateTime? nextRetry = null, DateTime? scheduled = null) =>
        new(scheduled ?? Due, 1, "Do it", "None", "low", false, "1", Due, 0, nextRetry);

    private static DeliveryReceipt Receipt(
        string status, DateTimeOffset updated, string? claimId) =>
        new("u1", "task1", new DateTimeOffset(Due, TimeSpan.Zero), status, 0, null,
            UpdatedUtc: updated, ClaimId: claimId);

    private static DateTimeOffset AtNow => new(Now, TimeSpan.Zero);

    [Fact]
    public void NoClaim_ReturnsIdle()
    {
        Assert.Equal(TaskActivityKind.Idle,
            TaskListBuilder.ResolveActivity(Claimed(null), null, Now));
    }

    [Fact]
    public void StaleClaimOverFinishedWork_ReturnsUnknown()
    {
        var record = Claimed(Claim(scheduled: Water));
        Assert.Equal(TaskActivityKind.UnknownLease,
            TaskListBuilder.ResolveActivity(record, null, Now));
    }

    [Fact]
    public void FutureRetryNote_ReturnsRetryWait()
    {
        var record = Claimed(Claim(nextRetry: Now.AddMinutes(5)));
        var live = Receipt(OccurrenceExecution.StatusGenerating, AtNow, "worker");
        Assert.Equal(TaskActivityKind.RetryWait,
            TaskListBuilder.ResolveActivity(record, live, Now));
    }

    [Fact]
    public void ExpiredRetryNote_FallsThroughToLease()
    {
        var record = Claimed(Claim(nextRetry: Now.AddMinutes(-5)));
        var live = Receipt(OccurrenceExecution.StatusGenerating, AtNow, "worker");
        Assert.Equal(TaskActivityKind.Claimed,
            TaskListBuilder.ResolveActivity(record, live, Now));
    }

    [Fact]
    public void LiveLease_ReturnsClaimed()
    {
        var record = Claimed(Claim());
        var live = Receipt(OccurrenceExecution.StatusGenerating, AtNow.AddMinutes(-1), "worker");
        Assert.Equal(TaskActivityKind.Claimed,
            TaskListBuilder.ResolveActivity(record, live, Now));
    }

    [Fact]
    public void StaleLease_WithPendingWork_ReturnsRecovering()
    {
        var record = Claimed(Claim());
        var stale = Receipt(OccurrenceExecution.StatusGenerating, AtNow.AddHours(-1), "dead-worker");
        Assert.Equal(TaskActivityKind.RecoveringWithPending,
            TaskListBuilder.ResolveActivity(record, stale, Now));
    }

    [Fact]
    public void MissingReceipt_WithPendingWork_ReturnsRecovering()
    {
        Assert.Equal(TaskActivityKind.RecoveringWithPending,
            TaskListBuilder.ResolveActivity(Claimed(Claim()), null, Now));
    }

    [Fact]
    public void ReleasedLease_WithPendingWork_ReturnsRecovering()
    {
        var record = Claimed(Claim());
        var released = Receipt(OccurrenceExecution.StatusGenerating, AtNow.AddHours(-1), null);
        Assert.Equal(TaskActivityKind.RecoveringWithPending,
            TaskListBuilder.ResolveActivity(record, released, Now));
    }

    [Theory]
    [InlineData("sent")]
    [InlineData("failed")]
    public void TerminalReceipt_WithLingeringClaim_ReturnsUnknown(string status)
    {
        var record = Claimed(Claim());
        var terminal = Receipt(status, AtNow.AddHours(-1), "worker");
        Assert.Equal(TaskActivityKind.UnknownLease,
            TaskListBuilder.ResolveActivity(record, terminal, Now));
    }
}
