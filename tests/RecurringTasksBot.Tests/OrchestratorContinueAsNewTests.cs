// Orchestrator control-flow regression: ContinueAsNew must end the current
// invocation. Code after it would schedule another timer/activity in the
// same invocation and grow history without bound.
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RecurringTasksBot.Application;
using RecurringTasksBot.FunctionApp;

namespace RecurringTasksBot.Tests;

public sealed class OrchestratorContinueAsNewTests
{
    private sealed class RecordingContext(RecurrenceState input, DateTime now) : TaskOrchestrationContext
    {
        public readonly List<string> Calls = new();
        public int ContinueAsNewIndex = -1;

        public override string InstanceId => "test-instance";
        public override bool IsReplaying => false;
        public override TaskName Name => new("RecurrenceLifecycle");
        public override ParentOrchestrationInstance? Parent => null;
        public override DateTime CurrentUtcDateTime => now;
        protected override ILoggerFactory LoggerFactory => NullLoggerFactory.Instance;

        public override T GetInput<T>() where T : default => (T)(object)input;

        public override Guid NewGuid() => Guid.NewGuid();
        public override void SendEvent(string instanceId, string eventName, object payload) =>
            throw new NotSupportedException();
        public override void SetCustomStatus(object? customStatus) { }
        public override Task<T> WaitForExternalEvent<T>(string eventName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<TResult> CallActivityAsync<TResult>(
            TaskName name, object? input, TaskOptions? options)
        {
            Calls.Add($"activity:{name.Name}");
            object result = name.Name switch
            {
                "LoadOperation" => new LoadedOperation("active", "0 0 9 * * *"),
                "DeliverOccurrence" => new SingleAttemptResult(SingleAttemptOutcome.Sent, 1, null, null),
                _ => throw new NotSupportedException($"Unexpected activity {name.Name}"),
            };
            return Task.FromResult((TResult)result);
        }

        public override Task<TResult> CallSubOrchestratorAsync<TResult>(
            TaskName orchestratorName, object? input, TaskOptions? options) =>
            throw new NotSupportedException();

        public override void ContinueAsNew(object? newInput, bool preserveUnprocessedEvents = false)
        {
            ContinueAsNewIndex = Calls.Count;
            Calls.Add("continue-as-new");
        }

        public override Task CreateTimer(DateTime fireAt, CancellationToken cancellationToken)
        {
            Calls.Add("timer");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ContinueAsNew_EndsInvocationWithoutFurtherCalls()
    {
        var now = new DateTime(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc);
        var due = now.AddMinutes(-5);
        var state = new RecurrenceState("i1", "op1", "u1", "0 0 9 * * *", due, 0);
        var ctx = new RecordingContext(state, now);

        var operations = new FakeOperationStore();
        var clock = new FakeClock();
        var functions = new RecurrenceFunctions(
            operations,
            new FakeOccurrenceRepository(operations, clock),
            new ExecuteOccurrenceHandler(
                operations,
                new FakeOccurrenceRepository(operations, clock),
                new FakeTelegramSender(),
                new FakeLlmExecutor(),
                TestLlm.Execution(),
                TestLlm.ProviderName,
                TestLlm.ModelName,
                clock));

        await functions.Run(ctx);

        Assert.NotEqual(-1, ctx.ContinueAsNewIndex);
        Assert.Equal(ctx.Calls.Count - 1, ctx.ContinueAsNewIndex);
    }
}
