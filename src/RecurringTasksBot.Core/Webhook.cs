// HTTP acknowledgment matrix from docs/Spec.md. HTTP status and the Telegram
// reply are separate concerns:
// - 403 for an invalid webhook secret.
// - 200 for processed updates, completed duplicates, invalid/unknown commands
//   (with help where applicable), and unsupported update types.
// - 503 for transient processing failures so Telegram retries.
// A failed confirmation reply must not repeat an already-completed command:
// receipts track command completion separately from reply delivery.
namespace RecurringTasksBot.Core;

public enum TelegramUpdateKind
{
    Message,
    Unsupported,
}

public sealed record IncomingUpdate(
    long UpdateId,
    long UserId,
    long ChatId,
    TelegramUpdateKind Kind,
    string? Text,
    // Reply-based creation: the replied-to prompt and its author's user ID.
    // Text already carries the normalized prompt (plain text or rich
    // message); ReplyPrompt carries the replied-to message when present.
    string? ReplyPrompt = null,
    long? ReplyUserId = null);

public sealed record WebhookDecision(
    int HttpStatusCode,
    string? ReplyText,
    bool ExecuteCommand,
    bool IsDuplicate)
{
    public static WebhookDecision Forbidden() => new(403, null, false, false);

    public static WebhookDecision Ok(string? reply, bool execute, bool duplicate = false) =>
        new(200, reply, execute, duplicate);

    public static WebhookDecision Retry() => new(503, null, false, false);
}

public static class WebhookDispatcher
{
    public static WebhookDecision Decide(
        bool secretValid,
        IncomingUpdate? update,
        UpdateReceipt? existingReceipt,
        bool transientFailure,
        Func<IncomingUpdate, WebhookDecision>? onNewCommand = null)
    {
        if (!secretValid)
            return WebhookDecision.Forbidden();

        if (update is null || update.Kind == TelegramUpdateKind.Unsupported)
            return WebhookDecision.Ok(null, false);

        // Completed duplicate: acknowledge without re-executing the command.
        if (existingReceipt is { CommandCompleted: true })
            return WebhookDecision.Ok(null, false, duplicate: true);

        // In-flight duplicate (redelivery while first attempt runs): ack; the
        // first attempt owns execution. Telegram retries only on non-200.
        if (existingReceipt is { CommandCompleted: false })
            return WebhookDecision.Ok(null, false, duplicate: true);

        if (transientFailure)
            return WebhookDecision.Retry();

        if (onNewCommand is not null)
            return onNewCommand(update);

        var text = (update.Text ?? string.Empty).Trim();
        if (text.StartsWith("/create", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("/list", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("/delete", StringComparison.OrdinalIgnoreCase))
            return WebhookDecision.Ok(null, true);

        return WebhookDecision.Ok(ListFormatter.HelpMessage, false);
    }
}
