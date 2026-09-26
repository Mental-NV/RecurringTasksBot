// Telegram Bot API client. Phase 3 answers go out through one typed
// request per sender call; each call sends exactly one message. API errors
// map to TelegramSendException (HTTP status and Telegram error_code both
// retained) or TelegramUnknownMethodException for classification by the
// delivery service; network failures propagate so callers can return 503.
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

    // Typed one-request send: exactly one message per call. A purported
    // success without a valid message ID is an ambiguous transport/protocol
    // failure, never a confirmed delivery. An unavailable rich endpoint
    // surfaces as TelegramUnknownMethodException so the delivery service can
    // switch to regular literal messages; this method never splits, changes
    // formats, or swallows a rejection.
    public async Task<long> SendPayloadAsync(long chatId, TelegramPayload payload, CancellationToken ct = default)
    {
        var (method, body) = payload.Kind switch
        {
            TelegramPayloadKind.Markdown =>
                ("sendRichMessage", TelegramRichMessage.BuildMarkdownRequestJson(chatId, payload.Content)),
            TelegramPayloadKind.LiteralRich =>
                ("sendRichMessage", TelegramRichMessage.BuildLiteralRichRequestJson(chatId, payload.Content)),
            TelegramPayloadKind.LegacyHtml =>
                ("sendRichMessage", TelegramRichMessage.BuildRequestJson(chatId, payload.Content)),
            TelegramPayloadKind.LiteralPlain =>
                ("sendMessage", JsonSerializer.Serialize(new SendRequest(chatId, payload.Content), Json)),
            _ => throw new ArgumentOutOfRangeException(nameof(payload)),
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Phase3Limits.TelegramRequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://api.telegram.org/bot{options.BotToken}/{method}");
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            var response = await http.SendAsync(request, timeout.Token);
            return await ReadPayloadResultAsync(response, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TelegramSendException(null, "Telegram request timed out after 30 seconds.");
        }
    }

    public async Task<long> SendRichTextAsync(long chatId, string part, CancellationToken ct = default)
    {
        try
        {
            return await SendPayloadAsync(
                chatId, new TelegramPayload(TelegramPayloadKind.LegacyHtml, part), ct);
        }
        catch (TelegramUnknownMethodException)
        {
            // Legacy adapter: the rich endpoint is unavailable, so degrade to
            // one plain-text message. Delivery planning owns chunking.
            return await SendPlainFallbackAsync(chatId, part, ct);
        }
    }

    // Plain-text degradation for legacy callers (command replies): the
    // stripped text is split for the conservative 4,096-unit transport and
    // every chunk is sent; the first message ID is returned. Delivery
    // planning owns bounded replacement parts for occurrence sends.
    private async Task<long> SendPlainFallbackAsync(long chatId, string part, CancellationToken ct)
    {
        var plain = RichMessageParts.ToPlainText(part);
        long firstId = 0;
        foreach (var chunk in Phase3LiteralChunker.SplitConservative(plain))
        {
            if (chunk.Length == 0)
                continue;
            using var response = await http.PostAsJsonAsync(
                $"https://api.telegram.org/bot{options.BotToken}/sendMessage",
                new SendRequest(chatId, chunk), Json, ct);
            var id = await ReadMessageIdAsync(response, ct);
            if (firstId == 0)
                firstId = id;
        }
        return firstId;
    }

    private static async Task<long> ReadPayloadResultAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var httpStatus = (int)response.StatusCode;
        using (response)
        {
            JsonDocument doc;
            try
            {
                using var stream = await response.Content.ReadAsStreamAsync(ct);
                doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                throw new TelegramSendException(httpStatus, "unreadable Telegram response");
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                {
                    if (root.TryGetProperty("result", out var result) &&
                        result.TryGetProperty("message_id", out var messageId) &&
                        messageId.ValueKind == JsonValueKind.Number &&
                        messageId.TryGetInt64(out var id) && id > 0)
                        return id;
                    throw new TelegramSendException(httpStatus,
                        "ambiguous Telegram acknowledgement without a valid message id");
                }

                var errorCode = root.TryGetProperty("error_code", out var code) &&
                    code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var number)
                    ? number : (int?)null;
                var description = root.TryGetProperty("description", out var desc) &&
                    desc.ValueKind == JsonValueKind.String
                    ? desc.GetString() ?? "send failed" : "send failed";
                TimeSpan? retryAfter = null;
                if (root.TryGetProperty("parameters", out var parameters) &&
                    parameters.TryGetProperty("retry_after", out var retry) &&
                    retry.ValueKind == JsonValueKind.Number && retry.TryGetInt32(out var seconds) &&
                    seconds >= 0)
                    retryAfter = TimeSpan.FromSeconds(seconds);

                if (TelegramErrorClassifier.Classify(httpStatus, errorCode, description).Disposition ==
                    TelegramDisposition.UnknownMethod)
                    throw new TelegramUnknownMethodException(httpStatus, errorCode, description);
                throw new TelegramSendException(httpStatus, description, retryAfter, errorCode);
            }
        }
    }

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
