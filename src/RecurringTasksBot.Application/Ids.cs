using System.Security.Cryptography;
using System.Text;

namespace RecurringTasksBot.Application;

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

    // Duplicate-delivery suppression key: (operationId, scheduledUtc).
    public static string DeliveryDedupKey(string operationId, DateTime scheduledUtc) =>
        $"{operationId}:{scheduledUtc.Ticks}";
}
