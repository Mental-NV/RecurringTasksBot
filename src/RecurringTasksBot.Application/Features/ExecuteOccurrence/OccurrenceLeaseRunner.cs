using Microsoft.Extensions.Logging;
// Claim lease management for one occurrence attempt: heartbeat renewal
// while work runs, ownership checks before storage writes, and a
// failure-safe release that never overwrites the attempt outcome.
namespace RecurringTasksBot.Application;

public sealed class OccurrenceLeaseRunner(
    IOccurrenceRepository occurrences,
    TimeSpan? renewalInterval = null,
    ILogger? logger = null)
{
    // The complete leased attempt runs inside ownership handling:
    // ownership loss reports WaitingForClaim, transient storage failures
    // report NeedRetry, and only the caller's own cancellation propagates.
    public async Task<SingleAttemptResult> RunAsync(
        string ownerId, string operationId, DateTime scheduledUtc, string claimId,
        int attemptIndex, Func<CancellationToken, Task<SingleAttemptResult>> work,
        CancellationToken ct = default)
    {
        using var workSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stopHeartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = RenewWhileRunningAsync(
            ownerId, operationId, scheduledUtc, claimId, workSource, stopHeartbeat.Token);
        SingleAttemptResult result;
        try
        {
            result = await work(workSource.Token);
        }
        catch (ClaimLostException)
        {
            result = AttemptResults.ClaimWait();
        }
        catch (TransientStoreException ex)
        {
            result = AttemptResults.StorageRetry(attemptIndex, ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The lease runner cancelled work (renewal unconfirmed): the
            // claim is gone, so wait for recovery rather than retrying.
            result = AttemptResults.ClaimWait();
        }
        finally
        {
            try
            {
                await stopHeartbeat.CancelAsync();
            }
            catch (Exception) { }
            try
            {
                await heartbeat;
            }
            catch (Exception ex)
            {
                // Teardown noise must not escape as an attempt failure.
                logger?.LogDebug(ex,
                    "Lease heartbeat teardown failed for {OwnerId}/{OperationId} at {ScheduledUtc:u}.",
                    ownerId, operationId, scheduledUtc);
            }
            try
            {
                await occurrences.ReleaseClaimAsync(
                    ownerId, operationId, scheduledUtc, claimId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // A release failure must not overwrite the attempt outcome:
                // the lease expires on its own and recovery reclaims it.
                logger?.LogWarning(ex,
                    "Claim release failed for {OwnerId}/{OperationId} at {ScheduledUtc:u}.",
                    ownerId, operationId, scheduledUtc);
            }
        }
        return result;
    }

    public async Task EnsureOwnedAsync(DeliveryReceipt receipt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (receipt.ClaimId is null || !await occurrences.RenewClaimAsync(receipt.OwnerId,
            receipt.OperationId, receipt.ScheduledUtc.UtcDateTime, receipt.ClaimId, ct))
            throw new ClaimLostException();
    }

    private async Task RenewWhileRunningAsync(string ownerId, string operationId, DateTime scheduledUtc,
        string claimId, CancellationTokenSource work, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(renewalInterval ?? OccurrenceExecution.ClaimRenewalInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
                if (!await occurrences.RenewClaimAsync(ownerId, operationId, scheduledUtc, claimId, stop))
                {
                    await work.CancelAsync();
                    return;
                }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception)
        {
            // Stop external work if renewal cannot be confirmed. A future
            // invocation can resume persisted progress after lease recovery.
            await work.CancelAsync();
        }
    }
}
