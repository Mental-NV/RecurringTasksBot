// Storage and external-service contracts. Implemented by the Functions app
// (Azure Tables / Durable / Telegram HTTP); faked or mocked in unit tests.
namespace RecurringTasksBot.Core;

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

public interface IDeliveryReceiptStore
{
    Task<DeliveryReceipt?> GetAsync(string ownerId, string operationId, DateTime scheduledUtc, CancellationToken ct = default);
    // Writes from an owner must be fenced by ClaimId and a live lease.
    Task UpsertAsync(DeliveryReceipt receipt, CancellationToken ct = default);

    // Acquire only an unowned or expired occurrence using a conditional write.
    Task<bool> TryClaimAsync(string ownerId, string operationId, DateTime scheduledUtc,
        string claimId, CancellationToken ct = default);
    Task<bool> RenewClaimAsync(string ownerId, string operationId, DateTime scheduledUtc,
        string claimId, CancellationToken ct = default);
    Task ReleaseClaimAsync(string ownerId, string operationId, DateTime scheduledUtc,
        string claimId, CancellationToken ct = default);
}

// Persisted deliverable text, stored before any Telegram send so delivery
// retries reuse it and resume at the first unconfirmed message part.
public interface IOccurrencePayloadStore
{
    Task PersistAsync(string ownerId, string operationId, DateTime scheduledUtc,
        IReadOnlyList<string> parts, CancellationToken ct = default, string? version = null);
    Task<IReadOnlyList<string>?> LoadAsync(string ownerId, string operationId, DateTime scheduledUtc,
        CancellationToken ct = default, string? version = null);
}

public interface ITelegramSender
{
    // Returns the Telegram message id on success.
    Task<long> SendTextAsync(long chatId, string text, CancellationToken ct = default);

    // Sends one validated rich-message part (up to 32,768 characters).
    // The default implementation reuses plain-text sends so existing fakes
    // and mocks keep working; the production sender uses sendRichMessage.
    Task<long> SendRichTextAsync(long chatId, string part, CancellationToken ct = default) =>
        SendTextAsync(chatId, part, ct);
}

public interface IOrchestrationClient
{
    // Durably accepts startup; throwing TransientStoreException signals retryable failure.
    Task StartAsync(string instanceId, string operationId, string ownerId, CancellationToken ct = default);
    Task TerminateAsync(string instanceId, CancellationToken ct = default);
    Task<string?> GetRuntimeStatusAsync(string instanceId, CancellationToken ct = default);
    Task PurgeHistoryAsync(string instanceId, CancellationToken ct = default);
}
