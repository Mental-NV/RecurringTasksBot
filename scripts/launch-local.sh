#!/usr/bin/env bash
# Local launch against DEVELOPMENT resources only. Never touches production.
# Usage: ./scripts/launch-local.sh [--tunnel-url <https-url>]
# Required env (development values only):
#   RecurringTasksBot__Telegram__BotToken
#   RecurringTasksBot__Telegram__WebhookSecret
#   RecurringTasksBot__AzureWebJobsStorage
set -euo pipefail

DEV_PROFILE="appsettings.Development.json"
TUNNEL_URL=""

while [ $# -gt 0 ]; do
  case "$1" in
    --tunnel-url) TUNNEL_URL="${2:-}"; shift 2 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

for v in RecurringTasksBot__Telegram__BotToken RecurringTasksBot__Telegram__WebhookSecret RecurringTasksBot__AzureWebJobsStorage RecurringTasksBot__Llm__ApiKey; do
  if [ -z "${!v:-}" ]; then echo "Missing required env var: $v" >&2; exit 1; fi
done

if [ ! -f "$DEV_PROFILE" ]; then echo "Missing $DEV_PROFILE" >&2; exit 1; fi

EXPECTED_STORAGE=$(python3 -c "import json; print(json.load(open('$DEV_PROFILE'))['RecurringTasksBot']['StorageAccountName'])")
ACTUAL_STORAGE=$(python3 -c "
cs = '''$RecurringTasksBot__AzureWebJobsStorage'''.replace(chr(10), '')
for part in cs.split(';'):
    if part.startswith('AccountName='):
        print(part.split('=', 1)[1]); break
")
if [ "$EXPECTED_STORAGE" != "$ACTUAL_STORAGE" ]; then
  echo "Storage mismatch: profile expects '$EXPECTED_STORAGE' but connection string names '$ACTUAL_STORAGE'. Refusing to start." >&2
  exit 1
fi

EXPECTED_BOT_ID=$(python3 -c "import json; print(json.load(open('$DEV_PROFILE'))['RecurringTasksBot']['ExpectedBotId'])")
ACTUAL_BOT_ID=$(curl -sS "https://api.telegram.org/bot${RecurringTasksBot__Telegram__BotToken}/getMe" | python3 -c "import json,sys; print(json.load(sys.stdin).get('result', {}).get('id', ''))")
if [ "$EXPECTED_BOT_ID" != "$ACTUAL_BOT_ID" ]; then
  echo "Bot ID mismatch: profile expects '$EXPECTED_BOT_ID'. Refusing to start." >&2
  exit 1
fi

if [ -n "$TUNNEL_URL" ]; then
  case "$TUNNEL_URL" in
    https://*) ;;
    *) echo "Tunnel URL must be https." >&2; exit 1 ;;
  esac
  python3 - "$DEV_PROFILE" "$TUNNEL_URL" <<'EOF'
import json, sys
path, url = sys.argv[1], sys.argv[2]
with open(path) as f:
    cfg = json.load(f)
cfg['RecurringTasksBot']['WebhookUrl'] = url + '/api/webhook'
with open(path, 'w') as f:
    json.dump(cfg, f, indent=2)
EOF
  echo "Updated development webhook URL."
fi

export ASPNETCORE_ENVIRONMENT=Development
export AZURE_FUNCTIONS_ENVIRONMENT=Development
export AzureFunctionsJobHost__extensions__durableTask__hubName="RecurringTasksDev"
# The Functions host and the Durable extension both need the storage
# connection under its plain name as well.
export AzureWebJobsStorage="$RecurringTasksBot__AzureWebJobsStorage"

if ! command -v func >/dev/null 2>&1; then
  echo "Azure Functions Core Tools ('func') not found." >&2
  echo "Install it first (macOS):" >&2
  echo "  brew tap azure/functions" >&2
  echo "  brew install azure-functions-core-tools@4" >&2
  exit 1
fi

echo "Starting Functions host (Development). Press Ctrl+C to stop."
# Start from the function app directory (validation above already ran at
# repo root). The runtime is pinned explicitly: Core Tools language
# detection does not recognize this project's target framework.
cd src/RecurringTasksBot
func start --dotnet-isolated
