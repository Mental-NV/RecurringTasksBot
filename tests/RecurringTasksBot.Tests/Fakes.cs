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
    private static string Key(string o, string id, DateTimeOffset t) => $"{o}:{id}:{t.UtcTicks}";

    public Task<DeliveryReceipt?> GetAsync(string ownerId, string operationId, DateTime scheduledUtc, CancellationToken ct = default)
    {
        _receipts.TryGetValue(Key(ownerId, operationId, scheduledUtc), out var r);
        return Task.FromResult(r);
    }

    public Task UpsertAsync(DeliveryReceipt receipt, CancellationToken ct = default)
    {
        _receipts[Key(receipt.OwnerId, receipt.OperationId, receipt.ScheduledUtc)] = receipt;
        return Task.CompletedTask;
    }
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
