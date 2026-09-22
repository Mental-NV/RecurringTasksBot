using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask.Client;
using RecurringTasksBot.Core;

namespace RecurringTasksBot;

public sealed class WebhookFunction(
    BotOptions options,
    IOperationStore operations,
    IUpdateReceiptStore receipts,
    ITelegramSender sender)
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
        var secretValid = IsSecretValid(request);

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

        var update = string.IsNullOrWhiteSpace(body) ? null : TelegramUpdateParser.Parse(body);
        if (update is null && !string.IsNullOrWhiteSpace(body))
        {
            // Unparseable payload: acknowledge without retrying a poison body.
            return Status(request, HttpStatusCode.OK);
        }

        var orchestrations = new DurableOrchestrationClient(durable);
        var scoped = new UpdateProcessor(operations, receipts, orchestrations, sender);
        var result = await scoped.ProcessAsync(secretValid, update,
            DateTimeOffset.UtcNow, cancellationToken);

        return Status(request, (HttpStatusCode)result.StatusCode);
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
