// Execution context: system prompt rendering, frozen snapshots, message shape, and context budgets.
using System.Text.Json;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Tests;

public sealed class ExecutionContextTests
{


    [Fact]
    public void SystemTemplate_SubstitutesOnlyNamedPlaceholders()
    {
        var rendered = RecurringTaskSystemPrompt.Render(24000, 32768, null);
        Assert.Contains("24000", rendered, StringComparison.Ordinal);
        Assert.Contains("32768", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("{TargetAnswerTextChars}", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("{MaxRichMessageChars}", rendered, StringComparison.Ordinal);
    }


    [Fact]
    public void SystemTemplate_AppendsAdminInstructionWithLabelAndBoundsIt()
    {
        var rendered = RecurringTaskSystemPrompt.Render(24000, 32768, "Be extra concise.");
        Assert.Contains("Additional administrator instruction", rendered, StringComparison.Ordinal);
        Assert.Contains("Be extra concise.", rendered, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() =>
            RecurringTaskSystemPrompt.Render(24000, 32768, new string('z', 20000)));
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
    public void ContextMessages_FirstRunHasTwoMessages()
    {
        var (instruction, snapshot) = Snapshot(false);
        Assert.False(snapshot.PreviousReplyPresent);
        var messages = ExecutionMessageBuilder.BuildMessages(instruction, snapshot, null);
        Assert.Equal(["system", "user"], messages.Select(m => m.Role));
        using var doc = JsonDocument.Parse(messages[1].Content);
        Assert.Equal("Report 🟢 status\r\nline \"quoted\" <tag> & more",
            doc.RootElement.GetProperty("task_instruction").GetString());
    }


    [Fact]
    public void ContextMessages_MemoryRunHasFourEndingInCurrentUser()
    {
        var (instruction, snapshot) = Snapshot(true);
        var messages = ExecutionMessageBuilder.BuildMessages(instruction, snapshot, "## Prior\nDone ✅");
        Assert.Equal(["system", "user", "assistant", "user"], messages.Select(m => m.Role));
        Assert.Equal("## Prior\nDone ✅", messages[2].Content);
        Assert.Contains(ExecutionMessageBuilder.ArchivedReplyLabel, messages[1].Content, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => ExecutionMessageBuilder.BuildMessages(instruction, snapshot, null));
        var (_, noMemory) = Snapshot(false);
        Assert.Throws<ArgumentException>(() =>
            ExecutionMessageBuilder.BuildMessages(instruction, noMemory, "## Prior\nDone ✅"));
    }


    [Fact]
    public void ContextBudget_IncludesFullPreviousReplyOrFailsExplicitly()
    {
        var (instruction, snapshot) = Snapshot(true);
        var previous = new string('z', ExecutionLimits.AnswerSourceMaxScalars);
        var messages = ExecutionMessageBuilder.BuildMessages(instruction, snapshot, previous);
        Assert.Equal(previous.Length, messages[2].Content.Length);
        var options = TestLlm.Execution();
        Assert.True(ContextBudget.FitsBudget(messages, options, 131072));
        Assert.False(ContextBudget.FitsBudget(messages, options with { DeclaredContextTokens = 100 }, 131072));
    }
}
