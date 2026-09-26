#!/usr/bin/env bash
# Operator cleanup using environment storage credentials only.
# Dry run by default; pass --apply to delete.
#
# Explicit retention flags (days; 0 = keep):
#   --receipts-older-than-days N    terminal occurrences (receipt + child
#                                   artifacts) older than N days; unfinished
#                                   occurrences are always preserved, as are
#                                   memory_ rows and update_ dedup receipts
#   --deleted-ops-older-than-days N tombstoned operations with their memory
#                                   and artifacts (operation row removed last)
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
  delete_rows "$TABLE" "Timestamp lt datetime'$CUTOFF' and Timestamp lt datetime'$DEDUP_GUARD' and (RowKey ge 'update_' and RowKey lt 'update\`')"
  echo "Terminal occurrences (receipt and child artifacts; unfinished occurrences, memory_ rows, and orphans newer than 24h kept):"
  TABLE_NAME="$TABLE" CUTOFF_TS="$CUTOFF" APPLY_FLAG="$APPLY" python3 - <<'PYEOF'
import json, os, subprocess
from datetime import datetime, timedelta, timezone

table = os.environ["TABLE_NAME"]
cutoff = os.environ["CUTOFF_TS"]
apply = os.environ["APPLY_FLAG"] == "1"
orphan_grace = (datetime.now(timezone.utc) - timedelta(hours=24)).strftime("%Y-%m-%dT%H:%M:%SZ")

def query(flt, select="PartitionKey,RowKey,Timestamp,Status"):
    out = subprocess.run(["az", "storage", "entity", "query", "--table-name", table,
        "--filter", flt, "--select", select, "--output", "json"],
        check=True, capture_output=True, text=True)
    return json.loads(out.stdout)

def delete(pk, rk):
    subprocess.run(["az", "storage", "entity", "delete", "--table-name", table,
        "--partition-key", pk, "--row-key", rk], check=True, capture_output=True)
    print("  deleted:", pk, rk)

def get_row(pk, rk):
    rows = query(f"PartitionKey eq '{pk}' and RowKey eq '{rk}'")
    return rows[0] if rows else None

# Candidate delivery rows older than the cutoff, grouped by occurrence.
# Rows sort so that children (delivery_<op>_<ticks>__*) follow their parent
# receipt (delivery_<op>_<ticks>); '`' (0x60) closes each '__' range.
candidates = query(f"Timestamp lt datetime'{cutoff}' and "
    "(RowKey ge 'delivery_' and RowKey lt 'delivery`')")
groups = {}
for r in candidates:
    parent = r["RowKey"].split("__", 1)[0]
    groups.setdefault((r["PartitionKey"], parent), []).append(r)
print(f"  candidate occurrences: {len(groups)}")

for (pk, parent), rows in sorted(groups.items()):
    receipt = get_row(pk, parent)
    if receipt is not None:
        status = receipt.get("Status")
        if status not in ("sent", "failed"):
            print(f"  keep unfinished occurrence: {pk} {parent} (status {status})")
            continue
        if receipt.get("Timestamp", "") >= cutoff:
            print(f"  keep recent terminal occurrence: {pk} {parent}")
            continue
        # Terminal and old: delete child artifacts before the parent receipt.
        children = [r for r in rows if r["RowKey"] != parent]
        extra = query(f"PartitionKey eq '{pk}' and RowKey ge '{parent}__' "
            f"and RowKey lt '{parent}_`'", select="PartitionKey,RowKey")
        seen = {r["RowKey"] for r in children}
        children += [r for r in extra if r["RowKey"] not in seen]
        if apply:
            for r in sorted(children, key=lambda x: x["RowKey"]):
                delete(pk, r["RowKey"])
            delete(pk, parent)
        else:
            print(f"  would delete terminal occurrence: {pk} {parent} "
                f"(+{len(children)} artifacts)")
        continue
    # No parent receipt: orphan artifacts. Require no live operation
    # publication and a 24-hour age grace before deletion.
    tail = parent[len("delivery_"):] if parent.startswith("delivery_") else ""
    op_id = tail.rsplit("_", 1)[0] if "_" in tail else ""
    if not op_id:
        print(f"  keep unparsable row group: {pk} {parent}")
        continue
    if get_row(pk, f"operation_{op_id}") is not None:
        print(f"  keep orphan with live operation: {pk} {parent}")
        continue
    if any(r.get("Timestamp", "") >= orphan_grace for r in rows):
        print(f"  keep young orphan: {pk} {parent}")
        continue
    if apply:
        for r in sorted(rows, key=lambda x: x["RowKey"]):
            delete(pk, r["RowKey"])
    else:
        print(f"  would delete orphan artifacts: {pk} {parent} ({len(rows)} rows)")
