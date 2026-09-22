#!/usr/bin/env bash
# Operator recovery using environment storage credentials only.
# No admin HTTP endpoints, no extra bot commands.
#
# Because orchestration startup is resumed through Telegram redelivery
# (stable operation IDs), this CLI performs the storage-side recovery and
# prints the exact /create command needed to reschedule when required.
#
# Usage:
#   ./scripts/operator-recover.sh recover [--stale-minutes N] [--apply]
#   ./scripts/operator-recover.sh restart <operation-id> [--apply]
#
# Env: AZURE_STORAGE_CONNECTION_STRING (presence only, never printed)
# Flags: --table (default RecurringTasks)
set -euo pipefail

TABLE="RecurringTasks"
STALE_MINUTES=15
APPLY=0
CMD="${1:-}"; shift || true

if [ -z "${AZURE_STORAGE_CONNECTION_STRING:-}" ]; then echo "Missing AZURE_STORAGE_CONNECTION_STRING" >&2; exit 1; fi

while [ $# -gt 0 ]; do
  case "$1" in
    --stale-minutes) STALE_MINUTES="${2:-}"; shift 2 ;;
    --table) TABLE="${2:-}"; shift 2 ;;
    --apply) APPLY=1; shift ;;
    *) break ;;
  esac
done

export AZURE_STORAGE_CONNECTION_STRING

cutoff() {
  python3 -c "from datetime import datetime, timedelta, timezone; print((datetime.now(timezone.utc) - timedelta(minutes=$1)).strftime('%Y-%m-%dT%H:%M:%SZ'))"
}

case "$CMD" in
  recover)
    CUTOFF=$(cutoff "$STALE_MINUTES")
    echo "Stranded starts (status=starting, Timestamp before $CUTOFF):"
    rows=$(az storage entity query --table-name "$TABLE" \
      --filter "Status eq 'starting' and Timestamp lt datetime'$CUTOFF'" \
      --select PartitionKey,RowKey,Status --output json)
    echo "$rows" | python3 -c "import json,sys; [print(r['PartitionKey'], r['RowKey']) for r in json.load(sys.stdin)]"
    if [ "$APPLY" = "1" ]; then
      echo "$rows" | python3 -c "
import json, subprocess, sys
for r in json.load(sys.stdin):
    pk, rk = r['PartitionKey'], r['RowKey']
    subprocess.run(['az', 'storage', 'entity', 'merge', '--table-name', '$TABLE',
        '--entity', f\"PartitionKey={pk}\", f\"RowKey={rk}\",
        'Status=failed', 'FailureSummary=Stranded start reclaimed by operator; resend /create to reschedule'],
        check=True, capture_output=True)
    print(f'marked failed: {pk} {rk}')
"
      echo "Recreate each with its original: /create <six-field-cron> <text> (see /list output before reclaim, or stored text)."
    else
      echo "(dry run; pass --apply to mark them failed for visible rescheduling)"
    fi
    ;;
  restart)
    OP_ID="${1:-}"; shift || true
    if [ -z "$OP_ID" ]; then echo "Usage: $0 restart <operation-id> [--apply]" >&2; exit 2; fi
    while [ $# -gt 0 ]; do case "$1" in --apply) APPLY=1; shift ;; --table) TABLE="${2:-}"; shift 2 ;; *) shift ;; esac; done
    echo "Failed operation(s) RowKey=operation_${OP_ID}:"
    rows=$(az storage entity query --table-name "$TABLE" \
      --filter "RowKey eq 'operation_${OP_ID}' and Status eq 'failed'" \
      --select PartitionKey,RowKey,Status --output json)
    echo "$rows" | python3 -c "import json,sys; [print(r['PartitionKey'], r['RowKey']) for r in json.load(sys.stdin)]"
    if [ "$APPLY" = "1" ]; then
      echo "$rows" | python3 -c "
import json, subprocess, sys
rows = json.load(sys.stdin)
if not rows: sys.exit('Nothing to restart: operation is not in failed status.')
for r in rows:
    pk, rk = r['PartitionKey'], r['RowKey']
    subprocess.run(['az', 'storage', 'entity', 'merge', '--table-name', '$TABLE',
        '--entity', f\"PartitionKey={pk}\", f\"RowKey={rk}\",
        'Status=deleted', 'FailureSummary='],
        check=True, capture_output=True)
    print(f'tombstoned (failed -> deleted): {pk} {rk}')
"
      echo "Cause must already be resolved. Reschedule by resending the original /create command."
    else
      echo "(dry run; pass --apply to tombstone so the original /create can safely reschedule)"
    fi
    ;;
  *) echo "Usage: $0 {recover|restart} [...]" >&2; exit 2 ;;
esac
