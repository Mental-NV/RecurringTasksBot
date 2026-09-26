# Setup and operation guide

Two bots and two storage accounts. Development and production run
simultaneously without sharing schedules or webhooks.

| | Development | Production |
| --- | --- | --- |
| Compute | Local macOS Functions host | Windows Consumption Function App |
| Bot | Dev bot token in env | Prod token in GitHub Environment `production` |
| Storage | Dev account, direct HTTPS | Prod account (business + Durable + deployment) |
| Business table | `RecurringTasks` (dev) | `RecurringTasks` (prod) |
| Task hub | `RecurringTasksDev` | `RecurringTasksProd` (never rename) |
| Webhook | HTTPS tunnel to local host | `https://func-recurringtasks-prod.azurewebsites.net/api/webhook` |

The app exposes one anonymous HTTP trigger at route `api/webhook`; the
`X-Telegram-Bot-Api-Secret-Token` header is mandatory.

## Prerequisites

- .NET 10 SDK, Azure Functions Core Tools (`func`), Azure CLI (`az login`).
- Two Telegram bots (BotFather) and two Standard GPV2 storage accounts.
- Fill non-secret IDs in `appsettings.Development.json` /
  `appsettings.Production.json` (bot IDs, storage names, Azure IDs).
- GitHub: Environment `production` holding the secrets and variables
  below (repo Settings → Environments → `production`); OIDC federation
  trust configured. No client secret anywhere.

### GitHub Environment `production`: secrets

| Secret | Value to store |
| --- | --- |
| `RecurringTasksBot__Telegram__BotToken` | Production bot token from BotFather |
| `RecurringTasksBot__Telegram__WebhookSecret` | Production-only random secret, 32–64 chars (`A–Z a–z 0–9 _ -`), different from dev |
| `RecurringTasksBot__AzureWebJobsStorage` | `prodrecurringtasksbot` connection string |

### GitHub Environment `production`: variables

| Variable | Value |
| --- | --- |
| `AZURE_CLIENT_ID` | OIDC app registration client ID (also in `OidcClientId`) |
| `AZURE_TENANT_ID` | Azure tenant ID |
| `AZURE_SUBSCRIPTION_ID` | Azure subscription ID |
| `AZURE_RESOURCE_GROUP` | `rg-recurringtasksbot` |
| `FUNCTION_APP_NAME` | `func-recurringtasks-prod` |
| `PRODUCTION_WEBHOOK_URL` | `https://func-recurringtasks-prod.azurewebsites.net/api/webhook` |

`PRODUCTION_WEBHOOK_URL` is constructed from `FUNCTION_APP_NAME`, and
that name is global DNS (`<name>.azurewebsites.net`), so check it is free
before the first deployment:

```sh
az rest --method post \
  --url "https://management.azure.com/subscriptions/<subscription-id>/providers/Microsoft.Web/checkNameAvailability?api-version=2023-12-01" \
  --body '{"name":"func-recurringtasks-prod","type":"Microsoft.Web/sites"}'
```

It must return `"nameAvailable": true`. If taken, pick a new name and
update it in `FUNCTION_APP_NAME`, `PRODUCTION_WEBHOOK_URL`,
`appsettings.Production.json`, and `infra/main.parameters.json`
(`WEBSITE_CONTENTSHARE` follows the app name automatically).

## Local development

Prerequisites (macOS):

```sh
brew tap azure/functions
brew install azure-functions-core-tools@4   # provides `func`
brew install cloudflare/cloudflare/cloudflared  # for the tunnel option
```

`./scripts/launch-local.sh` checks for `func` and stops with install
instructions if it is missing.

Telegram cannot reach `localhost`, so expose the local Functions host
(default `http://localhost:7071`) through an HTTPS tunnel. Use
Cloudflare's quick tunnel: it needs no account and no additional
application secret.

Run in separate terminals:

