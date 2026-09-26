// In-memory fakes enforcing the same contracts as the Table-backed stores:
// owner-scoped reads (user isolation) and conditional writes.
using RecurringTasksBot.Core;

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

public sealed class FakeDeliveryStore : IDeliveryReceiptStore
{
    private readonly Dictionary<string, DeliveryReceipt> _receipts = new();
    private readonly object _gate = new();
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public int RenewalCount { get; private set; }
    public bool LoseOnRenewal { get; set; }
    private static string Key(string o, string id, DateTimeOffset t) => $"{o}:{id}:{t.UtcTicks}";

    public Task<DeliveryReceipt?> GetAsync(string ownerId, string operationId, DateTime scheduledUtc, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _receipts.TryGetValue(Key(ownerId, operationId, scheduledUtc), out var r);
            return Task.FromResult(r);
        }
    }

    public Task UpsertAsync(DeliveryReceipt receipt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var key = Key(receipt.OwnerId, receipt.OperationId, receipt.ScheduledUtc);
            // Unowned writes seed test fixtures only. Owned writes follow the
            // production store's ownership/expiry checks.
            if (receipt.ClaimId is not null &&
                (!_receipts.TryGetValue(key, out var current) || current.ClaimId != receipt.ClaimId ||
                 OccurrenceExecution.IsClaimStale(current, Now))) throw new ClaimLostException();
            _receipts[key] = receipt.ClaimId is null ? receipt : receipt with { UpdatedUtc = Now };
            return Task.CompletedTask;
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
                    !OccurrenceExecution.IsClaimStale(existing, Now)) return Task.FromResult(false);
            }
            else existing = new DeliveryReceipt(ownerId, operationId, scheduledUtc,
                OccurrenceExecution.StatusGenerating, 0, null, null);
            _receipts[key] = existing with { UpdatedUtc = Now, ClaimId = claimId };
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
                OccurrenceExecution.IsClaimStale(current, Now)) return Task.FromResult(false);
            _receipts[key] = current with { UpdatedUtc = Now };
            return Task.FromResult(true);
        }
    }

    public Task ReleaseClaimAsync(string ownerId, string operationId, DateTime scheduledUtc,
        string claimId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var key = Key(ownerId, operationId, scheduledUtc);
            if (_receipts.TryGetValue(key, out var current) && current.ClaimId == claimId)
                _receipts[key] = current with { ClaimId = null };
            return Task.CompletedTask;
        }
    }

    public void SeedStaleClaim(string ownerId, string operationId, DateTime scheduledUtc)
    {
        lock (_gate)
            _receipts[Key(ownerId, operationId, scheduledUtc)] = new DeliveryReceipt(
                ownerId, operationId, scheduledUtc, OccurrenceExecution.StatusGenerating,
                0, null, null, UpdatedUtc: Now - TimeSpan.FromHours(1), ClaimId: "dead-worker");
    }
}

public sealed class FakePayloadStore : IOccurrencePayloadStore
{
    private readonly Dictionary<string, IReadOnlyList<string>> _payloads = new();
    private static string Key(string o, string id, DateTimeOffset t) => $"{o}:{id}:{t.UtcTicks}";

    public Task PersistAsync(string ownerId, string operationId, DateTime scheduledUtc,
        IReadOnlyList<string> parts, CancellationToken ct = default, string? version = null)
    {
        _payloads[Key(ownerId, operationId, scheduledUtc) + ":" + version] = parts.ToList();
        _payloads[Key(ownerId, operationId, scheduledUtc) + ":"] = parts.ToList();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>?> LoadAsync(string ownerId, string operationId,
        DateTime scheduledUtc, CancellationToken ct = default, string? version = null)
    {
        _payloads.TryGetValue(Key(ownerId, operationId, scheduledUtc) + ":" + version, out var parts);
        return Task.FromResult(parts);
    }
}

public sealed class FakeClock : IPhase3Clock
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UtcNow => Now;
}

