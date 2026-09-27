// Claim-gated repository rules: foreign claims are rejected, stopped operations refuse work, and completion requires full confirmation.
// (The regression class OccurrenceLeaseTests in ReviewRegressionTests covers worker handover.)
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Tests;

public sealed class RepositoryClaimTests
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
    public async Task Operations_EnforceClaimsAndActiveStatus()
    {
        var f = New();
        await Claim(f, Day1);
        await Assert.ThrowsAsync<ClaimLostException>(() =>
            f.Repo.InitializeContextAsync(Init("other", Day1)));
        await Assert.ThrowsAsync<ClaimLostException>(() =>
            f.Repo.PersistGenerationAsync(Persist("other", Day1, "x")));
        await Assert.ThrowsAsync<ClaimLostException>(() =>
            f.Repo.CompleteAsync(new CompleteRequest("u1", "op1", Day1, "other")));

        Assert.True(await f.Ops.CompareAndSwapStatusAsync(
            "u1", "op1", OperationStatus.Active, OperationStatus.Deleted));
        await Assert.ThrowsAsync<OperationStoppedException>(() =>
            f.Repo.InitializeContextAsync(Init("c1", Day1)));
    }


    [Fact]
    public async Task Complete_RequiresFullConfirmation()
    {
        var f = New();
        await Claim(f, Day1);
        await f.Repo.InitializeContextAsync(Init("c1", Day1));
        await f.Repo.PersistGenerationAsync(Persist("c1", Day1, new string('m', 40000)));
        await Assert.ThrowsAsync<OccurrenceConsistencyException>(() =>
            f.Repo.CompleteAsync(new CompleteRequest("u1", "op1", Day1, "c1")));
    }


    [Fact]
    public async Task Progress_ReplacesRejectedLeavesBeforeNextSend()
    {
        var f = New();
        await Claim(f, Day1);
        await f.Repo.InitializeContextAsync(Init("c1", Day1));
        await f.Repo.PersistGenerationAsync(Persist("c1", Day1, new string('m', 40000)));
        var replaced = await f.Repo.ReplaceLeafAsync(new ReplaceLeafRequest(
            "u1", "op1", Day1, "c1", "p0", PlanFallbackKind.ToLiteralRich));
        Assert.True(replaced.Fresh.Count >= 2);
        var receipt = (await f.Repo.GetReceiptAsync("u1", "op1", Day1))!;
        Assert.Equal(replaced.Plan.Leaves.Count, receipt.TotalParts);
        var confirmed = await f.Repo.ConfirmLeafAsync(new ConfirmLeafRequest(
            "u1", "op1", Day1, "c1", replaced.Fresh[0].Id, 901));
        Assert.True(confirmed.Leaves[0].Confirmed);
    }
}
