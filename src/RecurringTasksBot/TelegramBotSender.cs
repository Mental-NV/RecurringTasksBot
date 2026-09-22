using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using RecurringTasksBot.Core;

namespace RecurringTasksBot;

// Minimal Telegram Bot API client: plain-text sends without markup parsing.
// API errors map to TelegramSendException for Core classification; network
// failures propagate so callers can return 503.
public sealed class TelegramBotSender(HttpClient http, BotOptions options) : ITelegramSender
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<long> SendTextAsync(long chatId, string text, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync(
            $"https://api.telegram.org/bot{options.BotToken}/sendMessage",
            new SendRequest(chatId, text), Json, ct);

        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = doc.RootElement;

        if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean())
        {
            if (root.TryGetProperty("result", out var result) &&
                result.TryGetProperty("message_id", out var messageId))
                return messageId.GetInt64();
            return 0;
        }

        var errorCode = root.TryGetProperty("error_code", out var code)
            ? code.GetInt32() : (int?)null;
        var description = root.TryGetProperty("description", out var desc)
            ? desc.GetString() ?? "send failed" : "send failed";
        TimeSpan? retryAfter = null;
        if (root.TryGetProperty("parameters", out var parameters) &&
            parameters.TryGetProperty("retry_after", out var retry))
            retryAfter = TimeSpan.FromSeconds(retry.GetInt32());

        throw new TelegramSendException(errorCode, description, retryAfter);
    }

    private sealed record SendRequest(
        [property: JsonPropertyName("chat_id")] long ChatId,
        [property: JsonPropertyName("text")] string Text);
}
