// Telegram Bot API client. Answers go out through one typed
// request per sender call; each call sends exactly one message. API errors
// map to TelegramSendException (HTTP status and Telegram error_code both
// retained) or TelegramUnknownMethodException for classification by the
// delivery service; network failures propagate so callers can return 503.
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using RecurringTasksBot.Application;
using RecurringTasksBot.Infrastructure.Configuration;

namespace RecurringTasksBot.Infrastructure.Bot;

public sealed class TelegramBotSender(HttpClient http, TelegramOptions options) : ITelegramTransport
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Exactly one payload in exactly one HTTP request per call. A purported
    // success without a valid message ID is an ambiguous transport/protocol
    // failure, never a confirmed delivery. An unavailable rich endpoint
    // surfaces as TelegramUnknownMethodException so the caller can fall back
    // to literal messages; this method never splits, changes formats, or
    // swallows a rejection.
    public async Task<long> SendAsync(long chatId, TelegramPayload payload, CancellationToken ct = default)
    {
        var (method, content) = payload.Kind switch
        {
            TelegramPayloadKind.Markdown => ("sendRichMessage", (HttpContent)new StringContent(
                TelegramRichMessage.BuildMarkdownRequestJson(chatId, payload.Content),
                System.Text.Encoding.UTF8, "application/json")),
            TelegramPayloadKind.LiteralRich => ("sendRichMessage", (HttpContent)new StringContent(
                TelegramRichMessage.BuildLiteralRichRequestJson(chatId, payload.Content),
                System.Text.Encoding.UTF8, "application/json")),
            TelegramPayloadKind.LiteralPlain => ("sendMessage", (HttpContent)new StringContent(
                JsonSerializer.Serialize(new SendRequest(chatId, payload.Content), Json),
                System.Text.Encoding.UTF8, "application/json")),
            TelegramPayloadKind.Document =>
                ("sendDocument", BuildDocumentContent(chatId, payload)),
            TelegramPayloadKind.Table => ("sendRichMessage", (HttpContent)new StringContent(
                TelegramRichMessage.BuildTableRequestJson(chatId, payload.Caption, payload.Content),
                System.Text.Encoding.UTF8, "application/json")),
            _ => throw new ArgumentOutOfRangeException(nameof(payload)),
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TelegramLimits.RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://api.telegram.org/bot{options.BotToken}/{method}");
            request.Content = content;
            var response = await http.SendAsync(request, timeout.Token);
            return await ReadPayloadResultAsync(response, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TelegramSendException(null, "Telegram request timed out after 30 seconds.");
        }
    }

    // sendDocument is multipart: UTF-8 JSON bytes travel whole, never
    // truncated or chunked. The caption is a short label, not payload data.
    private static MultipartFormDataContent BuildDocumentContent(long chatId, TelegramPayload payload)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(payload.Content);
        if (bytes.Length > TelegramLimits.DocumentMaxBytes)
            throw new ArgumentOutOfRangeException(nameof(payload),
                $"Document is {bytes.Length} bytes, above the Telegram {TelegramLimits.DocumentMaxBytes}-byte ceiling.");
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(
            chatId.ToString(System.Globalization.CultureInfo.InvariantCulture)), "chat_id");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        form.Add(file, "document", payload.FileName ?? "result.json");
        if (!string.IsNullOrEmpty(payload.Caption))
            form.Add(new StringContent(payload.Caption), "caption");
        return form;
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

    private sealed record SendRequest(
        [property: JsonPropertyName("chat_id")] long ChatId,
        [property: JsonPropertyName("text")] string Text);
}
