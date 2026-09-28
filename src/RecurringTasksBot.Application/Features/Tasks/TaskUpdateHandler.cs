// Phase 5 /update: atomic patch against the stored explicit definition.
// Revision advances only on an actual definition change; a nonempty patch
// that changes nothing answers unchanged. Schedule replacement admits future
// work from the commit time; prompt/generation-only edits never reactivate.
using NodaTime;

namespace RecurringTasksBot.Application;

public sealed class TaskUpdateHandler(
    ITaskStore tasks,
    IUpdateReceiptStore receipts,
    ITaskOrchestrationClient orchestrations,
    BotReplySender replies)
{
    public async Task<ProcessResult> HandleUpdateAsync(
        string ownerId, IncomingUpdate update, string text, DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        if (!TaskCommandParser.TrySplitUpdateArgs(text, out var args, out var usageError) ||
            args is null)
        {
            return await CommandReplies.CompleteWithReplyAsync(receipts, replies,
                ownerId, update, text, nowUtc, usageError + "\n\n" + TaskHelp.UpdateUsage, ct);
        }

        var owned = await tasks.ListOwnedAsync(ownerId, ct);
        var resolution = TaskCommandParser.ResolveTaskRef(owned, args.TaskRef, out var record);
        if (resolution != TaskIdResolution.Found || record is null)
        {
            var notFound = resolution == TaskIdResolution.Ambiguous
                ? $"Ambiguous task prefix '{args.TaskRef}'. Use a longer prefix."
                : TaskHelp.TaskNotFound;
            return await CommandReplies.CompleteWithReplyAsync(
                receipts, replies, ownerId, update, text, nowUtc, notFound, ct);
        }

        // Redelivered after a crash between the mutation and its receipt:
        // the row already reflects this command, so acknowledge without
        // re-applying or re-validating. A later user edit carries a
        // different updateId and is never mistaken for this command. The
        // wake is raised again: the first attempt's signal may have been
        // the thing that failed.
        // Already applied (found by command record, not by latest-writer
        // marker): acknowledge without re-applying. A later user edit
        // carries a different updateId and is never overwritten by a retry.
        if (await tasks.GetAppliedCommandAsync(ownerId, record.TaskId, update.UpdateId, ct)
            is not null)
        {
            return await AcknowledgeAppliedAsync(ownerId, record, update, text, nowUtc, ct);
        }

        if (record.Status == TaskState.Failed)
        {
            return await CommandReplies.CompleteWithReplyAsync(receipts, replies,
                ownerId, update, text, nowUtc,
                "Failed tasks cannot be updated. Create a replacement task.", ct);
        }

        if (!TaskDefinitionParser.TryParseUpdate(args.JsonBody, record.Definition.Timezone,
            out var patch, out var parseError) || patch is null)
        {
            return await CommandReplies.CompleteWithReplyAsync(receipts, replies,
                ownerId, update, text, nowUtc,
                TaskHelp.FieldError(parseError!) + "\n\n" + TaskHelp.UpdateUsage, ct);
        }

        var applyError = TaskUpdater.ApplyUpdate(record.Definition, patch, out var applied);
        if (applyError is not null || applied is null)
        {
            return await CommandReplies.CompleteWithReplyAsync(receipts, replies,
                ownerId, update, text, nowUtc,
                TaskHelp.FieldError(applyError!) + "\n\n" + TaskHelp.UpdateUsage, ct);
        }

        if (applied.IsUnchanged)
        {
            // An unchanged resubmission still carries outstanding
            // coordination: a persisted pending boundary means an earlier
            // signal may have been lost, so wake the lifecycle again. A
            // transient signal failure propagates to a 503; redelivery
            // retries the wake without re-applying anything.
            if (record.PendingActivationUtc is not null)
                await orchestrations.SignalTaskUpdatedAsync(record.InstanceId, ct);
            return await CommandReplies.CompleteWithReplyAsync(receipts, replies,
                ownerId, update, text, nowUtc,
                $"Task {record.TaskId} unchanged (revision {record.Revision}).", ct);
        }

        TaskTimezones.TryResolve(record.Definition.Timezone, out var zone);
        var commitUtc = nowUtc.UtcDateTime;
        var expirationChanged =
            !Equals(applied.Definition.Parameters.ExpiresAt, record.Definition.Parameters.ExpiresAt);
        var updateError = TaskCommitValidator.ValidateForUpdate(applied.Definition, zone!,
            commitUtc, applied.ScheduleChanged, expirationChanged, record.ExpiresAtUtc);
        if (updateError is not null)
        {
            return await CommandReplies.CompleteWithReplyAsync(receipts, replies,
                ownerId, update, text, nowUtc,
                TaskHelp.FieldError(updateError) + "\n\n" + TaskHelp.UpdateUsage, ct);
        }

        if (record.Status == TaskState.Completed &&
            !applied.ScheduleChanged && !applied.LimitsChanged)
        {
            return await CommandReplies.CompleteWithReplyAsync(receipts, replies,
                ownerId, update, text, nowUtc,
                "Completed tasks resume only through a replacement schedule or relaxed limits.", ct);
        }

        var expiresAtUtc = expirationChanged
            ? TaskCommitValidator.ResolveExpiresAtUtc(applied.Definition.Parameters.ExpiresAt, zone!)
            : record.ExpiresAtUtc;
        if (record.Status == TaskState.Completed)
        {
            var resumeError = TaskCommitValidator.RequireReactivationWork(applied.Definition, zone!,
                commitUtc, expiresAtUtc, applied.Definition.Parameters.MaxOccurrences,
                record.StartedOccurrences);
            if (resumeError is not null)
            {
                return await CommandReplies.CompleteWithReplyAsync(receipts, replies,
                    ownerId, update, text, nowUtc,
                    TaskHelp.FieldError(resumeError) + "\n\n" + TaskHelp.UpdateUsage, ct);
            }
        }

        // Waterline and pending boundaries are computed inside the guarded
        // store write from the fresh row, never from this older snapshot.
        var reactivating = record.Status == TaskState.Completed;
        var nextRevision = record.Revision + 1;
        bool swapped;
        if (reactivating)
        {
            swapped = await tasks.TryReactivateTaskAsync(ownerId, record.TaskId,
                record.Revision, applied.Definition, nextRevision, expiresAtUtc,
                update.UpdateId, commitUtc, ct);
        }
        else
        {
            swapped = await tasks.TryUpdateDefinitionAsync(ownerId, record.TaskId,
                record.Revision, record.Status, applied.Definition, nextRevision, expiresAtUtc,
                applied.ScheduleChanged, update.UpdateId, commitUtc, ct);
        }

        if (!swapped)
        {
            // Lost either to a concurrent commit or to our own retried
            // command winning first: the command record distinguishes them.
            if (await tasks.GetAppliedCommandAsync(ownerId, record.TaskId, update.UpdateId, ct)
                is not null)
            {
                return await AcknowledgeAppliedAsync(ownerId, record, update, text, nowUtc, ct);
            }

            var current = await tasks.GetAsync(ownerId, record.TaskId, ct);
            if (current?.Status != record.Status)
            {
                return await CommandReplies.CompleteWithReplyAsync(receipts, replies,
                    ownerId, update, text, nowUtc,
                    $"Task {record.TaskId} changed while editing " +
                    $"(now {current?.Status.ToString() ?? "missing"}). Re-read and retry.", ct);
            }

            return await CommandReplies.CompleteWithReplyAsync(receipts, replies,
                ownerId, update, text, nowUtc,
                "Concurrent edit detected. Re-read the task and retry.", ct);
        }

        if (reactivating)
            await orchestrations.StartTaskAsync(record.InstanceId, ownerId, record.TaskId, ct);
        else if (applied.ScheduleChanged || applied.LimitsChanged)
        {
            await orchestrations.SignalTaskUpdatedAsync(record.InstanceId, ct);
            await orchestrations.StartTaskAsync(record.InstanceId, ownerId, record.TaskId, ct);
        }

        // The reply reflects the committed row; overlay only this command's
        // known revision and definition when no newer edit has landed since.
        var committed = await tasks.GetAsync(ownerId, record.TaskId, ct) ?? record;
        var updated = committed.Revision != nextRevision ? committed : committed with
        {
            Definition = applied.Definition,
            ExpiresAtUtc = expiresAtUtc,
        };
        var reply = Confirmation(updated, applied.Changed, zone!);
        // Commit before replying: redelivery after this point acknowledges
        // without re-applying, so a later edit can never be re-applied.
        await CommandReplies.EnsureReceiptAsync(receipts, ownerId, update, text, nowUtc, ct);
        await receipts.MarkCompletedAsync(ownerId, update.UpdateId, record.TaskId, ct);
        await replies.SendReplyAsync(update.ChatId, reply, ct);
        await receipts.MarkReplyDeliveredAsync(ownerId, update.UpdateId, ct);
        return new ProcessResult(200, [reply]);
    }

    // Acknowledge an already-applied command from the committed row. Also
    // ensures a lifecycle is running: a crash between commit and startup
    // leaves an active task with no orchestration, and signaling that old
    // instance would never start one. A live instance just wakes.
    private async Task<ProcessResult> AcknowledgeAppliedAsync(
        string ownerId, TaskRecord record, IncomingUpdate update, string text,
        DateTimeOffset nowUtc, CancellationToken ct)
    {
        var current = (await tasks.GetAsync(ownerId, record.TaskId, ct)) ?? record;
        var runtime = await orchestrations.GetRuntimeStatusAsync(current.InstanceId, ct);
        if (string.Equals(runtime, "Running", StringComparison.Ordinal))
            await orchestrations.SignalTaskUpdatedAsync(current.InstanceId, ct);
        else
            await orchestrations.StartTaskAsync(
                current.InstanceId, ownerId, current.TaskId, ct);
        TaskTimezones.TryResolve(current.Definition.Timezone, out var currentZone);
        var alreadyReply = Confirmation(current, [], currentZone ?? DateTimeZone.Utc);
        await CommandReplies.EnsureReceiptAsync(receipts, ownerId, update, text, nowUtc, ct);
        await receipts.MarkCompletedAsync(ownerId, update.UpdateId, record.TaskId, ct);
        await replies.SendReplyAsync(update.ChatId, alreadyReply, ct);
        await receipts.MarkReplyDeliveredAsync(ownerId, update.UpdateId, ct);
        return new ProcessResult(200, [alreadyReply]);
    }

    private static string Confirmation(TaskRecord record, IReadOnlyList<string> changed, DateTimeZone zone)
    {
        var lines = new List<string>
        {
            $"Task {record.TaskId} updated (revision {record.Revision}).",
            "Changed: " + (changed.Count == 0 ? "none" : string.Join("; ", changed)),
            TaskConfirmationPreview.LimitsLine(record),
        };
        lines.AddRange(TaskConfirmationPreview.NextOccurrenceLines(
            record.Definition, zone, Max(record.WaterlineUtc, DateTime.UtcNow)));
        return string.Join('\n', lines);
    }

    private static DateTime Max(DateTime a, DateTime b) => a >= b ? a : b;
}
