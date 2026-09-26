// Phase 3 activity tests: the real Deliver entry point, including the
// post-handler receipt read and content-free v3 telemetry logging.
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using RecurringTasksBot;
using RecurringTasksBot.Core;

namespace RecurringTasksBot.Tests;

public sealed class Phase3ActivityTests
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
        var receipts = new FakeDeliveryStore();
        var sender = new FakeTelegramSender();
        var llm = new FakeLlmExecutor();
        var clock = new FakeClock { Now = new DateTimeOffset(Day2, TimeSpan.Zero) };
        receipts.Now = clock.Now;
        ops.Seed(TestRecords.Operation("u1", "op1"));
        var repo = new FakeOccurrenceRepository(ops, receipts, clock);
        var functions = new RecurrenceFunctions(ops, receipts, new FakePayloadStore(),
            sender, llm, TestLlm.Options(), repo, Phase3Config.Read(_ => null), llm, clock);
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
        var v3 = f.Logger.Messages.Where(m => m.Contains("Occurrence v3", StringComparison.Ordinal)).ToList();
        Assert.Single(v3);
        Assert.DoesNotContain("Canned answer.", string.Concat(f.Logger.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deliver_LegacyReceiptSkipsV3Telemetry()
    {
        var ops = new FakeOperationStore();
        var receipts = new FakeDeliveryStore();
        var payloads = new FakePayloadStore();
        var sender = new FakeTelegramSender();
        var llm = new FakeLlmExecutor();
        ops.Seed(TestRecords.Operation("u1", "op1"));
        await payloads.PersistAsync("u1", "op1", Day1, ["<b>old two</b>"], version: "v1");
        await receipts.UpsertAsync(new DeliveryReceipt("u1", "op1", Day1,
            OccurrenceExecution.StatusGenerating, 1, null, 5,
            ExecutionStatus: "generated", SentParts: 0, TotalParts: 1,
            MessageIds: string.Empty, PayloadVersion: "v1"));
        var clock = new FakeClock { Now = new DateTimeOffset(Day1, TimeSpan.Zero) };
        receipts.Now = clock.Now;
        var repo = new FakeOccurrenceRepository(ops, receipts, clock);
        var functions = new RecurrenceFunctions(ops, receipts, payloads, sender, llm,
            TestLlm.Options(), repo, Phase3Config.Read(_ => null), llm, clock);
        var logger = new TestLogger();
        var result = await functions.Deliver(
            new AttemptRequest("u1", "op1", Day1.Ticks, 0), ContextFor(logger));
        Assert.Equal(SingleAttemptOutcome.Sent, result.Outcome);
        Assert.DoesNotContain("Occurrence v3", string.Concat(logger.Messages), StringComparison.Ordinal);
    }
}
