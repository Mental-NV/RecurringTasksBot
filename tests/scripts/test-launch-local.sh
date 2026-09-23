#!/usr/bin/env bash
# Focused regression test for scripts/launch-local.sh prerequisite handling:
# 1. missing `func` fails early with install guidance (not `command not found`);
# 2. present `func` reaches `func start`.
# Network-free: curl and func are PATH shims; storage check uses a dummy
# connection string whose AccountName matches appsettings.Development.json.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$REPO_ROOT"

SHIM="$(mktemp -d)"
trap 'rm -rf "$SHIM"' EXIT

cat > "$SHIM/curl" <<'EOF'
#!/usr/bin/env bash
echo '{"ok":true,"result":{"id":8898814847}}'
EOF
chmod +x "$SHIM/curl"

export RecurringTasksBot__Telegram__BotToken="dummy"
export RecurringTasksBot__Telegram__WebhookSecret="dummy-dev-secret-00000000000000000000"
export RecurringTasksBot__AzureWebJobsStorage="DefaultEndpointsProtocol=https;AccountName=devrecurringtasksbot;AccountKey=Zm9v;EndpointSuffix=core.windows.net"

# 1. `func` absent from PATH: must fail with install guidance.
if PATH="$SHIM:/usr/bin:/bin" bash scripts/launch-local.sh >"$SHIM/out1.txt" 2>&1; then
  echo "FAIL: expected non-zero exit when func is missing"
  exit 1
fi
grep -q "brew install azure-functions-core-tools" "$SHIM/out1.txt" \
  || { echo "FAIL: missing install guidance"; cat "$SHIM/out1.txt"; exit 1; }
echo "PASS: missing func fails early with install guidance"

# 2. `func` present: must reach `func start --dotnet-isolated` from the app directory.
cat > "$SHIM/func" <<'EOF'
#!/usr/bin/env bash
echo "FUNC_INVOKED $* IN $(pwd)"
EOF
chmod +x "$SHIM/func"
PATH="$SHIM:/usr/bin:/bin" bash scripts/launch-local.sh >"$SHIM/out2.txt" 2>&1 \
  || { echo "FAIL: unexpected non-zero exit with func present"; cat "$SHIM/out2.txt"; exit 1; }
grep -q "FUNC_INVOKED start --dotnet-isolated IN .*/src/RecurringTasksBot" "$SHIM/out2.txt" \
  || { echo "FAIL: func start --dotnet-isolated not run from src/RecurringTasksBot"; cat "$SHIM/out2.txt"; exit 1; }
echo "PASS: present func reaches func start --dotnet-isolated from the app directory"
