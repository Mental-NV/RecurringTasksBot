// In-memory fakes enforcing the same contracts as the Table-backed stores:
// owner-scoped reads (user isolation) and conditional writes.
using System.Net;
using System.Text;
using RecurringTasksBot.Application;
using RecurringTasksBot.Infrastructure.Llm;

namespace RecurringTasksBot.Tests;

public sealed class FakeOperationStore : IOperationStore
{
    private readonly Dictionary<(string Owner, string Id), OperationRecord> _ops = new();

    public int ListCalls { get; private set; }

    public Task<OperationRecord?> GetAsync(string ownerId, string operationId, CancellationToken ct = default)
    {
        _ops.TryGetValue((ownerId, operationId), out var op);
        return Task.FromResult(op);
    }

    public Task InsertStartingAsync(OperationRecord record, CancellationToken ct = default)
    {
        if (_ops.ContainsKey((record.OwnerId, record.OperationId)))
            throw new ConcurrencyConflictException("operation exists");
        _ops[(record.OwnerId, record.OperationId)] = record;
        return Task.CompletedTask;
    }

    public Task<bool> CompareAndSwapStatusAsync(string ownerId, string operationId,
        OperationStatus expected, OperationStatus next, string? failureSummary = null,
        CancellationToken ct = default)
    {
        if (!_ops.TryGetValue((ownerId, operationId), out var op) || op.Status != expected)
            return Task.FromResult(false);
        _ops[(ownerId, operationId)] = op with
        {
            Status = next,
            FailureSummary = failureSummary ?? op.FailureSummary,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<OperationRecord>> ListOwnedAsync(string ownerId, CancellationToken ct = default)
    {
        ListCalls++;
        IReadOnlyList<OperationRecord> result = _ops.Values
            .Where(o => o.OwnerId == ownerId && o.Status != OperationStatus.Deleted)
            .OrderBy(o => o.CreatedUtc)
            .ToList();
        return Task.FromResult(result);
    }

    public void Seed(OperationRecord record) => _ops[(record.OwnerId, record.OperationId)] = record;
}

public sealed class FakeReceiptStore : IUpdateReceiptStore
{
    private readonly Dictionary<(string Owner, long Update), UpdateReceipt> _receipts = new();

    public Task<UpdateReceipt?> GetAsync(string ownerId, long updateId, CancellationToken ct = default)
    {
        _receipts.TryGetValue((ownerId, updateId), out var r);
        return Task.FromResult(r);
    }

    public Task InsertAsync(UpdateReceipt receipt, CancellationToken ct = default)
    {
        if (_receipts.ContainsKey((receipt.OwnerId, receipt.UpdateId)))
            throw new ConcurrencyConflictException("receipt exists");
        _receipts[(receipt.OwnerId, receipt.UpdateId)] = receipt;
        return Task.CompletedTask;
    }

    public Task MarkCompletedAsync(string ownerId, long updateId, string? operationId, CancellationToken ct = default)
    {
        if (_receipts.TryGetValue((ownerId, updateId), out var r))
            _receipts[(ownerId, updateId)] = r with
            {
                CommandCompleted = true,
                OperationId = operationId ?? r.OperationId,
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
        return Task.CompletedTask;
    }

    public Task MarkReplyDeliveredAsync(string ownerId, long updateId, CancellationToken ct = default)
    {
        if (_receipts.TryGetValue((ownerId, updateId), out var r))
            _receipts[(ownerId, updateId)] = r with { ReplyDelivered = true };
        return Task.CompletedTask;
    }
}

public sealed class FakeClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}

// In-memory occurrence repository enforcing the same atomicity, claim,
// version, hash, and memory rules as the Table-backed implementation:
// pointer-once publication, ordered confirmations, newer-wins memory.
public sealed class FakeOccurrenceRepository(
    FakeOperationStore operations,
    FakeClock clock) : IOccurrenceRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FrozenContextRecord> _contexts = new();
    private readonly Dictionary<string, AnswerArtifact> _answers = new();
    private readonly Dictionary<string, DeliveryPlanDoc> _plans = new();
    private readonly Dictionary<string, string> _planVersions = new();
    private readonly Dictionary<(string Owner, string Id), MemoryRecord> _memory = new();
    private readonly Dictionary<string, DeliveryReceipt> _receipts = new();
    public int RenewalCount { get; private set; }
    public bool LoseOnRenewal { get; set; }
    private static string Key(string o, string id, DateTime t) => $"{o}:{id}:{t.Ticks}";

    public Task<DeliveryReceipt?> GetReceiptAsync(string ownerId, string operationId,
        DateTime scheduledUtc, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _receipts.TryGetValue(Key(ownerId, operationId, scheduledUtc), out var receipt);
            return Task.FromResult(receipt);
        }
    }

