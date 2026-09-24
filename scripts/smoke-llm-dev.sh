#!/usr/bin/env bash
# One paid development request through the bot's actual .NET adapter.
# Uses common + Development JSON profiles and environment overrides.
# Required: RecurringTasksBot__Llm__ApiKey (never printed).
# Optional: SMOKE_MODEL, SMOKE_PROMPT_FILE (path to a UTF-8 research prompt).
# No Telegram messages or storage writes. Ordinary CI must not run this.
set -euo pipefail
cd "$(dirname "$0")/.."
if [ -z "${RecurringTasksBot__Llm__ApiKey:-}" ]; then
  echo "Missing required env var: RecurringTasksBot__Llm__ApiKey" >&2
  exit 1
fi
exec dotnet run --project tools/LlmSmoke/LlmSmoke.csproj --configuration Release
