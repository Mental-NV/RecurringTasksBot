// Literal-reply canary: the build-time log states whether the reply was
// LLM-authored or a literal fallback, with model and receipt versions.
using Microsoft.Extensions.Logging;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Tests;

public sealed class TelemetryTests
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

    private static (ExecuteOccurrenceHandler Handler, FakeOperationStore Ops,
        FakeTelegramSender Sender, FakeLlmExecutor Llm, FakeOccurrenceRepository Repo,
        FakeClock Clock, CaptureLogger Log) New()
    {
        var ops = new FakeOperationStore();
        var sender = new FakeTelegramSender();
        var llmExec = new FakeLlmExecutor();
        var clock = new FakeClock { Now = new DateTimeOffset(Day, TimeSpan.Zero) };
        var log = new CaptureLogger();
        ops.Seed(TestRecords.Operation("u1", "op1"));
        var repo = new FakeOccurrenceRepository(ops, clock);
        var handler = new ExecuteOccurrenceHandler(ops, repo, sender, llmExec,
            TestLlm.Execution(), TestLlm.ProviderName, TestLlm.ModelName, clock, logger: log);
        return (handler, ops, sender, llmExec, repo, clock, log);
    }

    [Fact]
    public async Task HappyPath_LogsLlmAuthoredWithVersions()
    {
        var v = New();
        v.Llm.Responder = _ => Task.FromResult(FakeLlmExecutor.Answer("All good"));
        var result = await AttemptLoop.RunUntilDone(v.Handler, "u1", "op1", Day);
        Assert.Equal(SingleAttemptOutcome.Sent, result.Outcome);
        var line = Assert.Single(v.Log.Lines, l => l.Contains("replySource="));
        Assert.Contains("replySource=llm-authored", line);
        Assert.Contains("receiptSchema=1", line);
        Assert.DoesNotContain("All good", line);
    }

    [Fact]
    public async Task BlockedTag_LogsLiteralSource()
    {
        var v = New();
        v.Llm.Responder = _ => Task.FromResult(FakeLlmExecutor.Answer("See ![pic](http://x/y.png)"));
        var result = await AttemptLoop.RunUntilDone(v.Handler, "u1", "op1", Day);
        Assert.Equal(SingleAttemptOutcome.Sent, result.Outcome);
        var line = Assert.Single(v.Log.Lines, l => l.Contains("replySource="));
        Assert.Contains("replySource=literal", line);
    }

    [Fact]
    public async Task PermanentFailure_LogsFailureNoticeLiteral()
    {
        var v = New();
        v.Llm.Responder = _ => Task.FromException<LlmResult>(
            new LlmExecutionException(LlmFailureKind.Permanent, "rejected"));
        var result = await AttemptLoop.RunUntilDone(v.Handler, "u1", "op1", Day);
        Assert.Equal(SingleAttemptOutcome.Sent, result.Outcome);
        var line = Assert.Single(v.Log.Lines, l => l.Contains("replySource="));
        Assert.Contains("replySource=failure-notice-literal", line);
        Assert.DoesNotContain("rejected", line);
    }
}
