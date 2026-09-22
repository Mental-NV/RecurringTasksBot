using System.Security.Cryptography;
using System.Text;

namespace RecurringTasksBot.Core;

// Stable, deterministic IDs derived from the Telegram update so that webhook
// redelivery resumes unfinished work instead of starting another recurrence.
public static class Ids
{
    public static string DeriveOperationId(long userId, long updateId, string commandText)
    {
        var hash = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{userId}:{updateId}:{commandText}"));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    public static string DeriveInstanceId(string operationId) => $"recurring-{operationId}";

    public static string OperationRowKey(string operationId) => $"operation_{operationId}";

    public static string UpdateReceiptRowKey(long updateId) => $"update_{updateId}";

    public static string DeliveryReceiptRowKey(string operationId, DateTime scheduledUtc) =>
        $"delivery_{operationId}_{scheduledUtc.Ticks}";

    // Duplicate-delivery suppression key: (operationId, scheduledUtc).
    public static string DeliveryDedupKey(string operationId, DateTime scheduledUtc) =>
        $"{operationId}:{scheduledUtc.Ticks}";
}
