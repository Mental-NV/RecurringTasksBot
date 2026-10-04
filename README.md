# RecurringTasksBot

A Telegram bot that runs your recurring prompts on a schedule. You create a
task once with `/create`; the bot executes the stored prompt fresh on every
occurrence with an LLM web search and sends the answer back to the same chat.

Supported commands (all UTC):

- `/create <sec min hour day month weekday> <prompt>` — schedule a recurring
  prompt (seconds must be `0`). Reply to a long message with
  `/create <schedule>` to use it as the prompt (up to 32,768 characters).
- `/list` — show your prompts.
- `/delete <id>` — delete a prompt.

Quick start (local, development bot only):

```sh
export RecurringTasksBot__Telegram__BotToken="<dev-bot-token>"
export RecurringTasksBot__Telegram__WebhookSecret="<dev-secret>"
export RecurringTasksBot__AzureWebJobsStorage="<dev-connection-string>"
export RecurringTasksBot__Llm__OpenRouter__ApiKey="<openrouter-key>"
export RecurringTasksBot__Llm__ActiveProfile="DeepSeek"
./scripts/launch-local.sh
./scripts/poll-dev.sh
```

See [docs/Setup.md](docs/Setup.md) for prerequisites, tunnel setup, and
verification.

Project map:

- `src/RecurringTasksBot.Application/` — scheduling, commands, generation
  and delivery logic. No Azure or Telegram SDKs.
- `src/RecurringTasksBot.Infrastructure/` — Telegram transport, LLM
  adapters (OpenRouter, direct DeepSeek; one selected per host), Azure
  Tables persistence, configuration loading.
- `src/RecurringTasksBot.FunctionApp/` — Functions host: webhook endpoint,
  Durable orchestration, DI composition root.
- `tools/LlmSmoke/` — bounded live LLM smoke test (opt-in, uses a paid key).
- `scripts/` — local launch, polling, webhook, smoke, and operator scripts.
- `infra/` — Bicep deployment plus per-environment metadata.
- `tests/RecurringTasksBot.Tests/` — unit tests against production
  assemblies. `tests/scripts/` — no-secret script tests.

Docs: [docs/README.md](docs/README.md) (navigation),
[docs/Architecture.md](docs/Architecture.md),
[docs/Deployment.md](docs/Deployment.md),
[docs/Runbook.md](docs/Runbook.md).
History: [docs/History/README.md](docs/History/README.md).
