# Setup

## Prerequisites

- .NET 10 SDK, Azure Functions Core Tools (`func`), Azure CLI.
- Two Telegram bots (BotFather) and two Standard GPV2 storage accounts.
- Non-secret dev IDs filled in `appsettings.Development.json` (bot ID,
  storage name). Production values live in `infra/main.parameters.json`.
- Secrets per environment (see [Configuration](Configuration.md)):
  storage connection string, bot token, webhook secret, OpenRouter key,
  plus the direct DeepSeek key once the DeepSeek profile is selected.

macOS tooling:

```sh
brew tap azure/functions
brew install azure-functions-core-tools@4   # provides `func`
brew install cloudflare/cloudflare/cloudflared  # for the tunnel option
```

`./scripts/launch-local.sh` stops with install instructions if `func`
is missing.

## Local launch (development bot only, never production)

```sh
export RecurringTasksBot__Telegram__BotToken="<dev-bot-token>"
export RecurringTasksBot__Telegram__WebhookSecret="<dev-secret>"
export RecurringTasksBot__AzureWebJobsStorage="<dev-connection-string>"
export RecurringTasksBot__Llm__OpenRouter__ApiKey="<openrouter-key>"
export RecurringTasksBot__Llm__ActiveProfile="DeepSeek"
./scripts/launch-local.sh [--tunnel-url https://<tunnel-host>]
```

The script validates the storage account name against the connection
string, the token against the expected dev bot ID, and the platform hub
against the dev profile and `infra/environments/development.json`,
refusing to start on mismatch. The selected profile's LLM credential
is validated by the shared .NET resolver at host startup (only the
selected profile's key is required); the launcher performs no
fixed-provider key check. Select `DeepSeek` with
`RecurringTasksBot__Llm__ActiveProfile=DeepSeek` plus
`RecurringTasksBot__Llm__DeepSeek__ApiKey` once its key is provisioned.

## Receiving updates: tunnel or polling

Telegram cannot reach `localhost`. Either expose the host (default
`http://localhost:7071`) through an HTTPS tunnel:

```sh
cloudflared tunnel --url http://localhost:7071
./scripts/launch-local.sh --tunnel-url https://....trycloudflare.com
./scripts/register-webhook-dev.sh https://....trycloudflare.com
```

Free tunnel URLs change on every restart, so re-register each time, on
the development bot only. Or skip the tunnel with local polling, which
exercises the exact same webhook code path:

```sh
./scripts/launch-local.sh
./scripts/poll-dev.sh            # --port 7072 if the host runs elsewhere
./scripts/poll-dev.sh --timeout 20   # if a VPN resets long-held connections
```

The poller refuses the production token, deletes any webhook on the dev
bot, resumes from `/tmp/recurringtasksbot-poll-offset`, and supports
`--drop-pending`.

## Verification and shutdown

Create a task (`/create 0 * * * * * ping`), confirm the answer arrives,
then `/delete <id>`. Stop the host with Ctrl+C. `tests/scripts/`
holds the no-secret script gates; see [CI](CI.md).
