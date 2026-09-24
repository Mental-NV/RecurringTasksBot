// Operation lifecycle states from docs/Spec.md.
using System.Text;

namespace RecurringTasksBot.Core;

public enum OperationStatus
{
    Starting,
    Active,
    Failed,
    Deleted,
}

public sealed record OperationRecord(
    string OwnerId,
    string OperationId,
    long ChatId,
    string CronExpression,
    string Text,
    OperationStatus Status,
    string? InstanceId,
    string? FailureSummary,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public sealed record UpdateReceipt(
    string OwnerId,
    long UpdateId,
    string Command,
    string? OperationId,
    bool CommandCompleted,
    bool ReplyDelivered,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public sealed record DeliveryReceipt(
    string OwnerId,
    string OperationId,
    DateTimeOffset ScheduledUtc,
    string Status,
    int Attempts,
    string? ErrorSummary,
    long? TelegramMessageId,
    // Phase 2 execution fields. Execution failure (generation) is distinct
    // from delivery failure (Telegram sends). Status keeps the phase-one
    // values ("sent", "failed") plus the transient "generating" claim.
    int GenerationAttempts = 0,
    string? ExecutionStatus = null,
    string? Provider = null,
    string? ModelName = null,
    long PromptTokens = 0,
    long CompletionTokens = 0,
    int SearchResults = 0,
    bool SearchUsed = false,
    int SentParts = 0,
    int TotalParts = 0,
    string? MessageIds = null,
    string? FailureNotice = null,
    DateTimeOffset UpdatedUtc = default,
    // Each activity invocation owns a unique lease; retry indices are not
    // ownership tokens. Null means the prior worker explicitly released it.
    string? ClaimId = null,
    string? PayloadVersion = null);

public static class UpdateReceipts
{
    // Update receipts carry command metadata, not content: a full-length
    // prompt stored verbatim would exceed Azure's 64-KiB property limit
    // (32,768 ASCII characters are 65,536 UTF-16 bytes). Only a bounded,
    // rune-safe prefix is stored; the operation row holds the full prompt.
    public const int MaxCommandChars = 2000;

    public static string BoundCommand(string? command)
    {
        if (string.IsNullOrEmpty(command))
            return string.Empty;
        var runes = command.EnumerateRunes().ToArray();
        return runes.Length <= MaxCommandChars
            ? command
            : string.Concat(runes[..MaxCommandChars].Select(r => r.ToString()));
    }
}

public static class OperationStatusNames
{
    public const string Starting = "starting";
    public const string Active = "active";
    public const string Failed = "failed";
    public const string Deleted = "deleted";

    public static string ToName(OperationStatus status) => status switch
    {
        OperationStatus.Starting => Starting,
        OperationStatus.Active => Active,
        OperationStatus.Failed => Failed,
        OperationStatus.Deleted => Deleted,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static OperationStatus Parse(string name) => name switch
    {
        Starting => OperationStatus.Starting,
        Active => OperationStatus.Active,
        Failed => OperationStatus.Failed,
        Deleted => OperationStatus.Deleted,
        _ => throw new ArgumentException($"Unknown operation status '{name}'.", nameof(name)),
    };
}