    public Task<bool> TryClaimAsync(string ownerId, string operationId, DateTime scheduledUtc,
        string claimId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var key = Key(ownerId, operationId, scheduledUtc);
            if (_receipts.TryGetValue(key, out var existing))
            {
                if (OccurrenceExecution.IsTerminal(existing) || existing.ClaimId is not null &&
                    !OccurrenceExecution.IsClaimStale(existing, clock.Now)) return Task.FromResult(false);
            }
            else existing = new DeliveryReceipt(ownerId, operationId, scheduledUtc,
                OccurrenceExecution.StatusGenerating, 0, null);
            _receipts[key] = existing with { UpdatedUtc = clock.Now, ClaimId = claimId };
            return Task.FromResult(true);
        }
    }

    public Task<bool> RenewClaimAsync(string ownerId, string operationId, DateTime scheduledUtc,
        string claimId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            RenewalCount++;
            var key = Key(ownerId, operationId, scheduledUtc);
            if (LoseOnRenewal || !_receipts.TryGetValue(key, out var current) || current.ClaimId != claimId ||
                OccurrenceExecution.IsClaimStale(current, clock.Now)) return Task.FromResult(false);
            _receipts[key] = current with { UpdatedUtc = clock.Now };
            return Task.FromResult(true);
        }
    }

    public bool ThrowOnRelease { get; set; }

    public Task ReleaseClaimAsync(string ownerId, string operationId, DateTime scheduledUtc,
        string claimId, CancellationToken ct = default)
    {
        if (ThrowOnRelease)
            throw new TransientStoreException("release failed");
        lock (_gate)
        {
            var key = Key(ownerId, operationId, scheduledUtc);
            if (_receipts.TryGetValue(key, out var current) && current.ClaimId == claimId)
                _receipts[key] = current with { ClaimId = null };
            return Task.CompletedTask;
        }
    }

    public Task MarkUnsupportedVersionAsync(string ownerId, string operationId, DateTime scheduledUtc,
        string claimId, int attempts, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var key = Key(ownerId, operationId, scheduledUtc);
            if (!_receipts.TryGetValue(key, out var current) || current.ClaimId != claimId ||
                OccurrenceExecution.IsClaimStale(current, clock.Now)) throw new ClaimLostException();
            _receipts[key] = current with
            {
                Status = "failed",
                ErrorSummary = OccurrenceFailureCodes.UnsupportedPayloadVersion,
                Attempts = attempts,
                UpdatedUtc = clock.Now,
            };
            return Task.CompletedTask;
        }
    }

    public Task SeedReceiptAsync(DeliveryReceipt receipt, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _receipts[Key(receipt.OwnerId, receipt.OperationId, receipt.ScheduledUtc.UtcDateTime)] = receipt;
            return Task.CompletedTask;
        }
    }

    public void SeedStaleClaim(string ownerId, string operationId, DateTime scheduledUtc)
    {
        lock (_gate)
            _receipts[Key(ownerId, operationId, scheduledUtc)] = new DeliveryReceipt(
                ownerId, operationId, scheduledUtc, OccurrenceExecution.StatusGenerating,
                0, null, UpdatedUtc: clock.Now - TimeSpan.FromHours(1), ClaimId: "dead-worker");
    }

    public Task<MemoryRecord?> ReadPreviousReplyAsync(string ownerId, string operationId,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            _memory.TryGetValue((ownerId, operationId), out var memory);
            return Task.FromResult(memory);
        }
    }

    public void SeedMemory(MemoryRecord memory)
    {
        lock (_gate)
            _memory[(memory.OwnerId, memory.OperationId)] = memory;
    }

    public async Task<ContextInitResult> InitializeContextAsync(
        FrozenContextRequest request, CancellationToken ct = default)
    {
        var receipt = await OwnedReceiptAsync(request.OwnerId, request.OperationId,
            request.ScheduledUtc, request.ClaimId);
        var op = await ActiveOperationAsync(request.OwnerId, request.OperationId);
        lock (_gate)
        {
            if (receipt.ContextVersion is not null)
            {
                if (!_contexts.TryGetValue(Key(request.OwnerId, request.OperationId, request.ScheduledUtc),
                    out var existing))
                    throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt, "context row is missing");
                return new ContextInitResult(existing, false);
            }
            _memory.TryGetValue((request.OwnerId, request.OperationId), out var memory);
            var scheduled = UtcTime.Utc(request.ScheduledUtc);
            var eligible = memory is not null && UtcTime.Utc(memory.SourceScheduledUtc) < scheduled;
            var context = new FrozenContextRecord(
                ArtifactVersion.New(), request.OwnerId, request.OperationId, scheduled,
                op.Text, op.CronExpression,
                ExecutionMessageBuilder.OccurrenceIdFor(request.OperationId, scheduled),
                ExecutionMessageBuilder.ToIso8601(scheduled),
                ExecutionMessageBuilder.ToIso8601(request.Inputs.ExecutionStartedUtc),
                eligible,
                eligible ? ExecutionMessageBuilder.ToIso8601(memory!.SourceScheduledUtc) : null,
                eligible ? ExecutionMessageBuilder.ToIso8601(memory!.SourceExecutedUtc) : null,
                eligible ? memory!.Answer : null,
                eligible ? null : memory is null ? "no_memory_row" : "no_eligible_previous_reply",
                request.Inputs.EffectiveSystemInstruction,
                request.Inputs.TargetAnswerTextChars,
                request.Inputs.MaxRichMessageChars,
                request.Inputs.MemoryMode,
                ExecutionLimits.EnabledCapabilities,
                ExecutionLimits.InstructionVersion,
                clock.GetUtcNow());
            _contexts[Key(request.OwnerId, request.OperationId, request.ScheduledUtc)] = context;
            WriteOwned(receipt with
            {
                ContextVersion = context.ContextVersion,
                ContextInitialized = true,
                InstructionVersion = ExecutionLimits.InstructionVersion,
                PayloadSchemaVersion = StorageLimits.SchemaVersion,
            });
            return new ContextInitResult(context, true);
        }
    }

    public async Task PersistGenerationAsync(
        PersistGenerationRequest request, CancellationToken ct = default)
    {
        var receipt = await OwnedReceiptAsync(request.OwnerId, request.OperationId,
            request.ScheduledUtc, request.ClaimId);
        await ActiveOperationAsync(request.OwnerId, request.OperationId);
        if (request.InitialPlan.AnswerVersion != request.Answer.AnswerVersion ||
            request.InitialPlan.SourceSha256 != request.Answer.SourceSha256)
            throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt,
                "initial plan does not match the generated answer");
        DeliveryPlan.Validate(request.InitialPlan, request.Answer.Text);
        lock (_gate)
        {
            if (receipt.AnswerVersion is not null || receipt.PlanVersion is not null)
                return; // Pointers already committed: never regenerate.
            var planVersion = ArtifactVersion.New();
            _answers[Key(request.OwnerId, request.OperationId, request.ScheduledUtc)] = request.Answer;
            _plans[Key(request.OwnerId, request.OperationId, request.ScheduledUtc)] = request.InitialPlan;
            _planVersions[Key(request.OwnerId, request.OperationId, request.ScheduledUtc)] = planVersion;
            WriteOwned(receipt with
            {
                ExecutionStatus = request.Answer.Kind == AnswerKind.Answer ? "generated" : "failed",
                ErrorSummary = request.ErrorSummary ?? receipt.ErrorSummary,
                Provider = request.Usage.Provider,
                ModelName = request.Usage.ModelName,
                PromptTokens = request.Usage.PromptTokens,
                CompletionTokens = request.Usage.CompletionTokens,
                SearchResults = request.Usage.SearchResults,
                SearchUsed = request.Usage.SearchUsed,
                AnswerVersion = request.Answer.AnswerVersion,
                PlanVersion = planVersion,
                PayloadSchemaVersion = StorageLimits.SchemaVersion,
                SentParts = 0,
                TotalParts = request.InitialPlan.Leaves.Count,
                MessageIds = string.Empty,
                FailureNotice = request.Answer.Kind == AnswerKind.FailureNotice
                    ? request.Answer.Text : receipt.FailureNotice,
            });
        }
    }

    public async Task<DeliveryPlanDoc> ConfirmLeafAsync(
        ConfirmLeafRequest request, CancellationToken ct = default)
    {
        var receipt = await OwnedReceiptAsync(request.OwnerId, request.OperationId,
            request.ScheduledUtc, request.ClaimId);
        RequirePointers(receipt);
        lock (_gate)
        {
            var key = Key(request.OwnerId, request.OperationId, request.ScheduledUtc);
            var plan = _plans.TryGetValue(key, out var stored)
                ? stored
                : throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt, "plan row is missing");
            if (plan.AnswerVersion != receipt.AnswerVersion)
                throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt,
                    "plan references another answer");
            var confirmed = DeliveryPlan.Confirm(plan, request.LeafId, request.MessageId);
            if (ReferenceEquals(confirmed, plan))
                return confirmed; // Idempotent replay writes nothing.
            _plans[key] = confirmed;
            WriteOwned(WithDeliveryFailures(
                DeliveryPlanProgress.ApplyToReceipt(receipt, confirmed), request.DeliveryFailures));
            return confirmed;
        }
    }

    public async Task<PlanReplacement> ReplaceLeafAsync(
        ReplaceLeafRequest request, CancellationToken ct = default)
    {
        var receipt = await OwnedReceiptAsync(request.OwnerId, request.OperationId,
            request.ScheduledUtc, request.ClaimId);
        RequirePointers(receipt);
        lock (_gate)
        {
            var key = Key(request.OwnerId, request.OperationId, request.ScheduledUtc);
            var plan = _plans.TryGetValue(key, out var stored)
                ? stored
                : throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt, "plan row is missing");
            if (!_answers.TryGetValue(key, out var answer))
                throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt, "answer row is missing");
            if (plan.AnswerVersion != receipt.AnswerVersion)
                throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt,
                    "plan references another answer");
            var (replaced, fresh) = request.Fallback switch
            {
                PlanFallbackKind.ToLiteralRich =>
                    DeliveryPlan.ReplaceWithLiteralRich(plan, answer.Text, request.LeafId),
                PlanFallbackKind.ToSmallLiteral =>
                    DeliveryPlan.ReplaceWithSmallLiteral(plan, answer.Text, request.LeafId),
                PlanFallbackKind.ToPlain =>
                    DeliveryPlan.ReplaceWithPlain(plan, answer.Text, request.LeafId),
                _ => throw new ArgumentOutOfRangeException(nameof(request)),
            };
            _plans[key] = replaced;
            WriteOwned(WithDeliveryFailures(
                DeliveryPlanProgress.ApplyToReceipt(receipt, replaced), request.DeliveryFailures));
            return new PlanReplacement(replaced, fresh);
        }
    }

    public async Task<ProgressRead> ReadProgressAsync(
        string ownerId, string operationId, DateTime scheduledUtc, string claimId,
        CancellationToken ct = default)
    {
        var receipt = await OwnedReceiptAsync(ownerId, operationId, scheduledUtc, claimId);
        if (receipt.AnswerVersion is null || receipt.PlanVersion is null)
            throw new OccurrenceConsistencyException(
                "plan progress requires committed answer and plan pointers");
        lock (_gate)
        {
            var key = Key(ownerId, operationId, scheduledUtc);
            var answer = _answers.TryGetValue(key, out var stored)
                ? stored
                : throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt,
                    "answer row is missing");
            var plan = _plans.TryGetValue(key, out var existing)
                ? existing
                : throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt,
                    "plan row is missing");
            if (plan.AnswerVersion != receipt.AnswerVersion)
                throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt,
                    "plan references another answer");
            return new ProgressRead(plan, answer);
        }
    }

    public async Task<int> TrackGenerationAsync(
        TrackGenerationRequest request, CancellationToken ct = default)
    {
        var receipt = await OwnedReceiptAsync(request.OwnerId, request.OperationId,
            request.ScheduledUtc, request.ClaimId);
        lock (_gate)
        {
            var updated = receipt with
            {
                GenerationAttempts = receipt.GenerationAttempts + 1,
                ErrorSummary = request.Summary,
            };
            WriteOwned(updated);
            return updated.GenerationAttempts;
        }
    }

    public async Task<int> TrackDeliveryAsync(
        TrackDeliveryRequest request, CancellationToken ct = default)
    {
        var receipt = await OwnedReceiptAsync(request.OwnerId, request.OperationId,
            request.ScheduledUtc, request.ClaimId);
        lock (_gate)
        {
            var updated = receipt with
            {
                Attempts = receipt.Attempts + 1,
                DeliveryTransientFailures = receipt.DeliveryTransientFailures + 1,
                ErrorSummary = request.Summary,
            };
            WriteOwned(updated);
            return updated.DeliveryTransientFailures;
        }
    }

    public async Task FailAsync(FailRequest request, CancellationToken ct = default)
    {
        var receipt = await OwnedReceiptAsync(request.OwnerId, request.OperationId,
            request.ScheduledUtc, request.ClaimId);
        lock (_gate)
        {
            WriteOwned(receipt with
            {
                Status = "failed",
                ErrorSummary = request.ErrorSummary,
                Attempts = receipt.Attempts + request.HttpAttempts,
                DeliveryTransientFailures = receipt.DeliveryTransientFailures + request.DeliveryFailures,
            });
        }
    }

    private static DeliveryReceipt WithDeliveryFailures(DeliveryReceipt receipt, int failures) =>
        receipt with { DeliveryTransientFailures = receipt.DeliveryTransientFailures + failures };

    public async Task CompleteAsync(CompleteRequest request, CancellationToken ct = default)
    {
        var receipt = await OwnedReceiptAsync(request.OwnerId, request.OperationId,
            request.ScheduledUtc, request.ClaimId);
        var op = await ActiveOperationAsync(request.OwnerId, request.OperationId);
        if (receipt.AnswerVersion is null || receipt.PlanVersion is null)
            throw new OccurrenceConsistencyException("completion requires committed answer and plan pointers");
        lock (_gate)
        {
            var key = Key(request.OwnerId, request.OperationId, request.ScheduledUtc);
            var answer = _answers.TryGetValue(key, out var stored)
                ? stored
                : throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt, "answer row is missing");
            var plan = _plans.TryGetValue(key, out var existing)
                ? existing
                : throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt, "plan row is missing");
            if (plan.Leaves.Any(l => !l.Confirmed))
                throw new OccurrenceConsistencyException("completion requires all leaves confirmed");
            if (answer.Kind == AnswerKind.Answer)
            {
                _memory.TryGetValue((request.OwnerId, request.OperationId), out var memory);
                var publish = MemoryPublication.Decide(memory,
                    request.OwnerId, request.OperationId,
                    UtcTime.Utc(request.ScheduledUtc),
                    UtcTime.Utc(op.CreatedUtc.UtcDateTime),
                    answer, clock.GetUtcNow());
                if (publish is not null)
                    _memory[(request.OwnerId, request.OperationId)] = publish;
            }
            WriteOwned(WithDeliveryFailures(
                receipt with { Status = "sent" }, request.DeliveryFailures));
        }
    }

    private async Task<DeliveryReceipt> OwnedReceiptAsync(
        string ownerId, string operationId, DateTime scheduledUtc, string claimId)
    {
        var receipt = await GetReceiptAsync(ownerId, operationId, scheduledUtc)
            ?? throw new ClaimLostException();
        if (receipt.ClaimId is null || receipt.ClaimId != claimId ||
            OccurrenceExecution.IsClaimStale(receipt, clock.GetUtcNow()))
            throw new ClaimLostException();
        return receipt;
    }

    private void WriteOwned(DeliveryReceipt receipt)
    {
        var key = Key(receipt.OwnerId, receipt.OperationId, receipt.ScheduledUtc.UtcDateTime);
        if (receipt.ClaimId is not null &&
            (!_receipts.TryGetValue(key, out var current) || current.ClaimId != receipt.ClaimId ||
             OccurrenceExecution.IsClaimStale(current, clock.Now))) throw new ClaimLostException();
        _receipts[key] = receipt.ClaimId is null ? receipt : receipt with { UpdatedUtc = clock.Now };
    }

    private async Task<OperationRecord> ActiveOperationAsync(string ownerId, string operationId)
    {
        var op = await operations.GetAsync(ownerId, operationId)
            ?? throw new OperationStoppedException($"operation {operationId} is missing");
        if (op.Status is not (OperationStatus.Active or OperationStatus.Starting))
            throw new OperationStoppedException($"operation {operationId} is {op.Status}");
        return op;
    }

    private static void RequirePointers(DeliveryReceipt receipt)
    {
        if (receipt.AnswerVersion is null || receipt.PlanVersion is null)
            throw new OccurrenceConsistencyException("plan progress requires committed answer and plan pointers");
    }
}

