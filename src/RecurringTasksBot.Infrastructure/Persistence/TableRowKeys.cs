// Azure Tables row keys. Moved from Application: row-key shapes are a
// Table storage concern; Application keeps only deterministic business
// IDs (see Ids).
namespace RecurringTasksBot.Infrastructure.Persistence;

public static class TableRowKeys
{
    public static string Operation(string operationId) => $"operation_{operationId}";

    public static string UpdateReceipt(long updateId) => $"update_{updateId}";

    public static string DeliveryReceipt(string operationId, DateTime scheduledUtc) =>
        $"delivery_{operationId}_{scheduledUtc.Ticks}";

    public static string Memory(string operationId) => $"memory_{operationId}";

    public static string Context(string operationId, DateTime scheduledUtc, string contextVersion) =>
        $"delivery_{operationId}_{scheduledUtc.Ticks}__context_{contextVersion}";

    public static string Answer(string operationId, DateTime scheduledUtc, string answerVersion) =>
        $"delivery_{operationId}_{scheduledUtc.Ticks}__answer_{answerVersion}";

    public static string Plan(string operationId, DateTime scheduledUtc, string planVersion) =>
        $"delivery_{operationId}_{scheduledUtc.Ticks}__plan_{planVersion}";
}
