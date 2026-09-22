using RecurringTasksBot.Core;

namespace RecurringTasksBot.Tests;

public sealed class WebhookAckTests
{
    private static readonly IncomingUpdate CreateUpdate = new(10, 42, 777,
        TelegramUpdateKind.Message, "/create 0 0 9 * * * hi");

    [Fact]
    public void InvalidSecret_Returns403_AndDoesNothing()
    {
        var d = WebhookDispatcher.Decide(false, CreateUpdate, null, false);
        Assert.Equal(403, d.HttpStatusCode);
        Assert.Null(d.ReplyText);
        Assert.False(d.ExecuteCommand);
    }

    [Fact]
    public void InvalidSecret_WinsOverEverything()
    {
        var d = WebhookDispatcher.Decide(false, null, null, true);
        Assert.Equal(403, d.HttpStatusCode);
    }

    [Fact]
    public void NewCommand_Executes_200()
    {
        var d = WebhookDispatcher.Decide(true, CreateUpdate, null, false);
        Assert.Equal(200, d.HttpStatusCode);
        Assert.True(d.ExecuteCommand);
        Assert.False(d.IsDuplicate);
    }

    [Fact]
    public void CompletedDuplicate_Acks200_WithoutReExecuting()
    {
        var receipt = new UpdateReceipt("42", 10, "/create 0 0 9 * * * hi", "op1",
            CommandCompleted: true, ReplyDelivered: false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        // Even with an undelivered confirmation reply, the command itself
        // must not run again.
        var d = WebhookDispatcher.Decide(true, CreateUpdate, receipt, false);
        Assert.Equal(200, d.HttpStatusCode);
        Assert.False(d.ExecuteCommand);
        Assert.True(d.IsDuplicate);
    }

    [Fact]
    public void InFlightDuplicate_Acks200_WithoutReExecuting()
    {
        var receipt = new UpdateReceipt("42", 10, "/create 0 0 9 * * * hi", "op1",
            CommandCompleted: false, ReplyDelivered: false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var d = WebhookDispatcher.Decide(true, CreateUpdate, receipt, false);
        Assert.Equal(200, d.HttpStatusCode);
        Assert.False(d.ExecuteCommand);
        Assert.True(d.IsDuplicate);
    }

    [Fact]
    public void InvalidCommand_Acks200_WithHelp()
    {
        var bad = CreateUpdate with { Text = "/create bogus" };
        var d = WebhookDispatcher.Decide(true, bad, null, false,
            _ => WebhookDecision.Ok(ListFormatter.HelpMessage, false));
        Assert.Equal(200, d.HttpStatusCode);
        Assert.False(d.ExecuteCommand);
        Assert.Contains("/create", d.ReplyText);
    }

    [Fact]
    public void UnknownCommand_Acks200_WithHelp()
    {
        var unknown = CreateUpdate with { Text = "/frobnicate" };
        var d = WebhookDispatcher.Decide(true, unknown, null, false);
        Assert.Equal(200, d.HttpStatusCode);
        Assert.False(d.ExecuteCommand);
        Assert.Equal(ListFormatter.HelpMessage, d.ReplyText);
    }

    [Fact]
    public void UnsupportedUpdateType_Acks200_Silently()
    {
        var edited = CreateUpdate with { Kind = TelegramUpdateKind.Unsupported };
        var d = WebhookDispatcher.Decide(true, edited, null, false);
        Assert.Equal(200, d.HttpStatusCode);
        Assert.False(d.ExecuteCommand);
        Assert.Null(d.ReplyText);
    }

    [Fact]
    public void NullUpdate_Acks200()
    {
        var d = WebhookDispatcher.Decide(true, null, null, false);
        Assert.Equal(200, d.HttpStatusCode);
        Assert.False(d.ExecuteCommand);
    }

    [Fact]
    public void TransientFailure_Returns503_ForRetry()
    {
        var d = WebhookDispatcher.Decide(true, CreateUpdate, null, true);
        Assert.Equal(503, d.HttpStatusCode);
        Assert.False(d.ExecuteCommand);
    }

    [Fact]
    public void NoTransientFailure_No503()
    {
        var d = WebhookDispatcher.Decide(true, CreateUpdate, null, false);
        Assert.Equal(200, d.HttpStatusCode);
    }
}
