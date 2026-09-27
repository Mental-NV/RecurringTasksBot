// BotReplySender tests: command replies go out literal-rich initially and
// fall back to every required conservative plain chunk on rejection.
// Failures propagate for the dispatcher to map.
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Tests;

public sealed class BotReplySenderTests
{
    [Fact]
    public async Task ShortReply_SendsOneLiteralRichPayload()
    {
        var transport = new FakeTelegramSender();
        var ids = await new BotReplySender(transport).SendReplyAsync(7, "Reminder op1 created.");
        var payload = Assert.Single(transport.Payloads);
        Assert.Equal(7L, payload.ChatId);
        Assert.Equal(TelegramPayloadKind.LiteralRich, payload.Kind);
        Assert.Equal("Reminder op1 created.", payload.Content);
        Assert.Single(ids);
    }

    [Fact]
    public async Task LongReply_PreChunksLiteralRichParts()
    {
        var transport = new FakeTelegramSender();
        var text = new string('z', 40000);
        var ids = await new BotReplySender(transport).SendReplyAsync(7, text);
        Assert.True(transport.Payloads.Count > 1);
        Assert.All(transport.Payloads, p =>
        {
            Assert.Equal(TelegramPayloadKind.LiteralRich, p.Kind);
            Assert.True(TextLimits.CountChars(p.Content) <= TelegramLimits.RichTextChars);
        });
        Assert.Equal(text, string.Concat(transport.Payloads.Select(p => p.Content)));
        Assert.Equal(transport.Payloads.Count, ids.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectedRichReply_SendsAllPlainChunks(bool unknownMethod)
    {
        var transport = new FakeTelegramSender();
        var text = new string('x', 5000);
        transport.RejectPayload = payload =>
            payload.Kind == TelegramPayloadKind.LiteralRich
                ? unknownMethod
                    ? new TelegramUnknownMethodException(404, 404, "unknown method")
                    : new TelegramSendException(400, "Bad Request: can't parse entities", null, 400)
                : null;
        var ids = await new BotReplySender(transport).SendReplyAsync(9, text);
        Assert.Equal(2, transport.Payloads.Count(p => p.Kind == TelegramPayloadKind.LiteralPlain));
        var plain = transport.Payloads.Where(p => p.Kind == TelegramPayloadKind.LiteralPlain).ToList();
        Assert.Equal(new string('x', 4096), plain[0].Content);
        Assert.Equal(new string('x', 904), plain[1].Content);
        Assert.Equal(2, ids.Count);
    }

    [Fact]
    public async Task TransientFailure_Propagates()
    {
        var transport = new FakeTelegramSender
        {
            AlwaysThrow = new TelegramSendException(500, "boom"),
        };
        await Assert.ThrowsAsync<TelegramSendException>(() =>
            new BotReplySender(transport).SendReplyAsync(7, "hi"));
    }
}
