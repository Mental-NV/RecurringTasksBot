# Persistence

Business table `RecurringTaskData` (per-environment storage account).
Partition key is the owner (user) ID, so every occurrence transaction
stays single-partition (`RequireSamePartition` fails fast otherwise).

## Row kinds and keys

| RowKey | EntityKind | Properties |
| --- | --- | --- |
| `operation_<id>` | `operation` | `ChatId`, `Cron`, `Status` (`active`/`failed`/`deleted`), `InstanceId`, `FailureSummary`, `CreatedUtc`, `UpdatedUtc`, segmented `Text_*` (prompt) |
| `update_<updateId>` | `update_receipt` | `Command`, `OperationId`, `CommandCompleted`, `ReplyDelivered`, `CreatedUtc`, `UpdatedUtc` |
| `delivery_<op>_<ticks>` | (receipt; no kind tag) | `Status`, `Attempts`, `ErrorSummary`, `GenerationAttempts`, `ExecutionStatus`, `Provider`, `ModelName`, `PromptTokens`, `CompletionTokens`, `SearchResults`, `SearchUsed`, `SentParts`, `TotalParts`, `MessageIds`, `FailureNotice`, `UpdatedUtc`, `ClaimId`, `PayloadSchemaVersion`, `ContextVersion`, `ContextInitialized`, `AnswerVersion`, `PlanVersion`, `InstructionVersion`, `DeliveryTransientFailures`, `StorageConflicts`, `SchemaVersion` |
| `delivery_<op>_<ticks>__context_<v>` | `frozen_context` | `OwnerId`, `OperationId`, `ScheduledUtc`, `ContextVersion`, `CreatedUtc`, `ScheduleCron`, `OccurrenceId`, `ScheduledAtUtc`, `ExecutionStartedAtUtc`, `PreviousReplyPresent`, `PreviousReplyScheduledAtUtc`, `PreviousReplyExecutedAtUtc`, `NoMemoryReason`, segmented `Task_*`, `Instruction_*`, `Previous_*`, plus `TargetAnswerTextChars`, `MaxRichMessageChars`, `MemoryMode`, `EnabledCapabilities`, `InstructionVersion` |
| `delivery_<op>_<ticks>__answer_<v>` | `answer` | `AnswerVersion`, `Kind` (`answer`/`failure_notice`), `ScheduledUtc`, `SourceExecutedUtc`, `CreatedUtc`, segmented `Answer_*` |
| `delivery_<op>_<ticks>__plan_<v>` | `delivery_plan` | `PlanVersion`, `AnswerVersion`, `Revision`, `SourceSha256`, segmented `Plan_*` (serialized leaves) |
| `memory_<op>` | `memory` | `OwnerId`, `OperationId`, `AnswerVersion`, `SourceScheduledUtc`, `SourceExecutedUtc`, `PublishedUtc`, segmented `Answer_*` |

Codecs: `OperationRowCodec`, `OccurrenceEntityCodec` (`ContextProps`,
`AnswerProps`, `PlanProps`, `MemoryProps`, plus `ParseAnswer`,
`ParsePlan`, `ParseMemory`), `PlanRowCodec`, `TableRowKeys` — all in
`Infrastructure/Persistence`.

## Segmentation and validation

Long text is split into 16,000-Unicode-scalar chunks
(`StorageLimits.PropertyChunkScalars`) stored as `<prefix>_0000…` with
`<prefix>_Count`, `<prefix>_Scalars`, and `<prefix>_Sha256`. Reads
reassemble and verify scalar count and SHA-256; a missing chunk, count
mismatch, or hash mismatch fails as `payload_corrupt`.

Every row kind is validated on read, not defaulted:

- Operation/context/answer/plan/memory rows: `TableRow.RequireKind`
  requires the expected `EntityKind` and `SchemaVersion = 1`, then
  `GetString`/`GetInt`/`GetInt64`/`GetDto` require each property
  (`payload_corrupt` when absent or mistyped,
  `unsupported_payload_version` for other versions).
- Receipt rows: `ToReceipt` rejects a missing `SchemaVersion`
  (`payload_corrupt`) or any other version
  (`unsupported_payload_version`).
- Plans additionally cross-check `AnswerVersion`, `Revision`, and
  `SourceSha256` against the receipt and leaf content; the initial plan
  must match the generated answer's version and hash or publication
  refuses it.
- A receipt with exactly one of `AnswerVersion`/`PlanVersion` set is
  corruption (publication writes both atomically) and fails the
  occurrence without regenerating.

## Transactions and ETags

