# Architecture

## Project direction

```
FunctionApp -> Application <- Infrastructure
FunctionApp -> Infrastructure
```

- `RecurringTasksBot.Application` references nothing else. It owns
  scheduling, command handling, generation/delivery decisions, storage
  abstractions (`ITaskStore`, `IOperationStore`, `IOccurrenceRepository`,
  `IUpdateReceiptStore`), the orchestration clients
  (`IOrchestrationClient`, `ITaskOrchestrationClient`), and the
  `ITelegramTransport` / `ILlmExecutor` contracts. Enforced by
  `ProjectBoundaryTests`.
- `RecurringTasksBot.Infrastructure` references Application. It owns the
  Telegram transport, the OpenRouter and direct DeepSeek adapters (one
  selected at a time via `Llm:ActiveProfile`), Azure Tables persistence,
  and configuration loading.
- `RecurringTasksBot.FunctionApp` references both. It owns the HTTP
  webhook, the Durable orchestration/activities, and the DI composition
  root (`Program.cs` via `FunctionAppServices.AddFunctionAppServices`,
  shared with the host tests). No business logic lives here beyond
  translating binding DTOs and mapping outcomes.

## Runtime flow

1. Telegram posts an update to `POST /api/webhook`. `WebhookFunction`
   validates the secret header first, then parses the body.
2. `UpdateDispatcher` (Application) routes the JSON task commands
   `/create`, `/get`, `/update`, `/list`, `/delete`, or usage text.
   Command receipts dedupe Telegram redeliveries.
3. `/create` stores a `task_` row and starts one `TaskLifecycle`
   orchestration per task via `ITaskOrchestrationClient`.
4. The orchestration loops through activities: `PlanTask` (planner
   decision), `ClaimTask` (reserve the latest due occurrence),
   `RunTask` (execute via `TaskOccurrenceRunner`), `CompleteTask`
   (commit + stop reason). Waits use Durable timers that update
   signals wake early; `FinishTask` stops the loop and `ContinueAsNew`
   bounds history while keeping the instance ID.
5. The run activity (`TaskOccurrenceRunner`, composing the shared
   `ExecuteOccurrenceHandler`) claims the occurrence lease, builds
   the frozen context, calls the LLM once, persists answer + delivery
   plan atomically, executes the plan over the typed transport, and
   publishes completion. Orchestration-level timers drive retries;
   the activity only reports the outcome.

No functions operate on the legacy `operation_`-row lifecycle
anymore; its storage readers (`TableOperationStore`,
`OperationRowCodec`, occurrence codecs) and the operator scripts
remain for the retained data. See [FunctionApp](FunctionApp.md).

## Responsibility boundaries

- Orchestrator: deterministic control flow, timers, retry counters
  (`OccurrenceExecution.ShouldRetry`). No I/O except activities.
- Activities: all external I/O (storage, LLM, Telegram).
- Repository: claim/receipt ownership and transaction building. Callers
  never assemble cross-row invariants themselves.
- Transport: exactly one payload per HTTP request; chunking, fallbacks,
  and retries belong to the caller.

## Rationale notes

- One occurrence repository owns claims and receipts so redelivery and
  crash recovery serialize on storage state, not on process memory.
- One LLM contract (`ILlmExecutor` + `LlmRequest`) keeps prompt
  construction testable and the adapter a verbatim serializer.
- Shared text codecs and `TableRowKeys` keep row shapes consistent
  between writers, readers, and the operator scripts.
- `TimeProvider` (not a custom clock) and `ExecutionOptions` keep time
  and budgets injectable and validated once at startup.

## Adding a command

1. Add a handler under `Application/Features/<Name>/` taking the stores
   it needs plus `BotReplySender`.
2. Route the verb in `UpdateDispatcher.ProcessAsync`.
3. Cover validation, receipt/redelivery, and HTTP outcomes in tests next
   to the sibling handler tests.

## Adding a provider adapter

Implement `ILlmExecutor.ExecuteAsync`: serialize `LlmRequest.Messages`
verbatim, enforce `MaxSourceScalars` while accumulating streamed content,
and map failures to `LlmFailureKind`. Add the profile's options record
under `Infrastructure/Llm/<Name>/`, construct it through the shared
`LlmAdapterFactory.CreateSelected` switch (an explicit two-provider
switch, not a registry), and add the named profile to the checked-in
`appsettings.json` plus deployment wiring. Scheduling, storage, and
Telegram behavior do not change.

## Non-goals

Replacing Durable Functions or Azure Tables; a different cron library;
ORM/CQRS/event sourcing; automatic failover and per-task provider
routing (one profile is selected per host, explicitly); queues between
generation and delivery; scale/load architecture; web UI; file
attachments; expanded memory; new commands; provider/model upgrades;
broad security redesign. See [Backlog](Backlog.md).
