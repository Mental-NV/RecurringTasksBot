# Telegram commands

All schedules are UTC. The bot answers in the same private chat.

## `/create <sec min hour day month weekday> <prompt>`

Six-field NCRONTAB, seconds fixed to `0`, with a future occurrence
required. Prompt text is 1–32,768 Unicode scalar characters; spaces and
line breaks are preserved. Oversized or empty prompts are rejected, never
truncated. Example:

```
/create 0 0 9 * * * Summarize today's AI news
```

Reply to a long message with `/create <schedule>` to use the replied-to
message as the prompt. Rich content (formatting, lists, tables, links) is
normalized to prompt text preserving reading order.

Creating stores the operation (schedule + prompt) and starts one
`RecurrenceLifecycle` orchestration. The stored text becomes the prompt
for every future occurrence.

## `/list`

Lists your operations with their IDs, schedules, and status.

## `/delete <id>`

Tombstones the operation and stops its orchestration. Tombstoned
operations are removed with their memory and artifacts by
[operator cleanup](Runbook.md); the operation row is deleted last.

## Unknown or invalid input

Unknown verbs, invalid schedules, and missing arguments answer with usage
text that discloses the external LLM/search processing. Nothing is
stored for invalid input.

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
