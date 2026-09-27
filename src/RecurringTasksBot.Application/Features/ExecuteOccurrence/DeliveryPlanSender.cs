using Microsoft.Extensions.Logging;
// Delivery-plan execution for one occurrence attempt: resume the
// persisted plan, send unconfirmed leaves over the typed transport with
// deterministic fallbacks, confirm progress leaf by leaf, and complete.
namespace RecurringTasksBot.Application;

internal sealed class DeliveryPlanSender(
    IOccurrenceRepository occurrences,
    IOperationStore operations,
    ITelegramTransport transport,
    AttemptSupport support)
{
    public async Task<SingleAttemptResult> DeliverPlanAsync(
        OperationRecord op, string claimId, DateTime scheduledUtc,
        int attemptIndex, int attempts, DateTimeOffset workStart, CancellationToken ct)
    {
        var ownerId = op.OwnerId;
        var operationId = op.OperationId;
        ProgressRead progress;
        try
        {
            progress = await occurrences.ReadProgressAsync(ownerId, operationId, scheduledUtc, claimId, ct);
        }
        catch (TransientStoreException ex) { return AttemptResults.StorageRetry(attemptIndex, ex.Message); }
        catch (ClaimLostException) { return AttemptResults.ClaimWait(); }
        catch (OccurrenceConsistencyException ex)
        {
            return await support.FailAsync(ownerId, operationId, scheduledUtc, claimId,
                ex.Message, attempts, 0, ct);
        }
        catch (PayloadIntegrityException ex)
        {
            return await support.FailAsync(ownerId, operationId, scheduledUtc, claimId,
                ex.Code, attempts, 0, ct);
        }

        var plan = progress.Plan;
        var source = progress.Answer.Text;
        while (true)
        {
            var leaf = plan.Leaves.FirstOrDefault(l => !l.Confirmed);
            if (leaf is null)
                break;
            if (!support.FitsTime(workStart, TelegramLimits.ProgressReserve))
                return AttemptResults.YieldRetry();

            OperationRecord? live;
            try
            {
                live = await operations.GetAsync(ownerId, operationId, ct);
            }
            catch (TransientStoreException ex) { return AttemptResults.StorageRetry(attemptIndex, ex.Message); }
            if (live is null || live.Status is OperationStatus.Deleted or OperationStatus.Failed)
                return AttemptResults.Stopped();

            bool renewed;
            try
            {
                renewed = await occurrences.RenewClaimAsync(
                    ownerId, operationId, scheduledUtc, claimId, ct);
            }
            catch (TransientStoreException ex) { return AttemptResults.StorageRetry(attemptIndex, ex.Message); }
            if (!renewed)
                return AttemptResults.ClaimWait();
            ct.ThrowIfCancellationRequested();

            TelegramPayload payload;
            try
            {
                payload = ToPayload(leaf, source);
            }
            catch (OccurrenceConsistencyException ex)
            {
                return await support.FailAsync(ownerId, operationId, scheduledUtc, claimId,
                    ex.Message, attempts, 0, ct);
            }

            long messageId;
            try
            {
                attempts++;
                messageId = await transport.SendAsync(live.ChatId, payload, ct);
            }
            catch (TelegramUnknownMethodException)
            {
                if (leaf.Kind == PlanLeafKind.LiteralPlain)
                {
                    return await support.FailAsync(ownerId, operationId, scheduledUtc, claimId,
                        "unknown_method", attempts, 1, ct);
                }
                var replaced = await ReplaceAsync(ownerId, operationId, scheduledUtc, claimId,
                    leaf.Id, PlanFallbackKind.ToPlain, attemptIndex, attempts, ct);
                if (replaced.Failure is not null)
                    return replaced.Failure;
                if (replaced.Plan is null)
                    return AttemptResults.StorageRetry(attemptIndex, "plan replacement did not persist");
                plan = replaced.Plan;
                continue;
            }
            catch (TelegramSendException ex)
            {
                var classification = TelegramErrorClassifier.ClassifyException(ex);
                switch (classification.Disposition)
                {
                    case TelegramDisposition.ContentRejection:
                    {
                        var fallback = DecideFallback(leaf, ex.Description);
                        if (fallback is null)
                        {
                            return await support.FailAsync(ownerId, operationId, scheduledUtc, claimId,
                                "content_rejected", attempts, 1, ct);
                        }
                        var replaced = await ReplaceAsync(ownerId, operationId, scheduledUtc, claimId,
                            leaf.Id, fallback.Value, attemptIndex, attempts, ct);
                        if (replaced.Failure is not null)
                            return replaced.Failure;
                        if (replaced.Plan is null)
                            return AttemptResults.StorageRetry(attemptIndex, "plan replacement did not persist");
                        plan = replaced.Plan;
                        continue;
                    }
                    case TelegramDisposition.UnknownMethod:
                    {
                        if (leaf.Kind == PlanLeafKind.LiteralPlain)
                        {
                            return await support.FailAsync(ownerId, operationId, scheduledUtc, claimId,
                                "unknown_method", attempts, 1, ct);
                        }
                        var replaced = await ReplaceAsync(ownerId, operationId, scheduledUtc, claimId,
                            leaf.Id, PlanFallbackKind.ToPlain, attemptIndex, attempts, ct);
                        if (replaced.Failure is not null)
                            return replaced.Failure;
                        if (replaced.Plan is null)
                            return AttemptResults.StorageRetry(attemptIndex, "plan replacement did not persist");
                        plan = replaced.Plan;
                        continue;
                    }
                    case TelegramDisposition.PermanentRecipient:
                    {
                        OperationRecord? current;
                        try
                        {
                            current = await operations.GetAsync(ownerId, operationId, ct);
                        }
                        catch (TransientStoreException ex2)
                        {
                            return AttemptResults.StorageRetry(attemptIndex, ex2.Message);
                        }
                        if (current is not null)
                        {
                            try
                            {
                                await operations.CompareAndSwapStatusAsync(ownerId, operationId,
                                    current.Status, OperationStatus.Failed, "permanent recipient failure", ct);
                            }
                            catch (TransientStoreException ex2)
                            {
                                return AttemptResults.StorageRetry(attemptIndex, ex2.Message);
                            }
                        }
                        try
                        {
                            await occurrences.FailAsync(new FailRequest(ownerId, operationId,
                                scheduledUtc, claimId, "permanent recipient failure", 1), ct);
                        }
                        catch (Exception failEx) when (failEx is not OperationCanceledException) { }
                        return new SingleAttemptResult(SingleAttemptOutcome.OperationFailed,
                            attempts, null, "permanent recipient failure");
                    }
                    case TelegramDisposition.RateLimited:
                    case TelegramDisposition.Transient:
                    {
                        var category = classification.Disposition == TelegramDisposition.RateLimited
                            ? "rate_limited" : "transient_delivery_failure";
                        int total;
                        try
                        {
                            total = await occurrences.TrackDeliveryAsync(
                                new TrackDeliveryRequest(ownerId, operationId, scheduledUtc,
                                    claimId, category), ct);
                            attempts++;
                        }
                        catch (TransientStoreException storeEx)
                        {
                            return AttemptResults.StorageRetry(attemptIndex, storeEx.Message);
                        }
                        catch (ClaimLostException) { return AttemptResults.ClaimWait(); }
                        catch (PayloadIntegrityException payloadEx)
                        {
                            return await support.FailAsync(ownerId, operationId, scheduledUtc, claimId,
                                payloadEx.Code, attempts, 0, ct);
                        }
                        if (total > DeliveryPolicy.MaxRetriesAfterInitial)
                        {
                            return await support.FailAsync(ownerId, operationId, scheduledUtc, claimId,
                                category, attempts, 0, ct);
                        }
                        return new SingleAttemptResult(SingleAttemptOutcome.NeedRetry, attempts,
                            DeliveryPolicy.RetryDelay(total - 1, ex.RetryAfter), category);
                    }
                    default:
                    {
                        var code = ex.ApiErrorCode == 401 || ex.HttpStatusCode == 401
                            ? "telegram_unauthorized" : "delivery_rejected";
                        return await support.FailAsync(ownerId, operationId, scheduledUtc, claimId,
                            code, attempts, 1, ct);
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return AttemptResults.ClaimWait();
            }
            catch (Exception)
            {
                int total;
                try
                {
                    total = await occurrences.TrackDeliveryAsync(
                        new TrackDeliveryRequest(ownerId, operationId, scheduledUtc,
                            claimId, "transient_delivery_failure"), ct);
                    attempts++;
                }
                catch (TransientStoreException storeEx) { return AttemptResults.StorageRetry(attemptIndex, storeEx.Message); }
                catch (ClaimLostException) { return AttemptResults.ClaimWait(); }
                catch (PayloadIntegrityException payloadEx)
                {
                    return await support.FailAsync(ownerId, operationId, scheduledUtc, claimId,
                        payloadEx.Code, attempts, 0, ct);
                }
                if (total > DeliveryPolicy.MaxRetriesAfterInitial)
                {
                    return await support.FailAsync(ownerId, operationId, scheduledUtc, claimId,
                        "transient_delivery_failure", attempts, 0, ct);
                }
                return new SingleAttemptResult(SingleAttemptOutcome.NeedRetry, attempts,
                    DeliveryPolicy.RetryDelay(total - 1), "transient_delivery_failure");
            }

            try
            {
                plan = await occurrences.ConfirmLeafAsync(new ConfirmLeafRequest(
                    ownerId, operationId, scheduledUtc, claimId, leaf.Id, messageId), ct);
            }
            catch (TransientStoreException ex) { return AttemptResults.StorageRetry(attemptIndex, ex.Message); }
            catch (ClaimLostException) { return AttemptResults.ClaimWait(); }
            catch (OccurrenceConsistencyException ex)
            {
                return await support.FailAsync(ownerId, operationId, scheduledUtc, claimId,
                    ex.Message, attempts, 0, ct);
            }
            catch (PayloadIntegrityException ex)
            {
                return await support.FailAsync(ownerId, operationId, scheduledUtc, claimId,
                    ex.Code, attempts, 0, ct);
            }
        }

        try
        {
            await occurrences.CompleteAsync(
                new CompleteRequest(ownerId, operationId, scheduledUtc, claimId), ct);
            return new SingleAttemptResult(SingleAttemptOutcome.Sent, attempts, null, null);
        }
        catch (TransientStoreException ex) { return AttemptResults.StorageRetry(attemptIndex, ex.Message); }
        catch (ClaimLostException) { return AttemptResults.ClaimWait(); }
        catch (OperationStoppedException) { return AttemptResults.Stopped(); }
        catch (OccurrenceConsistencyException ex)
        {
            return await support.FailAsync(ownerId, operationId, scheduledUtc, claimId,
                ex.Message, attempts, 0, ct);
        }
        catch (PayloadIntegrityException ex)
        {
            return await support.FailAsync(ownerId, operationId, scheduledUtc, claimId,
                ex.Code, attempts, 0, ct);
        }
    }

    // Replaces the rejected unsent leaf and persists the replacement before
    // any new send. Storage conflicts surface as a retryable failure; claim
    // loss propagates to the activity's claim handling.
    private async Task<(DeliveryPlanDoc? Plan, SingleAttemptResult? Failure)> ReplaceAsync(
        string ownerId, string operationId, DateTime scheduledUtc, string claimId,
        string leafId, PlanFallbackKind fallback, int attemptIndex, int attempts, CancellationToken ct)
    {
        try
        {
            var replaced = await occurrences.ReplaceLeafAsync(new ReplaceLeafRequest(
                ownerId, operationId, scheduledUtc, claimId, leafId, fallback), ct);
            return (replaced.Plan, null);
        }
        catch (TransientStoreException ex) { return (null, AttemptResults.StorageRetry(attemptIndex, ex.Message)); }
        catch (OccurrenceConsistencyException ex)
        {
            return (null, await support.FailAsync(ownerId, operationId, scheduledUtc, claimId,
                ex.Message, attempts, 0, ct));
        }
        catch (PayloadIntegrityException ex)
        {
            return (null, await support.FailAsync(ownerId, operationId, scheduledUtc, claimId,
                ex.Code, attempts, 0, ct));
        }
    }

    private static PlanFallbackKind? DecideFallback(PlanLeaf leaf, string description) =>
        (leaf.Kind, leaf.FallbackStage) switch
        {
            (PlanLeafKind.Markdown, PlanFallbackStage.Native) => PlanFallbackKind.ToLiteralRich,
            (PlanLeafKind.LiteralRich, PlanFallbackStage.RichFull) =>
                TelegramErrorClassifier.IsSizeRejection(description)
                    ? PlanFallbackKind.ToSmallLiteral
                    : PlanFallbackKind.ToPlain,
            (PlanLeafKind.LiteralRich, PlanFallbackStage.RichSmall) => PlanFallbackKind.ToPlain,
            _ => null,
        };

    private static TelegramPayload ToPayload(PlanLeaf leaf, string source)
    {
        if (leaf.StartUtf16 < 0 || leaf.LengthUtf16 <= 0 ||
            leaf.StartUtf16 + leaf.LengthUtf16 > source.Length)
            throw new OccurrenceConsistencyException("plan leaf range is out of bounds");
        var text = source.Substring(leaf.StartUtf16, leaf.LengthUtf16);
        return leaf.Kind switch
        {
            PlanLeafKind.Markdown => new TelegramPayload(TelegramPayloadKind.Markdown, text),
            PlanLeafKind.LiteralRich => new TelegramPayload(TelegramPayloadKind.LiteralRich, text),
            PlanLeafKind.LiteralPlain => new TelegramPayload(TelegramPayloadKind.LiteralPlain, text),
            _ => throw new OccurrenceConsistencyException($"unexpected leaf kind {leaf.Kind}"),
        };
    }
}
