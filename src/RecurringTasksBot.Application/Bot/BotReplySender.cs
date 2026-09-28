// Command/help/list reply sender: literal application text, no occurrence
// memory and no persisted reply plans. Each reply goes out literal-rich
// initially; a rejected part falls back to all required conservative plain
// chunks. Failures propagate so the dispatcher can map them to HTTP status.
namespace RecurringTasksBot.Application;

public sealed class BotReplySender(ITelegramTransport transport)
{
    public async Task<IReadOnlyList<long>> SendReplyAsync(
        long chatId, string text, CancellationToken ct = default)
    {
        var ids = new List<long>();
        foreach (var part in LiteralMessageChunker.SplitRich(text))
            ids.AddRange(await SendPartAsync(chatId, part, ct));
        return ids;
    }

    // Whole-document delivery for oversized output: one attachment, never
    // truncated. A transport without document support falls back to chunked
    // literal text so no byte is lost; other failures propagate.
    public async Task<IReadOnlyList<long>> SendDocumentAsync(
        long chatId, string fileName, string content, string? caption = null,
        CancellationToken ct = default)
    {
        try
        {
            return [await transport.SendAsync(chatId,
                new TelegramPayload(TelegramPayloadKind.Document, content, fileName, caption), ct)];
        }
        catch (TelegramUnknownMethodException)
        {
            return await SendReplyAsync(chatId, content, ct);
        }
    }

    // Native table delivery with a stacked-text fallback: transports that
    // reject the table block (or compact clients) get the same rows as
    // literal text. Other failures propagate for dispatcher mapping.
    public async Task<IReadOnlyList<long>> SendTableAsync(
        long chatId, string? caption, string cellsJson, string stackedFallback,
        CancellationToken ct = default)
    {
        try
        {
            return [await transport.SendAsync(chatId,
                new TelegramPayload(TelegramPayloadKind.Table, cellsJson, null, caption), ct)];
        }
        catch (TelegramUnknownMethodException)
        {
            return await SendReplyAsync(chatId, stackedFallback, ct);
        }
        catch (TelegramSendException ex) when (TelegramErrorClassifier.ClassifyException(ex).Disposition
            is TelegramDisposition.ContentRejection)
        {
            return await SendReplyAsync(chatId, stackedFallback, ct);
        }
    }

    private async Task<IReadOnlyList<long>> SendPartAsync(
        long chatId, string part, CancellationToken ct)
    {
        try
        {
            return [await transport.SendAsync(
                chatId, new TelegramPayload(TelegramPayloadKind.LiteralRich, part), ct)];
        }
        catch (TelegramUnknownMethodException)
        {
            return await SendPlainFallbackAsync(chatId, part, ct);
        }
        catch (TelegramSendException ex) when (TelegramErrorClassifier.ClassifyException(ex).Disposition
            is TelegramDisposition.ContentRejection)
        {
            return await SendPlainFallbackAsync(chatId, part, ct);
        }
    }

    private async Task<IReadOnlyList<long>> SendPlainFallbackAsync(
        long chatId, string part, CancellationToken ct)
    {
        var ids = new List<long>();
        foreach (var chunk in LiteralMessageChunker.SplitConservative(part))
        {
            if (chunk.Length == 0)
                continue;
            ids.Add(await transport.SendAsync(
                chatId, new TelegramPayload(TelegramPayloadKind.LiteralPlain, chunk), ct));
        }
        return ids;
    }
}