PYEOF
fi

if [ "$DELETED_OPS_DAYS" -gt 0 ]; then
  CUTOFF=$(cutoff_days "$DELETED_OPS_DAYS")
  echo "Tombstoned operations (status=deleted, Timestamp before $CUTOFF; memory/artifacts first, operation row last):"
  TABLE_NAME="$TABLE" CUTOFF_TS="$CUTOFF" APPLY_FLAG="$APPLY" python3 - <<'PYEOF'
import json, os, subprocess

table = os.environ["TABLE_NAME"]
cutoff = os.environ["CUTOFF_TS"]
apply = os.environ["APPLY_FLAG"] == "1"

def query(flt, select="PartitionKey,RowKey,Timestamp,Status"):
    out = subprocess.run(["az", "storage", "entity", "query", "--table-name", table,
        "--filter", flt, "--select", select, "--output", "json"],
        check=True, capture_output=True, text=True)
    return json.loads(out.stdout)

def delete(pk, rk):
    subprocess.run(["az", "storage", "entity", "delete", "--table-name", table,
        "--partition-key", pk, "--row-key", rk], check=True, capture_output=True)
    print("  deleted:", pk, rk)

tombs = query(f"Status eq 'deleted' and Timestamp lt datetime'{cutoff}' and "
    "(RowKey ge 'operation_' and RowKey lt 'operation`')")
for t in sorted(tombs, key=lambda x: (x["PartitionKey"], x["RowKey"])):
    pk, op_row = t["PartitionKey"], t["RowKey"]
    if not op_row.startswith("operation_") or "__" in op_row:
        continue
    op_id = op_row[len("operation_"):]
    # Recheck the tombstone before destructive batches (retry/interruption safe).
    current = query(f"PartitionKey eq '{pk}' and RowKey eq '{op_row}'")
    if not current or current[0].get("Status") != "deleted":
        print(f"  skip resurrected operation: {pk} {op_id}")
        continue
    owned = query(f"PartitionKey eq '{pk}' and RowKey ge 'delivery_{op_id}_' "
        f"and RowKey lt 'delivery_{op_id}_`'", select="PartitionKey,RowKey")
    texts = query(f"PartitionKey eq '{pk}' and RowKey ge 'operation_{op_id}__' "
        f"and RowKey lt 'operation_{op_id}_`'", select="PartitionKey,RowKey")
    mem = query(f"PartitionKey eq '{pk}' and RowKey eq 'memory_{op_id}'",
        select="PartitionKey,RowKey")
    # Memory and artifacts first, the operation row last.
    ordered = ([r["RowKey"] for r in mem] +
        sorted(r["RowKey"] for r in owned if "__" in r["RowKey"]) +
        sorted(r["RowKey"] for r in owned if "__" not in r["RowKey"]) +
        sorted(r["RowKey"] for r in texts) + [op_row])
    if apply:
        for rk in ordered:
            delete(pk, rk)
    else:
        print(f"  would delete tombstoned operation: {pk} {op_id} ({len(ordered)} rows)")
PYEOF
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
