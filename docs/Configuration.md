# Configuration

Single inventory. Owner is the file or option type that reads the key.
`AppConfiguration` (`Infrastructure/Configuration`) owns precedence:
common `appsettings.json`, then the environment profile, then
environment variables. Secrets come only from the environment, never
from JSON. Unknown `Memory:Mode` values and malformed explicit values
fail startup; code defaults apply only when a key is absent.

## Secrets (environment only, never printed)

| Key | Purpose |
| --- | --- |
| `RecurringTasksBot:AzureWebJobsStorage` | Storage connection string (business + Durable + content) |
| `RecurringTasksBot:Telegram:BotToken` | Bot token from BotFather |
| `RecurringTasksBot:Telegram:WebhookSecret` | Webhook secret header value, 32–64 chars `A–Z a–z 0–9 _ -` |
| `RecurringTasksBot:Llm:ApiKey` | OpenRouter key for execution and search |

## Runtime profiles (`FunctionApp/appsettings*.json`)

| Key | Owner | Default / value |
| --- | --- | --- |
| `RecurringTasksBot:TableName` | `TableStorageOptions` | `RecurringTaskData` (dev profile; production canonical in the parameter template) |
| `RecurringTasksBot:TaskHubName` | Dev profile value; effective setting owned by the platform (see below) | `RecurringTasksAppDev` (production canonical in the parameter template) |
| `RecurringTasksBot:StorageAccountName` | `launch-local.sh` storage guard; operator `--env dev` credential guard | `dev…` (production canonical in the parameter template) |
| `RecurringTasksBot:ExpectedBotId` | `launch-local.sh` bot guard; dev `/poll` / webhook guards | Dev bot ID (production canonical in the parameter template) |
| `RecurringTasksBot:WebhookUrl` | Dev profile only: recorded tunnel URL | Local tunnel URL |
| `RecurringTasksBot:Memory:Mode` | `ExecutionOptions` | `PreviousSuccessfulReply` (`None` disables) |
| `RecurringTasksBot:Llm:Provider` / `BaseUrl` | `OpenRouterOptions` | `OpenRouter` / `https://openrouter.ai/api/v1` |
| `RecurringTasksBot:Llm:Model` | `OpenRouterOptions` | `deepseek/deepseek-v4.1-flash` |
| `RecurringTasksBot:Llm:ReasoningEffort` | `OpenRouterOptions` | `Maximum` |
| `RecurringTasksBot:Llm:RequestTimeoutSeconds` | `ExecutionOptions` | `480` (startup-enforced under the 510s activity budget) |
| `RecurringTasksBot:Llm:CompletionTokenBudget` | `ExecutionOptions` | `131072` |
| `RecurringTasksBot:Llm:TargetAnswerTextChars` | `ExecutionOptions` | `24000` (≤ 32768 rich-text ceiling) |
| `RecurringTasksBot:Llm:MaxAnswerSourceChars` | `ExecutionOptions` | `131072` |
| `RecurringTasksBot:Llm:DeclaredContextTokens` | `ExecutionOptions` | `1048576` |
| `RecurringTasksBot:Llm:SearchContextReserveTokens` | `ExecutionOptions` | `65536` |
| `RecurringTasksBot:Llm:ContextEnvelopeReserveTokens` | `ExecutionOptions` | `8192` |
| `RecurringTasksBot:Llm:GenerationRetries` | `ExecutionOptions` | `2` |
| `RecurringTasksBot:Llm:SearchEnabled` / `SearchEngine` / `SearchMode` | `OpenRouterOptions` | `true` / `parallel` / `fast` |
| `RecurringTasksBot:Llm:MaxSearches` / `MaxResultsPerSearch` / `MaxTotalResults` | `OpenRouterOptions` | `8` / `5` / `40` |
| `RecurringTasksBot:Llm:SystemInstruction` | `ExecutionOptions` | `""` (appended to the system template) |

There is no `appsettings.Production.json`: every production value above
lives once in `infra/main.parameters.json` and reaches the host through
Bicep-provisioned app settings.

## Web search engine and mode

Outbound search uses the OpenRouter `openrouter:web_search` server
tool with `engine: parallel`, `mode: fast` (serialized at
`tools[0].parameters`). Environment overrides are
`RecurringTasksBot__Llm__SearchEngine` and
`RecurringTasksBot__Llm__SearchMode`. Pricing assumption (verified
2026-09-27): Parallel `fast`/`turbo` cost $0.001/search covering up
to 10 results per search; additional results cost $0.001 each, and
retries can create additional billable requests. Fast-mode language
coverage is unspecified upstream, so multilingual answer quality
requires a live check after rollout. Rollback: set
`SearchEngine=exa` with `SearchMode=fast` (Exa also supports Fast
mode). Production has no JSON profile; deploy these as app settings:

```text
RecurringTasksBot__Llm__SearchEngine=parallel
RecurringTasksBot__Llm__SearchMode=fast
```

## Platform and deployment settings

| Key | Owner | Purpose |
| --- | --- | --- |
| `AzureFunctionsJobHost__extensions__durableTask__hubName` | Functions platform | Effective task hub; must equal the profile `TaskHubName` (asserted by `launch-local.sh`, `test-deploy-config.sh`, and the deploy workflow) |
| `AzureWebJobsStorage` | Functions host | Same connection string under its plain name |
| `host.json` | Functions platform | 10-minute timeout, Durable concurrency 10/20 |
| `infra/environments/development.json` | Operator scripts | Region, resource group, dev storage account; the `--env dev` credential guard reads the account from here |
| `infra/environments/production.json` | Reference only | Azure IDs, resource group, region (production values are canonical in the parameter template) |
| `infra/main.parameters.json` | Deployment (sole template) | Static values plus `__PLACEHOLDER__`s for externally supplied values; `resolve-deploy-parameters.sh` fills them from GitHub vars/secrets into a temp file passed to Bicep, which provisions the runtime app settings from it |

## Protocol constants vs settings

`TelegramLimits` (32,768 rich chars, 4,096 plain fallback, 30s transport
timeout), `ExecutionLimits` (128 plan leaves, 131,072 source scalars,
540s activity budget), and `StorageLimits` (16,000-scalar segments,
schema version 1) are code constants in `Application/Limits.cs` — never
JSON knobs.
