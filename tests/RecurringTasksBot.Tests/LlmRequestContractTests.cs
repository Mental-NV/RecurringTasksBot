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

    [Theory]
    [InlineData("UTC", false)]
    [InlineData("Europe/Moscow", true)]
    [InlineData("Europe/Berlin", true)]
    [InlineData("UTC", true)]
    public async Task FrozenMessagesAndBound_TravelInOneRequest_RetriesReuseSnapshot(
        string timezone, bool withMemory)
    {
        var ops = new FakeOperationStore();
        var sender = new FakeTelegramSender();
        var llm = new FakeLlmExecutor();
        var clock = new FakeClock { Now = new DateTimeOffset(Day, TimeSpan.Zero) };
        ops.Seed(TestRecords.Operation("u1", "op1") with { ScheduleTimezone = timezone });
        var repo = new FakeOccurrenceRepository(ops, clock);
        var previousScheduled = Day.AddDays(-1).Date.AddHours(22);
        var previousExecuted = previousScheduled.AddSeconds(5);
        const string previousAnswer = "Previous result";
        if (withMemory)
        {
            var answer = AnswerArtifact.Create("previous", AnswerKind.Answer, previousAnswer);
            repo.SeedMemory(new MemoryRecord("u1", "op1", previousScheduled, previousExecuted,
                clock.Now, answer.AnswerVersion, answer.Text, answer.SourceSha256, answer.ScalarCount));
        }
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
        var messages = llm.Requests[0].Messages;
        Assert.Equal(withMemory ? ["system", "user", "assistant", "user"] : new[] { "system", "user" },
            messages.Select(m => m.Role));
        Assert.Contains("execution_context.schedule_timezone", messages[0].Content);
        Assert.Contains("convert it to\nschedule_timezone", messages[0].Content);
        Assert.Contains("previous_reply_scheduled_at_utc and previous_reply_executed_at_utc", messages[0].Content);
        Assert.Contains("execution_started_at_utc converted to schedule_timezone", messages[0].Content);
        using var envelope = JsonDocument.Parse(messages[^1].Content);
        var context = envelope.RootElement.GetProperty("execution_context");
        Assert.Equal(timezone, context.GetProperty("schedule_timezone").GetString());
        Assert.Equal("recurring-task-v2", context.GetProperty("instruction_version").GetString());
        if (withMemory)
        {
            Assert.Equal(previousAnswer, messages[2].Content);
            using var archived = JsonDocument.Parse(messages[1].Content);
            foreach (var metadata in new[] { archived.RootElement, context })
            {
                Assert.Equal(ExecutionMessageBuilder.ToIso8601(previousScheduled),
                    metadata.GetProperty("previous_reply_scheduled_at_utc").GetString());
                Assert.Equal(ExecutionMessageBuilder.ToIso8601(previousExecuted),
                    metadata.GetProperty("previous_reply_executed_at_utc").GetString());
            }
        }
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
    public async Task SharedSearchDefault_FlowsIntoMessagesAndNullRequestOverride()
    {
        var ops = new FakeOperationStore();
        var sender = new FakeTelegramSender();
        var llm = new FakeLlmExecutor();
        var clock = new FakeClock { Now = new DateTimeOffset(Day, TimeSpan.Zero) };
        ops.Seed(TestRecords.Operation("u1", "op1"));
        var repo = new FakeOccurrenceRepository(ops, clock);
        var execution = TestLlm.Execution() with { SearchEnabled = false };
        var handler = new ExecuteOccurrenceHandler(ops, repo, sender, llm,
            execution, TestLlm.ProviderName, TestLlm.ModelName, clock);

        var result = await handler.ExecuteAttemptAsync("u1", "op1", Day, 0);
        Assert.Equal(SingleAttemptOutcome.Sent, result.Outcome);

        // Prompt construction uses the shared default when the occurrence
        // carries no frozen override; the request leaves the override null
        // so the adapter applies the same shared default.
        var request = Assert.Single(llm.Requests);
        Assert.Null(request.SearchEnabled);
        Assert.Contains("Web search is disabled", request.Messages[^1].Content);
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
