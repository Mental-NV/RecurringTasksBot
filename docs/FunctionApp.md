# Function App

## Bindings and routes

One anonymous HTTP trigger: `POST /api/webhook` (`WebhookFunction`).
The `X-Telegram-Bot-Api-Secret-Token` header is compared in constant
time before the body is read or parsed; authenticated malformed payloads
are acknowledged without retry. Durable functions use
responsibility-based names (`DurableNames` in `RecurrenceFunctions.cs`):
orchestrator `RecurrenceLifecycle`, activities `LoadOperation` and
`DeliverOccurrence`. Renaming a function invalidates in-flight
histories; old live instances are not supported.

## DI lifecycle

`Program.cs` registers normal dependencies once as singletons: execution
and provider options (validated), table storage and Telegram options,
table clients, repository/store implementations, typed HTTP clients for
the Telegram transport and the LLM. `ExecuteOccurrenceHandler` is built
with real services and `TimeProvider.System`. The `DurableTaskClient`
exists per invocation, so the recurrence adapter is created through the
registered `Func<DurableTaskClient, IOrchestrationClient>` factory —
no service location, no manual graph reconstruction.

## Recurrence state and continuation

One `RecurrenceState` shape (`Scheduler.cs`) serves as initial input and
`ContinueAsNew` payload: instance/operation/owner IDs, cron expression,
nullable `NextScheduledUtc`, occurrence index. Null marks a new
operation: only the first invocation loads the operation and computes
the first due time (strictly after activation); continuations consume
persisted state. Each loop delivers at most one late occurrence after
downtime (`Scheduler.SingleCatchUp`), advances, and continues as new to
bound history while keeping the instance ID.

## Activity outcomes and retries

`DeliverOccurrence` returns `SingleAttemptResult`: `Sent`,
`SkippedStopped`, `SkippedDuplicate`, `NeedRetry`, `OccurrenceFailed`,
`OperationFailed`, `WaitingForClaim`. `NeedRetry` waits use Durable
timers (`RetryIn`, default 30s) with `AttemptIndex` advanced only for
work retries. Terminal failure or a stopped operation ends the
orchestration; future occurrences otherwise stay scheduled.

## Timeouts and determinism

Orchestrator code is deterministic (context clock, pure scheduling,
timers, activities). Function timeout is 10 minutes (`host.json`);
Durable concurrency is capped at 10 orchestrators / 20 activities.
One generation attempt has 480s; the activity work budget is 540s.
Telemetry is content-free (IDs, outcome, provider/model names, schema
versions) — see the reply-source canary in `ExecuteOccurrenceHandler`.
