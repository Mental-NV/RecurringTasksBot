# Architecture

## Project direction

```
FunctionApp -> Application <- Infrastructure
FunctionApp -> Infrastructure
```

- `RecurringTasksBot.Application` references nothing else. It owns
  scheduling, command handling, generation/delivery decisions, storage
  abstractions (`IOperationStore`, `IOccurrenceRepository`,
  `IUpdateReceiptStore`), and the `ITelegramTransport` / `ILlmExecutor`
  contracts. Enforced by `ProjectBoundaryTests`.
- `RecurringTasksBot.Infrastructure` references Application. It owns the
  Telegram transport, the OpenRouter adapter, Azure Tables persistence,
  and configuration loading.
- `RecurringTasksBot.FunctionApp` references both. It owns the HTTP
  webhook, the Durable orchestration/activities, and the DI composition
  root (`Program.cs`). No business logic lives here beyond translating
  binding DTOs and mapping outcomes.

## Runtime flow

1. Telegram posts an update to `POST /api/webhook`. `WebhookFunction`
   validates the secret header first, then parses the body.
2. `UpdateDispatcher` (Application) routes `/create`, `/list`, `/delete`,
   or usage text. Command receipts dedupe Telegram redeliveries.
3. `/create` stores the operation and starts one `RecurrenceLifecycle`
   orchestration per operation via `IOrchestrationClient`.
4. The orchestration computes the first due time once, then loops: Durable
   timer, `DeliverOccurrence` activity, `ContinueAsNew` with the advanced
   scheduling state.
5. The activity runs `ExecuteOccurrenceHandler.ExecuteAttemptAsync`: claim
   the occurrence lease, build the frozen context, call the LLM once,
   persist answer + delivery plan atomically, execute the plan over the
   typed transport, publish completion. Orchestration-level timers drive
   retries; the activity only reports the outcome.

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
and map failures to `LlmFailureKind`. Register it in `Program.cs` next
to `OpenRouterLlmExecutor`. Scheduling, storage, and Telegram behavior
do not change.

## Non-goals

Replacing Durable Functions or Azure Tables; a different cron library;
ORM/CQRS/event sourcing; multi-provider support; queues between
generation and delivery; scale/load architecture; web UI; file
attachments; expanded memory; new commands; provider/model upgrades;
broad security redesign. See [Backlog](Backlog.md).
