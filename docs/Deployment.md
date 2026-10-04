# Deployment

## Azure resources

Windows Consumption Function App (`func-recurringtasks-prod`) on the
existing production storage account (`prodrecurringtasksbot`), which
holds business data (`RecurringTaskData`), Durable state
(`RecurringTasksAppProd` hub), and deployment content. Bicep
(`infra/main.bicep`) never creates storage. No Always On, no scan jobs —
Durable timers only.

## OIDC and GitHub environment setup

GitHub Environment `production` holds five secrets (storage connection
string, bot token, webhook secret, OpenRouter LLM key, DeepSeek LLM
key) and variables
(`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`,
`AZURE_RESOURCE_GROUP`, `FUNCTION_APP_NAME`, `PRODUCTION_WEBHOOK_URL`).
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

## Workflow triggers

`.github/workflows/deploy-production.yml` runs on `workflow_dispatch`
only — a push never deploys. `.github/workflows/pr.yml` builds and
tests pull requests. Package path:
`src/RecurringTasksBot.FunctionApp/`.

Every dispatch first runs the offline gates: restore, Release
build, full test suite, both script tests, then publish and zip. Cloud
steps run only after the gates pass.

## Deploy flow

1. Check the required GitHub variables (`AZURE_RESOURCE_GROUP`,
   `FUNCTION_APP_NAME`, `PRODUCTION_WEBHOOK_URL`) and inventory the
   intended deployment from `infra/main.parameters.json` without
   printing credentials.
2. Log in with OIDC and resolve the parameter template into a temporary
   file (`scripts/resolve-deploy-parameters.sh` fills app name, webhook
   URL, and secrets from vars/secrets). The resolved file lives under
   `RUNNER_TEMP` and is never uploaded or logged.
3. Deploy `infra/main.bicep` with the resolved parameter file onto the
   existing production storage account, then deploy the prebuilt
   `app.zip` (which retains the hidden `.azurefunctions` extension
   content) with `config-zip`.
4. Register the production webhook (`setWebhook` with the secret token
   and `allowed_updates=["message"]`).
5. Verify the host is `Running` and the effective platform hub matches
   the template hub.

The workflow never deletes data, webhooks, or pending updates beyond
the `setWebhook` registration itself.

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
