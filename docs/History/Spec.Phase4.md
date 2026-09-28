# Phase 4 — Application consolidation and maintainable documentation

Status: proposed execution plan. Date: 2026-09-26. Inspected baseline: `c54142d`.

## 1. Objective and authority

Make the MVP easier to extend by removing obsolete implementations, naming code by responsibility, organizing commands as features, and establishing documentation of the current application.

This is an implementation specification for an AI agent. Execute the decisions below as defaults; the review points highlight consequential choices, not unresolved alternatives. Complete the code, tests, tooling, and documentation in one coordinated change. Do not implement product features from the backlog as part of this phase.

**There is no backward-compatibility requirement.** Existing application data, saved payloads, configuration aliases, and pre-cutover Durable histories are disposable. Do not add migration readers, compatibility switches, old API overloads, replay branches, or deprecated-setting warnings. Preserve the business behavior and reliability guarantees listed below for newly created work.

This plan supersedes compatibility and historical implementation requirements in Phases 1–3. Move those three specs unchanged into `docs/History/`; they remain historical artifacts. Keep this file at `docs/Spec.Phase4.md` as the current plan. After implementation, structured documentation is the baseline for operating and extending the app; readers must not have to combine four phase specs to understand current behavior.

The implementation run is local/offline by default: no deployment, deletion of cloud data, webhook registration, live Telegram sends, or paid LLM calls. Prepare runnable instructions and tooling for the separate operator cutover. This distinction does not require keeping compatibility code.

## 2. Decisions worth reviewing

| ID | Recommended decision | Consequence / tradeoff |
| --- | --- | --- |
| D1 | Use three production projects: `Application`, `Infrastructure`, and `FunctionApp`. Organize application use cases as vertical slices. | Enforces adapter boundaries without separate Domain, Contracts, Bot, or LLM assemblies. One additional project compared with today. |
| D2 | Make the current frozen-context → answer → persisted delivery-plan flow the only occurrence implementation. | Deletes the older prompt-only executor, HTML composition, answer truncation, legacy payload store, and compatibility fallbacks. |
| D3 | Cut over to an empty business table and new task hubs; recreate test schedules. | No preservation of old schedules, receipts, memory, or in-flight work. Stop old workers before cutover. No whole-account reset. |
| D4 | Retain one Durable activity per execution attempt, with internal services for generation and delivery. | Avoids a second workflow redesign during restructuring. Durable still owns retry waits; persisted artifacts permit recovery within the activity. |
| D5 | Keep Markdown answers plus literal-rich/plain fallback, previous-successful-answer memory, leases, and atomic persistence. | These are current features and correctness mechanisms. Removing them would change the product or weaken recovery. |
| D6 | Give the occurrence repository sole ownership of occurrence state; use one segmented text codec for persisted text. | Eliminates overlapping receipt/payload stores and duplicated prompt loading. Requires adapting transaction and concurrency tests. |
| D7 | Use structured current documentation and a short root README; archive historical specs byte-for-byte. | `SETUP.md` and `ToDoNotes.md` are replaced by concern-specific documentation and a curated backlog. |
| D8 | Make production deployment explicitly dispatched for the breaking cutover and require successful validation before deploying. | Prevents the current push-to-`master` workflow from starting incompatible code against old state. Automatic deployment can be reconsidered later. |
| D9 | Put runtime `appsettings*.json` in FunctionApp and deployment metadata in `infra/environments/`. | Makes configuration ownership explicit; scripts and smoke tooling load canonical files instead of maintaining copies. |

## 3. Findings in the inspected implementation

These paths describe the starting repository and will move during implementation.

| Evidence | Problem to resolve |
| --- | --- |
| `src/RecurringTasksBot.Core/Delivery.cs` (~1,290 lines) | Contains both `GenerateOnceAsync`/`DeliverPersistedAsync` and `GenerateV3Async`/`DeliverV3PlanAsync`; optional dependencies select behavior. Also contains policy, transport exceptions, heartbeat, composition, truncation, and a second retry-loop entry point. |
| `OpenRouterLlm.cs`, `OpenRouterStreaming.cs`, `Phase3LlmRequest.cs` | Parallel prompt-only and message-based request/response paths. The OpenRouter HTTP adapter resides in Core. |
| `tools/LlmSmoke/Program.cs` | Calls the old `ExecuteAsync(LlmPrompt)` and `AnswerComposer`, so its smoke test does not exercise the current generation path. |
| `Stores.cs`, `TelegramBotSender.cs`, `UpdateProcessor.cs` | Sender default interface methods preserve old fakes. Command replies still escape HTML and use `SendRichTextAsync`; its fallback may send multiple messages but returns only the first ID. |
| `Phase3*.cs` and `Phase3TableBatches` | Names encode project history. `Phase3Storage.cs` mixes use-case contracts, Azure-oriented codecs, row keys, and memory rules. |
| `TableStores.cs`, `TableOccurrenceRepository.cs` | Occurrence state is split between stores; operation text readers are duplicated; old chunk-row payload storage remains. |
| `Webhook.cs` | `WebhookDispatcher` is a separate decision implementation exercised by tests but not called by the real webhook. |
| `RecurrenceFunctions.cs` | Builds handlers manually with optional old/new dependencies. It reads `OrchestratorInput` but passes `RecurrenceState` to `ContinueAsNew`; explicitly verify state survives the next invocation instead of recalculating the next occurrence from scratch. |
| `WebhookFunction.cs` | Computes secret validity but may acknowledge an unparseable body before rejecting an invalid secret. Correct this while extracting the webhook boundary. |
| `tests/RecurringTasksBot.Tests/RecurringTasksBot.Tests.csproj` | Tests compile linked copies of host source instead of referencing the production assembly. |
| `docs/SETUP.md` | Mixes setup, operations, LLM configuration, phase changes, and compatibility rollout. There is no root README or current architecture index. |

