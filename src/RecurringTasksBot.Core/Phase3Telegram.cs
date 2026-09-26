// Typed Telegram payloads and deterministic error classification. The
// sender (step 2) maps these to exactly one message per call.
namespace RecurringTasksBot.Core;

public enum TelegramPayloadKind
{
    Markdown,
    LiteralRich,
    LiteralPlain,
    LegacyHtml,
}

public sealed record TelegramPayload(TelegramPayloadKind Kind, string Content);

public enum TelegramDisposition
{
    ContentRejection,
    UnknownMethod,
    PermanentRecipient,
    RateLimited,
    Transient,
    TerminalFailure,
}

public sealed record TelegramClassification(TelegramDisposition Disposition, string Category);

public static class TelegramErrorClassifier
{
    // Retains both HTTP status and Telegram error_code; descriptions are for
    // classification, not user display or raw logging.
    public static TelegramClassification ClassifyException(TelegramSendException exception) =>
        Classify(exception.HttpStatusCode, exception.ApiErrorCode, exception.Description);

    public static TelegramClassification Classify(
        int? httpStatus, int? apiErrorCode, string? description)
    {
        var desc = Normalize(description);
        // Permanent recipient failures are checked before method detection.
        if (httpStatus == 403 || apiErrorCode == 403 ||
            desc.Contains("chat not found", StringComparison.Ordinal) ||
            desc.Contains("user not found", StringComparison.Ordinal) ||
            desc.Contains("blocked", StringComparison.Ordinal) ||
            desc.Contains("deactivated", StringComparison.Ordinal))
            return new TelegramClassification(TelegramDisposition.PermanentRecipient, "recipient_failure");

        // Method detection is explicit; never a blanket "not found" match.
        if (httpStatus == 404 || apiErrorCode == 404 ||
            desc.Contains("unknown method", StringComparison.Ordinal) ||
            desc.Contains("method not found", StringComparison.Ordinal))
            return new TelegramClassification(TelegramDisposition.UnknownMethod, "unknown_method");

        if (httpStatus == 429 || apiErrorCode == 429)
            return new TelegramClassification(TelegramDisposition.RateLimited, "rate_limited");

        if (httpStatus == 400 || apiErrorCode == 400)
        {
            if (IsContentRejection(desc))
                return new TelegramClassification(TelegramDisposition.ContentRejection, "content_rejected");
            return new TelegramClassification(TelegramDisposition.TerminalFailure, "bad_request");
        }

        if (httpStatus is 408 or >= 500)
            return new TelegramClassification(TelegramDisposition.Transient, "transient");

        if (httpStatus == 401 || apiErrorCode == 401)
            return new TelegramClassification(TelegramDisposition.TerminalFailure, "unauthorized");

        if (httpStatus is >= 400 and < 500)
            return new TelegramClassification(TelegramDisposition.TerminalFailure, "bad_request");

        return new TelegramClassification(TelegramDisposition.Transient, "transient");
    }

    public static string Normalize(string? description) =>
        (description ?? string.Empty).ToLowerInvariant().Replace('’', '\'').Replace('‘', '\'');

    // Size-specific content rejections take the conservative small-chunk
    // step; every other content rejection skips straight to plain text.
    public static bool IsSizeRejection(string? description)
    {
        var d = Normalize(description);
        return d.Contains("message is too long", StringComparison.Ordinal) ||
            d.Contains("text is too long", StringComparison.Ordinal) ||
            d.Contains("message_too_long", StringComparison.Ordinal);
    }

    // Small isolated matcher for definite content rejections. Unknown 400
    // descriptions are terminal and never trigger speculative fallback.
    public static bool IsContentRejection(string normalizedDescription)
    {
        var d = normalizedDescription ?? string.Empty;
        if (d.Contains("can't parse entities", StringComparison.Ordinal) ||
            d.Contains("can't parse rich message", StringComparison.Ordinal) ||
            d.Contains("can't parse markdown", StringComparison.Ordinal) ||
            d.Contains("message is too long", StringComparison.Ordinal) ||
            d.Contains("text is too long", StringComparison.Ordinal) ||
            d.Contains("message_too_long", StringComparison.Ordinal) ||
            d.Contains("too many blocks", StringComparison.Ordinal) ||
            d.Contains("too many columns", StringComparison.Ordinal))
            return true;
        return d.Contains("nesting", StringComparison.Ordinal) &&
            (d.Contains("too deep", StringComparison.Ordinal) ||
             d.Contains("limit", StringComparison.Ordinal));
    }
}
