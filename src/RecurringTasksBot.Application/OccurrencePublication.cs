namespace RecurringTasksBot.Application;

// A same-occurrence/same-hash memory row is idempotent; a newer stored
// reply wins over an older receipt; anything else publishes.
public static class MemoryPublication
{
    public static MemoryRecord? Decide(
        MemoryRecord? memory, string ownerId, string operationId,
        DateTime scheduled, DateTime executed, AnswerArtifact answer, DateTimeOffset now)
    {
        if (memory is null)
            return new MemoryRecord(ownerId, operationId, scheduled, executed, now,
                answer.AnswerVersion, answer.Text, answer.SourceSha256, answer.ScalarCount);
        if (UtcTime.Utc(memory.SourceScheduledUtc) > scheduled)
            return null;
        if (UtcTime.Utc(memory.SourceScheduledUtc) == scheduled)
        {
            if (memory.SourceSha256 == answer.SourceSha256)
                return null;
            throw new OccurrenceConsistencyException("memory row for the same occurrence has a different hash");
        }
        return memory with
        {
            SourceScheduledUtc = scheduled,
            SourceExecutedUtc = executed,
            PublishedUtc = now,
            AnswerVersion = answer.AnswerVersion,
            Answer = answer.Text,
            SourceSha256 = answer.SourceSha256,
            ScalarCount = answer.ScalarCount,
        };
    }
}


// Plan-derived sent-parts/message-ID summaries are updated in the same
// transaction as the plan itself; the plan is authoritative.
public static class DeliveryPlanProgress
{
    public static DeliveryReceipt ApplyToReceipt(DeliveryReceipt receipt, DeliveryPlanDoc plan)
    {
        var confirmed = plan.Leaves.TakeWhile(l => l.Confirmed).ToList();
        return receipt with
        {
            // Each progress write follows exactly one Telegram send, so the
            // attempt count is send telemetry, never a failure count.
            Attempts = receipt.Attempts + 1,
            SentParts = confirmed.Count,
            TotalParts = plan.Leaves.Count,
            MessageIds = string.Join(',', confirmed.Select(l => l.MessageId!.Value)),
        };
    }
}
