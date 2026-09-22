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
| Webhook | HTTPS tunnel to local host | Deployed app `https://<app>.azurewebsites.net/api/webhook` |

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
| `PRODUCTION_WEBHOOK_URL` | `https://<FUNCTION_APP_NAME>.azurewebsites.net/api/webhook`, i.e. `https://func-recurringtasks-prod.azurewebsites.net/api/webhook` |

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

Telegram cannot reach `localhost`, so expose the local Functions host
(default `http://localhost:7071`) through an HTTPS tunnel. Use
Cloudflare's quick tunnel: it needs no account and no additional
application secret.

```sh
brew install cloudflare/cloudflare/cloudflared
```

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
are always kept.

## Retention defaults

Old receipts 30d, deleted operations 90d, terminal orchestration history
90d, retry-dedup guard 7d. Tune via the cleanup flags.
