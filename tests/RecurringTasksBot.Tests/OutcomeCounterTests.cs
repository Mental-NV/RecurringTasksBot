// Retry/counter table tests: the overall work-retry cap is five after the
// initial attempt (2 generation + 3 delivery). Every NeedRetry consumes it,
// including storage-conflict and budget-yield retries; WaitingForClaim never
// does and never advances the attempt index.
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Tests;

public sealed class OutcomeCounterTests
{
    [Fact]
    public void CombinedCap_IsFive()
    {
        Assert.Equal(2, GenerationPolicy.MaxRetriesAfterInitial);
        Assert.Equal(3, DeliveryPolicy.MaxRetriesAfterInitial);
        Assert.Equal(5, OccurrenceExecution.MaxCombinedRetriesAfterInitial);
    }

    [Theory]
    [InlineData(SingleAttemptOutcome.Sent)]
    [InlineData(SingleAttemptOutcome.SkippedStopped)]
    [InlineData(SingleAttemptOutcome.SkippedDuplicate)]
    [InlineData(SingleAttemptOutcome.OccurrenceFailed)]
    [InlineData(SingleAttemptOutcome.OperationFailed)]
    public void TerminalOutcomes_NeverRetry(SingleAttemptOutcome outcome)
    {
        var result = new SingleAttemptResult(outcome, 0, null, null);
        Assert.False(OccurrenceExecution.ShouldRetry(result, 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void NeedRetry_ConsumesCap_BelowFive(int attemptIndex)
    {
        var result = new SingleAttemptResult(SingleAttemptOutcome.NeedRetry, 0, null, null);
        Assert.True(OccurrenceExecution.ShouldRetry(result, attemptIndex));
        Assert.Equal(attemptIndex + 1, OccurrenceExecution.NextAttemptIndex(result, attemptIndex));
    }

    [Fact]
    public void NeedRetry_StopsAtCap()
    {
        var result = new SingleAttemptResult(SingleAttemptOutcome.NeedRetry, 0, null, null);
        Assert.False(OccurrenceExecution.ShouldRetry(result, 5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(100)]
    public void WaitingForClaim_RetriesWithoutConsumingCap(int attemptIndex)
    {
        var result = new SingleAttemptResult(
            SingleAttemptOutcome.WaitingForClaim, 0, TimeSpan.FromSeconds(30), null);
        Assert.True(OccurrenceExecution.ShouldRetry(result, attemptIndex));
        Assert.Equal(attemptIndex, OccurrenceExecution.NextAttemptIndex(result, attemptIndex));
    }
}
