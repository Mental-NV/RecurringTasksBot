// Activity tests: the real Deliver entry point, including the
// post-handler receipt read and content-free v3 telemetry logging.
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using RecurringTasksBot.FunctionApp;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Tests;

public sealed class ActivityTests
{
    private static readonly DateTime Day1 = new(2026, 9, 24, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2 = new(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc);

    private sealed class TestLogger : ILogger<RecurrenceFunctions>
    {
        public List<string> Messages = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
            NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class TestLoggerFactory(TestLogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => logger;
        public void Dispose() { }
    }

    private static FunctionContext ContextFor(TestLogger logger)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(new TestLoggerFactory(logger));
        services.AddSingleton<ILogger<RecurrenceFunctions>>(logger);
        var context = new Mock<FunctionContext>();
        context.Setup(c => c.CancellationToken).Returns(CancellationToken.None);
        context.Setup(c => c.InstanceServices).Returns(services.BuildServiceProvider());
        return context.Object;
    }

    private sealed record Fixture(
        RecurrenceFunctions Functions, TestLogger Logger, FunctionContext Context,
        FakeOccurrenceRepository Repo, FakeLlmExecutor Llm, FakeTelegramSender Sender);

    private static Fixture New()
    {
        var ops = new FakeOperationStore();
        var sender = new FakeTelegramSender();
        var llm = new FakeLlmExecutor();
        var clock = new FakeClock { Now = new DateTimeOffset(Day2, TimeSpan.Zero) };
        ops.Seed(TestRecords.Operation("u1", "op1"));
        var repo = new FakeOccurrenceRepository(ops, clock);
        var handler = new ExecuteOccurrenceHandler(ops, repo, sender, llm,
            TestLlm.Execution(), TestLlm.ProviderName, TestLlm.ModelName, clock);
        var functions = new RecurrenceFunctions(ops, repo, handler);
        var logger = new TestLogger();
        return new Fixture(functions, logger, ContextFor(logger), repo, llm, sender);
    }

    [Fact]
    public async Task Deliver_RunsV3FlowWithContentFreeTelemetry()
    {
        var f = New();
        var result = await f.Functions.Deliver(
            new AttemptRequest("u1", "op1", Day2.Ticks, 0), f.Context);
        Assert.Equal(SingleAttemptOutcome.Sent, result.Outcome);
        Assert.Equal("Canned answer.", (await f.Repo.ReadPreviousReplyAsync("u1", "op1"))!.Answer);
        var v3 = f.Logger.Messages.Where(m => m.Contains("Occurrence progress", StringComparison.Ordinal)).ToList();
        Assert.Single(v3);
        Assert.DoesNotContain("Canned answer.", string.Concat(f.Logger.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deliver_MissingOperation_StopsWithoutWork()
    {
        var f = New();
        var result = await f.Functions.Deliver(
            new AttemptRequest("u1", "ghost", Day2.Ticks, 0), f.Context);
        Assert.Equal(SingleAttemptOutcome.SkippedStopped, result.Outcome);
        Assert.Empty(f.Sender.Payloads);
    }

    [Fact]
    public async Task Deliver_LoggingReadFailure_DoesNotFailAttempt()
    {
        var f = New();
        var logging = new Mock<IOccurrenceRepository>();
        logging.Setup(r => r.GetReceiptAsync("u1", "op1", Day2, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TransientStoreException("storage down"));
        var ops = new FakeOperationStore();
        var sender = new FakeTelegramSender();
        var llm = new FakeLlmExecutor();
        var clock = new FakeClock { Now = new DateTimeOffset(Day2, TimeSpan.Zero) };
        ops.Seed(TestRecords.Operation("u1", "op1"));
        var repo = new FakeOccurrenceRepository(ops, clock);
        var handler = new ExecuteOccurrenceHandler(ops, repo, sender, llm,
            TestLlm.Execution(), TestLlm.ProviderName, TestLlm.ModelName, clock);
        var functions = new RecurrenceFunctions(ops, logging.Object, handler);

        var result = await functions.Deliver(
            new AttemptRequest("u1", "op1", Day2.Ticks, 0), f.Context);

        Assert.Equal(SingleAttemptOutcome.Sent, result.Outcome);
        Assert.NotEmpty(sender.Payloads);
    }
}
