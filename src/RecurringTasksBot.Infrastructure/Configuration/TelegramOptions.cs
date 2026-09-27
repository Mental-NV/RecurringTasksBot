namespace RecurringTasksBot.Infrastructure.Configuration;

public sealed record TelegramOptions(
    string BotToken,
    string WebhookSecret);
