// Phase 3 focused tests: source bound, capability gate, literal chunking,
// error classification, delivery plan, context snapshot, budget, config,
// wire contract, and Phase 3 request construction. All offline.
using System.Text;
using System.Text.Json;
using RecurringTasksBot.Core;

namespace RecurringTasksBot.Tests;

public sealed class Phase3CoreTests
{
    // ---- Answer source bound ----

    [Fact]
    public void SourceBound_AcceptsExactly131072Scalars()
    {
        var canonical = new string('a', Phase3Limits.AnswerSourceMaxScalars);
        Assert.Same(canonical, AnswerSourceBound.RequireWithinBound(canonical));
    }

    [Fact]
    public void SourceBound_RejectsOneScalarBeyondWithoutTruncation()
    {
        var canonical = new string('a', Phase3Limits.AnswerSourceMaxScalars + 1);
        var ex = Assert.Throws<Phase3PayloadException>(() => AnswerSourceBound.RequireWithinBound(canonical));
        Assert.Equal(Phase3FailureCodes.AnswerSourceLimit, ex.Code);
    }

    [Fact]
    public void Accumulator_CountsAcrossChunksWithoutSplittingScalars()
    {
        // Decoded SSE chunks never end mid-scalar; each chunk's runes count
        // once, including supplementary characters and combining sequences.
        var acc = new AnswerSourceAccumulator();
        acc.Append("a\U0001F600");
        acc.Append("e\u0301b");
        Assert.Equal(5, acc.EffectiveScalarCount);
        Assert.False(acc.IsOverLimit);
        Assert.Equal("a\U0001F600e\u0301b", acc.GetCanonical());
    }

    [Fact]
    public void Accumulator_EdgeWhitespaceCannotCauseFalseRejection()
    {
        var acc = new AnswerSourceAccumulator();
        acc.Append("   \n");
        acc.Append(new string('a', Phase3Limits.AnswerSourceMaxScalars));
        acc.Append(new string(' ', 5000));
        Assert.False(acc.IsOverLimit);
        Assert.Equal(new string('a', Phase3Limits.AnswerSourceMaxScalars), acc.GetCanonical());
    }

    [Fact]
    public void Accumulator_ContentBeyondBoundIsTerminal()
    {
        var acc = new AnswerSourceAccumulator();
        acc.Append(new string('b', Phase3Limits.AnswerSourceMaxScalars));
        Assert.False(acc.IsOverLimit);
        acc.Append("c");
        Assert.True(acc.IsOverLimit);
        Assert.Throws<Phase3PayloadException>(() => acc.GetCanonical());
    }

    [Fact]
    public void Accumulator_WhitespaceFloodStaysBoundedAndTrimsExactly()
    {
        var acc = new AnswerSourceAccumulator();
        acc.Append("x");
        acc.Append(new string(' ', 1_000_001));
        Assert.Equal(1, acc.EffectiveScalarCount);
        Assert.False(acc.IsOverLimit);
        Assert.True(acc.HasContent);
        Assert.Equal("x", acc.GetCanonical());
    }

    [Fact]
    public void Accumulator_ContentAfterExcessWhitespaceTripsSourceLimit()
    {
        var acc = new AnswerSourceAccumulator();
        acc.Append("x");
        acc.Append(new string(' ', Phase3Limits.AnswerSourceMaxScalars + 2));
        var ex = Assert.Throws<Phase3PayloadException>(() => acc.Append("y"));
        Assert.Equal(Phase3FailureCodes.AnswerSourceLimit, ex.Code);
    }

    // ---- Capability gate ----

