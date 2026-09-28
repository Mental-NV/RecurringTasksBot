// Phase 5 /get: explicit (default) returns the saved user-authored
// definition; effective fills current global defaults. Both carry the
// settings shape only. Reads never change task state or timers.
namespace RecurringTasksBot.Application;

public sealed class TaskGetHandler(
    ITaskStore tasks,
    IUpdateReceiptStore receipts,
    BotReplySender replies,
    TaskDefaults? taskDefaults = null)
{
    private TaskDefaults Globals => taskDefaults ?? TaskDefaults.Default;

    public async Task<ProcessResult> HandleGetAsync(
        string ownerId, IncomingUpdate update, string text, DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        if (!TaskCommandParser.TryParseGetArgs(text, out var args, out var usageError) ||
            args is null)
        {
            return await CommandReplies.CompleteWithReplyAsync(receipts, replies,
                ownerId, update, text, nowUtc, usageError + "\n\n" + TaskHelp.GetUsage, ct);
        }

        var owned = await tasks.ListOwnedAsync(ownerId, ct);
        var resolution = TaskCommandParser.ResolveTaskRef(owned, args.TaskRef, out var record);
        if (resolution != TaskIdResolution.Found || record is null)
        {
            var reply = resolution == TaskIdResolution.Ambiguous
                ? $"Ambiguous task prefix '{args.TaskRef}'. Use a longer prefix."
                : TaskHelp.TaskNotFound;
            return await CommandReplies.CompleteWithReplyAsync(
                receipts, replies, ownerId, update, text, nowUtc, reply, ct);
        }

        // A code block when the labeled output fits one message; otherwise
        // the whole UTF-8 JSON definition travels as an attachment. Nothing
        // is truncated or filtered either way.
        var json = args.Mode == ParsedGetCommand.Effective
            ? TaskJsonRenderer.RenderEffective(record.Definition, Globals)
            : TaskJsonRenderer.RenderExplicit(record.Definition);
        var labeled = $"Task {record.TaskId} ({args.Mode}):\n```json\n{json}\n```";
        if (LiteralMessageChunker.SplitRich(labeled).Count == 1)
        {
            return await CommandReplies.CompleteWithReplyAsync(
                receipts, replies, ownerId, update, text, nowUtc, labeled, ct);
        }

        var fileName = $"task-{record.TaskId}.json";
        var caption =
            $"Task {record.TaskId} ({args.Mode}) definition attached " +
            $"({System.Text.Encoding.UTF8.GetByteCount(json)} bytes).";
        return await CommandReplies.CompleteWithDocumentAsync(
            receipts, replies, ownerId, update, text, nowUtc, fileName, json, caption, ct);
    }
}
