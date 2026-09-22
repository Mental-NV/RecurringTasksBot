namespace RecurringTasksBot;

public sealed record BotOptions(
    string StorageConnectionString,
    string TableName,
    string BotToken,
    string WebhookSecret);
