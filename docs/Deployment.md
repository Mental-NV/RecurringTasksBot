# Deployment

## Azure resources

Windows Consumption Function App (`func-recurringtasks-prod`) on the
existing production storage account (`prodrecurringtasksbot`), which
holds business data (`RecurringTaskDataV5`), Durable state
(`RecurringTasksV5Prod` hub), and deployment content. Bicep
(`infra/main.bicep`) never creates storage. No Always On, no scan jobs —
Durable timers only.

`infra/monitoring.bicep` provisions the dedicated Log Analytics workspace
(`law-recurringtasks-prod`, Analytics SKU, 30-day retention), workspace-based
Application Insights (`appi-recurringtasks-prod`), an email action group, and two
static queue polling alerts. The workspace cap is 0.1 GB/day. Application Insights
shares this workspace with any temporary queue captures. Resource names, the cap,
and the hourly error threshold are canonical in `infra/main.parameters.json`.

The Function App template persists the telemetry connection string and logging
settings: default Warning, execution/aggregation/function categories Information,
adaptive sampling at two items/second except requests and exceptions. It resets
scale-controller logging to `AppInsights:None` on each infrastructure deployment.
No permanent queue diagnostic setting or HTTP availability probe is created.
Use [Monitoring](Monitoring.md) to start/stop temporary captures and query logs.

The connection string is returned as a secure monitoring-module output. Consuming
that output makes the Function App wait for the module's completion. A parent-side
`existing` component lookup can run before the module creates it, even with an
explicit dependency on the Function App. Secure outputs keep the connection
string out of deployment logs/history and require Bicep 0.35.1 or newer.
The workflows install the tested compiler version pinned in `infra/bicep-version`
and deploy the compiled `main.json`; the ARM action does not recompile the source
with another compiler. See [secure outputs](https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/outputs#secure-outputs).

## OIDC and GitHub environment setup

GitHub Environment `production` holds five secrets (storage connection
string, bot token, webhook secret, OpenRouter LLM key, DeepSeek LLM
key) and variables
(`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`,
`AZURE_RESOURCE_GROUP`, `FUNCTION_APP_NAME`, `PRODUCTION_WEBHOOK_URL`,
`MONITORING_ALERT_EMAIL`).
No client secret anywhere. Static deployment values (region, plan,
storage account, table, hub, bot ID, LLM profile selector) live in
`infra/main.parameters.json`; the resolver merges them with the
variables and secrets at deploy time. The DeepSeek secret input
(`DEPLOY_DEEPSEEK_API_KEY`, from GitHub secret
`RecurringTasksBot__Llm__DeepSeek__ApiKey`) is optional until the
DeepSeek profile is selected: an absent inactive key resolves to an
empty secure parameter, while the selected profile's key is required.
Supplied inactive credentials are preserved for rollback, and resolved
values are never printed. The selector is `DeepSeek` in the parameter
template; roll back by restoring it to `OpenRouter`.

Before the first monitoring deployment, set `MONITORING_ALERT_EMAIL` to the
operator's email address in the GitHub `production` environment. Missing or
malformed email stops parameter resolution before any cloud change. The OIDC
identity needs permission to create monitoring resources and update Application
Insights billing features, such as Contributor on the resource group.

If these providers are not registered, a subscription operator registers them
once using Azure CLI:

```bash
az provider register --namespace Microsoft.OperationalInsights --wait --output none
az provider register --namespace Microsoft.Insights --wait --output none
```

## Workflow triggers

`.github/workflows/deploy-production.yml` runs on pushes to `master` excluding
docs-only changes, or on `workflow_dispatch`. `.github/workflows/pr.yml` builds,
tests, and compiles Bicep for pull requests. Package path:
`src/RecurringTasksBot.FunctionApp/`.

Every deployment first runs the offline gates: restore, Release
build, full test suite, three script tests, Bicep compilation, then publish and
zip. Cloud steps run only after the gates pass.

## Deploy flow

1. Check the required GitHub variables (`AZURE_RESOURCE_GROUP`,
   `FUNCTION_APP_NAME`, `PRODUCTION_WEBHOOK_URL`, `MONITORING_ALERT_EMAIL`) and
   inventory the intended deployment from `infra/main.parameters.json` without
   printing credentials.
2. Log in with OIDC and resolve the parameter template into a temporary
   file (`scripts/resolve-deploy-parameters.sh` fills app name, webhook
   URL, and secrets from vars/secrets). The resolved file lives under
   `RUNNER_TEMP` and is never uploaded or logged.
3. Compile `infra/main.bicep` using the pinned Bicep compiler, check its monitoring
   dependencies and secure output, then deploy that `main.json` with the resolved
   parameter file onto the existing production storage account.
4. Run `scripts/configure-monitoring.sh` with the same parameter file. It sets
   Application Insights' 0.1 GB/day cap and reads back both caps, failing on a
   mismatch or CLI error. This runs immediately after infrastructure deployment,
   before publishing the app package. Bicep creates the capped workspace before
   connecting the Function App, so the workspace limit already applies.
5. Deploy the prebuilt
   `app.zip` (which retains the hidden `.azurefunctions` extension
   content) with `config-zip`.
6. Register the production webhook (`setWebhook` with the secret token
   and `allowed_updates=["message"]`).
7. Verify the host is `Running` and the effective platform hub matches
   the template hub.

The workflow never deletes data, webhooks, or pending updates beyond
the `setWebhook` registration itself.

Application Insights' own cap has no native Bicep property, so the small CLI step
is part of every deployment. It installs/updates the `application-insights`
extension and prints only the verified cap. `--stop false` keeps cap-notification
emails enabled; it does not turn off the cap. For a manual infrastructure
deployment, run the same step afterwards from the repository root:

```bash
bash scripts/configure-monitoring.sh --resource-group rg-recurringtasksbot
```

Pass `--parameters PATH` when using another resolved parameter file. The script
reads only monitoring names and the expected cap, even if that file contains
deployment credentials. Bicep represents the fractional quota as
`json(monitoringDailyCapGb)` because it has no fractional numeric literal.
See [Application Insights resource configuration](https://learn.microsoft.com/en-us/azure/azure-monitor/app/create-workspace-resource?tabs=bicep)
and the [billing CLI](https://learn.microsoft.com/en-us/cli/azure/monitor/app-insights/component/billing).

## LLM profile rollout

Both credentials are deployed (the inactive one may be empty); only
the selected profile's key is required at startup. To promote
DeepSeek after functional validation: drain retained generation work
(including queued retries), set the `llmActiveProfile` template value
to `DeepSeek`, redeploy, and monitor completion, errors, and latency.
Claims freeze prompt and settings but not provider, model, credential,
or budget, so in-flight work must finish on the old profile first.
Roll back by restoring the selector to `OpenRouter`; restore any
shared settings changed for compatibility. Keep both credentials
available during rollout.

## Failure handling

Fix forward against the current schema: re-dispatch after correcting
the cause. There is no old-binary rollback against new state, and
application code keeps no compatibility branch. Never delete the
shared storage account, deployment blobs, or Azure Files content.
