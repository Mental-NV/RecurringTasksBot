// Storage and external-service contracts. Implemented by the Functions app
// (Azure Tables / Durable / Telegram HTTP); faked or mocked in unit tests.
namespace RecurringTasksBot.Core;

public sealed class ConcurrencyConflictException(string message) : Exception(message);

public sealed class TransientStoreException(string message, Exception? inner = null)
    : Exception(message, inner);

public interface IOperationStore
{
    // Owner-scoped reads enforce user isolation: never return another owner's row.
    Task<OperationRecord?> GetAsync(string ownerId, string operationId, CancellationToken ct = default);
    Task InsertStartingAsync(OperationRecord record, CancellationToken ct = default);
    Task<bool> CompareAndSwapStatusAsync(
        string ownerId, string operationId,
        OperationStatus expected, OperationStatus next,
        string? failureSummary = null, CancellationToken ct = default);
    Task<IReadOnlyList<OperationRecord>> ListOwnedAsync(string ownerId, CancellationToken ct = default);
}

public interface IUpdateReceiptStore
{
    Task<UpdateReceipt?> GetAsync(string ownerId, long updateId, CancellationToken ct = default);
    Task InsertAsync(UpdateReceipt receipt, CancellationToken ct = default);
    Task MarkCompletedAsync(string ownerId, long updateId, string? operationId, CancellationToken ct = default);
    Task MarkReplyDeliveredAsync(string ownerId, long updateId, CancellationToken ct = default);
}

public interface IDeliveryReceiptStore
{
    Task<DeliveryReceipt?> GetAsync(string ownerId, string operationId, DateTime scheduledUtc, CancellationToken ct = default);
    Task UpsertAsync(DeliveryReceipt receipt, CancellationToken ct = default);
}

public interface ITelegramSender
{
    // Returns the Telegram message id on success.
    Task<long> SendTextAsync(long chatId, string text, CancellationToken ct = default);
}

public interface IOrchestrationClient
{
    // Durably accepts startup; throwing TransientStoreException signals retryable failure.
    Task StartAsync(string instanceId, string operationId, string ownerId, CancellationToken ct = default);
    Task TerminateAsync(string instanceId, CancellationToken ct = default);
    Task<string?> GetRuntimeStatusAsync(string instanceId, CancellationToken ct = default);
    Task PurgeHistoryAsync(string instanceId, CancellationToken ct = default);
}
