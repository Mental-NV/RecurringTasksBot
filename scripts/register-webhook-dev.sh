#!/usr/bin/env bash
# Register an HTTPS tunnel URL with the DEVELOPMENT bot only.
# Usage: ./scripts/register-webhook-dev.sh <https-tunnel-url>
# Refuses to run when the token belongs to the production bot.
set -euo pipefail

TUNNEL_URL="${1:-}"
case "$TUNNEL_URL" in
  https://*) ;;
  *) echo "Usage: $0 <https-tunnel-url>" >&2; exit 2 ;;
esac

if [ -z "${RecurringTasksBot__Telegram__BotToken:-}" ]; then echo "Missing RecurringTasksBot__Telegram__BotToken" >&2; exit 1; fi
if [ -z "${RecurringTasksBot__Telegram__WebhookSecret:-}" ]; then echo "Missing RecurringTasksBot__Telegram__WebhookSecret" >&2; exit 1; fi

DEV_PROFILE="appsettings.Development.json"
PROD_PROFILE="appsettings.Production.json"
EXPECTED_DEV_ID=$(python3 -c "import json; print(json.load(open('$DEV_PROFILE'))['RecurringTasksBot']['ExpectedBotId'])")
PROD_BOT_ID=$(python3 -c "import json; print(json.load(open('$PROD_PROFILE'))['RecurringTasksBot']['ExpectedBotId'])")
ACTUAL_BOT_ID=$(curl -sS "https://api.telegram.org/bot${RecurringTasksBot__Telegram__BotToken}/getMe" | python3 -c "import json,sys; print(json.load(sys.stdin).get('result', {}).get('id', ''))")

if [ "$ACTUAL_BOT_ID" != "$EXPECTED_DEV_ID" ]; then
  echo "Token does not belong to the development bot. Refusing." >&2; exit 1
fi
if [ "$ACTUAL_BOT_ID" = "$PROD_BOT_ID" ] && [ "$PROD_BOT_ID" != "0" ]; then
  echo "Token belongs to the production bot. Refusing." >&2; exit 1
fi

WEBHOOK_URL="${TUNNEL_URL}/api/webhook"
response=$(curl -sS -X POST "https://api.telegram.org/bot${RecurringTasksBot__Telegram__BotToken}/setWebhook" \
  --data-urlencode "url=${WEBHOOK_URL}" \
  --data-urlencode "secret_token=${RecurringTasksBot__Telegram__WebhookSecret}" \
  --data-urlencode "allowed_updates=[\"message\"]")
echo "$response" | python3 -c "import json,sys; ok=json.load(sys.stdin).get('ok', False); print('Webhook registered.' if ok else 'Registration failed.'); sys.exit(0 if ok else 1)"