```sh
export RecurringTasksBot__Telegram__BotToken="<dev-bot-token>"
export RecurringTasksBot__Telegram__WebhookSecret="<dev-secret>"
export RecurringTasksBot__AzureWebJobsStorage="<dev-connection-string>"
./scripts/launch-local.sh --tunnel-url https://<tunnel-host>
./scripts/register-webhook-dev.sh https://<tunnel-host>
```

Where `https://<tunnel-host>` comes from:

```sh
cloudflared tunnel --url http://localhost:7071
```

Use the printed `https://....trycloudflare.com` URL as `<tunnel-host>`.
If the host is already running without `--tunnel-url`, start the tunnel
first and then run only `./scripts/register-webhook-dev.sh` with its URL;
re-run launch with `--tunnel-url` to record the URL in the dev profile.
Free tunnel URLs change on every restart, so re-register the webhook
each time. Register tunnel URLs only on the development bot.

The launch script selects the Development profile, validates the storage
account name against the connection string and the token against the
expected dev bot ID, and refuses to start on mismatch. It never touches
production. Secrets are validated by presence only, never printed.

### Alternative: local polling, no tunnel

If the tunnel doesn't work in your network (e.g. VPN breaks DNS), use
`scripts/poll-dev.sh` instead of `register-webhook-dev.sh`. It long-polls
`getUpdates` and forwards each update to the local host, exercising the
exact same webhook code path (`POST /api/webhook` with the secret
header). No tunnel, no inbound connections, no firewall changes.

```sh
./scripts/launch-local.sh
./scripts/poll-dev.sh            # --port 7072 if the host runs elsewhere
./scripts/poll-dev.sh --timeout 20   # if your VPN resets long-held connections
```

The poller verifies the token belongs to the development bot, deletes
any webhook on it (Telegram won't queue `getUpdates` while a webhook is
set), and resumes from `/tmp/recurringtasksbot-poll-offset` on restart.
Use `--drop-pending` to discard updates queued while you were away.
Production is unaffected: it always uses webhooks via the deploy
workflow.

## Production deployment

Push to `main`: the deploy workflow builds, applies `infra/main.bicep`
onto the existing production storage account (Windows Consumption, no
Always On, no scan jobs), deploys the zip, and registers the production
webhook. Replay compatibility is preserved: the hub name stays
`RecurringTasksProd` and orchestration signatures are unchanged.

## Operators

```sh
export AZURE_STORAGE_CONNECTION_STRING="<selected-env-connection-string>"
./scripts/operator-recover.sh recover [--stale-minutes 15] [--apply]
./scripts/operator-recover.sh restart <operation-id> [--apply]
./scripts/operator-cleanup.sh --receipts-older-than-days 30 \
  --deleted-ops-older-than-days 90 --purge-orchestration-history-days 90 \
  --keep-dedup-days 7 [--apply]
```

Recovery uses storage credentials only. `recover` reclaims stranded
`starting` operations; `restart` tombstones a `failed` operation (after
its cause is resolved) so the original `/create` can safely reschedule.
Cleanup is dry-run by default; `update_` receipts inside the dedup window
are always kept. Receipt cleanup is occurrence-aware: rows are grouped by
owner/operation/occurrence and the parent receipt is inspected, so only
terminal (`sent`/`failed`) occurrences older than the cutoff lose their
receipt and child artifacts (children deleted before the parent receipt).
Unfinished occurrences are preserved regardless of age, as are `memory_`
rows. Orphan artifacts (no parent receipt and no live operation) are
deleted only after a 24-hour grace period. Deleted-operation cleanup
removes memory and artifacts first and the operation row last, rechecking
the tombstone before destructive batches so interrupted runs retry safely.

## Retention defaults

Old receipts 30d, deleted operations 90d, terminal orchestration history
90d, retry-dedup guard 7d. Tune via the cleanup flags.

## Phase 2: LLM execution

