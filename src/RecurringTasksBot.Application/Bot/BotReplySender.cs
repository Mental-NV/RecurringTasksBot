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
