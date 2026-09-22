#!/usr/bin/env bash
# Operator cleanup using environment storage credentials only.
# Dry run by default; pass --apply to delete.
#
# Explicit retention flags (days; 0 = keep):
#   --receipts-older-than-days N    old update_/delivery_ receipts
#   --deleted-ops-older-than-days N tombstoned operations
#   --purge-orchestration-history-days N terminal orchestration Instances+History
#   --keep-dedup-days M (default 7) never delete update_ receipts newer than M days
#   --table NAME (default RecurringTasks) --hub NAME (default RecurringTasksProd)
#
# Env: AZURE_STORAGE_CONNECTION_STRING (presence only, never printed)
set -euo pipefail

TABLE="RecurringTasks"
HUB="RecurringTasksProd"
RECEIPTS_DAYS=0
DELETED_OPS_DAYS=0
HISTORY_DAYS=0
KEEP_DEDUP_DAYS=7
APPLY=0

while [ $# -gt 0 ]; do
  case "$1" in
    --receipts-older-than-days) RECEIPTS_DAYS="${2:-}"; shift 2 ;;
    --deleted-ops-older-than-days) DELETED_OPS_DAYS="${2:-}"; shift 2 ;;
    --purge-orchestration-history-days) HISTORY_DAYS="${2:-}"; shift 2 ;;
    --keep-dedup-days) KEEP_DEDUP_DAYS="${2:-}"; shift 2 ;;
    --table) TABLE="${2:-}"; shift 2 ;;
    --hub) HUB="${2:-}"; shift 2 ;;
    --apply) APPLY=1; shift ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

if [ -z "${AZURE_STORAGE_CONNECTION_STRING:-}" ]; then echo "Missing AZURE_STORAGE_CONNECTION_STRING" >&2; exit 1; fi
export AZURE_STORAGE_CONNECTION_STRING

cutoff_days() {
  python3 -c "from datetime import datetime, timedelta, timezone; print((datetime.now(timezone.utc) - timedelta(days=$1)).strftime('%Y-%m-%dT%H:%M:%SZ'))"
}

delete_rows() {
  local table="$1" filter="$2"
  local rows
  rows=$(az storage entity query --table-name "$table" --filter "$filter" --select PartitionKey,RowKey --output json)
  local count
  count=$(echo "$rows" | python3 -c "import json,sys; print(len(json.load(sys.stdin)))")
  echo "  matched: $count"
  if [ "$APPLY" = "1" ] && [ "$count" -gt 0 ]; then
    echo "$rows" | TABLE_NAME="$table" python3 -c "
import json, os, subprocess, sys
for r in json.load(sys.stdin):
    subprocess.run(['az', 'storage', 'entity', 'delete', '--table-name', os.environ['TABLE_NAME'],
        '--partition-key', r['PartitionKey'], '--row-key', r['RowKey']],
        check=True, capture_output=True)
    print('  deleted:', r['PartitionKey'], r['RowKey'])
"
  fi
}

if [ "$RECEIPTS_DAYS" -gt 0 ]; then
  CUTOFF=$(cutoff_days "$RECEIPTS_DAYS")
  DEDUP_GUARD=$(cutoff_days "$KEEP_DEDUP_DAYS")
  echo "Old receipts (Timestamp before $CUTOFF; update_ receipts newer than $DEDUP_GUARD kept for retry dedup):"
  delete_rows "$TABLE" "Timestamp lt datetime'$CUTOFF' and (RowKey ge 'delivery_' and RowKey lt 'delivery\`')"
  delete_rows "$TABLE" "Timestamp lt datetime'$CUTOFF' and Timestamp lt datetime'$DEDUP_GUARD' and (RowKey ge 'update_' and RowKey lt 'update\`')"
fi

if [ "$DELETED_OPS_DAYS" -gt 0 ]; then
  CUTOFF=$(cutoff_days "$DELETED_OPS_DAYS")
  echo "Tombstoned operations (status=deleted, Timestamp before $CUTOFF):"
  delete_rows "$TABLE" "Status eq 'deleted' and Timestamp lt datetime'$CUTOFF' and (RowKey ge 'operation_' and RowKey lt 'operation\`')"
fi

if [ "$HISTORY_DAYS" -gt 0 ]; then
  CUTOFF=$(cutoff_days "$HISTORY_DAYS")
  echo "Terminal orchestration history in hub $HUB (Completed/Terminated/Failed before $CUTOFF):"
  instances=$(az storage entity query --table-name "${HUB}Instances" \
    --filter "Timestamp lt datetime'$CUTOFF' and (RuntimeStatus eq 'Completed' or RuntimeStatus eq 'Terminated' or RuntimeStatus eq 'Failed')" \
    --select PartitionKey,RowKey --output json)
  echo "$instances" | python3 -c "import json,sys; [print(' ', r['PartitionKey'], r['RowKey']) for r in json.load(sys.stdin)]"
  if [ "$APPLY" = "1" ]; then
    echo "$instances" | HUB_NAME="$HUB" python3 -c "
import json, os, subprocess, sys
hub = os.environ['HUB_NAME']
for r in json.load(sys.stdin):
    iid = r['RowKey']
    hist = subprocess.run(['az', 'storage', 'entity', 'query', '--table-name', f'{hub}History',
        '--filter', f\"PartitionKey eq '{iid}'\", '--select', 'PartitionKey,RowKey', '--output', 'json'],
        check=True, capture_output=True, text=True)
    for h in json.loads(hist.stdout):
        subprocess.run(['az', 'storage', 'entity', 'delete', '--table-name', f'{hub}History',
            '--partition-key', h['PartitionKey'], '--row-key', h['RowKey']], check=True, capture_output=True)
    subprocess.run(['az', 'storage', 'entity', 'delete', '--table-name', f'{hub}Instances',
        '--partition-key', r['PartitionKey'], '--row-key', iid], check=True, capture_output=True)
    print('  purged:', iid)
"
  else
    echo "(dry run; pass --apply to purge)"
  fi
fi

if [ "$APPLY" = "0" ]; then echo "Dry run complete; nothing deleted. Pass --apply to delete."; fi
