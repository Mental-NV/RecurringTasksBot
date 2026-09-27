// Delivery plans: initial leaves, validation, confirmation, fallback transitions, row codec, and repository completion gates.
using RecurringTasksBot.Application;
using RecurringTasksBot.Infrastructure.Persistence;

namespace RecurringTasksBot.Tests;

public sealed class DeliveryPlanTests
{


    [Fact]
    public void InitialPlan_MatchesSpecExampleShape()
    {
        var source = "## Status\nReady";
        var plan = DeliveryPlan.CreateInitial("v1", source);
        Assert.Equal(1, plan.SchemaVersion);
        Assert.Equal("v1", plan.AnswerVersion);
        Assert.Equal(DeliveryPlan.HashSource(source), plan.SourceSha256);
        Assert.Equal(1, plan.Revision);
        var leaf = Assert.Single(plan.Leaves);
        Assert.Equal("p0", leaf.Id);
        Assert.Equal(PlanLeafKind.Markdown, leaf.Kind);
        Assert.Equal(0, leaf.StartUtf16);
        Assert.Equal(15, leaf.LengthUtf16);
        Assert.Equal(PlanFallbackStage.Native, leaf.FallbackStage);
        Assert.False(leaf.Confirmed);
        Assert.Null(leaf.MessageId);
    }


    [Fact]
    public void InitialPlan_KeepsOversizedMarkdownAsOneLeaf()
    {
        // Long URLs/markup may occupy little displayed text; the server may
        // accept the source intact.
        var plan = DeliveryPlan.CreateInitial("v1", new string('x', 40000));
        var leaf = Assert.Single(plan.Leaves);
        Assert.Equal(PlanLeafKind.Markdown, leaf.Kind);
        Assert.Equal(40000, leaf.LengthUtf16);
    }


    [Fact]
    public void PlanValidation_RejectsMalformedState()
    {
        var source = "## Status\nReady";
        var valid = DeliveryPlan.CreateInitial("v1", source);
        Assert.Throws<PayloadIntegrityException>(() =>
            DeliveryPlan.Validate(valid with { SchemaVersion = 4 }, source));
        try
        {
            DeliveryPlan.Validate(valid with { SchemaVersion = 4 }, source);
        }
        catch (PayloadIntegrityException ex)
        {
            Assert.Equal(OccurrenceFailureCodes.UnsupportedPayloadVersion, ex.Code);
        }
        Corrupt(valid with
        {
            Leaves = [new PlanLeaf("p0", PlanLeafKind.Markdown, 1, 14, PlanFallbackStage.Native, false, null)],
        });
        Corrupt(valid with
        {
            Leaves = [new PlanLeaf("p0", PlanLeafKind.LiteralRich, 0, 15, PlanFallbackStage.Native, false, null)],
        });
        Corrupt(valid with
        {
            Leaves = [new PlanLeaf("p0", PlanLeafKind.Markdown, 0, 15, PlanFallbackStage.Native, true, null)],
        });
        Corrupt(valid with
        {
            Leaves = [new PlanLeaf("p0", PlanLeafKind.Markdown, 0, 15, PlanFallbackStage.Native, false, 901)],
        });
        Corrupt(valid with
        {
            Leaves =
            [
                new PlanLeaf("a", PlanLeafKind.Markdown, 0, 7, PlanFallbackStage.Native, false, null),
                new PlanLeaf("b", PlanLeafKind.Markdown, 7, 8, PlanFallbackStage.Native, true, 902),
            ],
        });

        void Corrupt(DeliveryPlanDoc plan)
        {
            var ex = Assert.Throws<PayloadIntegrityException>(() => DeliveryPlan.Validate(plan, source));
            Assert.Equal(OccurrenceFailureCodes.PayloadCorrupt, ex.Code);
        }
    }


    [Fact]
    public void PlanFallback_MarkdownRejectionBecomesLiteralRich()
    {
        var source = new string('m', 40000);
        var plan = DeliveryPlan.CreateInitial("v1", source);
        var (next, fresh) = DeliveryPlan.ReplaceWithLiteralRich(plan, source, "p0");
        Assert.Equal(2, next.Revision);
        Assert.All(next.Leaves, l =>
        {
            Assert.Equal(PlanLeafKind.LiteralRich, l.Kind);
            Assert.Equal(PlanFallbackStage.RichFull, l.FallbackStage);
            Assert.False(l.Confirmed);
        });
        Assert.StartsWith("p0#2:", fresh[0].Id);
        Assert.Equal(source, string.Concat(next.Leaves.Select(l => source.Substring(l.StartUtf16, l.LengthUtf16))));
        DeliveryPlan.Validate(next, source);
    }


    [Fact]
    public void PlanConfirmation_IsIdempotentAndOrdered()
    {
        var source = new string('m', 40000);
        var plan = DeliveryPlan.CreateInitial("v1", source);
        var (next, fresh) = DeliveryPlan.ReplaceWithLiteralRich(plan, source, "p0");
        Assert.True(fresh.Count >= 2);
        Assert.Throws<PayloadIntegrityException>(() => DeliveryPlan.Confirm(next, fresh[1].Id, 903));
        var confirmed = DeliveryPlan.Confirm(next, fresh[0].Id, 901);
        Assert.True(confirmed.Leaves[0].Confirmed);
        Assert.Equal(901, confirmed.Leaves[0].MessageId);
        Assert.Same(confirmed, DeliveryPlan.Confirm(confirmed, fresh[0].Id, 901));
        Assert.Throws<PayloadIntegrityException>(() => DeliveryPlan.Confirm(confirmed, fresh[0].Id, 902));
        Assert.Throws<PayloadIntegrityException>(() => DeliveryPlan.ReplaceWithLiteralRich(confirmed, source, fresh[0].Id));
    }


