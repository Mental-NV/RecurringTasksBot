# Telegram commands

Tasks are JSON. The bot answers in the same private chat. All schedules
resolve in the task's IANA timezone (default UTC, immutable after
creation).

## `/create <JSON object>`

Fields: `prompt` (required, 1–32,768 Unicode scalars), `schedule.cron`
and/or `schedule.once` (at least one, with a future occurrence),
`timezone`, `parameters.memoryMode` / `reasoningEffort` / `webSearch` /
`expiresAt` / `maxOccurrences`. Omitted generation parameters inherit
the derived shared defaults (`memoryMode: IncludePreviousMessage`,
`reasoningEffort: max`, `webSearch: true`); explicit values always win,
and already-claimed occurrences keep their frozen settings. Example:

```
/create {"prompt": "Summarize today's AI news", "schedule": {"cron": "0 0 9 * * *"}, "timezone": "Europe/Moscow"}
```

Reply to a long message with `/create {"schedule": {...}}` to use the
replied-to message as the prompt. Rich content (formatting, lists,
tables, links) is normalized to prompt text preserving reading order.

Creating stores a `task_` row and starts one `TaskLifecycle`
orchestration. The confirmation shows the task ID/revision, limits,
and the next occurrences in local time with the UTC instant alongside.

## `/get <task-id> [explicit|effective]`

Shows saved settings (`explicit`, the default) or settings with
the current derived defaults applied (`effective`, same values new
occurrences inherit).

## `/update <task-id> <JSON patch object>`

Changes only the supplied fields. A patch that changes nothing
answers `unchanged` and preserves the revision; otherwise the revision
bumps by one. Only schedule or limit changes signal the orchestration
to replan (and ensure it is running) — prompt-only edits do not wake
it, and an already-claimed occurrence keeps its frozen settings.

## `/list [page] [compact]`

Lists your tasks with `ID`, `Status`, `Next`, `Expire`, and `Prompt`
columns — ten per page, grouped by timezone. `Expire` shows the earlier
of `expiresAt` and the projected time when `maxOccurrences` is reached,
in the task's timezone. The count projection uses remaining slots and the
current schedule; future delays or schedule edits can change it. A reached
count uses its recorded closure time. `—` means no applicable expiration
date (including a count the remaining schedule cannot reach). The default
rendering sends a native table with stacked text as the fallback;
`compact` sends the stacked text only.

Statuses: `active`, `executing`, `retrying`, `recovering`,
`completed`, `failed`. `unknown` means the task is active but its
orchestration health could not be confirmed (no readable runtime
status); `failed` means the orchestration confirmedly failed.

## `/delete <task-id>`

Tombstones the task and stops its orchestration (tombstone first,
termination best-effort). Deleted tasks disappear from `/list`; their
memory and retained rows are removed last by
[operator cleanup](Runbook.md).

## Unknown or invalid input

Unknown verbs, invalid JSON, bad schedules, and missing arguments
answer with usage text that discloses the external LLM/search
processing. Nothing is stored for invalid input.

## Receipts and redelivery

Every processed update writes an `update_<id>` receipt before replying.
A redelivered update with a completed receipt is acknowledged without
re-executing the command; an incomplete receipt resumes reply delivery.
Receipt-store outages answer `503` so Telegram retries later.

## HTTP outcomes

- `403` — missing or wrong `X-Telegram-Bot-Api-Secret-Token`. Checked
  before the body is parsed.
- `200` — processed, acknowledged-but-unsupported update, or
  authenticated malformed payload (no retry of poison bodies).
- `503` — transient receipt-store failure; Telegram should retry.
