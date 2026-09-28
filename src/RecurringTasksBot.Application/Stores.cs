// Storage and external-service contracts. Implemented by the Functions app
// (Azure Tables / Durable / Telegram HTTP); faked or mocked in unit tests.
namespace RecurringTasksBot.Application;

public sealed class ConcurrencyConflictException(string message) : Exception(message);

public sealed class TransientStoreException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed class ClaimLostException() : Exception("Occurrence lease is no longer owned.");

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

public interface ITelegramTransport
{
    // Sends exactly one payload in exactly one HTTP request and returns the
    // confirmed Telegram message ID. Any failure throws; the caller owns
    // chunking, fallback transitions, and retries.
    Task<long> SendAsync(long chatId, TelegramPayload payload, CancellationToken ct = default);
}

public interface IOrchestrationClient
{
    Task TerminateAsync(string instanceId, CancellationToken ct = default);
    Task<string?> GetRuntimeStatusAsync(string instanceId, CancellationToken ct = default);
    Task PurgeHistoryAsync(string instanceId, CancellationToken ct = default);
}