    [Theory]
    [InlineData("![cover](https://example.org/cover.png)", true)]
    [InlineData("see <img src=\"https://example.org/x.png\"> here", true)]
    [InlineData("see <IMG SRC=\"https://example.org/x.png\"> here", true)]
    [InlineData("see <  img  src=\"x\"> here", true)]
    [InlineData("<video src=\"x\">", true)]
    [InlineData("<audio src=\"x\">", true)]
    [InlineData("<source src=\"x\">", true)]
    [InlineData("<iframe src=\"x\">", true)]
    [InlineData("<script>alert(1)</script>", true)]
    [InlineData("<tg-button text=\"Go\">", true)]
    [InlineData("<TG-MAP>", true)]
    [InlineData("<tg-emoji id=\"1\">", true)]
    [InlineData("<tg-thinking>", true)]
    [InlineData("<tg-document>", true)]
    [InlineData("<tg-collage>", true)]
    [InlineData("<tg-slideshow>", true)]
    [InlineData("<tg-map-view />", true)]
    [InlineData("<TG-BUTTON-2 text=\"Go\">", true)]
    [InlineData("see <tg-emoji-custom id=\"1\"> here", true)]
    [InlineData("use tg-map wisely", false)]
    [InlineData("```\n<img>\n```", true)] // code examples stay literal too
    [InlineData("[report](https://example.org/report)", false)]
    [InlineData("Review the [report](https://example.org/report) ✅", false)]
    [InlineData("the source of truth <b>bold</b>", false)]
    [InlineData("an image word without markup", false)]
    [InlineData("a < b and c > d", false)]
    [InlineData("description with <details><summary>T</summary>B</details>", false)]
    [InlineData("", false)]
    public void CapabilityGate_MatchesSpec(string answer, bool expected) =>
        Assert.Equal(expected, Phase3CapabilityGate.RequiresLiteral(answer));

    // ---- Literal chunking ----

    [Theory]
    [InlineData(32767, 1)]
    [InlineData(32768, 1)]
    [InlineData(32769, 2)]
    public void SplitRich_BoundaryCounts(int scalars, int expectedParts) =>
        Assert.Equal(expectedParts, Phase3LiteralChunker.SplitRich(new string('a', scalars)).Count);

    [Fact]
    public void SplitRich_PrefersNewlineAndKeepsItInOriginalSlice()
    {
        var source = new string('a', 32700) + "\n" + new string('b', 1000);
        var parts = Phase3LiteralChunker.SplitRich(source);
        Assert.Equal(2, parts.Count);
        Assert.EndsWith("\n", parts[0]);
        Assert.Equal(32701, AnswerSourceBound.CountScalars(parts[0]));
        Assert.Equal(source, string.Concat(parts));
    }

