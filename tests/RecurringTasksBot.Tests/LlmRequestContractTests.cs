// Shared generation-contract tests: the occurrence handler drives one
// ILlmExecutor request carrying the frozen message list and the configured
// answer-source bound. Retries reuse the identical snapshot; the legacy
// prompt-only path is never invoked.
using System.Text.Json;
using RecurringTasksBot.Application;
using RecurringTasksBot.Infrastructure.Llm;

namespace RecurringTasksBot.Tests;

public sealed class LlmRequestContractTests
{
    private static readonly DateTime Day = new(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task FrozenMessagesAndBound_TravelInOneRequest_RetriesReuseSnapshot()
    {
        var ops = new FakeOperationStore();
        var sender = new FakeTelegramSender();
        var llm = new FakeLlmExecutor();
        var clock = new FakeClock { Now = new DateTimeOffset(Day, TimeSpan.Zero) };
        ops.Seed(TestRecords.Operation("u1", "op1"));
        var repo = new FakeOccurrenceRepository(ops, clock);
        var handler = new ExecuteOccurrenceHandler(ops, repo, sender, llm,
            TestLlm.Execution(), TestLlm.ProviderName, TestLlm.ModelName, clock);

        var calls = 0;
        llm.Responder = _ =>
        {
            calls++;
            if (calls == 1)
                throw new LlmExecutionException(LlmFailureKind.Transient, "boom");
            return Task.FromResult(FakeLlmExecutor.Answer("All good"));
        };

        var first = await handler.ExecuteAttemptAsync("u1", "op1", Day, 0);
        Assert.Equal(SingleAttemptOutcome.NeedRetry, first.Outcome);
        var second = await handler.ExecuteAttemptAsync("u1", "op1", Day, 1);
        Assert.Equal(SingleAttemptOutcome.Sent, second.Outcome);

        var execution = TestLlm.Execution();
        Assert.Equal(2, llm.Requests.Count);
        Assert.Equal(execution.MaxAnswerSourceChars, llm.Requests[0].MaxSourceScalars);
        Assert.Equal(execution.MaxAnswerSourceChars, llm.Requests[1].MaxSourceScalars);
        // Retries reuse the frozen snapshot values, rebuilt from the
        // persisted context: identical roles and content.
        Assert.Equal(llm.Requests[0].Messages, llm.Requests[1].Messages);
        Assert.Equal(["system", "user"], llm.Requests[0].Messages.Select(m => m.Role));
    }

    private static (string Instruction, ExecutionContextSnapshot Snapshot) Snapshot(bool withMemory)
    {
        var snapshot = ExecutionMessageBuilder.Create(
            "Report 🟢 status\r\nline \"quoted\" <tag> & more",
            "0 9 * * * *", "op1",
            new DateTime(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 25, 9, 0, 5, DateTimeKind.Utc),
            withMemory ? ExecutionMessageBuilder.ToIso8601(new DateTime(2026, 9, 24, 9, 0, 0, DateTimeKind.Utc)) : null,
            withMemory ? ExecutionMessageBuilder.ToIso8601(new DateTime(2026, 9, 24, 9, 0, 4, DateTimeKind.Utc)) : null,
            withMemory ? "## Prior\nDone ✅" : null,
            24000, 32768, null, out var instruction);
        return (instruction, snapshot);
    }


    [Fact]
    public void Request_FirstRunHasTwoMessagesWithPreservedSettings()
    {
        var (instruction, snapshot) = Snapshot(false);
        var messages = ExecutionMessageBuilder.BuildMessages(instruction, snapshot, null);
        var json = OpenRouterRequestBuilder.BuildRequestJson(TestLlm.Provider(), TestLlm.Execution(), messages);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("deepseek/deepseek-v4.1-flash", root.GetProperty("model").GetString());
        var wire = root.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["system", "user"], wire.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal(instruction, wire[0].GetProperty("content").GetString());
        Assert.Equal("max", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("openrouter:web_search",
            Assert.Single(root.GetProperty("tools").EnumerateArray()).GetProperty("type").GetString());
    }

    [Fact]
    public void Request_MemoryRunCarriesOneArchivedAssistantTurn()
    {
        var (instruction, snapshot) = Snapshot(true);
        const string previous = "## Prior\nDone ✅";
        var messages = ExecutionMessageBuilder.BuildMessages(instruction, snapshot, previous);
        var json = OpenRouterRequestBuilder.BuildRequestJson(TestLlm.Provider(), TestLlm.Execution(), messages);
        using var doc = JsonDocument.Parse(json);
        var wire = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(4, wire.Count);
        Assert.Equal(["system", "user", "assistant", "user"],
            wire.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal(previous, wire[2].GetProperty("content").GetString());
        Assert.Equal("user", wire[3].GetProperty("role").GetString());
    }
}