Each scheduled occurrence runs the stored prompt through DeepSeek V4.1
Flash on OpenRouter (`deepseek/deepseek-v4.1-flash`) with OpenRouter Web
Search (Exa engine, OpenRouter-managed search credentials) and sends the
answer as a rich Telegram message. Prompts are sent to an external
LLM/search service; only the prompt text and scheduling context leave the
system — never credentials, other users' data, or internal data.

### Credentials

One new secret, same name in both environments with different values:

| Secret | Development | Production |
| --- | --- | --- |
| `RecurringTasksBot__Llm__ApiKey` | Export the OpenRouter key in the local launch environment. | Store as a GitHub Environment `production` secret; the deploy workflow maps it to the Function App settings. |

The three phase-one secrets are unchanged. No separate DeepSeek or
search key exists. Local launch validates the key by presence only and
never prints it:

```sh
export RecurringTasksBot__Llm__ApiKey="<openrouter-key>"
export RecurringTasksBot__Telegram__BotToken="<dev-bot-token>"
export RecurringTasksBot__Telegram__WebhookSecret="<dev-secret>"
export RecurringTasksBot__AzureWebJobsStorage="<dev-connection-string>"
./scripts/launch-local.sh --tunnel-url https://<tunnel-host>
```

Non-secret LLM settings live under `RecurringTasksBot:Llm` in
`appsettings.json` (provider, model, maximum reasoning effort, 480s
request timeout, 131,072-token completion budget, 2 generation retries,
search limits). Phase 3 adds `Memory:Mode` (`PreviousSuccessfulReply`
or `None`) and, under `RecurringTasksBot:Llm`, `TargetAnswerTextChars`
(24,000), `MaxAnswerSourceChars` (131,072), `DeclaredContextTokens`
(1,048,576), `SearchContextReserveTokens` (65,536), and
`ContextEnvelopeReserveTokens` (8,192). The host loads
`appsettings.json`, then the
environment profile (`appsettings.Development.json` locally,
`appsettings.Production.json` in Azure), then environment variables —
so editing and redeploying the JSON changes behavior. Bicep sets only
the LLM API key; it no longer installs non-secret LLM overrides. Remove
any manually configured `RecurringTasksBot__Llm__*` overrides (except
`ApiKey`) if you want the published profiles to control those values. The Functions
timeout (`host.json`, 10 minutes on Consumption) exceeds one 480s
generation attempt plus persistence.

`Llm:MaxStoredAnswerChars` was removed from shipped configuration: new
answers are never substring-truncated (an oversized answer fails visibly
instead). An old environment override for it is deprecated and ignored;
startup logs a value-free deprecation notice when one is detected. Unknown
`Memory:Mode` values fail startup.

The shared search budget is 8 calls per generation attempt, with up to 5
results per call and 40 results total. `MaxSearches` controls both the tool's
`max_uses` and the request's `max_tool_calls`; `MaxTotalResults` controls the
result budget and retained citations. Increasing only the call count can
still leave later searches with no result allowance. These are upper bounds,
not required usage; more research may increase cost and latency. Restart the
local host after editing settings, or redeploy in production. Existing tasks
use the new budget on future generation attempts without being recreated.

The adapter uses SSE transport so OpenRouter can send keep-alive events
during reasoning/search. It collects the complete answer before delivery;
a disconnected or incomplete stream is retried, never sent as a partial
answer. The 480-second deadline covers connection and the entire body read,
and keep-alives do not extend it. Connection failures can occur earlier and
are recorded separately from request timeouts, with safe transport/socket
categories and elapsed time. Provider errors inside HTTP 200 responses are
also classified as failures. Maximum reasoning remains enabled.

### Limits

Prompts and answers accept up to 32,768 characters (Unicode scalar
count, not bytes or UTF-16 units). Reply to a long message with
`/create <schedule>` to use it as the prompt. New (schema 3) answers are
delivered natively: AI heading/list/table/code/link/math/details fixtures
arrive unchanged in `rich_message.markdown`, without wrappers or appended
sources. An oversized answer fails visibly without truncation. Long
prompts/answers are segmented across bounded table entities (16,000
scalars per property) and reassembled on read with hash verification;
generated content follows the receipt retention policy. Legacy HTML
receipts keep their original transport, IDs, and progress; only a
definitely rejected, unsent legacy part is converted to literal text, and
legacy deliveries never publish Phase 3 memory.

