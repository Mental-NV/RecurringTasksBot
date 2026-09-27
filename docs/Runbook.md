# Runbook

All commands use storage credentials only
(`AZURE_STORAGE_CONNECTION_STRING`, presence-checked, never printed).
Target the environment explicitly; `--table`/`--hub` override one piece:

```sh
export AZURE_STORAGE_CONNECTION_STRING="<selected-env-connection-string>"
./scripts/operator-cleanup.sh --env prod        # inventory only (dry run)
./scripts/operator-recover.sh recover --env prod
```

Cutover procedures live in [Deployment](Deployment.md); what follows is
steady-state operation.

## Diagnostics with content-free fields

Logs carry IDs, outcome, attempt counts, provider/model names, and
schema versions — never prompts, answers, history, or tokens. The
reply-source canary (`replySource=llm-authored|literal|
failure-notice-literal` with provider, model, receipt schema) answers
whether delivered text was LLM-authored or a fallback.

## Failed or stranded starts

```sh
./scripts/operator-recover.sh recover [--stale-minutes 15] [--apply]
./scripts/operator-recover.sh restart <operation-id> [--apply]
```

`recover` lists operation rows stuck in `starting` status older than the
window; `--apply` marks them `failed` so the original `/create` can
reschedule. `restart`
tombstones a `failed` operation (cause must already be resolved) so the
original `/create` safely recreates it. Orchestration startup itself
resumes through Telegram redelivery of stable operation IDs.

## Delivery and LLM failures

Terminal occurrence failures send a short notice identifying the
operation and occurrence; future runs stay scheduled. Provider errors
and credentials are never exposed. Generation retries (2) are separate
from delivery retries; `AnswerIncomplete`/`SourceLimit` never retry.
Classify via `LlmFailureKind` and `TelegramDisposition` (see
[LLM](LLM.md) and [TelegramTransport](TelegramTransport.md)).

## Leases

Each executing activity holds a conditional lease renewed every minute.
A crashed worker's lease expires after 15 minutes; another worker can
then claim. Waiting for a claim consumes no retries. Never delete a
live lease row to "unstick" work — investigate the owner activity first.

## Deletion races

Tombstoned-operation cleanup rechecks the tombstone before destructive
batches. If an operation resurrects mid-cleanup (row present, status
changed), the batch skips it.

## Cleanup, dry run, retention

```sh
./scripts/operator-cleanup.sh --env prod \
  --receipts-older-than-days 30 --deleted-ops-older-than-days 90 \
  --purge-orchestration-history-days 90 --keep-dedup-days 7 [--apply]
```

Dry run by default. Retention defaults: receipts 30d, deleted
operations 90d, terminal orchestration history 90d, retry-dedup guard
7d. History purge needs the hub (`--env` or `--hub`). See
[Persistence](Persistence.md) for what each pass preserves.

## Restart recovery

After host downtime the orchestration delivers at most one late
occurrence per operation, then resumes schedule. No operator action is
needed; use `recover` only for operations stuck in `starting`.
