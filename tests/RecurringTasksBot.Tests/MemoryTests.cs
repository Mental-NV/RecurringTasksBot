// Occurrence memory through the repository: first-freeze reuse, eligibility, failure notices, and publication rules.
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Tests;

public sealed class MemoryTests
{
    private static readonly DateTime Day1 = new(2026, 9, 24, 9, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Day2 = new(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc);

    private sealed record Fixture(
        FakeOperationStore Ops, FakeClock Clock, FakeOccurrenceRepository Repo);

    private static Fixture New()
    {
        var ops = new FakeOperationStore();
        var clock = new FakeClock { Now = new DateTimeOffset(Day2, TimeSpan.Zero) };
        ops.Seed(TestRecords.Operation("u1", "op1"));
        return new Fixture(ops, clock, new FakeOccurrenceRepository(ops, clock));
    }

    private static async Task Claim(Fixture f, DateTime scheduled, string claim = "c1") =>
        Assert.True(await f.Repo.TryClaimAsync("u1", "op1", scheduled, claim));

    private static FrozenContextRequest Init(string claim, DateTime scheduled) => new(
        "u1", "op1", scheduled, claim,
        new FrozenContextInputs("sys", 24000, 32768,
            ExecutionOptions.PreviousSuccessfulReply, scheduled));

    private static PersistGenerationRequest Persist(
        string claim, DateTime scheduled, string text, AnswerKind kind = AnswerKind.Answer)
    {
        var answer = AnswerArtifact.Create(ArtifactVersion.New(), kind, text);
        return new PersistGenerationRequest("u1", "op1", scheduled, claim, answer,
            DeliveryPlan.CreateInitial(answer.AnswerVersion, text), scheduled,
            new ReceiptUsage("OpenRouter", "m", 10, 20, 0, false));
    }


    [Fact]
    public async Task FullOccurrence_PublishesMemoryForNextRun()
    {
        var f = New();
        await Claim(f, Day1);
        var init = await f.Repo.InitializeContextAsync(Init("c1", Day1));
        Assert.True(init.Created);
        Assert.False(init.Context.PreviousReplyPresent);

        var again = await f.Repo.InitializeContextAsync(Init("c1", Day1));
        Assert.False(again.Created);
        Assert.Equal(init.Context.ContextVersion, again.Context.ContextVersion);

        await f.Repo.PersistGenerationAsync(Persist("c1", Day1, "## Day one"));
        // A repeated persist after pointers commit never regenerates.
        await f.Repo.PersistGenerationAsync(Persist("c1", Day1, "## Changed"));
        var receipt = (await f.Repo.GetReceiptAsync("u1", "op1", Day1))!;
        Assert.Equal("generated", receipt.ExecutionStatus);

        var confirmed = await f.Repo.ConfirmLeafAsync(
            new ConfirmLeafRequest("u1", "op1", Day1, "c1", "p0", 901));
        Assert.True(confirmed.Leaves[0].Confirmed);
        receipt = (await f.Repo.GetReceiptAsync("u1", "op1", Day1))!;
        Assert.Equal("901", receipt.MessageIds);

        await f.Repo.CompleteAsync(new CompleteRequest("u1", "op1", Day1, "c1"));
        Assert.Equal("sent", (await f.Repo.GetReceiptAsync("u1", "op1", Day1))!.Status);
        Assert.Equal("## Day one", (await f.Repo.ReadPreviousReplyAsync("u1", "op1"))!.Answer);

        // The next occurrence freezes the earlier reply as context.
        await Claim(f, Day2, "c2");
        var init2 = await f.Repo.InitializeContextAsync(Init("c2", Day2));
        Assert.True(init2.Context.PreviousReplyPresent);
        Assert.Equal("## Day one", init2.Context.PreviousReplyAnswer);
        var messages = ExecutionMessageBuilder.BuildMessages("sys", init2.Context.ToSnapshot(), "## Day one");
        Assert.Equal(4, messages.Count);
    }


    [Fact]
    public async Task Init_NeverReselectsMemoryAfterFirstFreeze()
    {
        var f = New();
        await Claim(f, Day1);
        var init = await f.Repo.InitializeContextAsync(Init("c1", Day1));
        Assert.False(init.Context.PreviousReplyPresent);
        // A reply published concurrently cannot invalidate the frozen copy.
        f.Repo.SeedMemory(new MemoryRecord("u1", "op1", Day1.AddHours(-1), Day1.AddHours(-1),
            f.Clock.Now, "late", "## Late", "hash", 7));
        var again = await f.Repo.InitializeContextAsync(Init("c1", Day1));
        Assert.False(again.Created);
        Assert.False(again.Context.PreviousReplyPresent);
        Assert.Null(again.Context.PreviousReplyAnswer);
    }


    [Fact]
    public async Task Init_ExcludesFutureAndFailedReplies()
    {
        var f = New();
        f.Repo.SeedMemory(new MemoryRecord("u1", "op1", Day2.AddHours(1), Day2.AddHours(1),
            f.Clock.Now, "future", "## Future", "hash", 9));
        await Claim(f, Day1);
        var init = await f.Repo.InitializeContextAsync(Init("c1", Day1));
        Assert.False(init.Context.PreviousReplyPresent);
        Assert.Equal("no_eligible_previous_reply", init.Context.NoMemoryReason);
    }


    [Fact]
    public async Task FailureNotice_CompletesWithoutMemory()
    {
        var f = New();
        await Claim(f, Day1);
        await f.Repo.InitializeContextAsync(Init("c1", Day1));
        await f.Repo.PersistGenerationAsync(Persist("c1", Day1,
            "This run failed. Future runs remain scheduled.", AnswerKind.FailureNotice));
        await f.Repo.ConfirmLeafAsync(new ConfirmLeafRequest("u1", "op1", Day1, "c1", "p0", 901));
        await f.Repo.CompleteAsync(new CompleteRequest("u1", "op1", Day1, "c1"));
        Assert.Null(await f.Repo.ReadPreviousReplyAsync("u1", "op1"));
        var receipt = (await f.Repo.GetReceiptAsync("u1", "op1", Day1))!;
        Assert.Equal("sent", receipt.Status);
        Assert.Equal("failed", receipt.ExecutionStatus);
    }



    [Fact]
    public void MemoryDecision_PublishesNewerSkipsNewerIdempotentOrFails()
    {
        var now = DateTimeOffset.UtcNow;
        var first = new DateTime(2026, 9, 24, 9, 0, 0, DateTimeKind.Utc);
        var second = new DateTime(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc);
        var answer1 = AnswerArtifact.Create("a1", AnswerKind.Answer, "first");
        var answer2 = AnswerArtifact.Create("a2", AnswerKind.Answer, "second");

        var created = MemoryPublication.Decide(null, "u1", "op1", first, first, answer1, now);
        Assert.NotNull(created);
        Assert.Equal("first", created!.Answer);
        Assert.Equal(first, created.SourceScheduledUtc);

        // Same occurrence, same hash: idempotent.
        Assert.Null(MemoryPublication.Decide(created, "u1", "op1", first, first, answer1, now));
        // Same occurrence, different hash: consistency failure.
        Assert.Throws<OccurrenceConsistencyException>(() =>
            MemoryPublication.Decide(created, "u1", "op1", first, first, answer2, now));
        // Newer memory wins over an older receipt.
        Assert.Null(MemoryPublication.Decide(
            created with { SourceScheduledUtc = second }, "u1", "op1", first, first, answer1, now));
        // Older memory advances.
        var advanced = MemoryPublication.Decide(created, "u1", "op1", second, second, answer2, now);
        Assert.Equal("second", advanced!.Answer);
        Assert.Equal("a2", advanced.AnswerVersion);
    }
}
