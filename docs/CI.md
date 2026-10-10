# CI

## Exact offline commands

```sh
dotnet restore RecurringTasksBot.sln
dotnet build RecurringTasksBot.sln --configuration Release --no-restore
dotnet test RecurringTasksBot.sln --configuration Release --no-build
bash tests/scripts/test-launch-local.sh
bash tests/scripts/test-deploy-config.sh
bash tests/scripts/test-monitoring.sh
az config set bicep.use_binary_from_path=false --output none
az bicep install --version "$(cat infra/bicep-version)"
az bicep build --file infra/main.bicep --outfile /tmp/recurringtasksbot-infra.json
python3 tests/scripts/test-monitoring-template.py /tmp/recurringtasksbot-infra.json
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
- `test-monitoring.sh` runs the post-deployment CLI step against a strict `az`
  shim: repeatable updates, mismatched/missing caps, CLI failures, and credential
  redaction. It also checks the provider preflight: all required namespaces,
  missing registrations, regional registration in progress, and CLI failures.
  It does not call Azure. Bicep compilation validates the resource
  definitions without deploying them.
- `test-monitoring-template.py` inspects compiled ARM: the Function App must wait
  for the module output, the connection string must be secure, and verbose scale
  logs must stay off. It catches the first-deployment resource lookup race, which
  plain Bicep compilation does not detect. The compiler version is pinned in
  `infra/bicep-version` for both gates and the actual deployment.

## Fake vs live coverage

Fakes cover redelivery, leases, retries, codecs, chunking, and error
mapping. Live coverage is opt-in only: `scripts/smoke-llm-dev.sh`
(bounded paid LLM call) and the manual local/production checks in
[Setup](Setup.md) and [Deployment](Deployment.md). Mocked behavior is
not presented as live verification.

## Gates

- Pull requests (`pr.yml`): Release build, full suite, three script
  tests, and Bicep compilation.
- Production deployment (`deploy-production.yml`): pushes to `master` excluding
  docs-only changes, or manual dispatch; offline gates re-run before any cloud
  change. Cap verification after infrastructure deployment must pass before the
  app package is deployed.
