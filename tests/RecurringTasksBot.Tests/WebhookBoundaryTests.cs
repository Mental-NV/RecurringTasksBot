// Host-boundary regression tests for the real webhook decision path in
// App/WebhookFunction.cs: the secret is validated before the body is
// parsed, so a malformed payload can never bypass a 403.
using System.Collections.Specialized;
using System.Net;
using System.Security.Claims;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Moq;
using RecurringTasksBot.Application;
using RecurringTasksBot.FunctionApp;
using RecurringTasksBot.Infrastructure.Configuration;

namespace RecurringTasksBot.Tests;

public sealed class WebhookBoundaryTests
{
    private const string ValidUpdate =
        """{"update_id":10,"message":{"from":{"id":42},"chat":{"id":777},"text":"/list"}}""";

    [Fact]
    public void InvalidSecret_WithMalformedBody_Returns403()
    {
        var (earlyStatus, update) = WebhookFunction.ClassifyRequest(false, "{{{not-json");
        Assert.Equal(403, earlyStatus);
        Assert.Null(update);
    }

    [Fact]
    public void InvalidSecret_WithValidBody_Returns403()
    {
        var (earlyStatus, update) = WebhookFunction.ClassifyRequest(false, ValidUpdate);
        Assert.Equal(403, earlyStatus);
        Assert.Null(update);
    }

    [Fact]
    public void InvalidSecret_WithEmptyBody_Returns403()
    {
        var (earlyStatus, _) = WebhookFunction.ClassifyRequest(false, string.Empty);
        Assert.Equal(403, earlyStatus);
    }

    [Fact]
    public void ValidSecret_WithMalformedBody_Acks200WithoutUpdate()
    {
        var (earlyStatus, update) = WebhookFunction.ClassifyRequest(true, "{{{not-json");
        Assert.Equal(200, earlyStatus);
        Assert.Null(update);
    }

    [Fact]
    public void ValidSecret_WithEmptyBody_ProceedsWithNullUpdate()
    {
        var (earlyStatus, update) = WebhookFunction.ClassifyRequest(true, "  ");
        Assert.Null(earlyStatus);
        Assert.Null(update);
    }

    [Fact]
    public void ValidSecret_WithValidBody_ProceedsWithParsedUpdate()
    {
        var (earlyStatus, update) = WebhookFunction.ClassifyRequest(true, ValidUpdate);
        Assert.Null(earlyStatus);
        Assert.NotNull(update);
        Assert.Equal(10, update.UpdateId);
        Assert.Equal(42, update.UserId);
    }

    // The body stream throws when read: an invalid secret must still
    // answer 403 without touching the body (never 503).
    private sealed class ThrowingBodyRequest(FunctionContext context) : HttpRequestData(context)
    {
        public override Stream Body => throw new IOException("unreadable");
        public override HttpHeadersCollection Headers { get; } = new();
        public override IReadOnlyCollection<IHttpCookie> Cookies => [];
        public override Uri Url => new("http://localhost/api/webhook");
        public override IEnumerable<ClaimsIdentity> Identities => [];
        public override string Method => "POST";
        public override NameValueCollection Query => new();
        public override HttpResponseData CreateResponse() => new TestResponse(FunctionContext);
    }

    private sealed class TestResponse(FunctionContext context) : HttpResponseData(context)
    {
        public override HttpStatusCode StatusCode { get; set; }
        public override HttpHeadersCollection Headers { get; set; } = new();
        public override Stream Body { get; set; } = Stream.Null;
        public override HttpCookies Cookies => throw new NotSupportedException();
    }

    [Fact]
    public async Task InvalidSecret_WithUnreadableBody_Returns403()
    {
        var options = new TelegramOptions("token", "expected-secret");
        var function = new WebhookFunction(
            options, new FakeTaskStore(), new FakeReceiptStore(),
            new FakeTelegramSender(), TaskDefaults.Default,
            new FakeOccurrenceRepository(new FakeOperationStore(), new FakeClock()),
            _ => throw new NotSupportedException());
        var context = new Mock<FunctionContext>();
        var response = await function.Run(
            new ThrowingBodyRequest(context.Object), null!, context.Object, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
