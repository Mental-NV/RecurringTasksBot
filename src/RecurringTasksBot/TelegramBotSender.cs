// Telegram Bot API client. Answers, lists, and bot replies go out as rich
// messages via sendRichMessage (up to 32,768 characters per part, already
// validated by Core); plain-text sends remain for compatibility. API errors
// map to TelegramSendException for Core classification; network failures
// propagate so callers can return 503.
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using RecurringTasksBot.Core;

namespace RecurringTasksBot;

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
        return await ReadMessageIdAsync(response, ct);
    }

    public async Task<long> SendRichTextAsync(long chatId, string part, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.telegram.org/bot{options.BotToken}/sendRichMessage");
        request.Content = new StringContent(
            TelegramRichMessage.BuildRequestJson(chatId, part),
            System.Text.Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        try
        {
            return await ReadMessageIdAsync(response, ct);
        }
        catch (TelegramSendException ex) when (IsUnknownMethod(ex))
        {
            // The rich endpoint is unavailable: degrade to plain-text parts
            // without losing the answer.
            return await SendPlainFallbackAsync(chatId, part, ct);
        }
    }

    private async Task<long> SendPlainFallbackAsync(long chatId, string part, CancellationToken ct)
    {
        // Plain sendMessage caps at 4,096 characters; split the stripped
        // text and return the first message id.
        var plain = System.Text.RegularExpressions.Regex.Replace(part, @"<[^>]+>", string.Empty);
        plain = plain.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
        long firstId = 0;
        foreach (var chunk in TextLimits.SplitByChars(plain, 4096))
        {
            using var response = await http.PostAsJsonAsync(
                $"https://api.telegram.org/bot{options.BotToken}/sendMessage",
                new SendRequest(chatId, chunk), Json, ct);
            var id = await ReadMessageIdAsync(response, ct);
            if (firstId == 0)
                firstId = id;
        }

        return firstId;
    }

    private static bool IsUnknownMethod(TelegramSendException ex) =>
        ex.HttpStatusCode == 404 ||
        (ex.Description?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true) ||
        (ex.Description?.Contains("unknown method", StringComparison.OrdinalIgnoreCase) == true);

    private static async Task<long> ReadMessageIdAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using (response)
        {
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
    }

    private sealed record SendRequest(
        [property: JsonPropertyName("chat_id")] long ChatId,
        [property: JsonPropertyName("text")] string Text);
}