public sealed class FakeLlmExecutor : ILlmExecutor
{
    public List<LlmRequest> Requests { get; } = new();
    public List<IReadOnlyList<ChatMessage>> MessageCalls { get; } = new();
    public Func<IReadOnlyList<ChatMessage>, Task<LlmResult>>? Responder { get; set; }

    public static LlmResult Answer(string text, IReadOnlyList<LlmSource>? sources = null) =>
        new(text, sources ?? [], new LlmUsage(10, 20, sources?.Count ?? 0, (sources?.Count ?? 0) > 0),
            "OpenRouter", "deepseek/deepseek-v4.1-flash", (sources?.Count ?? 0) > 0);

    public Task<LlmResult> ExecuteAsync(LlmRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return ExecuteMessagesAsync(request.Messages, request.MaxSourceScalars, ct);
    }

    public Task<LlmResult> ExecuteMessagesAsync(
        IReadOnlyList<ChatMessage> messages, int maxSourceScalars, CancellationToken ct = default)
    {
        MessageCalls.Add(messages);
        return Responder is not null
            ? Responder(messages)
            : Task.FromResult(Answer("Canned answer."));
    }

}

public static class TestLlm
{
    public const string ProviderName = "OpenRouter";
    public const string ModelName = "deepseek/deepseek-v4.1-flash";

