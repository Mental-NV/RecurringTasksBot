// Delivery plan: ordered leaves covering the canonical source exactly
// once, with deterministic fallback transitions. Source ranges use .NET
// UTF-16 offsets; chunking cuts are rune-aligned so surrogates never split.
using System.Security.Cryptography;
using System.Text;

namespace RecurringTasksBot.Core;

public enum PlanLeafKind
{
    Markdown,
    LiteralRich,
    LiteralPlain,
    LegacyHtml,
}

public enum PlanFallbackStage
{
    Native,
    RichFull,
    RichSmall,
    Plain,
}

public sealed record PlanLeaf(
    string Id,
    PlanLeafKind Kind,
    int StartUtf16,
    int LengthUtf16,
    PlanFallbackStage FallbackStage,
    bool Confirmed,
    long? MessageId);

public sealed record DeliveryPlanDoc(
    int SchemaVersion,
    string AnswerVersion,
    string SourceSha256,
    int Revision,
    IReadOnlyList<PlanLeaf> Leaves);

public static class DeliveryPlan
{
    public static string HashSource(string source) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();

    // One markdown leaf covering the entire canonical answer, even when the
    // source exceeds 32,768: long URLs/markup may occupy little displayed
    // text and the server may accept it intact.
    public static DeliveryPlanDoc CreateInitial(string answerVersion, string source)
    {
        if (string.IsNullOrEmpty(answerVersion))
            throw new ArgumentOutOfRangeException(nameof(answerVersion));
        var plan = new DeliveryPlanDoc(Phase3Limits.SchemaVersion, answerVersion,
            HashSource(source), 1,
            [new PlanLeaf("p0", PlanLeafKind.Markdown, 0, source.Length, PlanFallbackStage.Native, false, null)]);
        Validate(plan, source);
        return plan;
    }

    // Capability-gate initialization: bounded literal-rich leaves instead.
    public static DeliveryPlanDoc CreateInitialLiteral(string answerVersion, string source)
    {
        if (string.IsNullOrEmpty(answerVersion))
            throw new ArgumentOutOfRangeException(nameof(answerVersion));
        var chunks = Phase3LiteralChunker.SplitRich(source);
        var leaves = new List<PlanLeaf>(chunks.Count);
        var offset = 0;
        for (var i = 0; i < chunks.Count; i++)
        {
            leaves.Add(new PlanLeaf($"p0#1:{i}", PlanLeafKind.LiteralRich,
                offset, chunks[i].Length, PlanFallbackStage.RichFull, false, null));
            offset += chunks[i].Length;
        }
        var plan = new DeliveryPlanDoc(Phase3Limits.SchemaVersion, answerVersion,
            HashSource(source), 1, leaves);
        Validate(plan, source);
        return plan;
    }

