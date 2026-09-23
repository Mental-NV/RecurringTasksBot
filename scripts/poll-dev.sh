#!/usr/bin/env bash
# Local polling forwarder for the DEVELOPMENT bot only.
# Long-polls Telegram getUpdates and forwards each update to the local
# Functions host, exercising the exact same webhook code path as production.
# No tunnel needed. Refuses to run with the production bot token.
# Usage: ./scripts/poll-dev.sh [--drop-pending] [--port 7071]
set -euo pipefail

DROP_PENDING="false"
PORT="7071"

while [ $# -gt 0 ]; do
  case "$1" in
    --drop-pending) DROP_PENDING="true"; shift ;;
    --port) PORT="${2:-}"; shift 2 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

for v in RecurringTasksBot__Telegram__BotToken RecurringTasksBot__Telegram__WebhookSecret; do
  if [ -z "${!v:-}" ]; then echo "Missing required env var: $v" >&2; exit 1; fi
done

DEV_PROFILE="appsettings.Development.json"
PROD_PROFILE="appsettings.Production.json"
EXPECTED_DEV_ID=$(python3 -c "import json; print(json.load(open('$DEV_PROFILE'))['RecurringTasksBot']['ExpectedBotId'])")
PROD_BOT_ID=$(python3 -c "import json; print(json.load(open('$PROD_PROFILE'))['RecurringTasksBot']['ExpectedBotId'])")
API_BASE="${TELEGRAM_API_BASE:-https://api.telegram.org}"
ACTUAL_BOT_ID=$(curl -sS "${API_BASE}/bot${RecurringTasksBot__Telegram__BotToken}/getMe" | python3 -c "import json,sys; print(json.load(sys.stdin).get('result', {}).get('id', ''))")

if [ "$ACTUAL_BOT_ID" != "$EXPECTED_DEV_ID" ]; then
  echo "Token does not belong to the development bot. Refusing." >&2; exit 1
fi
if [ "$ACTUAL_BOT_ID" = "$PROD_BOT_ID" ] && [ "$PROD_BOT_ID" != "0" ]; then
  echo "Token belongs to the production bot. Refusing." >&2; exit 1
fi

export POLL_DROP_PENDING="$DROP_PENDING" POLL_PORT="$PORT" POLL_API_BASE="$API_BASE"
echo "Polling as dev bot $ACTUAL_BOT_ID, forwarding to http://localhost:${PORT}/api/webhook. Ctrl+C to stop."
exec python3 - <<'EOF'
import json
import os
import sys
import time
import urllib.request

TOKEN = os.environ["RecurringTasksBot__Telegram__BotToken"]
SECRET = os.environ["RecurringTasksBot__Telegram__WebhookSecret"]
API = os.environ["POLL_API_BASE"]
WEBHOOK = f"http://localhost:{os.environ['POLL_PORT']}/api/webhook"
OFFSET_FILE = "/tmp/recurringtasksbot-poll-offset"
DROP = os.environ["POLL_DROP_PENDING"] == "true"


def api(method, payload):
    req = urllib.request.Request(
        f"{API}/bot{TOKEN}/{method}",
        data=json.dumps(payload).encode(),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=70) as resp:
        return json.loads(resp.read().decode())


def load_offset():
    try:
        with open(OFFSET_FILE) as f:
            return int(f.read().strip())
    except (OSError, ValueError):
        return 0


def save_offset(offset):
    with open(OFFSET_FILE, "w") as f:
        f.write(str(offset))


def forward(update):
    req = urllib.request.Request(
        WEBHOOK,
        data=json.dumps(update).encode(),
        headers={
            "Content-Type": "application/json",
            "X-Telegram-Bot-Api-Secret-Token": SECRET,
        },
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=120) as resp:
            return resp.status
    except urllib.error.HTTPError as e:
        return e.code
    except OSError as e:
        print(f"Local host unreachable: {e}. Is the Functions host running?", flush=True)
        return None


drop_resp = api("deleteWebhook", {"drop_pending_updates": DROP})
if not drop_resp.get("ok"):
    sys.exit(f"deleteWebhook failed: {drop_resp}")

offset = load_offset()
while True:
    try:
        resp = api("getUpdates", {"offset": offset, "timeout": 50,
                                  "allowed_updates": ["message"]})
    except OSError as e:
        print(f"getUpdates failed: {e}; retrying...", flush=True)
        time.sleep(5)
        continue
    if not resp.get("ok"):
        desc = str(resp.get("description", ""))
        if "conflict" in desc.lower():
            sys.exit("Another getUpdates consumer is running (conflict). Stop it first.")
        print(f"getUpdates error: {resp}; retrying...", flush=True)
        time.sleep(5)
        continue
    for update in resp.get("result", []):
        status = forward(update)
        if status == 200:
            offset = update["update_id"] + 1
            save_offset(offset)
        elif status == 503:
            print("Host busy (503); retrying same update...", flush=True)
            time.sleep(5)
            status = forward(update)
            if status == 200:
                offset = update["update_id"] + 1
                save_offset(offset)
        elif status == 403:
            sys.exit("Local host rejected the secret (403). Check RecurringTasksBot__Telegram__WebhookSecret.")
        elif status is None:
            time.sleep(5)
        else:
            print(f"Unexpected host status {status}; skipping update {update.get('update_id')}.", flush=True)
            offset = update["update_id"] + 1
            save_offset(offset)
EOF
