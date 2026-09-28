# Function App

## Bindings and routes

One anonymous HTTP trigger: `POST /api/webhook` (`WebhookFunction`).
The `X-Telegram-Bot-Api-Secret-Token` header is compared in constant
time before the body is read or parsed; authenticated malformed payloads
are acknowledged without retry. Seven functions share one responsibility-based naming scheme
(`TaskLifecycleNames` in `TaskLifecycleFunctions.cs`, plus the
`Webhook` HTTP trigger): orchestrator `TaskLifecycle` with activities
`PlanTask`, `ClaimTask`, `RunTask`, `CompleteTask`, and `FinishTask`.
Renaming a function invalidates in-flight histories; old live instances
are not supported. The generated catalog is pinned by the host tests.

No functions remain for the legacy `operation_`-row lifecycle.
`TableOperationStore`, `OperationRowCodec`, and the legacy operator
scripts remain for existing operation data. The occurrence repository
still contains legacy operation-row lookups. Shared occurrence codecs
remain part of live task execution: they persist and read receipts,
frozen contexts, answers, delivery plans, and memory.

## DI lifecycle

`FunctionAppServices.AddFunctionAppServices` (called by `Program.cs`
and by the host tests) registers normal dependencies once as
singletons: execution and provider options (validated), table storage
and Telegram options, table clients, repository/store implementations,
typed HTTP clients for the Telegram transport and the LLM.
`ExecuteOccurrenceHandler` is built with real services and
`TimeProvider.System`. The `DurableTaskClient` exists per invocation,
so the recurrence adapter is created through the registered
`Func<DurableTaskClient, ITaskOrchestrationClient>` factory — no
service location, no manual graph reconstruction.

## Task lifecycle passes and continuation

One `TaskLifecycleInput` (owner + task IDs) starts the orchestration.
Each pass plans from fresh storage state: start the latest due
occurrence (latest-only catch-up after downtime), claim and run it,
commit the waterline, and wait on a Durable timer that update signals
wake early. After `MaxPassesPerExecution` passes the orchestration
continues as new with the same input to bound history while keeping
the instance ID.

## Activity outcomes and retries

`RunTask` returns `SingleAttemptResult`: `Sent`, `SkippedStopped`,
`SkippedDuplicate`, `NeedRetry`, `OccurrenceFailed`,
`OperationFailed`, `WaitingForClaim`. `NeedRetry` waits use Durable
timers (`RetryIn`, default 30s) with `AttemptIndex` advanced only for
work retries. Claim waits (`WaitingForClaim`) back off without
consuming that budget. Terminal failure or a stopped task ends the
orchestration; future occurrences otherwise stay scheduled.

## Timeouts and determinism

Orchestrator code is deterministic (context clock, pure scheduling,
timers, activities). Function timeout is 10 minutes (`host.json`);
Durable concurrency is capped at 10 orchestrators / 20 activities.
One generation attempt has 480s; the activity work budget is 540s.
Telemetry is content-free (IDs, outcome, provider/model names, schema
versions) — see the reply-source canary in `ExecuteOccurrenceHandler`.