    public static void Validate(DeliveryPlanDoc plan, string source)
    {
        if (plan.SchemaVersion != Phase3Limits.SchemaVersion)
            throw new Phase3PayloadException(Phase3FailureCodes.UnsupportedPayloadVersion,
                $"unsupported payload version {plan.SchemaVersion}");
        FailWhenCorrupt(string.IsNullOrEmpty(plan.AnswerVersion), "missing answer version");
        var leaves = plan.Leaves ?? [];
        FailWhenCorrupt(leaves.Count == 0, "no leaves");
        FailWhenCorrupt(leaves.Count > Phase3Limits.MaxPlanLeaves, "too many leaves");
        FailWhenCorrupt(!string.Equals(plan.SourceSha256, HashSource(source), StringComparison.Ordinal),
            "source hash mismatch");
        var offset = 0;
        var seenUnconfirmed = false;
        foreach (var leaf in leaves)
        {
            FailWhenCorrupt(string.IsNullOrEmpty(leaf.Id), "missing leaf id");
            FailWhenCorrupt(leaf.LengthUtf16 <= 0, "non-positive leaf length");
            FailWhenCorrupt(leaf.StartUtf16 != offset, "range coverage gap");
            FailWhenCorrupt(!ValidStageKind(leaf), "invalid stage/kind combination");
            FailWhenCorrupt(leaf.StartUtf16 > 0 && IsSplitSurrogate(source, leaf.StartUtf16),
                "range splits a surrogate pair");
            FailWhenCorrupt(leaf.StartUtf16 + leaf.LengthUtf16 < source.Length &&
                IsSplitSurrogate(source, leaf.StartUtf16 + leaf.LengthUtf16),
                "range splits a surrogate pair");
            FailWhenCorrupt(leaf.Confirmed && (leaf.MessageId is null || leaf.MessageId <= 0),
                "confirmed leaf without message id");
            FailWhenCorrupt(!leaf.Confirmed && leaf.MessageId is > 0,
                "unconfirmed leaf with message id");
            FailWhenCorrupt(seenUnconfirmed && leaf.Confirmed, "confirmed leaf after unconfirmed leaf");
            seenUnconfirmed |= !leaf.Confirmed;
            offset += leaf.LengthUtf16;
        }
        FailWhenCorrupt(offset != source.Length, "ranges do not cover the source exactly");
    }

