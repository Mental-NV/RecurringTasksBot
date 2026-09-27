#!/usr/bin/env bash
# Deployment configuration tests (no secrets, no cloud).
# 1. Deploy workflow is operator-invoked only, single flow, params-file based.
# 2. main.parameters.json carries static values plus placeholders for
#    externally supplied values (never embedded secrets).
# 3. The resolver fills placeholders from the environment without printing them.
# 4. The packaged archive retains hidden extension directories.
# 5. launch-local exports the profile hub; operator scripts resolve --env
#    targets and refuse cross-environment credentials.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$REPO_ROOT"

fail() { echo "FAIL: $1"; exit 1; }
WF=".github/workflows/deploy-production.yml"
TEMPLATE="infra/main.parameters.json"

# 1. Workflow shape.
grep -q "workflow_dispatch" "$WF" || fail "deploy workflow lacks workflow_dispatch"
grep -qE "^[[:space:]]+push:" "$WF" && fail "deploy workflow still runs on push"
grep -q "fresh_cutover\|retire_old_state" "$WF" && fail "cutover/retirement branches remain"
grep -q "parameters: >" "$WF" && fail "inline Bicep parameter overrides remain"
grep -q "parameters: \${{ env.RESOLVED_PARAMS }}" "$WF" || fail "params file not passed to deployment"
grep -q "python3 - infra/main.parameters.json" "$WF" || fail "inventory step does not pass the template on stdin correctly"
grep -q "path: ./app.zip" "$WF" || fail "artifact upload is not the prebuilt zip"
grep -q "path: ./publish$" "$WF" && fail "publish directory uploaded directly (drops hidden dirs)"
for v in AZURE_CLIENT_ID AZURE_TENANT_ID AZURE_SUBSCRIPTION_ID \
    AZURE_RESOURCE_GROUP FUNCTION_APP_NAME PRODUCTION_WEBHOOK_URL; do
  grep -q "vars.$v" "$WF" || fail "workflow ignores GitHub variable $v"
done
echo "PASS: single-flow operator workflow uses vars and a params file"

# 2. Template statics and placeholders. The template is the single source
# for production storage/hub/table/bot values: no prod profile may compete.
py() { python3 -c "import json,sys; print(json.load(open('$TEMPLATE'))['parameters']['$1']['value'])"; }
[ "$(py tableName)" = "RecurringTaskData" ] || fail "template table wrong"
[ "$(py taskHubName)" = "RecurringTasksAppProd" ] || fail "template hub wrong"
[ "$(py storageAccountName)" = "prodrecurringtasksbot" ] || fail "template storage account wrong"
[ "$(py expectedBotId)" != "0" ] || fail "template keeps the zero bot-id placeholder"
[ -e "src/RecurringTasksBot.FunctionApp/appsettings.Production.json" ] \
  && fail "redundant production profile competes with the template"
for s in scripts/operator-cleanup.sh scripts/operator-recover.sh \
    scripts/poll-dev.sh scripts/register-webhook-dev.sh; do
  grep -q "main.parameters.json" "$s" || fail "$s does not read the canonical template for prod"
done
for p in functionAppName webhookUrl storageConnectionString telegramBotToken \
    telegramWebhookSecret llmApiKey; do
  v=$(py "$p")
  case "$v" in
    __*__) ;;
    *) fail "template parameter $p is not a placeholder ($v)" ;;
  esac
done
echo "PASS: parameter template carries statics and placeholders"

# 3. Resolver behavior with fixture values (never real secrets).
export FUNCTION_APP_NAME="func-fixture"
export PRODUCTION_WEBHOOK_URL="https://func-fixture.azurewebsites.net/api/webhook"
export DEPLOY_STORAGE_CONNECTION_STRING='Account=fixture;Key="a\b;c"'
export DEPLOY_TELEGRAM_BOT_TOKEN="fixture-bot-token"
export DEPLOY_TELEGRAM_WEBHOOK_SECRET="fixture-webhook-secret"
export DEPLOY_LLM_API_KEY="fixture-llm-key"
before=$(shasum -a 256 "$TEMPLATE" | cut -d' ' -f1)
export SHIM_RESOLVED="$(mktemp)"
out=$(bash scripts/resolve-deploy-parameters.sh --out "$SHIM_RESOLVED") \
  || fail "resolver failed"
