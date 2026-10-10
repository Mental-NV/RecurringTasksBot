#!/usr/bin/env bash
# Read-only preflight for the active Azure CLI subscription.
set -euo pipefail

if [[ $# -ne 0 ]]; then
  echo "Usage: bash scripts/check-azure-providers.sh (after Azure login)" >&2
  exit 1
fi

failed=0
for namespace in Microsoft.Web Microsoft.Storage Microsoft.Insights Microsoft.OperationalInsights Microsoft.AlertsManagement; do
  if ! state=$(az provider show --namespace "$namespace" --query registrationState --output tsv); then
    echo "Cannot read $namespace registration. Check Azure login and provider read permissions." >&2
    failed=1
    continue
  fi
  case "$state" in
    Registered)
      echo "$namespace: Registered"
      ;;
    Registering)
      # Registration is regional; Azure permits deployment while other regions finish.
      echo "$namespace: Registering; Azure will check readiness in the target region."
      ;;
    *)
      echo "$namespace: ${state:-unknown registration state}" >&2
      echo "A subscription operator should run: az provider register --namespace $namespace --wait --output none" >&2
      failed=1
      ;;
  esac
done

exit "$failed"