// In-memory occurrence repository enforcing the same atomicity, claim,
// version, hash, and memory rules as the Table-backed implementation:
// pointer-once publication, ordered confirmations, newer-wins memory.
public sealed class FakeOccurrenceRepository(
    FakeOperationStore operations,
    FakeDeliveryStore receipts,
    FakeClock clock) : IOccurrenceRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FrozenContextRecord> _contexts = new();
    private readonly Dictionary<string, AnswerArtifact> _answers = new();
    private readonly Dictionary<string, DeliveryPlanDoc> _plans = new();
    private readonly Dictionary<string, string> _planVersions = new();
    private readonly Dictionary<(string Owner, string Id), MemoryRecord> _memory = new();
    private static string Key(string o, string id, DateTime t) => $"{o}:{id}:{t.Ticks}";

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
                    throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt, "context row is missing");
                return new ContextInitResult(existing, false);
            }
            _memory.TryGetValue((request.OwnerId, request.OperationId), out var memory);
            var scheduled = Phase3Times.Utc(request.ScheduledUtc);
            var eligible = memory is not null && Phase3Times.Utc(memory.SourceScheduledUtc) < scheduled;
            var context = new FrozenContextRecord(
                Phase3Versions.New(), request.OwnerId, request.OperationId, scheduled,
                op.Text, op.CronExpression,
                Phase3ContextEnvelope.OccurrenceIdFor(request.OperationId, scheduled),
                Phase3ContextEnvelope.ToIso8601(scheduled),
                Phase3ContextEnvelope.ToIso8601(request.Inputs.ExecutionStartedUtc),
                eligible,
                eligible ? Phase3ContextEnvelope.ToIso8601(memory!.SourceScheduledUtc) : null,
                eligible ? Phase3ContextEnvelope.ToIso8601(memory!.SourceExecutedUtc) : null,
                eligible ? memory!.Answer : null,
                eligible ? null : memory is null ? "no_memory_row" : "no_eligible_previous_reply",
                request.Inputs.EffectiveSystemInstruction,
                request.Inputs.TargetAnswerTextChars,
                request.Inputs.MaxRichMessageChars,
                request.Inputs.MemoryMode,
                Phase3Limits.EnabledCapabilities,
                Phase3Limits.InstructionVersion,
                clock.UtcNow);
            _contexts[Key(request.OwnerId, request.OperationId, request.ScheduledUtc)] = context;
            receipts.UpsertAsync(receipt with
            {
                ContextVersion = context.ContextVersion,
                ContextInitialized = true,
                InstructionVersion = Phase3Limits.InstructionVersion,
                PayloadSchemaVersion = Phase3Limits.SchemaVersion,
            }, ct).GetAwaiter().GetResult();
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
            throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt,
                "initial plan does not match the generated answer");
        DeliveryPlan.Validate(request.InitialPlan, request.Answer.Text);
        lock (_gate)
        {
            if (receipt.AnswerVersion is not null || receipt.PlanVersion is not null)
                return; // Pointers already committed: never regenerate.
            var planVersion = Phase3Versions.New();
            _answers[Key(request.OwnerId, request.OperationId, request.ScheduledUtc)] = request.Answer;
            _plans[Key(request.OwnerId, request.OperationId, request.ScheduledUtc)] = request.InitialPlan;
            _planVersions[Key(request.OwnerId, request.OperationId, request.ScheduledUtc)] = planVersion;
            receipts.UpsertAsync(receipt with
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
                PayloadSchemaVersion = Phase3Limits.SchemaVersion,
                SentParts = 0,
                TotalParts = request.InitialPlan.Leaves.Count,
                MessageIds = string.Empty,
                FailureNotice = request.Answer.Kind == AnswerKind.FailureNotice
                    ? request.Answer.Text : receipt.FailureNotice,
            }, ct).GetAwaiter().GetResult();
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
                : throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt, "plan row is missing");
            if (plan.AnswerVersion != receipt.AnswerVersion)
                throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt,
                    "plan references another answer");
            var confirmed = DeliveryPlan.Confirm(plan, request.LeafId, request.MessageId);
            if (ReferenceEquals(confirmed, plan))
                return confirmed; // Idempotent replay writes nothing.
            _plans[key] = confirmed;
            receipts.UpsertAsync(WithDeliveryFailures(
                DeliveryPlanProgress.ApplyToReceipt(receipt, confirmed), request.DeliveryFailures), ct)
                .GetAwaiter().GetResult();
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
                : throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt, "plan row is missing");
            if (!_answers.TryGetValue(key, out var answer))
                throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt, "answer row is missing");
            if (plan.AnswerVersion != receipt.AnswerVersion)
                throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt,
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
            receipts.UpsertAsync(WithDeliveryFailures(
                DeliveryPlanProgress.ApplyToReceipt(receipt, replaced), request.DeliveryFailures), ct)
                .GetAwaiter().GetResult();
            return new PlanReplacement(replaced, fresh);
        }
    }

    public async Task<ProgressRead> ReadProgressAsync(
        string ownerId, string operationId, DateTime scheduledUtc, string claimId,
        CancellationToken ct = default)
    {
        var receipt = await OwnedReceiptAsync(ownerId, operationId, scheduledUtc, claimId);
        if (receipt.AnswerVersion is null || receipt.PlanVersion is null)
            throw new Phase3ConsistencyException(
                "plan progress requires committed answer and plan pointers");
        lock (_gate)
        {
            var key = Key(ownerId, operationId, scheduledUtc);
            var answer = _answers.TryGetValue(key, out var stored)
                ? stored
                : throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt,
                    "answer row is missing");
            var plan = _plans.TryGetValue(key, out var existing)
                ? existing
                : throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt,
                    "plan row is missing");
            if (plan.AnswerVersion != receipt.AnswerVersion)
                throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt,
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
            receipts.UpsertAsync(updated, ct).GetAwaiter().GetResult();
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
            receipts.UpsertAsync(updated, ct).GetAwaiter().GetResult();
            return updated.DeliveryTransientFailures;
        }
    }

    public async Task FailAsync(FailRequest request, CancellationToken ct = default)
    {
        var receipt = await OwnedReceiptAsync(request.OwnerId, request.OperationId,
            request.ScheduledUtc, request.ClaimId);
        lock (_gate)
        {
            receipts.UpsertAsync(receipt with
            {
                Status = "failed",
                ErrorSummary = request.ErrorSummary,
                Attempts = receipt.Attempts + request.HttpAttempts,
                DeliveryTransientFailures = receipt.DeliveryTransientFailures + request.DeliveryFailures,
            }, ct).GetAwaiter().GetResult();
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
            throw new Phase3ConsistencyException("completion requires committed answer and plan pointers");
        lock (_gate)
        {
            var key = Key(request.OwnerId, request.OperationId, request.ScheduledUtc);
            var answer = _answers.TryGetValue(key, out var stored)
                ? stored
                : throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt, "answer row is missing");
            var plan = _plans.TryGetValue(key, out var existing)
                ? existing
                : throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt, "plan row is missing");
            if (plan.Leaves.Any(l => !l.Confirmed))
                throw new Phase3ConsistencyException("completion requires all leaves confirmed");
            if (answer.Kind == AnswerKind.Answer)
            {
                _memory.TryGetValue((request.OwnerId, request.OperationId), out var memory);
                var publish = MemoryPublication.Decide(memory,
                    request.OwnerId, request.OperationId,
                    Phase3Times.Utc(request.ScheduledUtc),
                    Phase3Times.Utc(op.CreatedUtc.UtcDateTime),
                    answer, clock.UtcNow);
                if (publish is not null)
                    _memory[(request.OwnerId, request.OperationId)] = publish;
            }
            receipts.UpsertAsync(WithDeliveryFailures(
                receipt with { Status = "sent" }, request.DeliveryFailures), ct)
                .GetAwaiter().GetResult();
        }
    }

    private async Task<DeliveryReceipt> OwnedReceiptAsync(
        string ownerId, string operationId, DateTime scheduledUtc, string claimId)
    {
        var receipt = await receipts.GetAsync(ownerId, operationId, scheduledUtc)
            ?? throw new ClaimLostException();
        if (receipt.ClaimId is null || receipt.ClaimId != claimId ||
            OccurrenceExecution.IsClaimStale(receipt, clock.UtcNow))
            throw new ClaimLostException();
        return receipt;
    }

    private async Task<OperationRecord> ActiveOperationAsync(string ownerId, string operationId)
    {
        var op = await operations.GetAsync(ownerId, operationId)
            ?? throw new Phase3OperationStoppedException($"operation {operationId} is missing");
        if (op.Status is not (OperationStatus.Active or OperationStatus.Starting))
            throw new Phase3OperationStoppedException($"operation {operationId} is {op.Status}");
        return op;
    }

    private static void RequirePointers(DeliveryReceipt receipt)
    {
        if (receipt.AnswerVersion is null || receipt.PlanVersion is null)
            throw new Phase3ConsistencyException("plan progress requires committed answer and plan pointers");
    }
}

