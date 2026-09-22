# Recurring Tasks Bot — MVP Specification

## Purpose and stack

A Telegram bot that lets each user create, list, and delete recurring text notifications.

- C#, .NET 10 isolated Azure Functions, ASP.NET Core HTTP integration.
- One production Windows Function App on the classic Consumption plan.
- Durable Functions with the Azure Storage provider for scheduling.
- Separate development and production Azure Storage accounts for business data in Azure Tables and Durable state; production storage also holds deployment data.
- Separate development and production Telegram bots.
- GitHub Actions for CI/CD; macOS for local development, connected directly to development Azure resources.

## User behavior

Support private Telegram chats. Identify users by numeric Telegram user ID and enforce ownership on every operation.

| Command | Behavior |
| --- | --- |
| `/create <six NCRONTAB fields> <text>` | Validate, persist, and schedule an operation. Return its ID and `starting` or `active` status. |
| `/list` | List the caller's undeleted operations: ID, schedule, text, and status. Automatically split the output into plain-text messages of at most 4,096 characters, keeping each operation together where possible. |
| `/delete <id>` | Mark the caller's operation deleted and terminate its orchestration. Repeated deletion is harmless. |

Example: `/create 0 0 9 * * * Water the plants`.

- Interpret schedules in UTC and explain this in command help. Require six fields with seconds fixed to `0`. Reject invalid expressions and expressions without a future occurrence.
- Accept 1–2,000 characters of plain text. Preserve spaces and line breaks after the schedule fields.
- Send `Hi!`, a newline, and the supplied text without Telegram markup parsing.
- Activate an operation when its orchestration initializes; its first occurrence must be strictly after activation.
- Editing, group chats, custom time zones, attachments, and a web UI are outside the MVP.

## Execution and reliability

The HTTP webhook validates `X-Telegram-Bot-Api-Secret-Token`, processes commands, and uses the Durable client to start or terminate orchestrations. Use anonymous Functions HTTP authorization with mandatory webhook-secret validation.

HTTP acknowledgments and Telegram reply messages are separate. Return HTTP 200 for successfully processed updates, completed duplicates, invalid/unknown commands, and unsupported update types; send help for invalid commands where applicable. Return HTTP 503 for transient processing failures so Telegram retries, and HTTP 403 for an invalid webhook secret. A failed confirmation reply must not repeat an already-completed command; update receipts track command completion separately from reply delivery.

Each operation has one durable orchestration. It calculates the next UTC NCRONTAB occurrence, awaits a durable timer, and invokes a delivery activity. After delivery, advance to the next future occurrence and use `ContinueAsNew` to bound history while retaining the instance ID and scheduling state. Keep orchestration code deterministic and use its context clock. Perform Table Storage and Telegram I/O in activities or webhook handlers.

Before sending, the activity reads the operation by owner and ID and stops recurrence if it is deleted or failed. Retry transient network/server failures at most three times after the initial attempt, with increasing delays; honor Telegram's `retry_after` for rate limiting and persist long retry waits through Durable scheduling. After transient retries are exhausted, record the failed occurrence, keep the operation active, and continue future occurrences. A permanent recipient failure, such as the user blocking the bot or an unavailable chat, marks the operation failed and stops recurrence. Unexpected orchestration failures also appear as failed in `/list`: reconcile with Durable instance status on demand and retain a short error summary. Failure in one occurrence is distinct from failure of the entire operation.

After downtime, send at most one late notification and skip the remaining missed occurrences. Cold-start delays are acceptable.

Creation must recover from interruption between the business-data write and orchestration startup. Derive stable operation/instance IDs from the Telegram update, handle concurrent duplicate updates, and acknowledge success only after startup is durably accepted. Webhook redelivery must resume unfinished work without starting another recurrence. Provide a small operator CLI script to recover stranded starts or explicitly restart failed operations after their cause is resolved. Repeated webhook updates must not restart failed or deleted operations.

Persist deletion before termination. Initialization and delivery must respect deletion during concurrent creation, retries, and sending. An already-started send may finish. Suppress repeated deliveries using `(operationId, scheduledUtc)`; a crash after Telegram accepts a message can still cause a duplicate.

## Business data

Use `Azure.Data.Tables` and one application-owned table named `RecurringTasks`, separate from the tables managed by Durable Functions. Use the Telegram user ID as the string `PartitionKey` and these `RowKey` patterns:

| Entity | RowKey | Data |
| --- | --- | --- |
| Operation | `operation_<id>` | Chat ID, NCRONTAB expression, text, status, orchestration instance ID, last failure summary, UTC creation/update timestamps. |
| Update receipt | `update_<updateId>` | Command, operation ID, command progress, reply-delivery status, UTC timestamps. |
| Delivery receipt | `delivery_<operationId>_<scheduledUtcTicks>` | Scheduled time, delivery status, attempts, error summary, Telegram message ID when available. |