    // Idempotent by leaf ID/message ID. Confirms the first unconfirmed leaf.
    public static DeliveryPlanDoc Confirm(DeliveryPlanDoc plan, string leafId, long messageId)
    {
        if (messageId <= 0)
            throw new ArgumentOutOfRangeException(nameof(messageId));
        var index = plan.Leaves.ToList().FindIndex(l => l.Id == leafId);
        if (index < 0)
            throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt, "unknown leaf id");
        var leaf = plan.Leaves[index];
        if (leaf.Confirmed)
        {
            if (leaf.MessageId != messageId)
                throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt,
                    "confirmation message id mismatch");
            return plan;
        }
        if (plan.Leaves.Take(index).Any(l => !l.Confirmed))
            throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt,
                "confirmation out of order");
        var leaves = plan.Leaves.ToList();
        leaves[index] = leaf with { Confirmed = true, MessageId = messageId };
        return plan with { Leaves = leaves };
    }

    // Definite content rejection of a markdown leaf: bounded literal-rich
    // leaves with the same boundary preference. No structure splitting.
    public static (DeliveryPlanDoc Plan, IReadOnlyList<PlanLeaf> Fresh) ReplaceWithLiteralRich(
        DeliveryPlanDoc plan, string source, string leafId)
    {
        var (index, leaf) = FirstUnconfirmed(plan, leafId);
        if (leaf.Kind != PlanLeafKind.Markdown || leaf.FallbackStage != PlanFallbackStage.Native)
            throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt,
                "invalid fallback transition");
        var chunks = Phase3LiteralChunker.SplitRich(LeafText(source, leaf));
        return Splice(plan, index, leaf, PlanLeafKind.LiteralRich, PlanFallbackStage.RichFull, chunks);
    }

    // A literal-rich leaf rejected specifically for size: conservative chunks.
    public static (DeliveryPlanDoc Plan, IReadOnlyList<PlanLeaf> Fresh) ReplaceWithSmallLiteral(
        DeliveryPlanDoc plan, string source, string leafId)
    {
        var (index, leaf) = FirstUnconfirmed(plan, leafId);
        if (leaf.Kind != PlanLeafKind.LiteralRich || leaf.FallbackStage != PlanFallbackStage.RichFull)
            throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt,
                "invalid fallback transition");
        var chunks = Phase3LiteralChunker.SplitConservative(LeafText(source, leaf));
        return Splice(plan, index, leaf, PlanLeafKind.LiteralRich, PlanFallbackStage.RichSmall, chunks);
    }

    // Non-size rejection of literal blocks, or an unavailable rich method
    // (directly from markdown): conservative plain leaves. Plain content
    // rejection is terminal for the occurrence and has no transition.
    public static (DeliveryPlanDoc Plan, IReadOnlyList<PlanLeaf> Fresh) ReplaceWithPlain(
        DeliveryPlanDoc plan, string source, string leafId)
    {
        var (index, leaf) = FirstUnconfirmed(plan, leafId);
        var fromMarkdown = leaf.Kind == PlanLeafKind.Markdown && leaf.FallbackStage == PlanFallbackStage.Native;
        var fromLiteral = leaf.Kind == PlanLeafKind.LiteralRich &&
            leaf.FallbackStage is PlanFallbackStage.RichFull or PlanFallbackStage.RichSmall;
        if (!fromMarkdown && !fromLiteral)
            throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt,
                "invalid fallback transition");
        var chunks = Phase3LiteralChunker.SplitConservative(LeafText(source, leaf));
        return Splice(plan, index, leaf, PlanLeafKind.LiteralPlain, PlanFallbackStage.Plain, chunks);
    }

    private static (int Index, PlanLeaf Leaf) FirstUnconfirmed(DeliveryPlanDoc plan, string leafId)
    {
        var leaves = plan.Leaves.ToList();
        var index = leaves.FindIndex(l => l.Id == leafId);
        if (index < 0 || leaves[index].Confirmed || leaves.Take(index).Any(l => !l.Confirmed))
            throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt,
                "replacement targets a confirmed or out-of-order leaf");
        return (index, leaves[index]);
    }

    private static (DeliveryPlanDoc Plan, IReadOnlyList<PlanLeaf> Fresh) Splice(
        DeliveryPlanDoc plan, int index, PlanLeaf parent,
        PlanLeafKind kind, PlanFallbackStage stage,
        IReadOnlyList<string> chunks)
    {
        var total = plan.Leaves.Count - 1 + chunks.Count;
        if (total > Phase3Limits.MaxPlanLeaves)
            throw new Phase3PayloadException(Phase3FailureCodes.DeliveryPlanLimit,
                $"plan would exceed {Phase3Limits.MaxPlanLeaves} leaves");
        var revision = plan.Revision + 1;
        var fresh = new List<PlanLeaf>(chunks.Count);
        var offset = parent.StartUtf16;
        for (var i = 0; i < chunks.Count; i++)
        {
            fresh.Add(new PlanLeaf($"{parent.Id}#{revision}:{i}", kind,
                offset, chunks[i].Length, stage, false, null));
            offset += chunks[i].Length;
        }
        var leaves = plan.Leaves.ToList();
        leaves.RemoveAt(index);
        leaves.InsertRange(index, fresh);
        return (plan with { Revision = revision, Leaves = leaves }, fresh);
    }

    private static string LeafText(string source, PlanLeaf leaf)
    {
        if (leaf.StartUtf16 < 0 || leaf.LengthUtf16 <= 0 ||
            leaf.StartUtf16 + leaf.LengthUtf16 > source.Length)
            throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt, "leaf range out of bounds");
        return source.Substring(leaf.StartUtf16, leaf.LengthUtf16);
    }

    private static bool ValidStageKind(PlanLeaf leaf) => (leaf.FallbackStage, leaf.Kind) switch
    {
        (PlanFallbackStage.Native, PlanLeafKind.Markdown) => true,
        (PlanFallbackStage.RichFull, PlanLeafKind.LiteralRich) => true,
        (PlanFallbackStage.RichSmall, PlanLeafKind.LiteralRich) => true,
        (PlanFallbackStage.Plain, PlanLeafKind.LiteralPlain) => true,
        _ => false,
    };

    private static bool IsSplitSurrogate(string source, int offset) =>
        offset > 0 && offset < source.Length &&
        char.IsHighSurrogate(source[offset - 1]) && char.IsLowSurrogate(source[offset]);

    private static void FailWhenCorrupt(bool condition, string detail)
    {
        if (condition)
            throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt, $"plan invalid: {detail}");
    }
}
