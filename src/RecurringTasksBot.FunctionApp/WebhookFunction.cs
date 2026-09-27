using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask.Client;
using RecurringTasksBot.Application;
using RecurringTasksBot.Infrastructure.Bot;
using RecurringTasksBot.Infrastructure.Configuration;

namespace RecurringTasksBot.FunctionApp;

public sealed class WebhookFunction(
    TelegramOptions options,
    IOperationStore operations,
    IUpdateReceiptStore receipts,
    ITelegramTransport transport,
    Func<DurableTaskClient, IOrchestrationClient> orchestrationClients)
{
    public const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";

    [Function("Webhook")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "webhook")]
        HttpRequestData request,
        [DurableClient] DurableTaskClient durable,
        FunctionContext context,
        CancellationToken cancellationToken)
    {
        // Reject unauthenticated callers before touching the body: an
        // unreadable body must not turn a 403 into a 503.
        // Reject unauthenticated callers before touching the body: an
        // unreadable body must not turn a 403 into a 503.
        if (!IsSecretValid(request))
            return Status(request, HttpStatusCode.Forbidden);

        string body;
        try
        {
            using var reader = new StreamReader(request.Body);
            body = await reader.ReadToEndAsync(cancellationToken);
        }
        catch
        {
            return Status(request, HttpStatusCode.ServiceUnavailable);
        }

        var (earlyStatus, update) = ClassifyRequest(secretValid: true, body);
        if (earlyStatus is not null)
            return Status(request, (HttpStatusCode)earlyStatus.Value);

        var orchestrations = orchestrationClients(durable);
        var scoped = new UpdateDispatcher(operations, receipts, orchestrations, transport);
        var result = await scoped.ProcessAsync(secretValid: true, update,
            DateTimeOffset.UtcNow, cancellationToken);

        return Status(request, (HttpStatusCode)result.StatusCode);
    }

    // Host-boundary rule: the webhook secret is validated before the body is
    // parsed, so a malformed payload can never bypass a 403. Returns the
    // early HTTP status when the request is decided without command
    // processing, or null with the parsed update to proceed. An unparseable
    // non-empty body is acknowledged without retrying a poison body.
    internal static (int? EarlyStatusCode, IncomingUpdate? Update) ClassifyRequest(
        bool secretValid, string body)
    {
        if (!secretValid)
            return (403, null);
        var update = string.IsNullOrWhiteSpace(body) ? null : TelegramUpdateParser.Parse(body);
        if (update is null && !string.IsNullOrWhiteSpace(body))
            return (200, null);
        return (null, update);
    }

    private bool IsSecretValid(HttpRequestData request)
    {
        if (!request.Headers.TryGetValues(SecretHeader, out var values))
            return false;
        var presented = values.FirstOrDefault() ?? string.Empty;
        var expectedBytes = Encoding.UTF8.GetBytes(options.WebhookSecret);
        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        return expectedBytes.Length == presentedBytes.Length &&
            expectedBytes.Length > 0 &&
            CryptographicOperations.FixedTimeEquals(expectedBytes, presentedBytes);
    }

    private static HttpResponseData Status(HttpRequestData request, HttpStatusCode status)
    {
        var response = request.CreateResponse(status);
        return response;
    }
}