    [Fact]
    public void PlanFallback_AdvancesThroughSmallLiteralToPlain()
    {
        var source = new string('m', 40000);
        var plan = DeliveryPlan.CreateInitial("v1", source);
        var (rich, fresh) = DeliveryPlan.ReplaceWithLiteralRich(plan, source, "p0");
        var (small, smallFresh) = DeliveryPlan.ReplaceWithSmallLiteral(rich, source, fresh[0].Id);
        Assert.All(smallFresh, l =>
        {
            Assert.Equal(PlanLeafKind.LiteralRich, l.Kind);
            Assert.Equal(PlanFallbackStage.RichSmall, l.FallbackStage);
        });
        var (plain, plainFresh) = DeliveryPlan.ReplaceWithPlain(small, source, smallFresh[0].Id);
        Assert.All(plainFresh, l =>
        {
            Assert.Equal(PlanLeafKind.LiteralPlain, l.Kind);
            Assert.Equal(PlanFallbackStage.Plain, l.FallbackStage);
        });
        Assert.True(plainFresh.All(l => l.LengthUtf16 <= 4096));
        Assert.Equal(source, string.Concat(plain.Leaves.Select(l => source.Substring(l.StartUtf16, l.LengthUtf16))));
        // Plain content rejection is terminal: no transition out of plain.
        Assert.Throws<PayloadIntegrityException>(() =>
            DeliveryPlan.ReplaceWithPlain(plain, source, plainFresh[0].Id));
        // No skipping stages either.
        Assert.Throws<PayloadIntegrityException>(() =>
            DeliveryPlan.ReplaceWithSmallLiteral(plan, source, "p0"));
    }


    [Fact]
    public void PlanFallback_UnavailableRichMethodGoesDirectlyToPlain()
    {
        var source = "## Hi\nBody";
        var plan = DeliveryPlan.CreateInitial("v1", source);
        var (next, fresh) = DeliveryPlan.ReplaceWithPlain(plan, source, "p0");
        var leaf = Assert.Single(next.Leaves);
        Assert.Equal(PlanLeafKind.LiteralPlain, leaf.Kind);
        Assert.Equal(source, source.Substring(leaf.StartUtf16, leaf.LengthUtf16));
        Assert.Single(fresh);
    }


    [Fact]
    public void PlanLeafLimit_IsExplicitFailure()
    {
        var source = new string('a', 32769 + 127);
        var leaves = new List<PlanLeaf>
        {
            new("p0", PlanLeafKind.Markdown, 0, 32769, PlanFallbackStage.Native, false, null),
        };
        for (var i = 0; i < 127; i++)
            leaves.Add(new PlanLeaf($"p{i + 1}", PlanLeafKind.Markdown, 32769 + i, 1, PlanFallbackStage.Native, false, null));
        var plan = new DeliveryPlanDoc(StorageLimits.SchemaVersion, "v1", DeliveryPlan.HashSource(source), 1, leaves);
        DeliveryPlan.Validate(plan, source);
        var ex = Assert.Throws<PayloadIntegrityException>(() =>
            DeliveryPlan.ReplaceWithLiteralRich(plan, source, "p0"));
        Assert.Equal(OccurrenceFailureCodes.DeliveryPlanLimit, ex.Code);
    }



    [Fact]
    public void PlanCodec_RoundTripsProgress()
    {
        var source = new string('m', 40000);
        var plan = DeliveryPlan.CreateInitial("av1", source);
        var (replaced, _) = DeliveryPlan.ReplaceWithLiteralRich(plan, source, "p0");
        var confirmed = DeliveryPlan.Confirm(replaced, replaced.Leaves[0].Id, 901);
        var json = PlanRowCodec.Serialize(confirmed);
        var parsed = PlanRowCodec.Deserialize(json, source);
        Assert.Equal(json, PlanRowCodec.Serialize(parsed));
        Assert.Contains("\"message_id\":901", json, StringComparison.Ordinal);
        Assert.Contains("\"message_id\":null", json, StringComparison.Ordinal);
    }


    [Fact]
    public void PlanCodec_RejectsBadPayloads()
    {
        var source = "## Status\nReady";
        var plan = DeliveryPlan.CreateInitial("av1", source);
        var json = PlanRowCodec.Serialize(plan);
        Assert.Throws<PayloadIntegrityException>(() =>
            PlanRowCodec.Deserialize(json.Replace("markdown", "bogus"), source));
        Assert.Throws<PayloadIntegrityException>(() =>
            PlanRowCodec.Deserialize(json.Replace("\"schema_version\":1", "\"schema_version\":4"), source));
        try
        {
            PlanRowCodec.Deserialize(json.Replace("\"schema_version\":1", "\"schema_version\":4"), source);
        }
        catch (PayloadIntegrityException ex)
        {
            Assert.Equal(OccurrenceFailureCodes.UnsupportedPayloadVersion, ex.Code);
        }
        Assert.Throws<PayloadIntegrityException>(() => PlanRowCodec.Deserialize("not json", source));
        Assert.Throws<PayloadIntegrityException>(() =>
            PlanRowCodec.Deserialize(json, source + "extra"));
    }
}
