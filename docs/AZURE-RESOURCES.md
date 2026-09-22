# Azure resources

All resources live in one resource group in one region:

| Setting | Value |
| --- | --- |
| Resource group | `rg-recurringtasksbot` |
| Location | `switzerlandnorth` |
| Dev storage account | `devrecurringtasksbot` |
| Prod storage account | `prodrecurringtasksbot` |

Both storage accounts are Standard general-purpose v2 (`StorageV2`,
`Standard_LRS`) with Blob, Queue, Table, and File services. The business
table `RecurringTasks` is created automatically by the app on first run
(`CreateIfNotExists`) — do not create it manually.

## Development (local): storage account only, no Function App

Local runs need just the dev storage account. There is no development
Function App and no App Service plan: compute is the local macOS
Functions host started by `./scripts/launch-local.sh`, talking directly
to the dev storage account over HTTPS. Do not create any Function App
for development.

```sh
az group create --name rg-recurringtasksbot --location switzerlandnorth
az storage account create \
  --resource-group rg-recurringtasksbot \
  --name devrecurringtasksbot \
  --location switzerlandnorth \
  --sku Standard_LRS \
  --kind StorageV2
az storage account show-connection-string \
  --resource-group rg-recurringtasksbot \
  --name devrecurringtasksbot --output tsv
```

Export the connection string as `RecurringTasksBot__AzureWebJobsStorage`
(see [SETUP.md](SETUP.md)). The launch script validates that the
connection string names `devrecurringtasksbot`.

## Production: storage account first, compute via Bicep

Create the resource group (once) and the prod storage account manually.
The Consumption plan (`Y1` Dynamic) and the Windows Function App are
created by `infra/main.bicep` on top of the existing prod storage
account — never create them by hand, so deployments stay reproducible.

```sh
az storage account create \
  --resource-group rg-recurringtasksbot \
  --name prodrecurringtasksbot \
  --location switzerlandnorth \
  --sku Standard_LRS \
  --kind StorageV2
```

Bicep parameters (`infra/main.parameters.json`) already point at
`prodrecurringtasksbot` in `switzerlandnorth`. Bicep creates:

| Resource | Name | Notes |
| --- | --- | --- |
| App Service plan | `plan-recurringtasks-prod` | Consumption `Y1` Dynamic; no Always On |
| Function App | `func-recurringtasks-prod` | Windows, `dotnet-isolated`, `httpsOnly`, TLS 1.2+ |

The deployment reuses the prod connection string for
`AzureWebJobsStorage`, `RecurringTasksBot__AzureWebJobsStorage`, and
`WEBSITE_CONTENTAZUREFILECONNECTIONSTRING`; no separate Files credential
is needed. The Durable task hub stays `RecurringTasksProd`. The public
webhook is `https://func-recurringtasks-prod.azurewebsites.net/api/webhook`.

## GitHub OIDC (production deploys)

One manual step outside Bicep: an app registration with a federated
credential trusting the repo's `production` Environment, assigned a role
(e.g. Website Contributor) scoped to `rg-recurringtasksbot`. Its client
ID goes to `OidcClientId` in `appsettings.Production.json` and the
`AZURE_*` variables of the `production` Environment. No client secret is
used anywhere.