Planning verification: `dotnet test RecurringTasksBot.sln --configuration Release --no-restore` passed all 394 tests, and `bash tests/scripts/test-launch-local.sh` passed both launcher checks. This includes historical-path tests and does not establish production-host wiring or live provider correctness. Re-run the baseline at implementation start; do not preserve an obsolete test merely to retain this count.

## 4. Business behavior to retain

### Commands and scheduling

- Private chats only; every operation lookup and mutation is scoped to the Telegram user ID. Keep webhook-secret validation and environment isolation.
- `/create`: six UTC NCRONTAB fields, seconds fixed to `0`, a future occurrence required, and a prompt of 1–32,768 Unicode scalars. Preserve accepted whitespace and rich-input normalization. A schedule-only command can use the same user's replied-to text/rich message.
- Stable IDs and update receipts prevent duplicate creation; interrupted starts resume without a second recurrence. A failed create confirmation can be resent without restarting the task.
- `/list`: caller-owned undeleted operations with schedule, prompt, and status; split long output; reconcile unexpected Durable failure with visible status.
- `/delete`: persist the tombstone before best-effort termination. Repeated deletion is harmless. Unknown/invalid commands return help, including external LLM/search disclosure.
- Webhook results: invalid secret → 403; supported processing/duplicates/unsupported updates → 200; transient processing failures → 503. Permanent reply failure is acknowledged. Preserve the current limited command-reply recovery semantics; do not build a new command outbox.
- One recurrence per operation; first occurrence strictly after activation, UTC scheduling, at most one catch-up occurrence after downtime, next occurrence in the future, bounded history through `ContinueAsNew`.

### Generation and delivery

- Freeze task, schedule, execution timestamps, system instruction, and eligible previous successful answer once per occurrence. Retries reuse the snapshot. Support both `PreviousSuccessfulReply` and `None` memory modes.
- Retain configured OpenRouter model/reasoning/search behavior and context-budget preflight. No provider fallback, automatic model substitution, extra tools, or reasoning in delivered content.
- Generate one complete canonical answer; only outer trimming is permitted. Keep the 131,072-scalar maximum source bound and terminal handling of `length`/source-limit/context-budget failures. Never send an accepted answer's truncated prefix.
- Answers contain the model's content and inline citations, with no bot-added operation banner, timestamp, or source appendix. Failure notice: `This run failed. Future runs remain scheduled.`
- Send the initial Markdown plan; use deterministic literal-rich and literal-plain fallback only for classified rejections/capability restrictions. Preserve Unicode boundaries, exact source coverage, confirmed message IDs, fallback transitions, and the 128-leaf ceiling.
- Persist context and generated answer/plan before sends; persist each confirmed leaf and every replacement plan. Delivery retries reuse the answer and resume unconfirmed leaves without another LLM request.
- Separate generation failures, delivery failures, storage conflicts, lease waits, and activity-budget yields. Retain bounded retries, `Retry-After`, claim renewal, cancellation on claim loss, and deletion checks before new work/publication/sends. An already-started send may finish.
- Publish memory only after a successful complete answer delivery, only for an active operation, and only if newer than existing memory. Failure notices and incomplete deliveries never replace memory.
- Terminal occurrence failure leaves future recurrences active; permanent recipient failure stops the operation. Telegram acknowledgement ambiguity can still produce duplicate delivery after a crash; do not claim exactly-once delivery.
- Retain content-free diagnostics, operator recovery, retention cleanup, and independent development/production resources.

## 5. Target structure and dependency rules

Use vertical slices inside a small application boundary. Business rules and orchestration-independent use cases live together; adapters implement their ports. Do not introduce MediatR, an event bus, generic repositories, a unit-of-work framework, AutoMapper, plugin discovery, or a DI container beyond the existing Microsoft container.