    public static ExecutionOptions Execution() => new(
        MemoryMode: ExecutionOptions.PreviousSuccessfulReply,
        TargetAnswerTextChars: 24000,
        MaxAnswerSourceChars: ExecutionLimits.AnswerSourceMaxScalars,
        DeclaredContextTokens: 1048576,
        SearchContextReserveTokens: 65536,
        ContextEnvelopeReserveTokens: 8192,
        RequestTimeout: TimeSpan.FromSeconds(480),
        CompletionTokenBudget: 131072,
        GenerationRetries: 2,
        SystemInstruction: string.Empty);

    public static OpenRouterOptions Provider() => new(
        Provider: ProviderName,
        BaseUrl: "https://openrouter.ai/api/v1",
        Model: ModelName,
        ReasoningEffort: "Maximum",
        SearchEnabled: true,
        SearchEngine: "exa",
        MaxSearches: 2,
        MaxResultsPerSearch: 5,
        MaxTotalResults: 10);
}

public sealed class FakeOrchestrations : IOrchestrationClient
{
    private readonly HashSet<string> _started = new();
    public List<string> StartedInstances { get; } = new();
    public List<string> TerminatedInstances { get; } = new();
    public Func<string, Task>? OnStart { get; set; }

    public Task StartAsync(string instanceId, string operationId, string ownerId, CancellationToken ct = default)
    {
        if (OnStart is not null)
            return OnStart(instanceId);
        _started.Add(instanceId);
        StartedInstances.Add(instanceId);
        return Task.CompletedTask;
    }

