using Microsoft.Extensions.Logging;
// Answer generation for one occurrence attempt: frozen-context
// initialization, the single LLM call with retries, and atomic
// answer-plus-plan publication. Returns a terminal or continuation result,
// or null when the answer is persisted and ready to deliver.
namespace RecurringTasksBot.Application;

internal sealed class OccurrenceGenerator(
    IOccurrenceRepository occurrences,
    ILlmExecutor generation,
    ExecutionOptions options,
    AttemptSupport support,
    string providerName,
    string modelName,
    ILogger? logger = null)
{
    public async Task<(FrozenContextRecord? Context, SingleAttemptResult? Failure)> InitializeAsync(
        OperationRecord op, string claimId, DateTime scheduledUtc, DateTime executionStarted,
        string? adminInstruction, int attemptIndex, CancellationToken ct,
        OccurrenceGenerationOverrides? overrides = null)
    {
        try
        {
            var instruction = RecurringTaskSystemPrompt.Render(options.TargetAnswerTextChars,
                TelegramLimits.RichTextChars, adminInstruction);
            var init = await occurrences.InitializeContextAsync(new FrozenContextRequest(
                op.OwnerId, op.OperationId, scheduledUtc, claimId,
                new FrozenContextInputs(instruction, options.TargetAnswerTextChars,
                    TelegramLimits.RichTextChars,
                    overrides?.MemoryMode ?? options.MemoryMode, executionStarted)), ct);
            return (init.Context, null);
        }
        catch (TransientStoreException ex) { return (null, AttemptResults.StorageRetry(attemptIndex, ex.Message)); }
        catch (ClaimLostException) { return (null, AttemptResults.ClaimWait()); }
        catch (OperationStoppedException) { return (null, AttemptResults.Stopped()); }
        catch (PayloadIntegrityException ex)
        {
            return (null, await support.FailAsync(op.OwnerId, op.OperationId, scheduledUtc, claimId,
                ex.Code, 0, 0, ct));
        }
    }

    public async Task<SingleAttemptResult?> GenerateAsync(
        OperationRecord op, FrozenContextRecord context, string claimId, DateTime scheduledUtc,
        DateTime executionStarted, int attemptIndex, int attempts,
        DateTimeOffset workStart, CancellationToken ct,
        OccurrenceGenerationOverrides? overrides = null)
    {
        var ownerId = op.OwnerId;
        var operationId = op.OperationId;
        string? previous = context.PreviousReplyPresent &&
            IsPreviousReplyMode(context.MemoryMode)
            ? context.PreviousReplyAnswer
            : null;
        var snapshot = previous is null && context.PreviousReplyPresent
            ? context.ToSnapshot() with
            {
                PreviousReplyPresent = false,
                PreviousReplyScheduledAtUtc = null,
                PreviousReplyExecutedAtUtc = null,
            }
            : context.ToSnapshot();
        // Prompt construction uses the same effective search value as
        // request serialization: frozen overrides win, otherwise the
        // shared default. A task with webSearch=false omits search tools;
        // a task with true enables them even when the default is false.
        var searchEnabled = overrides?.SearchEnabled ?? options.SearchEnabled;
        var messages = ExecutionMessageBuilder.BuildMessages(
            context.EffectiveSystemInstruction, snapshot, previous, searchEnabled);
        if (!ContextBudget.FitsBudget(messages, options, options.CompletionTokenBudget))
            return await PersistNoticeAsync(op, claimId, scheduledUtc, executionStarted,
                attemptIndex, attempts, OccurrenceFailureCodes.ContextBudgetExceeded, ct, workStart);

        if (!support.FitsTime(workStart, options.RequestTimeout + TimeSpan.FromSeconds(30)))
            return AttemptResults.YieldRetry();


        LlmResult result;
        try
        {
            result = await generation.ExecuteAsync(
                new LlmRequest(messages, options.MaxAnswerSourceChars,
                    overrides?.ReasoningEffort, overrides?.SearchEnabled), ct);
        }
        catch (LlmExecutionException ex) when (ex.Kind is LlmFailureKind.Transient or LlmFailureKind.EmptyResponse)
        {
            var (failure, count) = await TrackGenerationAsync(ownerId, operationId, scheduledUtc,
                claimId, GenerationPolicy.Sanitize(ex.Summary), attemptIndex, ct);
            if (failure is not null)
                return failure;
            if (count > options.GenerationRetries)
                return await PersistNoticeAsync(op, claimId, scheduledUtc, executionStarted,
                    attemptIndex, attempts, GenerationPolicy.Sanitize(ex.Summary), ct, workStart);
            return new SingleAttemptResult(SingleAttemptOutcome.NeedRetry, attempts,
                GenerationPolicy.RetryDelay(count - 1, ex.RetryAfter),
                GenerationPolicy.Sanitize(ex.Summary));
        }
        catch (LlmExecutionException ex)
        {
            return await PersistNoticeAsync(op, claimId, scheduledUtc, executionStarted,
                attemptIndex, attempts, GenerationPolicy.Sanitize(ex.Summary), ct, workStart);
        }

        if (string.IsNullOrWhiteSpace(result.AnswerText))
        {
            var (failure, count) = await TrackGenerationAsync(ownerId, operationId, scheduledUtc,
                claimId, "empty LLM response", attemptIndex, ct);
            if (failure is not null)
                return failure;
            if (count > options.GenerationRetries)
                return await PersistNoticeAsync(op, claimId, scheduledUtc, executionStarted,
                    attemptIndex, attempts, "empty LLM response", ct, workStart);
            return new SingleAttemptResult(SingleAttemptOutcome.NeedRetry, attempts,
                GenerationPolicy.RetryDelay(count - 1), "empty LLM response");
        }

        string canonical;
        try
        {
            canonical = AnswerSourceBound.RequireWithinBound(
                AnswerSourceBound.Canonicalize(result.AnswerText), options.MaxAnswerSourceChars);
        }
        catch (PayloadIntegrityException)
        {
            return await PersistNoticeAsync(op, claimId, scheduledUtc, executionStarted,
                attemptIndex, attempts, OccurrenceFailureCodes.AnswerSourceLimit, ct, workStart);
        }

        var answer = AnswerArtifact.Create(ArtifactVersion.New(), AnswerKind.Answer, canonical);
        var literalRequired = TextOnlyCapabilityPolicy.RequiresLiteral(canonical);
        var initialPlan = literalRequired
            ? DeliveryPlan.CreateInitialLiteral(answer.AnswerVersion, canonical)
            : DeliveryPlan.CreateInitial(answer.AnswerVersion, canonical);
        ReplyCanary.Log(logger, ownerId, operationId, scheduledUtc,
            literalRequired ? "literal" : "llm-authored",
            result.Provider, result.Model, StorageLimits.SchemaVersion);
        try
        {
            await occurrences.PersistGenerationAsync(new PersistGenerationRequest(
                ownerId, operationId, scheduledUtc, claimId, answer, initialPlan,
                executionStarted,
                new ReceiptUsage(result.Provider, result.Model, result.Usage.PromptTokens,
                    result.Usage.CompletionTokens, result.Usage.SearchResults, result.Usage.SearchUsed)), ct);
        }
        catch (TransientStoreException ex) { return AttemptResults.StorageRetry(attemptIndex, ex.Message); }
        catch (ClaimLostException) { return AttemptResults.ClaimWait(); }
        catch (OperationStoppedException) { return AttemptResults.Stopped(); }
        catch (PayloadIntegrityException ex)
        {
            return await support.FailAsync(ownerId, operationId, scheduledUtc, claimId,
                ex.Code, attempts, 0, ct);
        }
        return null;
    }

    // Both the legacy token and the Phase 5 task token enable previous-reply
    // memory; anything else omits it from model input.
    private static bool IsPreviousReplyMode(string memoryMode) =>
        memoryMode.Equals(ExecutionOptions.PreviousSuccessfulReply, StringComparison.Ordinal) ||
        memoryMode.Equals(TaskMemoryModes.IncludePreviousMessage, StringComparison.Ordinal);

    // Tracks one generation failure. Returns a failure result for
    // storage/claim/corruption outcomes, otherwise null with the new
    // attempt count for the caller's retry decision.
    private async Task<(SingleAttemptResult? Failure, int Count)> TrackGenerationAsync(
        string ownerId, string operationId, DateTime scheduledUtc, string claimId,
        string summary, int attemptIndex, CancellationToken ct)
    {
        try
        {
            var count = await occurrences.TrackGenerationAsync(
                new TrackGenerationRequest(ownerId, operationId, scheduledUtc, claimId, summary), ct);
            return (null, count);
        }
        catch (TransientStoreException ex) { return (AttemptResults.StorageRetry(attemptIndex, ex.Message), 0); }
        catch (ClaimLostException) { return (AttemptResults.ClaimWait(), 0); }
        catch (PayloadIntegrityException ex)
        {
            return (await support.FailAsync(ownerId, operationId, scheduledUtc, claimId,
                ex.Code, 0, 0, ct), 0);
        }
    }

    // Terminal pre-delivery failures persist the standard failure notice
    // and report ready-to-deliver (null) when storage remains usable.
    private async Task<SingleAttemptResult?> PersistNoticeAsync(
        OperationRecord op, string claimId, DateTime scheduledUtc, DateTime executionStarted,
        int attemptIndex, int attempts, string code, CancellationToken ct, DateTimeOffset workStart)
    {
        var ownerId = op.OwnerId;
        var operationId = op.OperationId;
        var notice = AnswerArtifact.Create(
            ArtifactVersion.New(), AnswerKind.FailureNotice, OccurrenceMessages.FailureNotice);
        var plan = DeliveryPlan.CreateInitial(notice.AnswerVersion, notice.Text);
        ReplyCanary.Log(logger, ownerId, operationId, scheduledUtc, "failure-notice-literal",
            providerName, modelName, StorageLimits.SchemaVersion);
        try
        {
            await occurrences.PersistGenerationAsync(new PersistGenerationRequest(
                ownerId, operationId, scheduledUtc, claimId, notice, plan, executionStarted,
                new ReceiptUsage(providerName, modelName, 0, 0, 0, false), code), ct);
        }
        catch (TransientStoreException ex) { return AttemptResults.StorageRetry(attemptIndex, ex.Message); }
        catch (ClaimLostException) { return AttemptResults.ClaimWait(); }
        catch (OperationStoppedException) { return AttemptResults.Stopped(); }
        catch (PayloadIntegrityException ex)
        {
            return await support.FailAsync(ownerId, operationId, scheduledUtc, claimId,
                ex.Code, attempts, 0, ct);
        }
        return null;
    }
}