```text
README.md
RecurringTasksBot.sln
src/
  RecurringTasksBot.Application/
    Features/
      CreateTask/             # command parser, handler, creation recovery, confirmation
      ListTasks/              # handler, status reconciliation, list formatter
      DeleteTask/             # handler and deletion ordering
      Help/                  # help text and handler
      ExecuteOccurrence/     # handler, generation service, lease runner, attempt outcome
        Context/             # frozen context, message builder, prompt template, budget
        Delivery/            # answer/plan types, transition policy, chunking, delivery service
        Memory/              # eligibility and publication rules
    Bot/                     # update DTO, dispatcher, receipt/reply coordination
    Scheduling/              # NCRONTAB parser and pure recurrence policy
    Operations/              # operation model, status and stable identity
    Ports/                   # IOperationStore, IUpdateReceiptStore, IOccurrenceRepository,
                             # ILlmExecutor, ITelegramTransport, IRecurrenceClient
    Configuration/           # execution options and business validation
    Common/                  # only truly shared Unicode/time/error primitives
  RecurringTasksBot.Infrastructure/
    Llm/OpenRouter/          # HTTP adapter, request builder, SSE/JSON readers, provider options
    Persistence/AzureTables/ # clients, stores, entity codecs, row keys, transaction builders
    Bot/Telegram/            # update parser, rich-input normalizer, typed HTTP transport
    Configuration/           # configuration reader and adapter option validation
    DependencyInjection.cs
  RecurringTasksBot.FunctionApp/
    Program.cs               # composition root
    appsettings.json
    appsettings.Development.json
    appsettings.Production.json
    Functions/
      TelegramWebhookFunction.cs
      RecurrenceOrchestrator.cs
      LoadOperationActivity.cs
      ExecuteOccurrenceActivity.cs
    Durable/                 # binding adapter, persisted state, request/result DTOs, names
    host.json
    local.settings.json      # generated locally, ignored by Git
  # Each of the three folders above contains its same-named .csproj.
tests/
  RecurringTasksBot.Tests/
    Application/             # mirrors features and pure policies
    Infrastructure/          # adapter, schema and transaction contract tests
    FunctionApp/             # webhook, Durable state, activity and DI wiring tests
    Support/                 # focused fakes and fixtures
  scripts/
tools/LlmSmoke/
scripts/                     # retain existing public script names
infra/
  main.bicep
  environments/
    development.json         # deployment metadata, no secrets
    production.json
.github/workflows/
docs/                        # detailed structure in section 10
```

References: `Infrastructure → Application`; `FunctionApp → Application + Infrastructure`; tests reference all three production projects; `LlmSmoke → Application + Infrastructure`. Application must not reference Infrastructure, Functions/Durable/Azure SDKs, or OpenRouter HTTP implementation types. Keep Microsoft logging abstractions where useful. Durable client adaptation stays in the host because its binding is host-specific.

Keep one test project with responsibility folders for this run. Remove every linked production `<Compile Include=...>` entry. Use internal visibility for adapter test seams where needed; do not make implementations public only to test them. Add a small boundary check for forbidden project/assembly references, without another architecture-test package.

Commands use explicit handler classes and a small switch dispatcher. No reflection registry. Put use-case-specific request/result types beside handlers; shared records get a clear owner. Avoid recreating `Models.cs`, `Stores.cs`, or a miscellaneous `Services` directory containing unrelated concerns.

## 6. Consolidate application behavior

### 6.1 One occurrence pipeline

`ExecuteOccurrenceHandler.ExecuteAttemptAsync` is the only production entry point for one occurrence attempt:

1. Read operation/receipt and short-circuit stopped or terminal work.
2. Acquire the occurrence claim and run bounded work with lease renewal.
3. Initialize or reload the frozen execution context.
4. If no answer/plan exists, build messages, check budget, execute LLM, and atomically persist the answer and initial plan; persist the terminal notice when required.
5. Deliver unconfirmed plan leaves; atomically record confirmations or fallback replacements.
6. Atomically complete the occurrence and, where eligible, publish memory.
7. Release ownership and return a typed outcome and optional durable retry delay.

Extract `OccurrenceGenerator`, `DeliveryPlanSender`, and a narrowly scoped `OccurrenceLeaseRunner` from the retained implementation. They are concrete collaborators, not interfaces for every class. The handler owns workflow order; repositories own atomic state changes; transport owns exactly one HTTP request per send. Required collaborators must be constructor parameters with no old-path defaults or downcasts.

Remove the production `DeliverAsync` convenience retry loop and its duplicate result types. Tests can drive attempts using a test-only helper. Keep one retry policy shared with the orchestrator. Generation/delivery counters must not be incremented by claim waits, storage-only conflicts, or budget yields. Retain the current overall work-retry cap of five after the initial attempt, but name/document it independently of provider retry budgets: `NeedRetry` consumes it, including storage/budget retries; `WaitingForClaim` does not. Keep at most two generation retries and three delivery retries, subject to that overall cap. Write down the exact outcome/counter table in the Function App documentation and test it; changing this retry model is outside this restructuring.

Keep the existing activity work budget and timeout relationship: 540 seconds of work within the configured 600-second host timeout; LLM timeout at most 510 seconds; Telegram request timeout 30 seconds; 45-second send/progress reserve. Do not split generation and delivery into new Durable activities in this phase.

Use `TimeProvider` for wall-clock access and controllable activity timers; remove `IPhase3Clock`, `SystemPhase3Clock`, and the private fallback clock. Orchestrators must continue using the Durable context clock, never `TimeProvider` or wall-clock APIs.

