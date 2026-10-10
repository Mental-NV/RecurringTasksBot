# Monitoring runbook

Permanent monitoring is deployed by `infra/monitoring.bicep`, called from
`infra/main.bicep`. The production workflow then runs
`scripts/configure-monitoring.sh` to set the Application Insights cap and verify
both caps. See [Deployment](Deployment.md) for setup and deployment.

Normal operation uses platform metrics, two polling alerts, and sampled function
telemetry. Queue diagnostic logs and verbose scale-controller logs stay off until
an operator starts a capture. Infrastructure deployments reset scale-controller
logging to `None`; a manually created queue capture persists until deleted.

## Cost and coverage

- The dedicated workspace uses Analytics logs and a **0.1 GB/day (100 MB)** cap,
  shared by application telemetry and temporary diagnostics. Application Insights
  also has a 0.1 GB/day cap. Caps can overshoot and excess ingestion can be billed;
  they are safeguards, not a guarantee of a zero bill.
- The nominal shared limit is 3.1 GB in a 31-day month. The 5 GB/month Analytics
  ingestion allowance is shared **per billing account**. Other resources can use
  it. Standard platform metrics are free; the two static alerts use two of the
  included 10 metric time series, subject to the available allowance.
- Normal logs should stay below the cap. If it is reached, log ingestion stops
  until reset; the storage metric alerts continue independently. Host sampling
  does not reduce storage diagnostic logs.
- Alerts detect more than `queuePollingErrorThreshold` (default 100) failed
  `PeekMessage` or `GetQueueMetadata` calls in an hour, evaluated every 15 minutes.
  They do not detect every error, successful redundant polling, or overdue tasks.
  Queue names and caller details require a storage capture. Application Insights
  alone does not show every external scale-controller queue request.

