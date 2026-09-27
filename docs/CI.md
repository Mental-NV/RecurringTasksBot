# CI

## Exact offline commands

```sh
dotnet restore RecurringTasksBot.sln
dotnet build RecurringTasksBot.sln --configuration Release --no-restore
dotnet test RecurringTasksBot.sln --configuration Release --no-build
bash tests/scripts/test-launch-local.sh
bash tests/scripts/test-deploy-config.sh
dotnet build tools/LlmSmoke/LlmSmoke.csproj --configuration Release
dotnet publish src/RecurringTasksBot.FunctionApp/RecurringTasksBot.FunctionApp.csproj \
  --configuration Release --no-restore --output /tmp/recurringtasksbot-phase4-publish
```

## Test organization

- `tests/RecurringTasksBot.Tests/` — unit tests named by responsibility
  (e.g. `OccurrenceTransactionTests`, `TelegramTransportTests`,
  `ProjectBoundaryTests`). They run against the production assemblies,
  use fakes for storage/transport/LLM, and `TimeProvider` for time.
- `tests/scripts/` — no-secret shell gates with PATH shims (`curl`,
  `func`, `az`): launch prerequisite handling, deployment configuration
  (workflow shape, parameter resolution, archive contents), and operator
  target/credential guards
  (workflow triggers, name consistency, hub resolution, operator
  targets).

## Fake vs live coverage

Fakes cover redelivery, leases, retries, codecs, chunking, and error
mapping. Live coverage is opt-in only: `scripts/smoke-llm-dev.sh`
(bounded paid LLM call) and the manual local/production checks in
[Setup](Setup.md) and [Deployment](Deployment.md). Mocked behavior is
not presented as live verification.

## Gates

- Pull requests (`pr.yml`): Release build, full suite, both script
  tests.
- Production deployment (`deploy-production.yml`): manual dispatch;
  offline gates re-run before any cloud change.