### 6.2 One LLM contract and adapter

Replace `ILlmPromptExecutor` and `IPhase3LlmExecutor` with `ILlmExecutor.ExecuteAsync(LlmRequest, CancellationToken)`, where `LlmRequest` carries the complete message list and answer-source bound. The application constructs the frozen context messages; the adapter serializes them without injecting a second system prompt.

Retain the message-based implementation in `Phase3LlmRequest.cs` as the starting point. Merge shared transport, usage/source extraction, error handling, and SSE parsing from the other files. There must be one request builder and one bounded reader per actual wire format (SSE and non-streaming JSON are both supported formats, not two product pipelines).

Remove `LlmPrompt`, the prompt-only overload/builder/system instruction, the unbounded historical parsers, and obsolete composition consumers. Keep source/usage metadata needed for observability and search checks; it must not reintroduce an appended bibliography. Move provider-specific code/options out of Application.

Update `LlmSmoke` to use the same context/message builder, `ILlmExecutor`, source bound, and initial-plan validation as the application. A single request smoke remains opt-in; it must not write schedules, Telegram messages, or application storage. Offline tests assert the shared request contract; live execution is a separate operator check.

### 6.3 Bot commands and a single send contract

Extract `/create`, `/list`, `/delete`, and help into the feature folders. Keep common update receipt handling and create-confirmation recovery in `Bot/UpdateDispatcher` and a small coordinator; avoid four subtly different copies of acknowledgement/error rules.

`ITelegramTransport.SendAsync(chatId, TelegramPayload, ct)` sends exactly one payload and returns one valid message ID. Remove default interface implementations, `SendRichTextAsync`, `SendTextAsync`, the HTML payload kind, and adapter-owned chunk loops. Consolidate error parsing/classification and acknowledgement validation.

Generated answers use persisted delivery plans. Command/help/list replies use a lightweight `BotReplySender` with the same literal chunker, payload types, and rejection policy, but no occurrence memory or new persisted reply-plan subsystem. Command text is literal-rich initially; fallback preserves literal text and sends all required conservative plain chunks. Never escape plain command strings into HTML as an intermediate representation.

Delete `AnswerComposer`, `Delivery.TruncateAnswer`, the Markdown-to-HTML `RichValidator`, and obsolete HTML helpers after moving every caller, including tests and tools. Keep inbound `RichNormalizer` functionality, rename it `TelegramPromptNormalizer`, and locate it with Telegram parsing. Remove documented older input aliases that exist solely for legacy support; retain current plain/rich input forms. Delete the unused `WebhookDispatcher`/`WebhookDecision`, moving meaningful assertions to the real webhook/dispatcher path.

### 6.4 Configuration and names

Move runtime configuration into `src/RecurringTasksBot.FunctionApp/appsettings.json`, `appsettings.Development.json`, and `appsettings.Production.json`. Preserve current model/search settings. Extract deployment metadata into `infra/environments/development.json` and `production.json`; remove the three root profiles after migrating every consumer. No compatibility copies or fallback to the old locations.

Configuration ownership:

| Canonical location | Settings and consumers |
| --- | --- |
| FunctionApp `appsettings*.json` | LLM settings, execution/memory budgets, business table, task hub, expected storage-account/bot identity, and runtime Telegram settings. Loaded by the host and relevant local/operator tools. |
| `infra/environments/*.json` | Azure subscription/tenant, resource group, region, Function App/plan names, OIDC client ID, and deployment endpoint inputs. Read by deployment and resource-management tooling, not by the running application. |
| FunctionApp `host.json` | Functions/Durable platform behavior, concurrency, logging and host timeout. |
| Environment variables / deployment secrets | Credentials and explicit runtime overrides; never committed to either JSON family. |

Each value has one authoritative file location. When provisioning needs runtime values such as the table, task hub or expected resource identity, tooling reads the selected runtime profile and passes them to Bicep; do not duplicate them in deployment metadata. Derive the production webhook URL from the deployment endpoint/app name unless an explicit endpoint override is required. Preserve the development tunnel URL workflow with one canonical development setting. Generate any combined Bicep parameter file temporarily; remove duplicated hand-maintained values from `infra/main.parameters.json` and redundant GitHub variables. Keep the OIDC bootstrap instructions usable before the first deployment.

Use clearly owned option groups: application `ExecutionOptions` (memory and context/answer budgets), infrastructure `OpenRouterOptions`, `TableStorageOptions`, and `TelegramOptions`; the host composes them. Application and Infrastructure libraries own typed options and validation, not environment-specific configuration files. Keep the reusable configuration loader in Infrastructure, accepting an explicit configuration directory and environment name. Runtime precedence remains common JSON → selected environment JSON → environment variables; secrets remain environment-only.

