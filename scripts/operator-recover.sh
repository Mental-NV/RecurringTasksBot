#!/usr/bin/env bash
# Operator recovery using environment storage credentials only.
# No admin HTTP endpoints, no extra bot commands.
#
# Because orchestration startup is resumed through Telegram redelivery
# (stable operation IDs), this CLI performs the storage-side recovery and
# prints the exact /create command needed to reschedule when required.
#
# Usage:
#   ./scripts/operator-recover.sh recover [--env dev|prod] [--stale-minutes N] [--apply]
#   ./scripts/operator-recover.sh restart <operation-id> [--env dev|prod] [--apply]
#
# Env: AZURE_STORAGE_CONNECTION_STRING (presence only, never printed)
# Flags (--env, --table, --apply) may appear before or after the command
# and operation id. Unknown flags are rejected: every argument is parsed
# before the storage account is validated, so --env always takes effect.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP_DIR="$REPO_ROOT/src/RecurringTasksBot.FunctionApp"

TABLE=""
ENV_NAME=""
STALE_MINUTES=15
APPLY=0
CMD=""
OP_ID=""

while [ $# -gt 0 ]; do
  case "$1" in
    --stale-minutes) STALE_MINUTES="${2:-}"; shift 2 ;;
    --env) ENV_NAME="${2:-}"; shift 2 ;;
    --table) TABLE="${2:-}"; shift 2 ;;
    --apply) APPLY=1; shift ;;
    --*) echo "Unknown argument: $1" >&2; exit 2 ;;
    *)
      if [ -z "$CMD" ]; then CMD="$1";
      elif [ -z "$OP_ID" ]; then OP_ID="$1";
      else echo "Unexpected argument: $1" >&2; exit 2; fi
      shift ;;
  esac
done

if [ -z "${AZURE_STORAGE_CONNECTION_STRING:-}" ]; then echo "Missing AZURE_STORAGE_CONNECTION_STRING" >&2; exit 1; fi

# Development names come from the dev runtime profile; production names
# come from the canonical deployment parameter template.
TEMPLATE="$REPO_ROOT/infra/main.parameters.json"
if [ -n "$ENV_NAME" ]; then
  case "$ENV_NAME" in
    dev|prod) ;;
    *) echo "Unknown --env '$ENV_NAME' (expected dev|prod)" >&2; exit 2 ;;
  esac
  if [ "$ENV_NAME" = "prod" ]; then
    [ -z "$TABLE" ] && TABLE=$(python3 -c "import json; print(json.load(open('$TEMPLATE'))['parameters']['tableName']['value'])")
  else
    [ -z "$TABLE" ] && TABLE=$(python3 -c "import json; print(json.load(open('$APP_DIR/appsettings.json'))['RecurringTasksBot']['TableName'])")
  fi
fi
[ -z "$TABLE" ] && TABLE="RecurringTaskDataV5"
echo "Target table: $TABLE${ENV_NAME:+ (env $ENV_NAME)}"

if [ -n "$ENV_NAME" ]; then
  if [ "$ENV_NAME" = "prod" ]; then
    EXPECTED_ACCOUNT=$(python3 -c "import json; print(json.load(open('$TEMPLATE'))['parameters']['storageAccountName']['value'])")
  else
    EXPECTED_ACCOUNT=$(python3 -c "import json; print(json.load(open('$REPO_ROOT/infra/environments/development.json'))['azure']['storageAccountName'])")
  fi
  ACTUAL_ACCOUNT=$(python3 -c "
cs = '''$AZURE_STORAGE_CONNECTION_STRING'''.replace(chr(10), '')
for part in cs.split(';'):
    if part.startswith('AccountName='):
        print(part.split('=', 1)[1]); break
")
  if [ "$EXPECTED_ACCOUNT" != "$ACTUAL_ACCOUNT" ]; then
    echo "Storage account mismatch: --env $ENV_NAME expects '$EXPECTED_ACCOUNT' but credentials name '$ACTUAL_ACCOUNT'. Refusing to run." >&2
    exit 1
  fi
fi

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
    if [ -z "$OP_ID" ]; then echo "Usage: $0 restart <operation-id> [--apply]" >&2; exit 2; fi
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
