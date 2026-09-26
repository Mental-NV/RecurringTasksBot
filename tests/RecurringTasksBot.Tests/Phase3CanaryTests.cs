// Literal-reply canary: the build-time log states whether the reply was
// LLM-authored or a literal fallback, with model and receipt versions.
using Microsoft.Extensions.Logging;
using RecurringTasksBot.Core;

namespace RecurringTasksBot.Tests;

public sealed class Phase3CanaryTests
{
    private static readonly DateTime Day = new(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc);

    private sealed class CaptureLogger : ILogger
    {
        public List<string> Lines { get; } = [];
        IDisposable ILogger.BeginScope<TState>(TState state) => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private static (DeliveryHandler Handler, FakeOperationStore Ops, FakeDeliveryStore Receipts,
        FakeTelegramSender Sender, FakeLlmExecutor Llm, FakeOccurrenceRepository Repo,
        FakeClock Clock, CaptureLogger Log) New()
    {
        var ops = new FakeOperationStore();
        var receipts = new FakeDeliveryStore();
        var sender = new FakeTelegramSender();
        var llmExec = new FakeLlmExecutor();
        var clock = new FakeClock { Now = new DateTimeOffset(Day, TimeSpan.Zero) };
        var log = new CaptureLogger();
        receipts.Now = clock.Now;
        ops.Seed(TestRecords.Operation("u1", "op1"));
        var repo = new FakeOccurrenceRepository(ops, receipts, clock);
        var handler = new DeliveryHandler(ops, receipts, new FakePayloadStore(), sender, llmExec,
            TestLlm.Options(), occurrences: repo,
            phase3Options: Phase3Config.Read(_ => null), phase3Llm: llmExec, clock: clock,
            logger: log);
        return (handler, ops, receipts, sender, llmExec, repo, clock, log);
    }

    [Fact]
    public async Task V3HappyPath_LogsLlmAuthoredWithVersions()
    {
        var v = New();
        v.Llm.Phase3Handler = _ => Task.FromResult(FakeLlmExecutor.Answer("All good"));
        var result = await v.Handler.DeliverAsync("u1", "op1", Day, _ => Task.CompletedTask);
        Assert.Equal(DeliveryOutcome.Sent, result.Outcome);
        var line = Assert.Single(v.Log.Lines, l => l.Contains("replySource="));
        Assert.Contains("replySource=llm-authored", line);
        Assert.Contains("receiptSchema=3", line);
        Assert.DoesNotContain("All good", line);
    }

    [Fact]
    public async Task V3BlockedTag_LogsLiteralSource()
    {
        var v = New();
        v.Llm.Phase3Handler = _ => Task.FromResult(FakeLlmExecutor.Answer("See ![pic](http://x/y.png)"));
        var result = await v.Handler.DeliverAsync("u1", "op1", Day, _ => Task.CompletedTask);
        Assert.Equal(DeliveryOutcome.Sent, result.Outcome);
        var line = Assert.Single(v.Log.Lines, l => l.Contains("replySource="));
        Assert.Contains("replySource=literal", line);
    }

    [Fact]
    public async Task V3PermanentFailure_LogsFailureNoticeLiteral()
    {
        var v = New();
        v.Llm.Phase3Handler = _ => Task.FromException<LlmResult>(
            new LlmExecutionException(LlmFailureKind.Permanent, "rejected"));
        var result = await v.Handler.DeliverAsync("u1", "op1", Day, _ => Task.CompletedTask);
        Assert.Equal(DeliveryOutcome.Sent, result.Outcome);
        var line = Assert.Single(v.Log.Lines, l => l.Contains("replySource="));
        Assert.Contains("replySource=failure-notice-literal", line);
        Assert.DoesNotContain("rejected", line);
    }
}