The host loads profiles from its output directory; its project copies/publishes its own runtime JSON instead of linking files from the repository root. Launch, webhook, polling, recovery, cleanup, and smoke scripts resolve paths relative to their script/repository location rather than the caller's working directory. They explicitly select the environment and load the relevant canonical files. The LLM smoke tool receives the FunctionApp configuration directory from its wrapper and uses the shared loader without referencing the FunctionApp assembly or copying its profiles; validate only the LLM/execution settings and credential needed by that tool. Deployment must select `Production` explicitly and map effective runtime values to the required platform settings, including the Durable hub setting.

Remove `MaxStoredAnswerChars`, its detection/warning code, phase-named option types, unsupported configurability, and duplicated defaults. Make malformed explicit values fail startup instead of silently using defaults. Require the webhook secret at startup. Treat Telegram/storage protocol ceilings as code constants; remove `Limits` JSON knobs if they are not genuinely consumed. Keep adjustable business/model budgets in configuration, with limits validated once before work begins.

Use these naming destinations (split large source files by responsibility rather than merely renaming them):

| Existing symbol/file family | Destination |
| --- | --- |
| `Phase3Limits`, `Phase3FailureCodes`, `Phase3DeliveryText` | `TelegramLimits`, `ExecutionLimits`, `StorageLimits`, `OccurrenceFailureCodes`, `OccurrenceMessages` |
| `Phase3SystemTemplate`, `Phase3ContextSnapshot`, `Phase3ContextEnvelope`, `Phase3ContextBudget` | `RecurringTaskSystemPrompt`, `ExecutionContextSnapshot`, `ExecutionMessageBuilder`, `ContextBudget` |
| `Phase3LiteralChunker`, `Phase3CapabilityGate` | `LiteralMessageChunker`, `TextOnlyCapabilityPolicy` |
| `Phase3Options` / `Phase3Config` | `ExecutionOptions` / shared configuration loader |
| `Phase3LlmRequest`, `BuildPhase3RequestJson`, `ParsePhase3*` | `OpenRouterRequestBuilder`, `OpenRouterResponseReader`, `OpenRouterStreamReader` |
| `Phase3Storage`, `Phase3RowKeys`, `Phase3StorageBounds`, `Phase3TableBatches` | owned model/port files; `TableRowKeys`, `TableStorageLimits`, `OccurrenceTransactions`, `OccurrenceEntityCodec` |
| `Phase3PayloadException`, `Phase3ConsistencyException`, `Phase3OperationStoppedException` | `PayloadIntegrityException`, `OccurrenceConsistencyException`, `OperationStoppedException` |
| `Phase3Versions`, `Phase3Times` | inline simple version creation or `ArtifactVersion`; shared UTC utility only where needed |
| `Phase3WriteFence`, `phase3-v1`, `__v3*` row suffixes | `PublicationFence`, `recurring-task-v1`, `__context_` / `__answer_` / `__plan_` |
| `Phase2Tests`, `Phase3*Tests`, history-specific test names | split by behavior: context, generation, transport, plans, memory, leases, transactions, activities |

Names must not use `Phase4`, `V4`, `New`, `Modern`, or `Legacy` as substitutes. Schema and prompt revisions may be versioned independently of development phases.

## 7. Persistence simplification

Keep Azure Tables, owner partitions, ETags, and same-partition transactions. Move Table-specific row keys, codecs, byte accounting, and transaction builders into Infrastructure. Pure plan transitions, answer invariants, and memory eligibility remain in Application.

