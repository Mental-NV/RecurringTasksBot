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

public sealed class FakeLlmExecutor : ILlmPromptExecutor
{
    public List<LlmPrompt> Calls { get; } = new();
    public Func<LlmPrompt, Task<LlmResult>>? Handler { get; set; }

    public static LlmResult Answer(string text, IReadOnlyList<LlmSource>? sources = null) =>
        new(text, sources ?? [], new LlmUsage(10, 20, sources?.Count ?? 0, (sources?.Count ?? 0) > 0),
            "OpenRouter", "deepseek/deepseek-v4.1-flash", (sources?.Count ?? 0) > 0);

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