    public Task TerminateAsync(string instanceId, CancellationToken ct = default)
    {
        TerminatedInstances.Add(instanceId);
        _started.Remove(instanceId);
        return Task.CompletedTask;
    }

    public Task<string?> GetRuntimeStatusAsync(string instanceId, CancellationToken ct = default) =>
        Task.FromResult<string?>(_started.Contains(instanceId) ? "Running" : null);

    public Task PurgeHistoryAsync(string instanceId, CancellationToken ct = default) =>
        Task.CompletedTask;
}

public sealed class FakeTelegramSender : ITelegramTransport
{
    private readonly Queue<Func<long, string, Task<long>>> _script = new();
    public Exception? AlwaysThrow { get; set; }
    public List<(long ChatId, TelegramPayloadKind Kind, string Content)> Payloads { get; } = new();
    public Func<TelegramPayload, Exception?>? RejectPayload { get; set; }
    private long _nextId = 900;

    public Task<long> SendAsync(long chatId, TelegramPayload payload, CancellationToken ct = default)
    {
        // Scripted failures apply to every send, mirroring one HTTP request
        // per transport call; successes record the typed payload.
        if (AlwaysThrow is not null)
            return Task.FromException<long>(AlwaysThrow);
        if (_script.Count > 0)
            return _script.Dequeue()(chatId, payload.Content);
        Payloads.Add((chatId, payload.Kind, payload.Content));
        if (RejectPayload?.Invoke(payload) is { } ex)
            return Task.FromException<long>(ex);
        return Task.FromResult(Interlocked.Increment(ref _nextId));
    }

    public void EnqueueThrow(Exception ex) =>
        _script.Enqueue((_, _) => Task.FromException<long>(ex));

    public void EnqueueSuccess(long messageId = 1) =>
        _script.Enqueue((_, _) => Task.FromResult(messageId));
}

public static class TestRecords
{
    public static OperationRecord Operation(string owner = "u1", string id = "op1",
        OperationStatus status = OperationStatus.Active) =>
        new(owner, id, 111L, "0 0 9 * * *", "Water the plants", status,
            Ids.DeriveInstanceId(id), null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}

// Scripted HTTP handler shared by transport tests.
internal sealed class ScriptHandler : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest;
    public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices": [{"message": {"content": "ok"}}]}""",
                Encoding.UTF8, "application/json"),
        };

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        return Task.FromResult(Responder(request));
    }
}