- Keep `IOperationStore` and `IUpdateReceiptStore` for their independent lifecycles.
- Fold receipt read/claim/renew/release into `IOccurrenceRepository`; remove `IDeliveryReceiptStore` and `IOccurrencePayloadStore` plus their production adapters. The repository owns all occurrence mutation rules, including fencing and terminal state.
- Replace unrestricted receipt upserts with named operations for generation, plan progress, failure, and completion. Reuse existing transaction builders; do not replace atomic multi-row publication with sequential CRUD calls.
- Use one operation entity codec from both operation store and occurrence transactions. Store the accepted prompt using numbered properties on the operation entity, reusing the segmented codec; remove separate operation text chunk rows/readers. Verify worst-case supplementary-character prompts fit the entity budget.
- Retain separate context, answer, plan, memory, operation, and receipt entities; merging them would endanger entity size limits and lifecycle rules.
- Fresh rows use mandatory `SchemaVersion = 1` for the consolidated schema. Unknown or missing schemas fail clearly before generation/sending; never infer HTML or regenerate missing persisted content. This is integrity validation, not a version migration framework.
- Remove legacy payload/version fields. Keep optional values only where the lifecycle genuinely permits absence (e.g. no answer before generation). Distinguish a valid uninitialized receipt from a corrupt initialized receipt.
- Keep plan-derived `SentParts`, `TotalParts`, and `MessageIds` as diagnostic summaries updated in the same transaction; the plan is authoritative. Remove redundant first-message and old payload-format fields after auditing script/log consumers.
- Preserve atomic context initialization with the active-operation fence; answer/plan publication; leaf confirmation/replacement with receipt summaries; and completion/memory publication. Keep stale-owner rejection and monotonic memory publication.
- Keep scalar counts, hashes, source coverage validation, and conservative entity/transaction size guards. Azure Tables limits individual entities to 1 MiB and string properties to 64 KiB; segmented properties remain necessary. See [Table service data model](https://learn.microsoft.com/en-us/rest/api/storageservices/understanding-the-table-service-data-model).

Update recovery/cleanup tooling to the sole new schema. Retain dry-run defaults, deletion of child artifacts before parent receipts, dedup retention, preservation of unfinished work and independent memory, and orphan grace periods. Orphans from interrupted cleanup are possible even after removing legacy readers. Do not turn routine retention cleanup into a whole-storage reset utility.

## 8. Function App and cutover

### Host responsibilities

Function entry points translate binding DTOs, resolve/use application services, emit content-free telemetry, and map outcomes. Register normal dependencies once in the composition root. Where `DurableTaskClient` is supplied per invocation, use a small explicit factory for that invocation's recurrence adapter/command coordinator; do not hide service location or manually reconstruct the entire dependency graph.

Use one `RecurrenceState` shape for initial input and `ContinueAsNew`, with an explicit nullable/uninitialized next time for a new operation. Compute the first due time only on initialization; subsequent invocations consume persisted scheduling state. Add a serialization/continuation regression test. Keep timers and replay-safe control flow in the orchestrator and external I/O in activities.

Move secret validation before reading/parsing the webhook body, so malformed payloads cannot bypass the 403 result. Keep authenticated malformed/unsupported updates acknowledged as before. Add real host-boundary tests rather than relying on the unused pure dispatcher.

Use responsibility-based Durable names consistently in the host, client adapter, scripts, and tests. Keep answers/history out of Durable DTOs and logs. Live instances from older code are not supported: changing orchestrator execution paths or activity contracts can invalidate existing histories. See [Durable Functions versioning](https://learn.microsoft.com/en-us/azure/azure-functions/durable-functions/durable-functions-versioning).

### Prepare a deliberate fresh-state cutover

Use business table `RecurringTaskData` and task hubs `RecurringTasksAppDev` / `RecurringTasksAppProd` in the respective existing accounts. No compatibility reads of `RecurringTasks` or the old hubs. These are stable responsibility/environment names, not phase numbers. Read table/hub names from the selected FunctionApp runtime profile consistently, including the Functions platform hub setting, Bicep parameters, launch and operator scripts; remove their hardcoded old defaults. Read deployment resource metadata from the matching `infra/environments` file. Assert the effective platform hub matches runtime configuration, including explicit overrides, in script/configuration tests.

Prepare the deployment workflow and runbook to perform this sequence when explicitly invoked by an operator:

1. Run offline validation and publish the new package. Inventory the intended environment, old/new table and hub names, and bot identity without printing credentials.
2. Disable incoming work for the selected environment and stop all old host/polling workers. A hub rename alone does not prevent an old worker from continuing to send.
3. Select the empty new business table and new task hub. Do not copy old schedules or receipts. Refuse a fresh-cutover operation if the target state is unexpectedly populated; ordinary subsequent deployment uses the selected current state.
4. With the Function App stopped, apply matching settings and package, then start the new host. Verify configuration and endpoint health before registering the selected environment's webhook. Explicitly discard pending pre-cutover Telegram updates so old `/create` commands do not recreate abandoned tasks.
5. Recreate designated test tasks and run the manual checks in section 11. Old schedules/memory are intentionally absent.
6. Retire old app-owned table/hub artifacts separately after validating resource ownership. Never delete the shared storage account, deployment blobs, or Azure Files content. No old worker may be restarted against the new state.

Failure handling: keep processing stopped if settings/package deployment fails partway. Prefer fixing forward against the new schema. Do not promise old-binary rollback against new state; any explicit return to old code uses its isolated old state and accepts losing new test work. Keep no compatibility branch in application code for this operator choice.

Update `.github/workflows/deploy-production.yml` to `workflow_dispatch` with build/test/script checks before cloud changes; preserve environment-scoped OIDC and secret handling. Update all renamed project paths in solution references, publish commands, local launch scripts, script tests, and tool references. Keep existing runtime/package versions unless moving a dependency requires a minimal change; no framework or SDK upgrade campaign.

## 9. Scope discipline

Included simplifications are directly related to consolidation: three-project boundaries, one generation/delivery path, one Telegram transport, one occurrence repository, shared text codecs, required dependencies, one clock abstraction, split command handlers, production assembly testing, and current documentation.

Defer: replacing Durable Functions or Azure Tables; a different cron library; ORM/CQRS/event sourcing; multi-provider support; queues between generation and delivery; scale/load architecture; web UI; file attachments; expanded memory; new commands; provider/model upgrades; broad security redesign; rewriting operator scripts in a new language. Preserve these as backlog items where already requested. Do not remove a business feature to reduce line count.

## 10. Documentation rewrite

Create this current documentation set. Each page owns its topic; other pages link instead of copying configuration tables or procedures.

| Path | Required content |
| --- | --- |
| `README.md` | Product purpose, supported commands, short quick-start entry, project map, links to docs/current plan/history. |
| `docs/README.md` | Navigation by developer/operator task; document authority and maintenance rules. |
| `docs/Architecture.md` | Project reference direction, runtime flow, responsibility boundaries, D1–D6 and D9 rationale, how to add a command/provider adapter, explicit non-goals. |
| `docs/TelegramCommands.md` | Each command's syntax, validation, ownership, reply-based creation, feature flow, receipts, redelivery behavior, examples, HTTP outcomes. |
| `docs/LLM.md` | Actual message roles/order, system prompt location/revision, frozen context, memory selection, source/context budgets, reasoning/search mapping, SSE/JSON errors, smoke test, data sent externally. |
| `docs/Persistence.md` | Complete current entity/key/property schema, state/claim transitions, transaction contents and ETags, segmentation/size proof, integrity errors, cleanup/reference rules. |
| `docs/FunctionApp.md` | Bindings/routes, DI lifecycle, recurrence state and continuation, activity outcomes/retry counters, timeouts/cancellation, deterministic vs I/O code, concurrency knobs without unmeasured capacity claims. |
| `docs/TelegramTransport.md` | Accepted input shapes, output payload kinds, actual request JSON, literal chunking, rejection classification, fallback transitions, acknowledgement ambiguity. |
| `docs/Configuration.md` | Single canonical setting inventory: key, owning file/option type, purpose, default, valid range/unit, secret source, environment overrides; runtime vs deployment ownership, loader/path rules and Bicep/platform mappings; distinguish protocol constants from settings. |
| `docs/Setup.md` | Prerequisites, all four application secrets including LLM key, configuration, launch, polling/tunnel alternatives, local verification and shutdown. |
| `docs/Deployment.md` | Azure resources, OIDC/GitHub environment setup, workflow triggers, package paths, normal deploy vs first clean cutover, webhook registration, failure handling. |
| `docs/CI.md` | Exact offline commands, test organization, fake-vs-live coverage, script shims, PR/deploy gates, opt-in external checks. |
| `docs/Runbook.md` | Diagnostics using content-free fields, failed/stranded starts, delivery/LLM failures, leases, deletion races, cleanup/dry-run/retention, restart recovery, links to cutover. |
| `docs/Backlog.md` | Preserve unresolved scale/security questions and future attachment/UI ideas from `ToDoNotes.md`; mark completed structural/documentation work with a link to this plan. |
| `docs/History/README.md` | Explain historical specs are frozen and their old paths, commands, compatibility instructions and limits are not current guidance. |
| `docs/History/Spec.Phase1.md` through `Spec.Phase3.md` | Original bytes, moved only. |

Rewrite `docs/SETUP.md` into the pages above and remove the old file. On case-insensitive filesystems, rename through an intermediate filename before creating `Setup.md`. Move all three historical specs together so their sibling links continue to resolve. Preserve their content even if they refer to old source paths; explain that in the history index. Replace `ToDoNotes.md` after carrying its uncompleted ideas into the backlog.

Document code as implemented, not the aspirational architecture. Record any discovered behavioral discrepancy in a short implementation report and add a regression check for fixes needed to meet section 4. Current docs must not say “Phase 2 does X; Phase 3 changes Y.” Link primary provider documentation when describing external contracts, record verification dates, and distinguish mocked behavior from live verification. No secrets or user content in examples.

## 11. Execution order and acceptance gates

Execute sequentially; keep every checkpoint buildable. New folders without migrated callers are not a completed checkpoint.

### A. Establish the behavior baseline

- Record Git state, SDK version, historical-spec hashes, test results, and all production entry points.
- Map tests to section 4: retained business behavior, replaced legacy behavior, or uncovered host/tool behavior. Add focused checks for the actual webhook, continuation state, and shared generation path where absent.
- Do not rewrite passing tests solely because their filenames contain a phase number. Preserve their assertions on current behavior when moving them.

### B. Establish project boundaries

- Rename Core to Application and the host to FunctionApp; create Infrastructure. Move adapters and SDK dependencies; retain package versions.
- Update solution, namespaces, references, launch/publish paths, tooling, and test project references. Remove linked source compilation.
- Move runtime JSON into FunctionApp, extract deployment metadata into `infra/environments`, and migrate script/tool/workflow consumers together. Remove root profiles and duplicated deployment parameter values. Verify copied/published runtime configuration and explicit environment selection.
- Gate: all three production projects and the smoke tool build; retained tests run against production assemblies; dependency direction is enforced.

### C. Remove parallel implementations

- Consolidate the message-based LLM adapter and migrate smoke tooling first.
- Make the current occurrence path mandatory; migrate fakes and shared behavior tests, then delete older generation/delivery/composition paths.
- Extract command features; migrate command replies to literal typed payloads and delete HTML adapters/default methods and the unused webhook policy.
- Gate: one generation contract, one occurrence-attempt entry point, one typed Telegram transport, and no old path reachable from tests or tools.

### D. Consolidate persistence and host wiring

- Give the occurrence repository claim/receipt ownership; move codecs and transaction builders; consolidate prompt storage and current schema.
- Apply responsibility names, shared clock/options, real handler DI, consistent Durable state, and early secret validation.
- Update cleanup/recovery and fresh-state deployment configuration together. Gate: race, transaction, schema, host, and script tests pass; deployment cannot run accidentally on a push.

### E. Rewrite documentation and remove residue

- Create the current docs, archive specs with hash verification, and carry forward the backlog.
- Audit all tracked application/test/tool/config/script files for `Phase[1234]`, `LegacyHtml`, `UseV3`, `AttemptV3`, `MaxStoredAnswerChars`, `AnswerComposer`, and old project paths. Historical specs, this plan, and their index links are expected exceptions. Remove obsolete comments as well as symbols.
- Resolve broken current-document links and commands. Check old spec hashes match exactly after moving.

### F. Final verification and handoff

Required offline checks:

```sh
dotnet restore RecurringTasksBot.sln
dotnet build RecurringTasksBot.sln --configuration Release --no-restore
dotnet test RecurringTasksBot.sln --configuration Release --no-build
bash tests/scripts/test-launch-local.sh
dotnet build tools/LlmSmoke/LlmSmoke.csproj --configuration Release
dotnet publish src/RecurringTasksBot.FunctionApp/RecurringTasksBot.FunctionApp.csproj \
  --configuration Release --no-restore --output /tmp/recurringtasksbot-phase4-publish
```

Inspect the publish output for function metadata, `host.json`, FunctionApp-owned runtime configuration profiles and referenced assemblies; ensure deployment metadata, secrets and local settings are not packaged. Validate Bicep syntax with installed tooling when available, and exercise deployment/cleanup/recovery scripts with shims or fixtures without cloud credentials. Report unavailable checks explicitly; never label them as passed.

Required regression coverage:

| Area | Evidence |
| --- | --- |
| Commands/webhook | All command flows, ownership, limits, same-author reply creation, duplicate/interrupted starts, confirmation recovery, long literal replies and fallback; invalid secret plus malformed body produces 403. |
| Scheduling | Initialization, state serialization across `ContinueAsNew`, next scheduled time preserved, single catch-up, future advancement, failure/stop outcomes, durable waits. |
| LLM | Shared host/smoke request contract, frozen message order and prompt, memory disabled/enabled, context budget, SSE boundaries, missing/incomplete completion, source limits, terminal `length`, retries, reasoning exclusion and search metadata. |
| Text/delivery | Exact source coverage, supplementary Unicode, bounds, capability gate, Markdown/literal-rich/plain transitions, definitive vs ambiguous rejection, malformed acknowledgements, partial restart, no regeneration after persistence. |
| Persistence/concurrency | Same-partition batch contents and ETags using real SDK types; atomic context/answer/plan publication; confirm/replace/final memory publication; lease loss, stale owner, delete races, corruption, unknown/missing schema, worst-case entity/transaction size. |
| Retry/time behavior | Generation/delivery limits, storage conflict counters, claim waits, budget yields, cancellation, request timeout/progress reserve; content-free logging still emitted from actual activity. |
| Maintenance | Dry-run no writes, child-first cleanup, independent memory retained, unfinished work retained, current row keys, dedup retention, environment selection. |
| Wiring/structure | Required dependencies resolve, actual assemblies are tested, forbidden references absent, smoke builds, function metadata generated, script and publish paths correct. |
| Configuration | Host/smoke share precedence and parsing; explicit environment selection; scripts work outside the repository working directory; runtime JSON is copied/published from FunctionApp; deployment metadata stays outside the package; Bicep/platform settings use canonical values; smoke does not require unrelated Telegram/storage credentials. |

Remove tests exclusively asserting legacy HTML, old payload continuation, obsolete configuration warnings, old answer banners/truncation, or pre-upgrade replay. Rewrite tests that mix these with valid business guarantees so useful coverage survives. Do not replace transaction/race tests with tests that only check class names or folder locations.

Separate operator checks after cutover: create/list/delete through Telegram; two recurrences showing previous-answer context; current-information query with search/inline links and no reasoning leakage; restart between message parts and after final acknowledgement; long rich input/output and fallback; cloud idle/resume and development/production isolation. The offline execution report must leave these visibly unverified until actually performed.

Definition of done:

- [ ] Three production projects with the specified boundaries; commands live in features.
- [ ] No phase-named production implementation or parallel historical execution path.
- [ ] No compatibility readers, old configuration aliases, HTML answer composition or substring truncation.
- [ ] Current business features and recovery invariants verified by meaningful tests.
- [ ] Host, tools, scripts, workflows, infrastructure settings and tests all use the consolidated contracts.
- [ ] Runtime configuration belongs to FunctionApp; deployment metadata belongs to `infra/environments`; all consumers use canonical files with no root-profile compatibility path.
- [ ] Current structured docs are complete; historical specs are byte-identical; backlog ideas are retained.
- [ ] Clean-state cutover and failure handling are concrete and reviewable without cloud mutations during implementation.
- [ ] Handoff states changed behavior, decisions/deviations, exact check results, remaining live checks and cutover consequences.