Each executing activity owns a unique, conditional Table lease, renewed
every minute and released when the attempt finishes. A crashed worker’s
lease expires after 15 minutes. Waiting for an owner does not consume
generation/delivery retries. Receipt writes verify ownership; generated
payloads use separate versions so stale workers cannot overwrite a newer
answer. Already-started external requests can still finish after lease loss.

### Provider replacement

Changing the OpenRouter model requires only editing
`RecurringTasksBot:Llm:Model` in the appropriate `appsettings` profile
and redeploying. It applies to new generation attempts;
already-persisted results are delivered unchanged.
Changing the API service (e.g. direct Alibaba/Qwen) requires changing
provider/base URL/model/options, replacing the same API-key value, and
adding an adapter next to `OpenRouterLlmExecutor` — scheduling,
storage, and Telegram behavior do not change. Configuration is
validated at startup and fails clearly; models are never silently
switched and search is never silently disabled.

### Rollout of existing operations

Existing operations keep their IDs and schedules; their stored text
becomes the prompt for future occurrences. Completed occurrences are
not rerun, orchestration activity names and state shapes are unchanged
(task hub stays `RecurringTasksProd`), and pre-upgrade histories replay
safely. On terminal generation failure the bot sends a short notice
identifying the operation and occurrence; future runs stay scheduled,
and provider errors or credentials are never exposed.

Deploy Phase 3 as a coordinated worker upgrade: drain/stop old workers
before new workers can write schema 3. Old binaries do not understand
new plans, so ordinary binary rollback after schema-3 writes is unsafe;
rollback must pause affected work and use a compatibility-capable build.

### Phase 3: recurring execution

Each occurrence runs one ritual in order inside the existing activities:
initialize the frozen context (effective system instruction plus, when
enabled, the one archived previous successful reply), generate the answer
with a single one-request LLM call, persist the answer and its delivery
plan atomically, execute the plan leaf by leaf over the typed Telegram
sender (structured rejections drive deterministic fallbacks: Markdown
rejection to literal rich, literal-size rejection to conservative chunks,
unavailable method to plain), then publish completion and record the new
previous reply. Generation retries (2) stay separate from delivery
retries; long waits persist as Durable timers and the activity yields
before the work budget expires.

Content is segmented at every layer: plan leaves (at most 128), OpenRouter
context messages, and table properties/entities/transactions, so
worst-case Unicode fits storage limits. Memory rows persist while their
operation exists, including failed runs, and are removed only with
deleted-operation cleanup — never by receipt retention or age. Every
reply build emits a content-free canary log (`replySource=llm-authored`,
`literal`, or `failure-notice-literal` with provider, model, and receipt
schema) so the dashboard can answer whether delivered text was
LLM-authored or a literal fallback. Health and configuration failures
remain visible through existing logs; captured logs never contain
prompts, answers, history, provider bodies, or tokens.

### Development smoke test

`scripts/smoke-llm-dev.sh` runs one bounded live request (DeepSeek V4.1
Flash, maximum reasoning, Exa web search) using the development
`RecurringTasksBot__Llm__ApiKey`. It verifies a current-information
answer with source links, that reasoning is excluded from the response,
and that the answer composes into valid rich-message parts. It runs the same
.NET adapter as the bot, with the common and Development JSON profiles.
To reproduce a particular prompt, save it in a UTF-8 file and run
`SMOKE_PROMPT_FILE=/absolute/path/prompt.txt scripts/smoke-llm-dev.sh` in a
shell with the development LLM key exported. It sends no Telegram messages
and prints only diagnostics/usage, not the prompt or answer. Ordinary CI
uses no live credentials or paid calls.
