namespace RecurringTasksBot.Application;

// Shared UTC normalization for persisted timestamps.
public static class UtcTime
{
    public static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
}
