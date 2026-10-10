#!/usr/bin/env bash
# Set the Application Insights cap (not exposed as a Bicep property) and
# verify both caps. Names and the expected cap come from deployment parameters.
# Usage: bash scripts/configure-monitoring.sh --resource-group RG [--parameters FILE]
set -euo pipefail

MONITORING_PARAMETERS='infra/main.parameters.json'
MONITORING_RESOURCE_GROUP=''
while [ "$#" -gt 0 ]; do
  case "$1" in
    --resource-group|--parameters)
      [ "$#" -ge 2 ] && [ -n "$2" ] || { echo "Missing value for $1" >&2; exit 2; }
      case "$1" in
        --resource-group) MONITORING_RESOURCE_GROUP="$2" ;;
        --parameters) MONITORING_PARAMETERS="$2" ;;
      esac
      shift 2 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done
[ -n "$MONITORING_RESOURCE_GROUP" ] || { echo 'Missing --resource-group RG' >&2; exit 2; }

# Read only the non-secret monitoring fields, even when the file contains
# resolved deployment credentials. Nothing from the full document is printed.
MONITORING_CONFIG=$(python3 - "$MONITORING_PARAMETERS" <<'PY'
import json, sys
from decimal import Decimal, InvalidOperation

with open(sys.argv[1]) as source:
    params = json.load(source)["parameters"]
names = ("monitoringWorkspaceName", "applicationInsightsName", "monitoringDailyCapGb")
values = [params[name]["value"] for name in names]
for name, value in zip(names, values):
    if not isinstance(value, str) or not value or value.startswith("__") or any(c.isspace() for c in value):
        raise SystemExit(f"Invalid or unresolved monitoring parameter: {name}")
try:
    cap = Decimal(values[2])
    numeric_cap = json.loads(values[2])
except (InvalidOperation, ValueError):
    raise SystemExit("monitoringDailyCapGb must be a JSON number of at least 0.023 GB")
if not isinstance(numeric_cap, (float, int)) or not cap.is_finite() or cap < Decimal("0.023"):
    raise SystemExit("monitoringDailyCapGb must be a JSON number of at least 0.023 GB")
print("\t".join(values))
PY
)
IFS=$'\t' read -r MONITORING_WORKSPACE MONITORING_APPINSIGHTS MONITORING_DAILY_CAP_GB <<< "$MONITORING_CONFIG"

az extension add --name application-insights --upgrade --only-show-errors --output none
az monitor app-insights component billing update \
  --resource-group "$MONITORING_RESOURCE_GROUP" --app "$MONITORING_APPINSIGHTS" \
  --cap "$MONITORING_DAILY_CAP_GB" --stop false --output none

MONITORING_WORKSPACE_CAP=$(az monitor log-analytics workspace show \
  --resource-group "$MONITORING_RESOURCE_GROUP" --workspace-name "$MONITORING_WORKSPACE" \
  --query workspaceCapping.dailyQuotaGb --output tsv)
MONITORING_APPINSIGHTS_BILLING=$(az monitor app-insights component billing show \
  --resource-group "$MONITORING_RESOURCE_GROUP" --app "$MONITORING_APPINSIGHTS" --output json)

python3 - "$MONITORING_DAILY_CAP_GB" "$MONITORING_WORKSPACE_CAP" "$MONITORING_APPINSIGHTS_BILLING" <<'PY'
import json, sys
from decimal import Decimal, InvalidOperation

billing = json.loads(sys.argv[3])
# Support normalized CLI output and the original billing API field casing.
volume = billing.get("dataVolumeCap", billing.get("DataVolumeCap", {}))
app_cap = volume.get("cap", volume.get("Cap"))
expected = Decimal(sys.argv[1])
for label, value in (("Log Analytics", sys.argv[2]), ("Application Insights", app_cap)):
    try:
        actual = Decimal(str(value))
    except InvalidOperation:
        raise SystemExit(f"Could not read {label} daily cap")
    if not actual.is_finite() or actual != expected:
        raise SystemExit(f"{label} daily cap does not match the expected {expected} GB/day")
print(f"Verified Log Analytics and Application Insights daily caps: {expected} GB/day.")
PY