`OccurrenceTransactions` builds single-partition `Add`/`UpdateReplace`
batches; replaces carry the read ETag, so a concurrent writer surfaces
as a 412 conflict (retryable) or a lost claim (wait). Every mutating
builder rechecks the claim (`RequireClaim`) and operation liveness
(`RequireActive`) against freshly read state:

- Claim: conditional receipt insert/replace when unclaimed or the lease
  is stale (older than 15 minutes).
- Context init (`BuildInit`): `Add` context row + receipt replace
  (`ContextVersion`, `PayloadSchemaVersion`, conflict count) + operation
  fence touch.
- Generation publication (`BuildPersist`): `Add` answer row + `Add` plan
  row + receipt replace (usage, both versions, part counts); returns
  null without writing when pointers are already committed, so retries
  never regenerate.
- Leaf confirm/replace: plan replace with progress; idempotent replay
  returns no actions.
- Completion (`BuildComplete`): receipt replace (`sent`) plus memory
  `Add` (first success) or `UpdateReplace`; failure notices complete
  without memory publication.
- Track generation/delivery: counter increments with terminal-failure
  persistence; fail: `failed` status with the machine-readable summary.

## State and claim transitions

Claim creation writes the receipt with status `generating` (there is no
`starting` receipt status; `starting` is an operation status only).
Terminal states are `sent` / `failed`. Each executing activity owns a
unique conditional lease, renewed every minute and released on finish;
a crashed worker's lease expires after 15 minutes and recovery reclaims
it. Waiting for a claim consumes no generation/delivery retries.

## Size proof

`TableStorageLimits.PropertyBytes` charges each property its UTF-16
name bytes plus 16 bytes, then its UTF-16 value bytes for a string or
8 bytes for a non-string. A BMP scalar uses 2 bytes; a supplementary
Unicode scalar uses 4. These are application accounting totals for
custom properties, excluding `PartitionKey`, `RowKey`, `Timestamp`, and
HTTP serialization overhead.

At the configured maximum text lengths, supplementary characters give
the largest UTF-16 payloads:

- Answer: 131,072 scalars × 4 = 524,288 bytes (512 KiB), before
  metadata. Segmentation produces 9 chunks (8 × 16,000 + 3,072).
  `AnswerProps` emits **19 properties**: 7 fixed, 9 chunks, and the
  count/scalar-count/hash fields.
- Frozen context: previous answer 512 KiB + task prompt
  (32,768 × 4) 128 KiB + effective system instruction
  (16,384 × 4) 64 KiB = **704 KiB of text**, before metadata.
  With memory present, `ContextProps` emits **43 properties**: 20
  fixed, 14 chunks (9 previous + 3 task + 2 instruction), and 9
  segmentation metadata fields.
- A full 16,000-scalar chunk occupies at most 64,000 UTF-16 bytes,
  below 64 KiB.

Measured through the production codecs and transaction builders with
those text lengths, all supplementary characters, a 19-digit owner ID,
32-character operation/artifact/claim IDs, and short provider/model names:

| Entity or publication | Accounted bytes | KiB | Properties / actions |
| --- | ---: | ---: | --- |
| Answer entity | 525,290 | 512.98 | 19 properties |
| Frozen context entity | 724,012 | 707.04 | 43 properties |
| Context + receipt + operation fence | 725,504 | 708.50 | 3 actions |
| Answer + initial one-leaf plan + receipt + fence | 528,264 | 515.88 | 4 actions |
| Answer + 128-leaf plan + receipt + fence | 566,360 | 553.09 | 4 actions |

The 128-leaf case uses valid, unconfirmed plain leaves covering 1,024
scalars each, with IDs `p0#1:0#2:0#3:<index>`. These measurements are
reproducible stress cases, not universal maxima for variable metadata
or serialized requests.

`CheckEntityFits` enforces at most 128 custom properties and 900 KiB of
accounted data per entity; `CheckBatchFits` checks every entity and the
4 MiB accounted batch budget before publication. Thus even four entities
at the per-entity ceiling total 3,600 KiB, below 4 MiB. The measured
publications pass both guards; larger metadata is included in the same
checks and an oversized publication is rejected without splitting the
transaction.

## Cleanup and retention

Terminal (`sent`/`failed`) occurrences lose receipts and child artifacts
only past the cutoff, children before the parent receipt; unfinished
occurrences, `memory_` rows, and orphans younger than 24h are kept.
`update_` receipts inside the dedup window are always kept. See
[Runbook](Runbook.md) for commands and retention defaults.
