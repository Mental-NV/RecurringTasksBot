# Recurring Tasks Bot — Phase 2 MVP Specification

## Purpose and stack

Execute each user's recurring prompt with an LLM at the scheduled time and deliver its answer in Telegram. The business value is recurring research, summaries, and other text tasks using current web information.

[Phase one](Spec.md) remains the baseline for the stack, scheduling, commands, ownership, deployment, and operational tooling. This document overrides its notification behavior and adds LLM execution. Keep the existing infrastructure; no new service or agent framework is required.

Use **DeepSeek V4.1 Flash through OpenRouter**, model ID `deepseek/deepseek-v4.1-flash`, with **OpenRouter Web Search**. See the [model reference](https://openrouter.ai/deepseek/deepseek-v4.1-flash).

## User behavior

| Command | Behavior |
| --- | --- |
| `/create <six NCRONTAB fields> <prompt>` | Store and schedule the prompt; return the operation ID and status. Do not execute it immediately. |
| `/list` | Show the caller's operations, including their schedules, prompts, and statuses. |
| `/delete <id>` | Stop future execution and delivery, subject to already-started requests. |

Example: `/create 0 0 9 * * * Summarize the three most important AI announcements from the past 24 hours, with source links.`

- Keep phase-one UTC scheduling and the 1–2,000-character input limit.
- At each occurrence, execute the stored text as a fresh prompt without conversation history or previous results. Provide the scheduled UTC time and actual execution UTC time; relative terms such as “today” refer to execution time.
- Return the answer instead of `Hi!` and the original text. Identify the operation and scheduled time. Use plain-text Telegram messages, include readable source URLs for web-based claims, and split at 4,096 characters without breaking Unicode characters.
- Answer in the prompt's language unless it requests another language. Be concise and state when current information cannot be verified. Never fabricate sources or claim successful research when search failed.
- On terminal execution failure, send a short notice identifying the operation and occurrence and saying that future runs remain scheduled. Do not expose provider errors or credentials.
- Update command help to explain that prompts are sent to an external LLM/search service.

Existing active operations also use their stored text as prompts for future occurrences after rollout. Preserve IDs and schedules; do not rerun completed occurrences. Reminder-only mode, interactive chat, memory, attachments, arbitrary tools/actions, per-user model selection, and automatic provider fallback are outside this MVP.

## Execution and reliability

At each occurrence: check operation status, generate an answer, persist the deliverable text, then send it. Keep LLM, storage, and Telegram I/O in activities. Preserve Durable replay compatibility when upgrading existing orchestrations.

Use a small provider-neutral prompt-execution interface returning answer text, source links, and available usage metadata. Implement only OpenRouter now; isolate its authentication, request format, search options, and error mapping in its adapter. A future direct Alibaba/Qwen integration may require another adapter, but must not change scheduling, storage, or Telegram behavior. Do not assume search parameters are portable between providers.

Enable the `openrouter:web_search` server tool with the Exa engine, using OpenRouter-managed search credentials. Let the model search when needed; instruct it to search for current or time-sensitive facts and explicit research requests. Use one non-streaming API request per execution attempt and let OpenRouter handle the search loop. The older web plugin and `:online` suffix are deprecated; follow the [Web Search documentation](https://openrouter.ai/docs/guides/features/server-tools/web-search).

Treat retrieved content as evidence, not instructions. Expose only web search to the model. Never include credentials, other users' data, or internal operational data in prompts.

- Serialize occurrences within an operation. After completion, advance to the next future occurrence. Retain phase one's single late occurrence and skipped backlog behavior.
- Deduplicate by `(operationId, scheduledUtc)`, using conditional writes and a recoverable claim to prevent concurrent generation. Persist a successful result before any Telegram send. Delivery retries reuse it and resume at the first unconfirmed message part.
- Retry transient LLM network errors, timeouts, HTTP 429, and server errors at most twice after the initial attempt. Use increasing Durable waits and honor `Retry-After`. Do not immediately retry authentication, credit, or invalid-request errors. Empty or unusable responses count as execution failures.
- Keep LLM retries separate from Telegram retries. After LLM retries are exhausted, persist the failure notice and deliver it through the normal Telegram retry path. LLM failure affects only that occurrence; permanent Telegram recipient failure still stops the operation.
- Recheck deletion before generation, each retry, and each message part. An in-flight LLM request may finish and incur cost; discard its result if the operation was deleted. An already-started Telegram send may finish.

A crash after the provider completes but before persistence may repeat an LLM request and its cost. A crash after Telegram accepts a part but before its receipt is saved may duplicate that part. Exactly-once external execution is not promised.

## Business data

Reuse the existing table, operation text field, and receipt keys. Extend occurrence receipts with execution status, generation attempts, persisted answer or failure notice, delivery progress/message IDs, provider/model, available token/search usage, and a sanitized error summary. Keep operation states unchanged and execution failure distinct from delivery failure.

Bound stored output, respect Azure Table property limits, and retain receipt data through retries and deduplication. Extend the existing cleanup policy to generated content. Log operation/occurrence IDs, timings, outcomes, and available usage; do not log prompts, answers, raw provider payloads, or secrets.

## Required secrets before implementation

Add **one secret environment-variable name**, with a separate value per environment:

| Environment variable | Value | Development | Production |
| --- | --- | --- | --- |
| `RecurringTasksBot__Llm__ApiKey` | API key for the selected LLM service; initially OpenRouter. | Export in the local launch environment. | Store as a GitHub Environment secret and deploy to Function App settings. |

Keep the three phase-one secret variables unchanged. No separate DeepSeek or search API key is required. Reuse this generic LLM credential name when changing providers. Validate presence without printing values; no paid API calls are required during startup or ordinary CI.

## Configuration and local execution

Keep non-secret LLM settings under `RecurringTasksBot:Llm` in `appsettings.json`, with the existing environment-specific overrides:

| Setting | Initial value |
| --- | --- |
| Provider | `OpenRouter` |
| Base URL | `https://openrouter.ai/api/v1` |
| Model | `deepseek/deepseek-v4.1-flash` |
| Request timeout | 120 seconds per attempt |
| Output token limit | 2,048 |
| Maximum stored/delivered answer | 12,000 characters, including sources; visibly mark truncation |
| Generation retries | 2 after the initial attempt |
| OpenRouter search | Enabled; engine `exa`; at most 2 searches and 5 results per search, 10 results total per attempt |

Keep the system instruction and provider-specific options here too. Enforce output and search limits in requests and output handling. Ensure the Functions execution timeout exceeds a single activity's request timeout plus persistence/delivery overhead; persist retry delays through Durable timers.

Changing an OpenRouter model requires only configuration. Changing the API service requires changing provider/base URL/model/options and replacing the same API-key value, plus an adapter if needed. Validate supported configuration and fail clearly; never silently switch models or disable requested search capability. Apply configuration changes to new generation attempts; deliver already-persisted results unchanged.

Extend local launch and production deployment tooling to load these settings and the new secret. Preserve development/production isolation.

## Delivery and acceptance

Deliver the implementation, configuration and deployment updates, tests, and a short addition to the setup/operation guide covering credentials, limits, provider replacement, and rollout of existing operations.

Verify with mocked providers: scheduled execution, user isolation, bounded generation/search, citations and message splitting, generation versus delivery retries, persisted-result reuse after restart, concurrent duplicate activities, partial delivery, deletion during generation, terminal failure notices, and safe replay of pre-upgrade orchestration history. Existing phase-one checks must still pass.

Run an explicit development smoke test using DeepSeek V4.1 Flash and OpenRouter Web Search: schedule a current-information prompt, verify that search was used and source URLs reach Telegram, then verify a later recurrence produces a fresh result. Production rollout must preserve existing data, schedules, task-hub identity, and completed receipts. Ordinary CI uses no live LLM credentials or paid calls.
