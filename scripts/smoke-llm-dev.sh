#!/usr/bin/env bash
# One paid development request through the bot's actual .NET adapter.
# Uses common + Development JSON profiles and environment overrides.
# The selected profile is RecurringTasksBot__Llm__ActiveProfile (default
# OpenRouter); only its credential is required and it is never printed.
# The shared .NET resolver owns selected-provider validation.
# Optional: SMOKE_MODEL, SMOKE_PROMPT_FILE (path to a UTF-8 research prompt).
# No Telegram messages or storage writes. Ordinary CI must not run this.
set -euo pipefail
cd "$(dirname "$0")/.."
export FUNCTIONAPP_CONFIG_DIR="$PWD/src/RecurringTasksBot.FunctionApp"
prompt_args=()
if [ -n "${SMOKE_PROMPT_FILE:-}" ]; then
  prompt_args=(--prompt "$(cat "$SMOKE_PROMPT_FILE")")
fi
model_args=()
if [ -n "${SMOKE_MODEL:-}" ]; then
  model_args=(--model "$SMOKE_MODEL")
fi
exec dotnet run --project tools/LlmSmoke/LlmSmoke.csproj --configuration Release -- \
  "${prompt_args[@]}" "${model_args[@]}" --execute
