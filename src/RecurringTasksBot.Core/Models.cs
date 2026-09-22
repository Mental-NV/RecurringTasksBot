// Operation lifecycle states from docs/Spec.md.
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
    long? TelegramMessageId);

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