public sealed class FakeLlmExecutor : ILlmPromptExecutor, IPhase3LlmExecutor
{
    public List<LlmPrompt> Calls { get; } = new();
    public Func<LlmPrompt, Task<LlmResult>>? Handler { get; set; }
    public List<IReadOnlyList<ChatMessage>> Phase3Calls { get; } = new();
    public Func<IReadOnlyList<ChatMessage>, Task<LlmResult>>? Phase3Handler { get; set; }

    public static LlmResult Answer(string text, IReadOnlyList<LlmSource>? sources = null) =>
        new(text, sources ?? [], new LlmUsage(10, 20, sources?.Count ?? 0, (sources?.Count ?? 0) > 0),
            "OpenRouter", "deepseek/deepseek-v4.1-flash", (sources?.Count ?? 0) > 0);

    public Task<LlmResult> ExecuteMessagesAsync(
        IReadOnlyList<ChatMessage> messages, int maxSourceScalars, CancellationToken ct = default)
    {
        Phase3Calls.Add(messages);
        return Phase3Handler is not null
            ? Phase3Handler(messages)
            : Task.FromResult(Answer("Canned answer."));
    }

    public Task<LlmResult> ExecuteAsync(LlmPrompt prompt, CancellationToken ct = default)
    {
        Calls.Add(prompt);
        return Handler is not null
            ? Handler(prompt)
            : Task.FromResult(Answer("Canned answer."));
    }
}