Operation states are `starting`, `active`, `failed`, and `deleted`. Use ETags and conditional writes for concurrency; use same-partition transactions where business entities must change atomically. Query `/list` within the caller's partition and operation-key range. Provide a small operator cleanup CLI script with explicit retention settings for old receipts, deleted operations, and terminal orchestration history; retain records needed for retry deduplication. Recovery and cleanup scripts use the selected environment's existing credentials and require no admin HTTP endpoints or extra bot commands.

## Required secrets before implementation

**Supply two sets of the three values below before the implementation agent begins: six secret values in total.** Create two bots and two Standard general-purpose v2 Azure Storage accounts with Blob, Queue, Table, and File services available. The variable names are identical in both environments; their values must differ.

| Environment variable | Value to supply | Development: local macOS | Production: GitHub Environment `production` |
| --- | --- | --- | --- |
| `Telegram__BotToken` | Bot token issued by BotFather. | Export the development bot token in the launch environment. | Store the production bot token as an Environment secret; map to this variable in deployment steps. |
| `Telegram__WebhookSecret` | Random secret of 32–64 characters using letters, digits, `_`, or `-`. | Export a development-only value. | Store a separate production value as an Environment secret; map to this variable. |
| `AzureWebJobsStorage` | Full account-key connection string. | Export the development storage connection string. | Store the production storage connection string as an Environment secret; map to this variable. |

In each environment, the application uses `AzureWebJobsStorage` for both business Table Storage access and the Durable/Functions backend. Deployment copies only the production values into Function App settings and reuses the production storage connection string for `WEBSITE_CONTENTAZUREFILECONNECTIONSTRING`. No separately supplied Azure Files credential is needed.

GitHub deploys through Azure OIDC federation scoped to the `production` Environment. Configure the GitHub-to-Azure trust and production deployment permissions before the first deployment; local Azure administration uses `az login`. Neither requires an Azure client-secret environment variable. Never commit or log secret values, connection strings, or Telegram API URLs containing the bot token. Validate required variables by presence without printing their values.

## Configuration and local execution

Keep common non-secret settings in `appsettings.json` and environment-specific values in `appsettings.Development.json` and `appsettings.Production.json`: Azure subscription and tenant IDs, OIDC client ID, region, resource group, storage account name, Function App name, expected Telegram bot ID, webhook URL, table name, task-hub name, limits, and retention settings. Keep Functions/Durable host configuration in `host.json`. Startup/deployment scripts select the appropriate profile and generate required non-secret platform app settings from these files; users supply only secrets through environment variables.

| Resource | Development | Production |
| --- | --- | --- |
| Compute | Local macOS Functions host. | Windows Consumption Function App. |
| Telegram | Dedicated development bot. | Dedicated production bot. |
| Azure resource group | Development group containing development storage. | Production group containing the Function App and production storage. |
| Storage | Development account, accessed directly over HTTPS. | Separate production account. |
| Business table | `RecurringTasks` in development storage. | `RecurringTasks` in production storage. |
| Durable task hub | `RecurringTasksDev`. | `RecurringTasksProd`. |
| Webhook | HTTPS tunnel to the local host. | Deployed Function App HTTPS endpoint. |

Both environments may run simultaneously. Development reminders execute only while the local host is running; persisted work resumes when it restarts. Integration tests use development resources and designated test operations, with one local host/test runner at a time. Unit tests use mocks. Validate the configured storage-account name against the supplied connection string before starting the host, and verify the bot token against the expected bot ID before registering a webhook. Development scripts must never stop or reconfigure production.

For local Telegram testing, expose the local HTTP endpoint through an HTTPS tunnel and register its URL only with the development bot. Use a tunnel that requires no additional application secret. The local launch script accepts its non-secret URL and updates the development profile. Production webhook registration belongs to the production deployment workflow.

## Delivery and acceptance

Deliver application code, Bicep deployment using the existing production storage account, GitHub Actions, local launch/webhook tooling, recovery/cleanup CLI scripts, and a short setup/operation guide. Pull requests build and run unit tests without secrets. The main-branch workflow uses GitHub Environment `production` to deploy the app and register the production webhook. Run integration tests explicitly against development resources. Preserve production data, task-hub identity, and orchestration replay compatibility across deployments.

Verify command validation and HTTP acknowledgments, user isolation, list-message splitting, NCRONTAB calculation, duplicate/concurrent updates, interrupted creation, deletion/start/send races, transient retry exhaustion, permanent recipient failures, failure-status reporting, missed occurrences, and bounded history. Demonstrate delivery after cloud scale-to-zero and local restart. Verify development and production can run simultaneously without consuming each other's schedules or changing each other's bot webhooks.

Do not enable Always On or run periodic application jobs to scan for due operations. Durable waits suspend execution. Charges include active function execution, orchestration replays, storage capacity/transactions, internal queue polling, and logs.
