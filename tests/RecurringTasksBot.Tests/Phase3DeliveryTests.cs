// Phase 3 delivery integration: native answers without decoration, one
// previous reply as context, deterministic fallback, separate retry
// counters, deadline yields, and legacy resumption. All offline.
using Moq;
using RecurringTasksBot.Core;

namespace RecurringTasksBot.Tests;

public sealed class Phase3DeliveryTests
{
    private static readonly DateTime Day1 = new(2026, 9, 24, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2 = new(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc);

    private sealed record V3(
        DeliveryHandler Handler, FakeOperationStore Ops, FakeDeliveryStore Receipts,
        FakeTelegramSender Sender, FakeLlmExecutor Llm,
        FakeOccurrenceRepository Repo, FakeClock Clock);

    private static V3 New(Phase3Options? phase3 = null, LlmOptions? llm = null)
    {
        var ops = new FakeOperationStore();
        var receipts = new FakeDeliveryStore();
        var sender = new FakeTelegramSender();
        var llmExec = new FakeLlmExecutor();
        var clock = new FakeClock { Now = new DateTimeOffset(Day2, TimeSpan.Zero) };
        receipts.Now = clock.Now;
        ops.Seed(TestRecords.Operation("u1", "op1"));
        var repo = new FakeOccurrenceRepository(ops, receipts, clock);
        var handler = new DeliveryHandler(ops, receipts, new FakePayloadStore(), sender, llmExec,
            llm ?? TestLlm.Options(), occurrences: repo,
            phase3Options: phase3, phase3Llm: llmExec, clock: clock);
        return new V3(handler, ops, receipts, sender, llmExec, repo, clock);
    }

    private static TelegramSendException ContentEx(string description = "Bad Request: can't parse entities") =>
        new(400, description, null, 400);

    private static async Task<DeliveryReceipt> Receipt(V3 v, DateTime scheduled) =>
        (await v.Receipts.GetAsync("u1", "op1", scheduled))!;

    [Fact]
    public async Task HappyPath_SendsNativeAnswerWithoutDecoration()
    {
        var v = New();
        v.Llm.Phase3Handler = _ => Task.FromResult(FakeLlmExecutor.Answer("## Done\nAll good"));
        var result = await v.Handler.DeliverAsync("u1", "op1", Day2, _ => Task.CompletedTask);
        Assert.Equal(DeliveryOutcome.Sent, result.Outcome);
        var sent = Assert.Single(v.Sender.Payloads);
        Assert.Equal(TelegramPayloadKind.Markdown, sent.Kind);
        Assert.Equal("## Done\nAll good", sent.Content);
        Assert.Equal(111L, sent.ChatId);
        var receipt = await Receipt(v, Day2);
        Assert.Equal(3, receipt.PayloadSchemaVersion);
        Assert.Equal("generated", receipt.ExecutionStatus);
        Assert.NotNull(receipt.AnswerVersion);
        Assert.NotNull(receipt.PlanVersion);
        Assert.Equal("901", receipt.MessageIds);
        Assert.Equal("## Done\nAll good", (await v.Repo.ReadPreviousReplyAsync("u1", "op1"))!.Answer);
        // One archived turn on the next run: the full previous answer.
        v.Llm.Phase3Handler = _ => Task.FromResult(FakeLlmExecutor.Answer("second"));
        var day3 = Day2.AddDays(1);
        var result2 = await v.Handler.DeliverAsync("u1", "op1", day3, _ => Task.CompletedTask);
        Assert.Equal(DeliveryOutcome.Sent, result2.Outcome);
        var roles = v.Llm.Phase3Calls[1].Select(m => m.Role).ToList();
        Assert.Equal(["system", "user", "assistant", "user"], roles);
        Assert.Equal("## Done\nAll good", v.Llm.Phase3Calls[1][2].Content);
    }

    [Fact]
    public async Task NoneMode_OmitsInclusionButStillRecords()
    {
        var v = New(Phase3Config.Read(_ => null) with { MemoryMode = Phase3Options.None });
        v.Repo.SeedMemory(new MemoryRecord("u1", "op1", Day1, Day1,
            v.Clock.Now, "old", "## Old", "hash", 7));
        v.Llm.Phase3Handler = _ => Task.FromResult(FakeLlmExecutor.Answer("new"));
        var result = await v.Handler.DeliverAsync("u1", "op1", Day2, _ => Task.CompletedTask);
        Assert.Equal(DeliveryOutcome.Sent, result.Outcome);
        Assert.Equal(["system", "user"], v.Llm.Phase3Calls[0].Select(m => m.Role));
        Assert.Equal("new", (await v.Repo.ReadPreviousReplyAsync("u1", "op1"))!.Answer);
    }

    [Fact]
    public async Task OverBoundAnswer_DeliversFailureNoticeWithoutMemory()
    {
        var v = New();
        v.Llm.Phase3Handler = _ => Task.FromResult(
            FakeLlmExecutor.Answer(new string('a', 131073)));
        var result = await v.Handler.DeliverAsync("u1", "op1", Day2, _ => Task.CompletedTask);
        Assert.Equal(DeliveryOutcome.Sent, result.Outcome);
        var sent = Assert.Single(v.Sender.Payloads);
        Assert.Equal(Phase3DeliveryText.FailureNotice, sent.Content);
        Assert.Null(await v.Repo.ReadPreviousReplyAsync("u1", "op1"));
        var receipt = await Receipt(v, Day2);
        Assert.Equal("failed", receipt.ExecutionStatus);
        Assert.Equal(Phase3DeliveryText.FailureNotice, receipt.FailureNotice);
    }

    [Fact]
    public async Task IncompleteAnswer_NeverRetriesNorDeliversPartially()
    {
        var v = New();
        v.Llm.Phase3Handler = _ => Task.FromException<LlmResult>(
            new LlmExecutionException(LlmFailureKind.AnswerIncomplete, "answer_incomplete"));
        var result = await v.Handler.DeliverAsync("u1", "op1", Day2, _ => Task.CompletedTask);
        Assert.Equal(DeliveryOutcome.Sent, result.Outcome);
        Assert.Single(v.Llm.Phase3Calls);
        Assert.Equal(Phase3DeliveryText.FailureNotice, Assert.Single(v.Sender.Payloads).Content);
    }

    [Fact]
    public async Task TransientGeneration_RetriesThenSucceeds()
    {
        var v = New();
        var calls = 0;
        v.Llm.Phase3Handler = _ => ++calls == 1
            ? Task.FromException<LlmResult>(new LlmExecutionException(LlmFailureKind.Transient, "boom"))
            : Task.FromResult(FakeLlmExecutor.Answer("recovered"));
        var first = await v.Handler.AttemptOnceAsync("u1", "op1", Day2, 0);
        Assert.Equal(SingleAttemptOutcome.NeedRetry, first.Outcome);
        Assert.Equal(1, (await Receipt(v, Day2)).GenerationAttempts);
        var second = await v.Handler.AttemptOnceAsync("u1", "op1", Day2, 1);
        Assert.Equal(SingleAttemptOutcome.Sent, second.Outcome);
        Assert.Equal("recovered", Assert.Single(v.Sender.Payloads).Content);
    }

    [Fact]
    public async Task ContextBudgetExceeded_FailsWithoutLlmCall()
    {
        var v = New(Phase3Config.Read(_ => null) with { DeclaredContextTokens = 100 });
        var result = await v.Handler.DeliverAsync("u1", "op1", Day2, _ => Task.CompletedTask);
        Assert.Equal(DeliveryOutcome.Sent, result.Outcome);
        Assert.Empty(v.Llm.Phase3Calls);
        Assert.Equal(Phase3DeliveryText.FailureNotice, Assert.Single(v.Sender.Payloads).Content);
        var receipt = await Receipt(v, Day2);
        Assert.Equal(Phase3FailureCodes.ContextBudgetExceeded, receipt.ErrorSummary);
        Assert.Null(await v.Repo.ReadPreviousReplyAsync("u1", "op1"));
    }

    [Fact]
    public async Task MarkdownRejection_FallsBackToLiteralRich()
    {
        var v = New();
        const string answer = "## Hi\nBody with **bold**";
        v.Llm.Phase3Handler = _ => Task.FromResult(FakeLlmExecutor.Answer(answer));
        v.Sender.RejectPayload = p => p.Kind == TelegramPayloadKind.Markdown ? ContentEx() : null;
        var result = await v.Handler.DeliverAsync("u1", "op1", Day2, _ => Task.CompletedTask);
        Assert.Equal(DeliveryOutcome.Sent, result.Outcome);
        Assert.Equal(2, v.Sender.Payloads.Count);
        Assert.Equal(TelegramPayloadKind.LiteralRich, v.Sender.Payloads[1].Kind);
        Assert.Equal(answer, v.Sender.Payloads[1].Content);
        var receipt = await Receipt(v, Day2);
        Assert.Equal(1, receipt.SentParts);
        Assert.Equal(1, receipt.TotalParts);
        Assert.Equal(2, receipt.Attempts); // both HTTP attempts are telemetry
        Assert.Equal(0, receipt.DeliveryTransientFailures); // fallback consumes no retry
    }

    [Fact]
    public async Task LiteralSizeRejection_UsesConservativeChunks()
    {
        var v = New();
        var answer = new string('m', 40000);
        v.Llm.Phase3Handler = _ => Task.FromResult(FakeLlmExecutor.Answer(answer));
        v.Sender.RejectPayload = p => (p.Kind, p.Content.Length) switch
        {
            (TelegramPayloadKind.Markdown, _) => ContentEx(),
            (TelegramPayloadKind.LiteralRich, > 4096) =>
                ContentEx("Bad Request: message is too long"),
            _ => null,
        };
        var result = await v.Handler.DeliverAsync("u1", "op1", Day2, _ => Task.CompletedTask);
        Assert.Equal(DeliveryOutcome.Sent, result.Outcome);
        // The fake records rejected sends as well; delivered leaves are the
        // ones within the conservative bound.
        var ok = v.Sender.Payloads
            .Where(p => p.Kind != TelegramPayloadKind.Markdown &&
                AnswerSourceBound.CountScalars(p.Content) <= 4096)
            .ToList();
        Assert.True(ok.Count > 2);
        Assert.Equal(answer, string.Concat(ok.Select(p => p.Content)));
        Assert.Equal(ok.Count, (await Receipt(v, Day2)).TotalParts);
    }

    [Fact]
    public async Task UnknownMethod_GoesDirectlyToPlain()
    {
        var v = New();
        v.Llm.Phase3Handler = _ => Task.FromResult(FakeLlmExecutor.Answer("plain me"));
        v.Sender.RejectPayload = p => p.Kind == TelegramPayloadKind.LiteralPlain
            ? null
            : new TelegramUnknownMethodException(404, 404, "unknown method");
        var result = await v.Handler.DeliverAsync("u1", "op1", Day2, _ => Task.CompletedTask);
        Assert.Equal(DeliveryOutcome.Sent, result.Outcome);
        var last = v.Sender.Payloads[^1];
        Assert.Equal(TelegramPayloadKind.LiteralPlain, last.Kind);
        Assert.Equal("plain me", last.Content);
    }

    [Theory]
    [InlineData(400, "Bad Request: something odd", "delivery_rejected")]
    [InlineData(401, "Unauthorized", "telegram_unauthorized")]
    public async Task TerminalRejection_FailsOccurrenceWithoutSecondMessage(
        int code, string description, string category)
    {
        var v = New();
        v.Llm.Phase3Handler = _ => Task.FromResult(FakeLlmExecutor.Answer("doomed"));
        v.Sender.RejectPayload = _ => new TelegramSendException(code, description, null, code);
        var result = await v.Handler.DeliverAsync("u1", "op1", Day2, _ => Task.CompletedTask);
        Assert.Equal(DeliveryOutcome.FailedOccurrenceKeptActive, result.Outcome);
        Assert.Single(v.Sender.Payloads);
        var receipt = await Receipt(v, Day2);
        Assert.Equal("failed", receipt.Status);
        Assert.Equal(category, receipt.ErrorSummary);
        Assert.Equal(1, receipt.Attempts);
    }

    [Fact]
    public async Task RateLimit_RetriesHonoringRetryAfter()
    {
        var v = New();
        v.Llm.Phase3Handler = _ => Task.FromResult(FakeLlmExecutor.Answer("slow"));
        var calls = 0;
        v.Sender.RejectPayload = _ => ++calls == 1
            ? new TelegramSendException(429, "Too Many Requests", TimeSpan.FromSeconds(30), 429)
            : null;
        var first = await v.Handler.AttemptOnceAsync("u1", "op1", Day2, 0);
        Assert.Equal(SingleAttemptOutcome.NeedRetry, first.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(30), first.RetryIn);
        Assert.Equal(1, (await Receipt(v, Day2)).DeliveryTransientFailures);
        var second = await v.Handler.AttemptOnceAsync("u1", "op1", Day2, 1);
        Assert.Equal(SingleAttemptOutcome.Sent, second.Outcome);
    }

    [Fact]
    public async Task Resume_SendsOnlyUnconfirmedLeaves()
    {
        var v = New();
        var answer = new string('m', 40000);
        v.Llm.Phase3Handler = _ => Task.FromResult(FakeLlmExecutor.Answer(answer));
        var calls = 0;
        v.Sender.RejectPayload = p =>
        {
            calls++;
            return (calls, p.Kind) switch
            {
                (1, TelegramPayloadKind.Markdown) => ContentEx(),
                (3, _) => new TelegramSendException(500, "boom"),
                _ => null,
            };
        };
        var first = await v.Handler.AttemptOnceAsync("u1", "op1", Day2, 0);
        Assert.Equal(SingleAttemptOutcome.NeedRetry, first.Outcome);
        Assert.Equal(3, v.Sender.Payloads.Count);
        var second = await v.Handler.AttemptOnceAsync("u1", "op1", Day2, 1);
        Assert.Equal(SingleAttemptOutcome.Sent, second.Outcome);
        Assert.Equal(4, v.Sender.Payloads.Count);
        // The confirmed first leaf was sent once; only the unconfirmed leaf
        // was resent, with identical content.
        Assert.Equal(32768, AnswerSourceBound.CountScalars(v.Sender.Payloads[1].Content));
        Assert.Equal(v.Sender.Payloads[2].Content, v.Sender.Payloads[3].Content);
        var receipt = await Receipt(v, Day2);
        Assert.Equal(2, receipt.SentParts);
        Assert.Equal("901,902", receipt.MessageIds);
    }

    [Fact]
    public async Task DeadlineYield_PersistsProgressAndResumes()
    {
        var v = New();
        v.Llm.Phase3Handler = messages =>
        {
            v.Clock.Now += TimeSpan.FromSeconds(500);
            return Task.FromResult(FakeLlmExecutor.Answer("slow answer"));
        };
        var first = await v.Handler.AttemptOnceAsync("u1", "op1", Day2, 0);
        Assert.Equal(SingleAttemptOutcome.NeedRetry, first.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(1), first.RetryIn);
        Assert.Empty(v.Sender.Payloads);
        var second = await v.Handler.AttemptOnceAsync("u1", "op1", Day2, 1);
        Assert.Equal(SingleAttemptOutcome.Sent, second.Outcome);
        Assert.Equal("slow answer", Assert.Single(v.Sender.Payloads).Content);
    }

    [Fact]
    public async Task OversizedLlmTimeout_IsRejectedWithoutWork()
    {
        var options = TestLlm.Options() with { RequestTimeout = TimeSpan.FromSeconds(511) };
        var v = New(llm: options);
        var result = await v.Handler.AttemptOnceAsync("u1", "op1", Day2, 0);
        Assert.Equal(SingleAttemptOutcome.OccurrenceFailed, result.Outcome);
        Assert.Empty(v.Llm.Phase3Calls);
        Assert.Empty(v.Sender.Payloads);
    }

    [Fact]
    public async Task PermanentRecipient_StopsOperation()
    {
        var v = New();
        v.Llm.Phase3Handler = _ => Task.FromResult(FakeLlmExecutor.Answer("hello"));
        v.Sender.RejectPayload = _ =>
            new TelegramSendException(403, "Forbidden: bot was blocked by the user");
        var result = await v.Handler.DeliverAsync("u1", "op1", Day2, _ => Task.CompletedTask);
        Assert.Equal(DeliveryOutcome.OperationFailed, result.Outcome);
        Assert.Equal(OperationStatus.Failed, (await v.Ops.GetAsync("u1", "op1"))!.Status);
        Assert.Equal("failed", (await Receipt(v, Day2)).Status);
    }

    [Fact]
    public async Task StorageExhaustion_FailsOccurrenceWithProgressRetained()
    {
        var v = New();
        v.Llm.Phase3Handler = _ => Task.FromResult(FakeLlmExecutor.Answer("hello"));
        var repo = new Mock<IOccurrenceRepository>(MockBehavior.Strict);
        var h = new DeliveryHandler(v.Ops, v.Receipts, new FakePayloadStore(), v.Sender, v.Llm,
            TestLlm.Options(), occurrences: repo.Object,
            phase3Options: Phase3Config.Read(_ => null), phase3Llm: v.Llm, clock: v.Clock);
        repo.Setup(r => r.InitializeContextAsync(It.IsAny<FrozenContextRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TransientStoreException("storage down"));
        repo.Setup(r => r.FailAsync(It.IsAny<FailRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TransientStoreException("storage down"));
        var result = await h.DeliverAsync("u1", "op1", Day2, _ => Task.CompletedTask);
        Assert.Equal(DeliveryOutcome.FailedOccurrenceKeptActive, result.Outcome);
        repo.Verify(r => r.InitializeContextAsync(
            It.IsAny<FrozenContextRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(6));
    }

    [Fact]
    public async Task LegacyReceipt_ResumesOldTransportWithoutMemory()
    {
        var ops = new FakeOperationStore();
        var receipts = new FakeDeliveryStore();
        var payloads = new FakePayloadStore();
        var sender = new FakeTelegramSender();
        var llm = new FakeLlmExecutor();
        ops.Seed(TestRecords.Operation("u1", "op1"));
        var parts = new[] { "<b>old one</b>", "<b>old two</b>" };
        await payloads.PersistAsync("u1", "op1", Day1, parts, version: "v1");
        await receipts.UpsertAsync(new DeliveryReceipt("u1", "op1", Day1,
            OccurrenceExecution.StatusGenerating, 1, null, 5,
            ExecutionStatus: "generated", SentParts: 1, TotalParts: 2,
            MessageIds: "5", PayloadVersion: "v1"));
        var clock = new FakeClock { Now = new DateTimeOffset(Day1, TimeSpan.Zero) };
        receipts.Now = clock.Now;
        var repo = new FakeOccurrenceRepository(ops, receipts, clock);
        var handler = new DeliveryHandler(ops, receipts, payloads, sender, llm, TestLlm.Options(),
            occurrences: repo, phase3Options: Phase3Config.Read(_ => null),
            phase3Llm: llm, clock: clock);
        var result = await handler.DeliverAsync("u1", "op1", Day1, _ => Task.CompletedTask);
        Assert.Equal(DeliveryOutcome.Sent, result.Outcome);
        Assert.Equal([(111L, "<b>old two</b>")], sender.Sent);
        Assert.Empty(llm.Phase3Calls);
        var receipt = (await receipts.GetAsync("u1", "op1", Day1))!;
        Assert.Null(receipt.PayloadSchemaVersion);
        Assert.Null(await repo.ReadPreviousReplyAsync("u1", "op1"));
    }

    [Fact]
    public async Task UnknownPayloadVersion_FailsWithoutRegeneration()
    {
        var ops = new FakeOperationStore();
        var receipts = new FakeDeliveryStore();
        var payloads = new FakePayloadStore();
        var sender = new FakeTelegramSender();
        var llm = new FakeLlmExecutor();
        ops.Seed(TestRecords.Operation("u1", "op1"));
        var parts = new[] { "<b>saved</b>" };
        await payloads.PersistAsync("u1", "op1", Day1, parts, version: "v9");
        await receipts.UpsertAsync(new DeliveryReceipt("u1", "op1", Day1,
            OccurrenceExecution.StatusGenerating, 1, null, null,
            ExecutionStatus: "generated", TotalParts: 1,
            MessageIds: null, PayloadVersion: "v9", PayloadSchemaVersion: 4));
        var clock = new FakeClock { Now = new DateTimeOffset(Day1, TimeSpan.Zero) };
        receipts.Now = clock.Now;
        var repo = new FakeOccurrenceRepository(ops, receipts, clock);
        var handler = new DeliveryHandler(ops, receipts, payloads, sender, llm, TestLlm.Options(),
            occurrences: repo, phase3Options: Phase3Config.Read(_ => null),
            phase3Llm: llm, clock: clock);
        llm.Phase3Handler = _ => Task.FromResult(FakeLlmExecutor.Answer("regenerated"));

        var result = await handler.AttemptOnceAsync("u1", "op1", Day1, 0);

        Assert.Equal(SingleAttemptOutcome.OccurrenceFailed, result.Outcome);
        Assert.Equal("unsupported_payload_version", result.ErrorSummary);
        Assert.Empty(llm.Phase3Calls);
        Assert.Empty(sender.Sent);
        var receipt = (await receipts.GetAsync("u1", "op1", Day1))!;
        Assert.Equal("failed", receipt.Status);
        Assert.Equal(4, receipt.PayloadSchemaVersion);
        Assert.Equal(parts, await payloads.LoadAsync("u1", "op1", Day1, version: "v9"));
        Assert.Equal(OperationStatus.Active, (await ops.GetAsync("u1", "op1"))!.Status);
    }

    // ---- Phase 3 provider parsing ----

    [Fact]
    public async Task Phase3Stream_LengthIsTerminalAndBoundIsEnforced()
    {
        var opts = TestLlm.Options();
        var lengthStream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"partial\"},\"finish_reason\":\"length\"}]}\n\n" +
            "data: [DONE]\n\n"));
        var length = await Assert.ThrowsAsync<LlmExecutionException>(() =>
            OpenRouterLlmExecutor.ParsePhase3StreamAsync(lengthStream, opts, 131072));
        Assert.Equal(LlmFailureKind.AnswerIncomplete, length.Kind);
        Assert.Equal("answer_incomplete", length.Summary);

        var bigStream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"0123456789ABCDEF\"},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n"));
        var bound = await Assert.ThrowsAsync<LlmExecutionException>(() =>
            OpenRouterLlmExecutor.ParsePhase3StreamAsync(bigStream, opts, 10));
        Assert.Equal(LlmFailureKind.SourceLimit, bound.Kind);

        var ok = OpenRouterLlmExecutor.ParsePhase3Response(
            "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\" hi \"}}]}", opts, 131072);
        Assert.Equal("hi", ok.AnswerText);
        var tooLong = Assert.Throws<LlmExecutionException>(() =>
            OpenRouterLlmExecutor.ParsePhase3Response(
                "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"" +
                new string('z', 11) + "\"}}]}", opts, 10));
        Assert.Equal(LlmFailureKind.SourceLimit, tooLong.Kind);
    }

    [Fact]
    public async Task Phase3Stream_WhitespaceFloodStaysBounded()
    {
        var opts = TestLlm.Options();
        var flood = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"x\"}}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" + new string(' ', 1_000_001) +
            "\"},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n"));
        var result = await OpenRouterLlmExecutor.ParsePhase3StreamAsync(flood, opts, 131072);
        Assert.Equal("x", result.AnswerText);
    }

    [Fact]
    public async Task Phase3Stream_ContentAfterExcessWhitespaceIsSourceLimit()
    {
        var opts = TestLlm.Options();
        var flood = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"x\"}}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" + new string(' ', 131073) +
            "\"}}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"y\"},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n"));
        var ex = await Assert.ThrowsAsync<LlmExecutionException>(() =>
            OpenRouterLlmExecutor.ParsePhase3StreamAsync(flood, opts, 131072));
        Assert.Equal(LlmFailureKind.SourceLimit, ex.Kind);
        Assert.Equal("answer_source_limit", ex.Summary);
    }
}