public static class TestLlm
{
    public static LlmOptions Options() => new(
        Provider: "OpenRouter",
        BaseUrl: "https://openrouter.ai/api/v1",
        Model: "deepseek/deepseek-v4.1-flash",
        ReasoningEffort: "Maximum",
        RequestTimeout: TimeSpan.FromSeconds(480),
        CompletionTokenBudget: 131072,
        MaxStoredAnswerChars: 32768,
        GenerationRetries: 2,
        SearchEnabled: true,
        SearchEngine: "exa",
        MaxSearches: 2,
        MaxResultsPerSearch: 5,
        MaxTotalResults: 10,
        SystemInstruction: string.Empty);
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

public sealed class FakeTelegramSender : ITelegramSender
{
    private readonly Queue<Func<long, string, Task<long>>> _script = new();
    public List<(long ChatId, string Text)> Sent { get; } = new();
    public Exception? AlwaysThrow { get; set; }
    public List<(long ChatId, TelegramPayloadKind Kind, string Content)> Payloads { get; } = new();
    public Func<TelegramPayload, Exception?>? RejectPayload { get; set; }
    private long _nextId = 900;

    public Task<long> SendPayloadAsync(long chatId, TelegramPayload payload, CancellationToken ct = default)
    {
        // Legacy HTML goes through the script-driven text seam, mirroring
        // the default interface adapter; typed payloads use rejections.
        if (payload.Kind == TelegramPayloadKind.LegacyHtml)
            return SendTextAsync(chatId, payload.Content, ct);
        Payloads.Add((chatId, payload.Kind, payload.Content));
        if (RejectPayload?.Invoke(payload) is { } ex)
            return Task.FromException<long>(ex);
        return Task.FromResult(Interlocked.Increment(ref _nextId));
    }

    public void EnqueueThrow(Exception ex) =>
        _script.Enqueue((_, _) => Task.FromException<long>(ex));

    public void EnqueueSuccess(long messageId = 1) =>
        _script.Enqueue((chat, text) =>
        {
            Sent.Add((chat, text));
            return Task.FromResult(messageId);
        });

    public Task<long> SendTextAsync(long chatId, string text, CancellationToken ct = default)
    {
        if (AlwaysThrow is not null)
            return Task.FromException<long>(AlwaysThrow);
        if (_script.Count > 0)
            return _script.Dequeue()(chatId, text);
        Sent.Add((chatId, text));
        return Task.FromResult(1L);
    }
}

public static class TestRecords
{
    public static OperationRecord Operation(string owner = "u1", string id = "op1",
        OperationStatus status = OperationStatus.Active) =>
        new(owner, id, 111L, "0 0 9 * * *", "Water the plants", status,
            Ids.DeriveInstanceId(id), null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