[ "$(shasum -a 256 "$TEMPLATE" | cut -d' ' -f1)" = "$before" ] || fail "resolver modified the template"
python3 - "$SHIM_RESOLVED" <<'EOF' || fail "resolved values wrong"
import json, sys
params = json.load(open(sys.argv[1]))["parameters"]
assert params["functionAppName"]["value"] == "func-fixture"
assert params["webhookUrl"]["value"] == "https://func-fixture.azurewebsites.net/api/webhook"
assert params["storageConnectionString"]["value"] == 'Account=fixture;Key="a\\b;c"'
assert params["telegramBotToken"]["value"] == "fixture-bot-token"
assert params["tableName"]["value"] == "RecurringTaskData"
assert not any(v.startswith("__") for p, v in
    ((n, e["value"]) for n, e in params.items()) if isinstance(v, str))
EOF
echo "$out" | grep -q "fixture" && fail "resolver printed a supplied value"
rm -f "$SHIM_RESOLVED"
if FUNCTION_APP_NAME="" bash scripts/resolve-deploy-parameters.sh --out /tmp/should-not-exist.json 2>/dev/null; then
  fail "resolver accepts missing variables"
fi
echo "PASS: resolver fills placeholders without leaking values"

# 4. Archive contents: publish, zip as the workflow does, inspect.
PUB="$(mktemp -d)"
trap 'rm -rf "$PUB"' EXIT
dotnet publish src/RecurringTasksBot.FunctionApp/RecurringTasksBot.FunctionApp.csproj \
  --configuration Release --output "$PUB/publish" >/dev/null \
  || fail "publish failed"
(cd "$PUB/publish" && zip -qr "$PUB/app.zip" .) || fail "zip failed"
listing=$(unzip -l "$PUB/app.zip")
for entry in ".azurefunctions/" "host.json" "appsettings.json" \
    "appsettings.Development.json" \
    "RecurringTasksBot.FunctionApp.dll" "RecurringTasksBot.Application.dll"; do
  echo "$listing" | grep -qF "$entry" || fail "archive lacks $entry"
done
[ -e "$PUB/publish/appsettings.Production.json" ] && fail "redundant prod profile shipped"
[ "$(echo "$listing" | grep -c "azurefunctions")" -gt 5 ] \
  || fail "archive lacks extension assemblies"
echo "PASS: deployment archive retains hidden extension content"

# 5. launch-local platform hub: profile by default, override honored.
SHIM="$(mktemp -d)"
trap 'rm -rf "$SHIM" "$PUB"' EXIT
cat > "$SHIM/curl" <<'EOF'
#!/usr/bin/env bash
echo '{"ok":true,"result":{"id":8898814847}}'
EOF
cat > "$SHIM/func" <<'EOF'
#!/usr/bin/env bash
echo "EFFECTIVE_HUB=$AzureFunctionsJobHost__extensions__durableTask__hubName"
EOF
chmod +x "$SHIM/curl" "$SHIM/func"
export RecurringTasksBot__Telegram__BotToken="dummy"
export RecurringTasksBot__Telegram__WebhookSecret="dummy-dev-secret-00000000000000000000"
export RecurringTasksBot__AzureWebJobsStorage="DefaultEndpointsProtocol=https;AccountName=devrecurringtasksbot;AccountKey=Zm9v;EndpointSuffix=core.windows.net"
export RecurringTasksBot__Llm__ApiKey="dummy-llm-key"
DEV_PROFILE_HUB=$(python3 -c "import json; print(json.load(open('src/RecurringTasksBot.FunctionApp/appsettings.Development.json'))['RecurringTasksBot']['TaskHubName'])")
out=$(PATH="$SHIM:/usr/bin:/bin" bash scripts/launch-local.sh 2>/dev/null | grep EFFECTIVE_HUB) \
  || fail "launch-local did not reach func start"
[ "${out#EFFECTIVE_HUB=}" = "$DEV_PROFILE_HUB" ] || fail "default platform hub wrong"
out=$(TASK_HUB_NAME="ThrowawayHub" PATH="$SHIM:/usr/bin:/bin" bash scripts/launch-local.sh 2>/dev/null | grep EFFECTIVE_HUB) \
  || fail "launch-local with override did not reach func start"
