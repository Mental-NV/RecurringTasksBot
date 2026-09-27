#!/usr/bin/env bash
# Resolve infra/main.parameters.json into a temporary deployment parameter
# file. Static values stay in the template; externally supplied values come
# from the environment and are never printed.
#
# Usage: ./scripts/resolve-deploy-parameters.sh [--template PATH] --out PATH
#
# Required env (names only are ever reported):
#   FUNCTION_APP_NAME, PRODUCTION_WEBHOOK_URL,
#   DEPLOY_STORAGE_CONNECTION_STRING, DEPLOY_TELEGRAM_BOT_TOKEN,
#   DEPLOY_TELEGRAM_WEBHOOK_SECRET, DEPLOY_LLM_API_KEY
set -euo pipefail

TEMPLATE="infra/main.parameters.json"
OUT=""

while [ $# -gt 0 ]; do
  case "$1" in
    --template) TEMPLATE="${2:-}"; shift 2 ;;
    --out) OUT="${2:-}"; shift 2 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

[ -n "$OUT" ] || { echo "Missing required --out PATH" >&2; exit 2; }
[ -f "$TEMPLATE" ] || { echo "Missing template $TEMPLATE" >&2; exit 1; }

missing=""
for v in FUNCTION_APP_NAME PRODUCTION_WEBHOOK_URL DEPLOY_STORAGE_CONNECTION_STRING \
    DEPLOY_TELEGRAM_BOT_TOKEN DEPLOY_TELEGRAM_WEBHOOK_SECRET DEPLOY_LLM_API_KEY; do
  if [ -z "${!v:-}" ]; then missing="$missing $v"; fi
done
if [ -n "$missing" ]; then echo "Missing required env:$missing" >&2; exit 1; fi

TEMPLATE_PATH="$TEMPLATE" OUT_PATH="$OUT" python3 - <<'PYEOF'
import json, os

template = os.environ["TEMPLATE_PATH"]
out = os.environ["OUT_PATH"]
with open(template) as f:
    raw = f.read()

subs = {
    "__FUNCTION_APP_NAME__": os.environ["FUNCTION_APP_NAME"],
    "__PRODUCTION_WEBHOOK_URL__": os.environ["PRODUCTION_WEBHOOK_URL"],
    "__STORAGE_CONNECTION_STRING__": os.environ["DEPLOY_STORAGE_CONNECTION_STRING"],
    "__TELEGRAM_BOT_TOKEN__": os.environ["DEPLOY_TELEGRAM_BOT_TOKEN"],
    "__TELEGRAM_WEBHOOK_SECRET__": os.environ["DEPLOY_TELEGRAM_WEBHOOK_SECRET"],
    "__LLM_API_KEY__": os.environ["DEPLOY_LLM_API_KEY"],
}
doc = json.loads(raw)
missing = [p for p in subs if p not in json.dumps(doc)]
if missing:
    raise SystemExit(f"Template {template} lacks placeholders: {' '.join(missing)}")

def resolve(node):
    if isinstance(node, dict):
        return {k: resolve(v) for k, v in node.items()}
    if isinstance(node, list):
        return [resolve(v) for v in node]
    return subs.get(node, node)

resolved = resolve(doc)
for name, entry in resolved.get("parameters", {}).items():
    if isinstance(entry.get("value"), str) and entry["value"].startswith("__"):
        raise SystemExit(f"Unresolved placeholder left in parameter '{name}'")
with open(out, "w") as f:
    json.dump(resolved, f, indent=2)
    f.write("\n")
print(f"Resolved {len(resolved.get('parameters', {}))} parameters from {template}.")
PYEOF