See [pricing](https://azure.microsoft.com/en-us/pricing/details/monitor/) and
[daily cap behavior](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/daily-cap).

## Prepare a diagnostic session

Run from the repository root in one Bash session, using a recent Azure CLI and
an account authorized for the production resource group. Cloud Shell is already
signed in; locally run `az login` first.

```bash
az account set --subscription '895bbba2-f762-48f6-83b9-e7029b6f3e96'
az extension add --name log-analytics --upgrade
RG='rg-recurringtasksbot'
FUNCTION_APP='func-recurringtasks-prod'
STORAGE=$(python3 -c "import json; print(json.load(open('infra/main.parameters.json'))['parameters']['storageAccountName']['value'])")
WORKSPACE=$(python3 -c "import json; print(json.load(open('infra/main.parameters.json'))['parameters']['monitoringWorkspaceName']['value'])")
STORAGE_ID=$(az storage account show --resource-group "$RG" --name "$STORAGE" --query id --output tsv)
QUEUE_SERVICE_ID="$STORAGE_ID/queueServices/default"
WORKSPACE_ID=$(az monitor log-analytics workspace show --resource-group "$RG" --workspace-name "$WORKSPACE" --query id --output tsv)
WORKSPACE_CUSTOMER_ID=$(az monitor log-analytics workspace show --resource-group "$RG" --workspace-name "$WORKSPACE" --query customerId --output tsv)
CAPTURE_NAME='queue-read-capture'
```

## Check metrics and function telemetry

Metrics require no resource-log capture:

```bash
az monitor metrics list --resource "$QUEUE_SERVICE_ID" --metrics Transactions \
  --aggregation Total --interval 1h --offset 1d \
  --filter "ApiName eq 'PeekMessage' and ResponseType eq '*'" --output json

az monitor log-analytics query --workspace "$WORKSPACE_CUSTOMER_ID" \
  --analytics-query 'AppRequests | summarize Executions=count(), Failed=countif(Success == false) by Name' \
  --timespan PT1H --output table
```

Repeat the metric query with `GetQueueMetadata` when needed. Successful empty
polling is normal; compare several quiet days before treating volume as redundant.
After a first deployment, send `/list` once and allow telemetry ingestion time.
This checks telemetry delivery; it does not test waking from zero. Avoid permanent
HTTP availability probes, which repeatedly wake the app.

## Capture queue reads

Enable only queue-service `StorageRead`, without exporting metrics:

```bash
az monitor diagnostic-settings create --name "$CAPTURE_NAME" \
  --resource "$QUEUE_SERVICE_ID" --workspace "$WORKSPACE_ID" \
  --export-to-resource-specific true \
  --logs '[{"category":"StorageRead","enabled":true}]' --output none

QUEUE_QUERY=$(cat <<'KQL'
StorageQueueLogs
| extend Queue = tostring(split(tostring(parse_url(Uri).Path), "/")[1])
| summarize Requests=count() by Queue, OperationName, StatusCode, UserAgentHeader, CallerIpAddress
| order by Requests desc
KQL
)
az monitor log-analytics query --workspace "$WORKSPACE_CUSTOMER_ID" \
  --analytics-query "$QUEUE_QUERY" --timespan PT1H --output table
```

Logs are not retroactive. Initial delivery may be delayed or the table may not
exist yet; confirm rows arrive, then capture a useful 30–60 minute idle window.
Stop sooner if ingestion grows quickly. Queue names can expose calls aimed at an
old/default hub; status codes explain failures. User agent/IP are caller clues,
not guaranteed identities for individual internal Azure monitors.

**Stop the capture when finished; it has no automatic expiry:**

```bash
az monitor diagnostic-settings delete --name "$CAPTURE_NAME" \
  --resource "$QUEUE_SERVICE_ID" --output none
```

This deletes only the capture setting; previously ingested rows remain for their
retention period. See [queue monitoring](https://learn.microsoft.com/en-us/azure/storage/queues/monitor-queue-storage)
and the [log schema](https://learn.microsoft.com/en-us/azure/azure-monitor/reference/tables/storagequeuelogs).

## Capture scale-controller decisions

This Functions feature is in preview. App-setting changes restart the host.
Enable it long enough to observe an idle period/wakeup, typically up to a day:

```bash
az functionapp config appsettings set --resource-group "$RG" --name "$FUNCTION_APP" \
  --settings 'SCALE_CONTROLLER_LOGGING_ENABLED=AppInsights:Verbose' --output none

az monitor log-analytics query --workspace "$WORKSPACE_CUSTOMER_ID" \
  --analytics-query 'AppTraces | where tostring(Properties.Category) == "ScaleControllerLogs" | project TimeGenerated, Message, Properties' \
  --timespan PT1H --output json
```

Allow ingestion time, then disable verbose logging after collecting the evidence:

```bash
az functionapp config appsettings set --resource-group "$RG" --name "$FUNCTION_APP" \
  --settings 'SCALE_CONTROLLER_LOGGING_ENABLED=AppInsights:None' --output none
```

See [scale-controller logging](https://learn.microsoft.com/en-us/azure/azure-functions/configure-monitoring#configure-scale-controller-logs).

## Check ingestion and cap events

Usage can lag and covers this workspace, not the full billing account allowance.
Review after the first day and after diagnostic captures:

```bash
az monitor log-analytics query --workspace "$WORKSPACE_CUSTOMER_ID" \
  --analytics-query 'Usage | where TimeGenerated >= startofmonth(now()) | where IsBillable == true | summarize IngestedGB=sum(Quantity)/1000 by DataType | order by IngestedGB desc' \
  --timespan P31D --output table

az monitor log-analytics query --workspace "$WORKSPACE_CUSTOMER_ID" \
  --analytics-query '_LogOperation | where Category =~ "Ingestion" | where Detail contains "OverQuota" | project TimeGenerated, Detail' \
  --timespan P7D --output table
```

Rerun the cap safeguard after correcting a failed deployment or suspected drift:

```bash
bash scripts/configure-monitoring.sh --resource-group "$RG"
```

The script sets the Application Insights cap and verifies the workspace cap; a
workspace mismatch requires redeploying Bicep. It does not change the Function
App settings or dump deployment credentials. Keep operational log queries manual
unless the separate price of a log-search alert is justified.