[ "${out#EFFECTIVE_HUB=}" = "ThrowawayHub" ] || fail "override platform hub wrong"
echo "PASS: platform hub matches runtime profile by default and honors overrides"

# 6. Operator --env resolution and credential guard.
export AZURE_STORAGE_CONNECTION_STRING="DefaultEndpointsProtocol=https;AccountName=devrecurringtasksbot;AccountKey=Zm9v;EndpointSuffix=core.windows.net"
out=$(bash scripts/operator-cleanup.sh --env dev 2>&1) \
  || fail "operator-cleanup --env dev failed"
echo "$out" | grep -q "Target table: RecurringTaskData; hub: RecurringTasksAppDev (env dev)" \
  || fail "cleanup dev targets wrong: $out"
export AZURE_STORAGE_CONNECTION_STRING="DefaultEndpointsProtocol=https;AccountName=prodrecurringtasksbot;AccountKey=Zm9v;EndpointSuffix=core.windows.net"
out=$(bash scripts/operator-cleanup.sh --env prod 2>&1) \
  || fail "operator-cleanup --env prod failed"
echo "$out" | grep -q "Target table: RecurringTaskData; hub: RecurringTasksAppProd (env prod)" \
  || fail "cleanup prod targets wrong: $out"
if bash scripts/operator-cleanup.sh --env dev >/dev/null 2>&1; then
  fail "cleanup --env dev with prod credentials should refuse"
fi
export AZURE_STORAGE_CONNECTION_STRING="DefaultEndpointsProtocol=https;AccountName=devrecurringtasksbot;AccountKey=Zm9v;EndpointSuffix=core.windows.net"
if PATH="$SHIM:/usr/bin:/bin" bash scripts/operator-recover.sh recover --env prod >/dev/null 2>&1; then
  fail "recover --env prod with dev credentials should refuse"
fi
# A storage shim lets refusal checks prove the guard (not a missing CLI)
# is what stops cross-environment runs.
cat > "$SHIM/az" <<'EOF'
#!/usr/bin/env bash
echo '[]'
EOF
chmod +x "$SHIM/az"
# Flags after the subcommand/operation id still take effect, and unknown
# flags are rejected before any storage command runs.
export AZURE_STORAGE_CONNECTION_STRING="DefaultEndpointsProtocol=https;AccountName=prodrecurringtasksbot;AccountKey=Zm9v;EndpointSuffix=core.windows.net"
if PATH="$SHIM:/usr/bin:/bin" bash scripts/operator-recover.sh restart op1 --env dev >/dev/null 2>&1; then
  fail "restart --env dev with prod credentials should refuse"
fi
export AZURE_STORAGE_CONNECTION_STRING="DefaultEndpointsProtocol=https;AccountName=devrecurringtasksbot;AccountKey=Zm9v;EndpointSuffix=core.windows.net"
if PATH="$SHIM:/usr/bin:/bin" bash scripts/operator-recover.sh recover --bogus-flag >/dev/null 2>&1; then
  fail "unknown recover flag should be rejected"
fi
if PATH="$SHIM:/usr/bin:/bin" bash scripts/operator-recover.sh restart op1 --bogus-flag >/dev/null 2>&1; then
  fail "unknown restart flag should be rejected"
fi
if bash scripts/operator-cleanup.sh --purge-orchestration-history-days 1 >/dev/null 2>&1; then
  fail "history purge without hub should require --env or --hub"
fi
export AZURE_STORAGE_CONNECTION_STRING="DefaultEndpointsProtocol=https;AccountName=prodrecurringtasksbot;AccountKey=Zm9v;EndpointSuffix=core.windows.net"
out=$(PATH="$SHIM:/usr/bin:/bin" bash scripts/operator-recover.sh recover --env prod 2>&1) \
  || fail "operator-recover --env prod failed"
echo "$out" | grep -q "Target table: RecurringTaskData (env prod)" \
  || fail "recover prod target wrong: $out"
echo "PASS: operator scripts resolve targets and guard credentials"