    [Fact]
    public void SplitRich_NeverSplitsSurrogatePairs()
    {
        var source = new string('a', 32767) + "\U0001F600" + new string('b', 100);
        var parts = Phase3LiteralChunker.SplitRich(source);
        Assert.Equal(source, string.Concat(parts));
        foreach (var part in parts)
            Assert.DoesNotContain("\uFFFD", Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(part)));
        Assert.StartsWith(new string('a', 32767) + "\U0001F600", parts[0]);
    }

    [Fact]
    public void SplitRich_PrefersGraphemeBoundaryOverSplittingMarks()
    {
        var source = new string('x', 32767) + "e\u0301" + new string('y', 100);
        var parts = Phase3LiteralChunker.SplitRich(source);
        Assert.Equal(source, string.Concat(parts));
        Assert.Equal(new string('x', 32767), parts[0]);
        Assert.StartsWith("e\u0301", parts[1]);
    }

    [Theory]
    [InlineData(4096, 1)]
    [InlineData(4097, 2)]
    public void SplitConservative_AsciiDualBound(int chars, int expectedParts) =>
        Assert.Equal(expectedParts, Phase3LiteralChunker.SplitConservative(new string('a', chars)).Count);

    [Fact]
    public void SplitConservative_CountsUtf16UnitsForAstralChars()
    {
        Assert.Single(Phase3LiteralChunker.SplitConservative(string.Concat(Enumerable.Repeat("\U0001F600", 2048))));
        var parts = Phase3LiteralChunker.SplitConservative(string.Concat(Enumerable.Repeat("\U0001F600", 2049)));
        Assert.Equal(2, parts.Count);
        Assert.Equal(4096, parts[0].Length);
        Assert.Equal(2, parts[1].Length);
    }

    [Fact]
    public void SplitConservative_NeverSplitsCrlf()
    {
        var source = new string('a', 4095) + "\r\n" + new string('b', 100);
        var parts = Phase3LiteralChunker.SplitConservative(source);
        Assert.Equal(source, string.Concat(parts));
        Assert.Equal(new string('a', 4095), parts[0]);
        Assert.StartsWith("\r\n", parts[1]);
    }

    [Fact]
    public void SplitConservative_KeepsHangulJamoTogether()
    {
        // Choseong + jungseong + jongseong: one text element, three runes.
        const string syllable = "\u1100\u1161\u11A8";
        var source = new string('x', 4094) + syllable + new string('y', 100);
        Assert.Equal(3, syllable.EnumerateRunes().Count());
        var parts = Phase3LiteralChunker.SplitConservative(source);
        Assert.Equal(source, string.Concat(parts));
        Assert.Equal(new string('x', 4094), parts[0]);
        Assert.StartsWith(syllable, parts[1]);
    }

    [Fact]
    public void SplitConservative_KeepsZwjSequenceTogether()
    {
        const string family = "👨‍👩‍👧‍👦";
        var source = new string('x', 4094) + family + new string('y', 100);
        var parts = Phase3LiteralChunker.SplitConservative(source);
        Assert.Equal(source, string.Concat(parts));
        Assert.Equal(new string('x', 4094), parts[0]);
        Assert.StartsWith(family, parts[1]);
    }

    // ---- Error classification ----

    [Theory]
    [InlineData(400, 400, "Bad Request: can't parse entities: ...", TelegramDisposition.ContentRejection, "content_rejected")]
    [InlineData(400, 400, "Bad Request: can’t parse rich message", TelegramDisposition.ContentRejection, "content_rejected")]
    [InlineData(400, 400, "can't parse markdown", TelegramDisposition.ContentRejection, "content_rejected")]
    [InlineData(400, 400, "Bad Request: message is too long", TelegramDisposition.ContentRejection, "content_rejected")]
    [InlineData(200, 400, "Bad Request: text is too long", TelegramDisposition.ContentRejection, "content_rejected")]
    [InlineData(200, 400, "MESSAGE_TOO_LONG", TelegramDisposition.ContentRejection, "content_rejected")]
    [InlineData(200, 400, "too many blocks", TelegramDisposition.ContentRejection, "content_rejected")]
    [InlineData(200, 400, "Too many columns", TelegramDisposition.ContentRejection, "content_rejected")]
    [InlineData(200, 400, "nesting too deep", TelegramDisposition.ContentRejection, "content_rejected")]
    [InlineData(200, 400, "nesting limit exceeded", TelegramDisposition.ContentRejection, "content_rejected")]
    [InlineData(400, 400, "Bad Request: message not found", TelegramDisposition.TerminalFailure, "bad_request")]
    [InlineData(404, 404, "Not Found", TelegramDisposition.UnknownMethod, "unknown_method")]
    [InlineData(200, -1, "unknown method: sendRichMessage", TelegramDisposition.UnknownMethod, "unknown_method")]
    [InlineData(200, 400, "method not found", TelegramDisposition.UnknownMethod, "unknown_method")]
    [InlineData(403, 403, "Forbidden: bot was blocked by the user", TelegramDisposition.PermanentRecipient, "recipient_failure")]
    [InlineData(200, 400, "Bad Request: chat not found", TelegramDisposition.PermanentRecipient, "recipient_failure")]
    [InlineData(404, 404, "chat not found", TelegramDisposition.PermanentRecipient, "recipient_failure")]
    [InlineData(429, 429, "Too Many Requests", TelegramDisposition.RateLimited, "rate_limited")]
    [InlineData(401, 401, "Unauthorized", TelegramDisposition.TerminalFailure, "unauthorized")]
    [InlineData(200, 400, "Bad Request: wrong file identifier", TelegramDisposition.TerminalFailure, "bad_request")]
    [InlineData(500, 500, "Internal Server Error", TelegramDisposition.Transient, "transient")]
    [InlineData(408, -1, "Request Timeout", TelegramDisposition.Transient, "transient")]
    [InlineData(-1, -1, "connection reset", TelegramDisposition.Transient, "transient")]
    public void ClassifiesTelegramErrors(
        int http, int api, string desc, TelegramDisposition expected, string category)
    {
        int? h = http < 0 ? null : http;
        int? a = api < 0 ? null : api;
        var result = TelegramErrorClassifier.Classify(h, a, desc);
        Assert.Equal(expected, result.Disposition);
        Assert.Equal(category, result.Category);
    }

    // ---- Delivery plan ----

    [Fact]
    public void InitialPlan_MatchesSpecExampleShape()
    {
        var source = "## Status\nReady";
        var plan = DeliveryPlan.CreateInitial("v1", source);
        Assert.Equal(3, plan.SchemaVersion);
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
        Assert.Throws<Phase3PayloadException>(() =>
            DeliveryPlan.Validate(valid with { SchemaVersion = 4 }, source));
        try
        {
            DeliveryPlan.Validate(valid with { SchemaVersion = 4 }, source);
        }
        catch (Phase3PayloadException ex)
        {
            Assert.Equal(Phase3FailureCodes.UnsupportedPayloadVersion, ex.Code);
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
            var ex = Assert.Throws<Phase3PayloadException>(() => DeliveryPlan.Validate(plan, source));
            Assert.Equal(Phase3FailureCodes.PayloadCorrupt, ex.Code);
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
        Assert.Throws<Phase3PayloadException>(() => DeliveryPlan.Confirm(next, fresh[1].Id, 903));
        var confirmed = DeliveryPlan.Confirm(next, fresh[0].Id, 901);
        Assert.True(confirmed.Leaves[0].Confirmed);
        Assert.Equal(901, confirmed.Leaves[0].MessageId);
        Assert.Same(confirmed, DeliveryPlan.Confirm(confirmed, fresh[0].Id, 901));
        Assert.Throws<Phase3PayloadException>(() => DeliveryPlan.Confirm(confirmed, fresh[0].Id, 902));
        Assert.Throws<Phase3PayloadException>(() => DeliveryPlan.ReplaceWithLiteralRich(confirmed, source, fresh[0].Id));
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
        Assert.Throws<Phase3PayloadException>(() =>
            DeliveryPlan.ReplaceWithPlain(plain, source, plainFresh[0].Id));
        // No skipping stages either.
        Assert.Throws<Phase3PayloadException>(() =>
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
        var plan = new DeliveryPlanDoc(3, "v1", DeliveryPlan.HashSource(source), 1, leaves);
        DeliveryPlan.Validate(plan, source);
        var ex = Assert.Throws<Phase3PayloadException>(() =>
            DeliveryPlan.ReplaceWithLiteralRich(plan, source, "p0"));
        Assert.Equal(Phase3FailureCodes.DeliveryPlanLimit, ex.Code);
    }

    // ---- System template, snapshot, envelope, budget ----

    [Fact]
    public void SystemTemplate_SubstitutesOnlyNamedPlaceholders()
    {
        var rendered = Phase3SystemTemplate.Render(24000, 32768, null);
        Assert.Contains("24000", rendered, StringComparison.Ordinal);
        Assert.Contains("32768", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("{TargetAnswerTextChars}", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("{MaxRichMessageChars}", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemTemplate_AppendsAdminInstructionWithLabelAndBoundsIt()
    {
        var rendered = Phase3SystemTemplate.Render(24000, 32768, "Be extra concise.");
        Assert.Contains("Additional administrator instruction", rendered, StringComparison.Ordinal);
        Assert.Contains("Be extra concise.", rendered, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() =>
            Phase3SystemTemplate.Render(24000, 32768, new string('z', 20000)));
    }

    private static (string Instruction, Phase3ContextSnapshot Snapshot) Snapshot(bool withMemory)
    {
        var snapshot = Phase3ContextEnvelope.Create(
            "Report 🟢 status\r\nline \"quoted\" <tag> & more",
            "0 9 * * * *", "op1",
            new DateTime(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 25, 9, 0, 5, DateTimeKind.Utc),
            withMemory ? Phase3ContextEnvelope.ToIso8601(new DateTime(2026, 9, 24, 9, 0, 0, DateTimeKind.Utc)) : null,
            withMemory ? Phase3ContextEnvelope.ToIso8601(new DateTime(2026, 9, 24, 9, 0, 4, DateTimeKind.Utc)) : null,
            withMemory ? "## Prior\nDone ✅" : null,
            24000, 32768, null, out var instruction);
        return (instruction, snapshot);
    }

    [Fact]
    public void ContextMessages_FirstRunHasTwoMessages()
    {
        var (instruction, snapshot) = Snapshot(false);
        Assert.False(snapshot.PreviousReplyPresent);
        var messages = Phase3ContextEnvelope.BuildMessages(instruction, snapshot, null);
        Assert.Equal(["system", "user"], messages.Select(m => m.Role));
        using var doc = JsonDocument.Parse(messages[1].Content);
        Assert.Equal("Report 🟢 status\r\nline \"quoted\" <tag> & more",
            doc.RootElement.GetProperty("task_instruction").GetString());
    }

    [Fact]
    public void ContextMessages_MemoryRunHasFourEndingInCurrentUser()
    {
        var (instruction, snapshot) = Snapshot(true);
        var messages = Phase3ContextEnvelope.BuildMessages(instruction, snapshot, "## Prior\nDone ✅");
        Assert.Equal(["system", "user", "assistant", "user"], messages.Select(m => m.Role));
        Assert.Equal("## Prior\nDone ✅", messages[2].Content);
        Assert.Contains(Phase3ContextEnvelope.ArchivedReplyLabel, messages[1].Content, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => Phase3ContextEnvelope.BuildMessages(instruction, snapshot, null));
        var (_, noMemory) = Snapshot(false);
        Assert.Throws<ArgumentException>(() =>
            Phase3ContextEnvelope.BuildMessages(instruction, noMemory, "## Prior\nDone ✅"));
    }

    [Fact]
    public void ContextBudget_IncludesFullPreviousReplyOrFailsExplicitly()
    {
        var (instruction, snapshot) = Snapshot(true);
        var previous = new string('z', Phase3Limits.AnswerSourceMaxScalars);
        var messages = Phase3ContextEnvelope.BuildMessages(instruction, snapshot, previous);
        Assert.Equal(previous.Length, messages[2].Content.Length);
        var options = Phase3Config.Read(_ => null);
        Assert.True(Phase3ContextBudget.FitsBudget(messages, options, 131072));
        Assert.False(Phase3ContextBudget.FitsBudget(messages, options with { DeclaredContextTokens = 100 }, 131072));
    }

    // ---- Configuration ----

    [Fact]
    public void Phase3Config_DefaultsAndValidation()
    {
        var options = Phase3Config.Read(_ => null);
        Assert.Equal("PreviousSuccessfulReply", options.MemoryMode);
        Assert.Equal(24000, options.TargetAnswerTextChars);
        Assert.Equal(131072, options.MaxAnswerSourceChars);
        Assert.Equal(1048576, options.DeclaredContextTokens);
        Assert.Equal(65536, options.SearchContextReserveTokens);
        Assert.Equal(8192, options.ContextEnvelopeReserveTokens);
        options.Validate();
        Assert.False(Phase3Config.HasLegacyMaxStoredAnswerOverride(_ => null));
        Assert.True(Phase3Config.HasLegacyMaxStoredAnswerOverride(key =>
            key == Phase3Config.LegacyMaxStoredAnswerCharsKey ? "32768" : null));
    }

    [Theory]
    [InlineData("Foo")]
    [InlineData("previoussuccessfulreply")]
    [InlineData("NONE")]
    public void Phase3Config_UnknownMemoryModeFailsStartup(string mode)
    {
        var options = Phase3Config.Read(key =>
            key == "RecurringTasksBot:Memory:Mode" ? mode : null);
        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Theory]
    [InlineData("RecurringTasksBot:Llm:TargetAnswerTextChars", "0")]
    [InlineData("RecurringTasksBot:Llm:TargetAnswerTextChars", "40000")]
    [InlineData("RecurringTasksBot:Llm:MaxAnswerSourceChars", "0")]
    [InlineData("RecurringTasksBot:Llm:MaxAnswerSourceChars", "200000")]
    public void Phase3Config_OutOfRangeBoundsFail(string key, string value)
    {
        var options = Phase3Config.Read(k => k == key ? value : null);
        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    // ---- Native wire contract ----

    [Fact]
    public void MarkdownPayload_ArrivesUnchangedWithoutWrappers()
    {
        const string answer = "## Progress\n\n- 🟢 Ready\n- Review the [report](https://example.org/report)";
        var json = TelegramRichMessage.BuildMarkdownRequestJson(123456789, answer);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(123456789, root.GetProperty("chat_id").GetInt64());
        Assert.Equal(answer, root.GetProperty("rich_message").GetProperty("markdown").GetString());
        Assert.False(root.TryGetProperty("parse_mode", out _));
        Assert.False(root.GetProperty("rich_message").TryGetProperty("html", out _));
        Assert.False(root.GetProperty("rich_message").TryGetProperty("blocks", out _));
        Assert.False(root.TryGetProperty("entities", out _));
    }

    [Fact]
    public void LiteralRichPayload_IsOneParagraphBlockRoundTrippingExactly()
    {
        const string text = "**This stays literal** <tag> & text\nsecond line \"quoted\"";
        var json = TelegramRichMessage.BuildLiteralRichRequestJson(7, text);
        using var doc = JsonDocument.Parse(json);
        var block = Assert.Single(doc.RootElement.GetProperty("rich_message").GetProperty("blocks").EnumerateArray());
        Assert.Equal("paragraph", block.GetProperty("type").GetString());
        Assert.Equal(text, block.GetProperty("text").GetString());
    }

    // ---- Phase 3 OpenRouter request ----

    [Fact]
    public void Phase3Request_FirstRunHasTwoMessagesWithPreservedSettings()
    {
        var (instruction, snapshot) = Snapshot(false);
        var messages = Phase3ContextEnvelope.BuildMessages(instruction, snapshot, null);
        var json = OpenRouterLlmExecutor.BuildPhase3RequestJson(TestLlm.Options(), messages);
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
    public void Phase3Request_MemoryRunCarriesOneArchivedAssistantTurn()
    {
        var (instruction, snapshot) = Snapshot(true);
        const string previous = "## Prior\nDone ✅";
        var messages = Phase3ContextEnvelope.BuildMessages(instruction, snapshot, previous);
        var json = OpenRouterLlmExecutor.BuildPhase3RequestJson(TestLlm.Options(), messages);
        using var doc = JsonDocument.Parse(json);
        var wire = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(4, wire.Count);
        Assert.Equal(["system", "user", "assistant", "user"],
            wire.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal(previous, wire[2].GetProperty("content").GetString());
        Assert.Equal("user", wire[3].GetProperty("role").GetString());
    }
}
