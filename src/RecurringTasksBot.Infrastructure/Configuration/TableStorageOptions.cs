namespace RecurringTasksBot.Infrastructure.Configuration;

public sealed record TableStorageOptions(
    string StorageConnectionString,
    string TableName);
