// Occurrence delivery through the handler: native answers, memory context, deterministic fallbacks, retry counters, and resumption. All offline.
using Moq;
using RecurringTasksBot.Application;
using RecurringTasksBot.Infrastructure.Llm;

namespace RecurringTasksBot.Tests;

public sealed class OccurrenceDeliveryTests
{
    private static readonly DateTime Day1 = new(2026, 9, 24, 9, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Day2 = new(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc);

    private sealed record Fixture(
        ExecuteOccurrenceHandler Handler, FakeOperationStore Ops,
        FakeTelegramSender Sender, FakeLlmExecutor Llm,
        FakeOccurrenceRepository Repo, FakeClock Clock);

    private static Fixture New(ExecutionOptions? execution = null)
    {
        var ops = new FakeOperationStore();
        var sender = new FakeTelegramSender();
        var llmExec = new FakeLlmExecutor();
        var clock = new FakeClock { Now = new DateTimeOffset(Day2, TimeSpan.Zero) };
        ops.Seed(TestRecords.Operation("u1", "op1"));
        var repo = new FakeOccurrenceRepository(ops, clock);
        var handler = new ExecuteOccurrenceHandler(ops, repo, sender, llmExec,
            execution ?? TestLlm.Execution(), TestLlm.ProviderName, TestLlm.ModelName, clock);
        return new Fixture(handler, ops, sender, llmExec, repo, clock);
    }

    private static TelegramSendException ContentEx(string description = "Bad Request: can't parse entities") =>
        new(400, description, null, 400);

    private static async Task<DeliveryReceipt> Receipt(Fixture v, DateTime scheduled) =>
        (await v.Repo.GetReceiptAsync("u1", "op1", scheduled))!;


    [Fact]
    public async Task HappyPath_SendsNativeAnswerWithoutDecoration()
    {
        var v = New();
        v.Llm.Responder = _ => Task.FromResult(FakeLlmExecutor.Answer("## Done\nAll good"));
        var result = await AttemptLoop.RunUntilDone(v.Handler, "u1", "op1", Day2);
        Assert.Equal(SingleAttemptOutcome.Sent, result.Outcome);
        var sent = Assert.Single(v.Sender.Payloads);
        Assert.Equal(TelegramPayloadKind.Markdown, sent.Kind);
        Assert.Equal("## Done\nAll good", sent.Content);
        Assert.Equal(111L, sent.ChatId);
        var receipt = await Receipt(v, Day2);
        Assert.Equal(1, receipt.PayloadSchemaVersion);
        Assert.Equal("generated", receipt.ExecutionStatus);
        Assert.NotNull(receipt.AnswerVersion);
        Assert.NotNull(receipt.PlanVersion);
        Assert.Equal("901", receipt.MessageIds);
        Assert.Equal("## Done\nAll good", (await v.Repo.ReadPreviousReplyAsync("u1", "op1"))!.Answer);
        // One archived turn on the next run: the full previous answer.
        v.Llm.Responder = _ => Task.FromResult(FakeLlmExecutor.Answer("second"));
        var day3 = Day2.AddDays(1);
        var result2 = await AttemptLoop.RunUntilDone(v.Handler, "u1", "op1", day3);
        Assert.Equal(SingleAttemptOutcome.Sent, result2.Outcome);
        var roles = v.Llm.MessageCalls[1].Select(m => m.Role).ToList();
        Assert.Equal(["system", "user", "assistant", "user"], roles);
        Assert.Equal("## Done\nAll good", v.Llm.MessageCalls[1][2].Content);
    }


    [Fact]
    public async Task NoneMode_OmitsInclusionButStillRecords()
    {
        var v = New(TestLlm.Execution() with { MemoryMode = ExecutionOptions.None });
        v.Repo.SeedMemory(new MemoryRecord("u1", "op1", Day1, Day1,
            v.Clock.Now, "old", "## Old", "hash", 7));
        v.Llm.Responder = _ => Task.FromResult(FakeLlmExecutor.Answer("new"));
        var result = await AttemptLoop.RunUntilDone(v.Handler, "u1", "op1", Day2);
        Assert.Equal(SingleAttemptOutcome.Sent, result.Outcome);
        Assert.Equal(["system", "user"], v.Llm.MessageCalls[0].Select(m => m.Role));
        Assert.Equal("new", (await v.Repo.ReadPreviousReplyAsync("u1", "op1"))!.Answer);
    }


    [Fact]
    public async Task OverBoundAnswer_DeliversFailureNoticeWithoutMemory()
    {
        var v = New();
        v.Llm.Responder = _ => Task.FromResult(
            FakeLlmExecutor.Answer(new string('a', 131073)));
        var result = await AttemptLoop.RunUntilDone(v.Handler, "u1", "op1", Day2);
        Assert.Equal(SingleAttemptOutcome.Sent, result.Outcome);
        var sent = Assert.Single(v.Sender.Payloads);
        Assert.Equal(OccurrenceMessages.FailureNotice, sent.Content);
        Assert.Null(await v.Repo.ReadPreviousReplyAsync("u1", "op1"));
        var receipt = await Receipt(v, Day2);
        Assert.Equal("failed", receipt.ExecutionStatus);
        Assert.Equal(OccurrenceMessages.FailureNotice, receipt.FailureNotice);
    }


    [Fact]
    public async Task IncompleteAnswer_NeverRetriesNorDeliversPartially()
    {
        var v = New();
        v.Llm.Responder = _ => Task.FromException<LlmResult>(
            new LlmExecutionException(LlmFailureKind.AnswerIncomplete, "answer_incomplete"));
        var result = await AttemptLoop.RunUntilDone(v.Handler, "u1", "op1", Day2);
        Assert.Equal(SingleAttemptOutcome.Sent, result.Outcome);
        Assert.Single(v.Llm.MessageCalls);
        Assert.Equal(OccurrenceMessages.FailureNotice, Assert.Single(v.Sender.Payloads).Content);
    }


    [Fact]
    public async Task TransientGeneration_RetriesThenSucceeds()
    {
        var v = New();
        var calls = 0;
        v.Llm.Responder = _ => ++calls == 1
            ? Task.FromException<LlmResult>(new LlmExecutionException(LlmFailureKind.Transient, "boom"))
            : Task.FromResult(FakeLlmExecutor.Answer("recovered"));
        var first = await v.Handler.ExecuteAttemptAsync("u1", "op1", Day2, 0);
        Assert.Equal(SingleAttemptOutcome.NeedRetry, first.Outcome);
        Assert.Equal(1, (await Receipt(v, Day2)).GenerationAttempts);
        var second = await v.Handler.ExecuteAttemptAsync("u1", "op1", Day2, 1);
        Assert.Equal(SingleAttemptOutcome.Sent, second.Outcome);
        Assert.Equal("recovered", Assert.Single(v.Sender.Payloads).Content);
    }


    [Fact]
    public async Task ContextBudgetExceeded_FailsWithoutLlmCall()
    {
        var v = New(TestLlm.Execution() with { DeclaredContextTokens = 100 });
        var result = await AttemptLoop.RunUntilDone(v.Handler, "u1", "op1", Day2);
        Assert.Equal(SingleAttemptOutcome.Sent, result.Outcome);
        Assert.Empty(v.Llm.MessageCalls);
        Assert.Equal(OccurrenceMessages.FailureNotice, Assert.Single(v.Sender.Payloads).Content);
        var receipt = await Receipt(v, Day2);
        Assert.Equal(OccurrenceFailureCodes.ContextBudgetExceeded, receipt.ErrorSummary);
        Assert.Null(await v.Repo.ReadPreviousReplyAsync("u1", "op1"));
    }


    [Fact]
    public async Task MarkdownRejection_FallsBackToLiteralRich()
    {
        var v = New();
        const string answer = "## Hi\nBody with **bold**";
        v.Llm.Responder = _ => Task.FromResult(FakeLlmExecutor.Answer(answer));
        v.Sender.RejectPayload = p => p.Kind == TelegramPayloadKind.Markdown ? ContentEx() : null;
        var result = await AttemptLoop.RunUntilDone(v.Handler, "u1", "op1", Day2);
        Assert.Equal(SingleAttemptOutcome.Sent, result.Outcome);
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
        v.Llm.Responder = _ => Task.FromResult(FakeLlmExecutor.Answer(answer));
        v.Sender.RejectPayload = p => (p.Kind, p.Content.Length) switch
        {
            (TelegramPayloadKind.Markdown, _) => ContentEx(),
            (TelegramPayloadKind.LiteralRich, > 4096) =>
                ContentEx("Bad Request: message is too long"),
            _ => null,
        };
        var result = await AttemptLoop.RunUntilDone(v.Handler, "u1", "op1", Day2);
        Assert.Equal(SingleAttemptOutcome.Sent, result.Outcome);
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
        v.Llm.Responder = _ => Task.FromResult(FakeLlmExecutor.Answer("plain me"));
        v.Sender.RejectPayload = p => p.Kind == TelegramPayloadKind.LiteralPlain
            ? null
            : new TelegramUnknownMethodException(404, 404, "unknown method");
        var result = await AttemptLoop.RunUntilDone(v.Handler, "u1", "op1", Day2);
        Assert.Equal(SingleAttemptOutcome.Sent, result.Outcome);
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
        v.Llm.Responder = _ => Task.FromResult(FakeLlmExecutor.Answer("doomed"));
        v.Sender.RejectPayload = _ => new TelegramSendException(code, description, null, code);
        var result = await AttemptLoop.RunUntilDone(v.Handler, "u1", "op1", Day2);
        Assert.Equal(SingleAttemptOutcome.OccurrenceFailed, result.Outcome);
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
        v.Llm.Responder = _ => Task.FromResult(FakeLlmExecutor.Answer("slow"));
        var calls = 0;
        v.Sender.RejectPayload = _ => ++calls == 1
            ? new TelegramSendException(429, "Too Many Requests", TimeSpan.FromSeconds(30), 429)
            : null;
        var first = await v.Handler.ExecuteAttemptAsync("u1", "op1", Day2, 0);
        Assert.Equal(SingleAttemptOutcome.NeedRetry, first.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(30), first.RetryIn);
        Assert.Equal(1, (await Receipt(v, Day2)).DeliveryTransientFailures);
        var second = await v.Handler.ExecuteAttemptAsync("u1", "op1", Day2, 1);
        Assert.Equal(SingleAttemptOutcome.Sent, second.Outcome);
    }


    [Fact]
    public async Task Resume_SendsOnlyUnconfirmedLeaves()
    {
        var v = New();
        var answer = new string('m', 40000);
        v.Llm.Responder = _ => Task.FromResult(FakeLlmExecutor.Answer(answer));
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
        var first = await v.Handler.ExecuteAttemptAsync("u1", "op1", Day2, 0);
        Assert.Equal(SingleAttemptOutcome.NeedRetry, first.Outcome);
        Assert.Equal(3, v.Sender.Payloads.Count);
        var second = await v.Handler.ExecuteAttemptAsync("u1", "op1", Day2, 1);
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
        v.Llm.Responder = messages =>
        {
            v.Clock.Now += TimeSpan.FromSeconds(500);
            return Task.FromResult(FakeLlmExecutor.Answer("slow answer"));
        };
        var first = await v.Handler.ExecuteAttemptAsync("u1", "op1", Day2, 0);
        Assert.Equal(SingleAttemptOutcome.NeedRetry, first.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(1), first.RetryIn);
        Assert.Empty(v.Sender.Payloads);
        var second = await v.Handler.ExecuteAttemptAsync("u1", "op1", Day2, 1);
        Assert.Equal(SingleAttemptOutcome.Sent, second.Outcome);
        Assert.Equal("slow answer", Assert.Single(v.Sender.Payloads).Content);
    }


    [Fact]
    public async Task OversizedLlmTimeout_IsRejectedWithoutWork()
    {
        var execution = TestLlm.Execution() with { RequestTimeout = TimeSpan.FromSeconds(511) };
        var v = New(execution: execution);
        var result = await v.Handler.ExecuteAttemptAsync("u1", "op1", Day2, 0);
        Assert.Equal(SingleAttemptOutcome.OccurrenceFailed, result.Outcome);
        Assert.Empty(v.Llm.MessageCalls);
        Assert.Empty(v.Sender.Payloads);
    }


    [Fact]
    public async Task PermanentRecipient_StopsOperation()
    {
        var v = New();
        v.Llm.Responder = _ => Task.FromResult(FakeLlmExecutor.Answer("hello"));
        v.Sender.RejectPayload = _ =>
            new TelegramSendException(403, "Forbidden: bot was blocked by the user");
        var result = await AttemptLoop.RunUntilDone(v.Handler, "u1", "op1", Day2);
        Assert.Equal(SingleAttemptOutcome.OperationFailed, result.Outcome);
        Assert.Equal(OperationStatus.Failed, (await v.Ops.GetAsync("u1", "op1"))!.Status);
        Assert.Equal("failed", (await Receipt(v, Day2)).Status);
    }


    [Fact]
    public async Task StorageExhaustion_FailsOccurrenceWithProgressRetained()
    {
        var v = New();
        v.Llm.Responder = _ => Task.FromResult(FakeLlmExecutor.Answer("hello"));
        var repo = new Mock<IOccurrenceRepository>(MockBehavior.Strict);
        var h = new ExecuteOccurrenceHandler(v.Ops, repo.Object, v.Sender, v.Llm,
            TestLlm.Execution(), TestLlm.ProviderName, TestLlm.ModelName, v.Clock);
        string? currentClaim = null;
        var order = new List<string>();
        repo.Setup(r => r.TryClaimAsync("u1", "op1", Day2,
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, DateTime, string, CancellationToken>(
                (_, _, _, claim, _) => currentClaim = claim)
            .ReturnsAsync(true);
        repo.Setup(r => r.GetReceiptAsync("u1", "op1", Day2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => currentClaim is null ? null : new DeliveryReceipt("u1", "op1", Day2,
                OccurrenceExecution.StatusGenerating, 0, null,
                UpdatedUtc: v.Clock.Now, ClaimId: currentClaim));
        repo.Setup(r => r.ReleaseClaimAsync("u1", "op1", Day2,
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, DateTime, string, CancellationToken>(
                (_, _, _, _, _) => { currentClaim = null; order.Add("release"); })
            .Returns(Task.CompletedTask);
        repo.Setup(r => r.InitializeContextAsync(It.IsAny<FrozenContextRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TransientStoreException("storage down"));
        string? failedClaim = "unpublished";
        repo.Setup(r => r.FailAsync(It.IsAny<FailRequest>(), It.IsAny<CancellationToken>()))
            .Callback<FailRequest, CancellationToken>((req, _) =>
            {
                failedClaim = req.ClaimId;
                order.Add("fail");
            })
            .Returns(Task.CompletedTask);
        var result = await AttemptLoop.RunUntilDone(h, "u1", "op1", Day2);
        Assert.Equal(SingleAttemptOutcome.OccurrenceFailed, result.Outcome);
        repo.Verify(r => r.InitializeContextAsync(
            It.IsAny<FrozenContextRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(6));
        // Terminal publication happens inside the lease: the single fail
        // precedes the final release, so FailAsync still owns its claim.
        Assert.NotEqual("unpublished", failedClaim);
        Assert.True(order.IndexOf("fail") < order.LastIndexOf("release"));
    }

    [Fact]
    public async Task UnknownPayloadVersion_FailsWithoutRegeneration()
    {
        var ops = new FakeOperationStore();
        var sender = new FakeTelegramSender();
        var llm = new FakeLlmExecutor();
        ops.Seed(TestRecords.Operation("u1", "op1"));
        var clock = new FakeClock { Now = new DateTimeOffset(Day1, TimeSpan.Zero) };
        var repo = new FakeOccurrenceRepository(ops, clock);
        await repo.SeedReceiptAsync(new DeliveryReceipt("u1", "op1", Day1,
            OccurrenceExecution.StatusGenerating, 1, null,
            ExecutionStatus: "generated", TotalParts: 1,
            MessageIds: null, PayloadSchemaVersion: 4));
        var handler = new ExecuteOccurrenceHandler(ops, repo, sender, llm, TestLlm.Execution(),
            TestLlm.ProviderName, TestLlm.ModelName, clock);
        llm.Responder = _ => Task.FromResult(FakeLlmExecutor.Answer("regenerated"));

        var result = await handler.ExecuteAttemptAsync("u1", "op1", Day1, 0);

        Assert.Equal(SingleAttemptOutcome.OccurrenceFailed, result.Outcome);
        Assert.Equal("unsupported_payload_version", result.ErrorSummary);
        Assert.Empty(llm.MessageCalls);
        Assert.Empty(sender.Payloads);
        var receipt = (await repo.GetReceiptAsync("u1", "op1", Day1))!;
        Assert.Equal("failed", receipt.Status);
        Assert.Equal("unsupported_payload_version", receipt.ErrorSummary);
        Assert.Equal(4, receipt.PayloadSchemaVersion);
        Assert.Equal(OperationStatus.Active, (await ops.GetAsync("u1", "op1"))!.Status);
    }

    [Fact]
    public async Task TransientInitialReceiptRead_RetriesInsteadOfThrowing()
    {
        var v = New();
        var repo = new Mock<IOccurrenceRepository>();
        repo.Setup(r => r.GetReceiptAsync("u1", "op1", Day2, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TransientStoreException("storage down"));
        var handler = new ExecuteOccurrenceHandler(v.Ops, repo.Object, v.Sender, v.Llm,
            TestLlm.Execution(), TestLlm.ProviderName, TestLlm.ModelName, v.Clock);

        var result = await handler.ExecuteAttemptAsync("u1", "op1", Day2, 0);

        Assert.Equal(SingleAttemptOutcome.NeedRetry, result.Outcome);
        Assert.NotNull(result.RetryIn);
    }

    [Fact]
    public async Task TransientOperationRead_RetriesInsteadOfThrowing()
    {
        var v = New();
        var ops = new Mock<IOperationStore>();
        ops.Setup(o => o.GetAsync("u1", "op1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TransientStoreException("storage down"));
        var handler = new ExecuteOccurrenceHandler(ops.Object, v.Repo, v.Sender, v.Llm,
            TestLlm.Execution(), TestLlm.ProviderName, TestLlm.ModelName, v.Clock);

        var result = await handler.ExecuteAttemptAsync("u1", "op1", Day2, 0);

        Assert.Equal(SingleAttemptOutcome.NeedRetry, result.Outcome);
    }

    [Fact]
    public async Task TransientClaim_RetriesInsteadOfThrowing()
    {
        var v = New();
        var repo = new Mock<IOccurrenceRepository>();
        repo.Setup(r => r.TryClaimAsync("u1", "op1", Day2, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TransientStoreException("storage down"));
        var handler = new ExecuteOccurrenceHandler(v.Ops, repo.Object, v.Sender, v.Llm,
            TestLlm.Execution(), TestLlm.ProviderName, TestLlm.ModelName, v.Clock);

        var result = await handler.ExecuteAttemptAsync("u1", "op1", Day2, 0);

        Assert.Equal(SingleAttemptOutcome.NeedRetry, result.Outcome);
    }

    [Fact]
    public async Task PartialAnswerPlanPointers_FailWithoutGenerating()
    {
        var v = New();
        await v.Repo.SeedReceiptAsync(new DeliveryReceipt("u1", "op1", Day2, "generating", 0, null,
            AnswerVersion: "a1", PlanVersion: null));
        v.Llm.Responder = _ => Task.FromResult(FakeLlmExecutor.Answer("must not generate"));

        var result = await v.Handler.ExecuteAttemptAsync("u1", "op1", Day2, 0);

        Assert.Equal(SingleAttemptOutcome.OccurrenceFailed, result.Outcome);
        Assert.Equal(OccurrenceFailureCodes.PayloadCorrupt, result.ErrorSummary);
        Assert.Empty(v.Llm.MessageCalls);
        Assert.Empty(v.Sender.Payloads);
    }

    [Fact]
    public async Task LeaseRunner_MapsOwnershipLossToClaimWait()
    {
        var v = New();
        var runner = new OccurrenceLeaseRunner(v.Repo);
        var result = await runner.RunAsync("u1", "op1", Day2, "ghost-claim", 0,
            _ => Task.FromException<SingleAttemptResult>(new ClaimLostException()));
        Assert.Equal(SingleAttemptOutcome.WaitingForClaim, result.Outcome);
    }

    [Fact]
    public async Task LeaseRunner_MapsTransientToNeedRetry()
    {
        var v = New();
        var runner = new OccurrenceLeaseRunner(v.Repo);
        var result = await runner.RunAsync("u1", "op1", Day2, "ghost-claim", 2,
            _ => Task.FromException<SingleAttemptResult>(new TransientStoreException("down")));
        Assert.Equal(SingleAttemptOutcome.NeedRetry, result.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(5), result.RetryIn);
    }

    [Fact]
    public async Task LeaseRunner_PreservesCallerCancellation()
    {
        var v = New();
        var runner = new OccurrenceLeaseRunner(v.Repo);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync("u1", "op1", Day2, "ghost-claim", 0,
                async t =>
                {
                    await Task.Delay(Timeout.Infinite, t);
                    return new SingleAttemptResult(SingleAttemptOutcome.SkippedStopped, 0, null, null);
                }, cts.Token));
    }

    [Fact]
    public async Task ReleaseFailure_DoesNotOverwriteSuccessfulDelivery()
    {
        var v = New();
        v.Repo.ThrowOnRelease = true;
        v.Llm.Responder = _ => Task.FromResult(FakeLlmExecutor.Answer("done"));

        var result = await AttemptLoop.RunUntilDone(v.Handler, "u1", "op1", Day2);

        Assert.Equal(SingleAttemptOutcome.Sent, result.Outcome);
        Assert.NotEmpty(v.Sender.Payloads);
    }
}
