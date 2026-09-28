# Phase 5: task creation, settings, and updates

Final specification for JSON-only `/create`, explicit/effective `/get`, partial
`/update`, and compact `/list`. Task timezone is permanently immutable. Use fresh
storage with no migration or backward compatibility for old tasks.

## 1. Command contract

```text
/create <JSON object>
/get <task-id> [explicit|effective]
/update <task-id> <JSON patch object>
/list [page] [compact]
```

Create/update share field definitions. Create requires `prompt` and `schedule`;
update changes only supplied fields. Generation parameters inherit live global
defaults unless explicitly overridden; schedule identity and limits do not.

| Field | Accepted values | Default / inheritance |
| --- | --- | --- |
| `prompt` | Nonempty text; existing 32,768 Unicode character limit | Required |
| `schedule.cron` | Optional six-field expression, entered in the selected timezone | Absent |
| `schedule.once` | Array of 0–10 local date-times | `[]` |
| `timezone` | Immutable IANA zone ID for calendar, input, and display; also `UTC` | `UTC`, fixed at creation |
| `parameters.memoryMode` | `IncludePreviousMessage`, `None` | Inherit global `None` |
| `parameters.reasoningEffort` | `low`, `med`, `high`, `xhigh`, `max` | Inherit global `low` |
| `parameters.webSearch` | Boolean | Inherit global `true` |
| `parameters.expiresAt` | Local date-time in the task timezone; exclusive deadline for starting new occurrences | `null` (no deadline) |
| `parameters.maxOccurrences` | Positive integer; maximum lifetime occurrences started across both schedule sources | `null` (unlimited) |

At least one schedule source is required. Both may be supplied together.
Cron-only and one-time-only tasks use the same contract.

**Combined task:** the fifth of every month at 09:00, plus two extra dates.

```text
/create {
  "prompt": "Review the release checklist",
  "schedule": {
    "cron": "0 0 9 5 * *",
    "once": ["2026-12-08T09:00:00", "2026-12-19T15:00:00"]
  },
  "timezone": "Europe/Moscow",
  "parameters": {
    "reasoningEffort": "high",
    "expiresAt": "2027-01-01T00:00:00",
    "maxOccurrences": 5
  }
}
```

Retain `0 0 9 5 * *` in `Europe/Moscow`. For example, the December cron run
resolves to `2026-12-05T06:00:00Z`; the extra dates resolve to
`2026-12-08T06:00:00Z` and `2026-12-19T12:00:00Z`.

**Partial update:**

```text
/update <task-id> {
  "prompt": "Summarize today's AI infrastructure news",
  "parameters": {"reasoningEffort": "med", "webSearch": false}
}
```

### Global defaults and task overrides

One global `TaskDefaults` section replaces the overlapping memory/reasoning/search
settings as their authoritative configuration source:

```json
{"memoryMode": "None", "reasoningEffort": "low", "webSearch": true}
```

`effective value = explicit task override, otherwise current global default`.
Store only explicit overrides on the task. Changing a global default affects
future occurrences of existing inheriting tasks after configuration rollout;
tasks do not need recreation. Hot configuration reload is not required.

At occurrence initialization, resolve all three values from one configuration
snapshot and freeze them with its revision. Retries retain that snapshot.
The adapter receives these effective settings and must not override them again
from provider configuration. Validate global defaults at startup/config rollout
and validate the effective task settings before a new occurrence starts.

**Creation-time exceptions:** resolve the task timezone once (default `UTC`);
keep it as task identity rather than a live global preference. Prompt and schedule
are explicit task data. `expiresAt` and `maxOccurrences` are task-only limits,
initially unlimited. Credentials, provider transport, and hard resource ceilings
remain operator configuration, outside the task override contract. Future fields
must declare whether they inherit or are fixed task settings.

### `/get <task-id> [explicit|effective]`

**`explicit` is the default.** Return the saved user-authored JSON object directly,
using the same field names as `/create`. This means the current explicit definition
after updates, not an immutable copy of the initial create message. Preserve
omitted fields and entered values; do not inject defaults, state, or resolved UTC
fields. JSON whitespace/key order need not match the original text. A prompt taken
from a replied-to message is stored as an explicit `prompt`.

**`effective` returns the same task-settings JSON structure**, with missing
values filled from the current global defaults and fixed task defaults. Preserve
explicit overrides. Do not add an envelope, mode, IDs, revisions, runtime state,
value-source labels, or other diagnostic fields. For example, a task with only
`reasoningEffort: "high"` overridden returns:

```json
{
  "prompt": "Review the release checklist",
  "schedule": {"cron": "0 0 9 5 * *", "once": []},
  "timezone": "Europe/Moscow",
  "parameters": {
    "memoryMode": "None",
    "reasoningEffort": "high",
    "webSearch": true,
    "expiresAt": null,
    "maxOccurrences": null
  }
}
```

Fill `timezone` and `schedule.once` when omitted; leave `schedule.cron` absent when
there is no recurrence. Include all effective parameters, with `null` for unlimited
stop limits. These are settings for a new occurrence; an active run can retain an
older snapshot. Resolve one consistent task/defaults snapshot internally, without
including its revision or source metadata in the JSON. Label the response mode
outside the JSON (or in the attachment filename) for readability.

Require ownership; missing, deleted, and another owner's task all return
`task_not_found`. Active/completed/failed tasks remain inspectable. Unknown modes
or extra arguments are rejected. `/get` does not change task state or timers.

Use valid JSON with lossless prompt escaping. Send a code block when it fits the
message limit, otherwise a UTF-8 JSON attachment. Never truncate or silently
filter past explicit dates. Normal command receipt handling applies.

### `/update` preserves inheritance

- Treat the input as a patch against the **stored explicit definition**, never
  against the effective/default-filled view. Omission preserves the existing
  override or its absence. Do not write resolved defaults into task storage.
- A concrete generation parameter creates/changes an override, even when equal
  to today's global default. `null` removes that override and restores inheritance.
  For task-only `expiresAt`/`maxOccurrences`, `null` means no limit. These semantics
  also apply on create; an inheritance reset is stored as an omitted field.
- Compare with the explicit definition before applying changes. Re-submitting an
  unchanged schedule/deadline is a no-op for those fields: do not reset activation,
  the waterline, the count, or revalidate past dates as new input. A changed
  `schedule` replaces both sources and undergoes full replacement validation.
- Use `/get <task-id> explicit` as the editing source and submit small patches.
  A complete effective object is valid settings-shaped input: its supplied
  concrete generation values become overrides. This intentionally pins those
  values. Never infer inheritance from equality with a current global default,
  or fill omitted patch fields before writing.

```text
/update <task-id> {"prompt": "Review today's release checklist"}
/update <task-id> {"parameters": {"reasoningEffort": "high"}}
/update <task-id> {"parameters": {"reasoningEffort": null}}
```

These change only the prompt, pin reasoning to `high`, and restore inherited
reasoning, respectively. A nonempty patch that changes nothing returns `unchanged`
without advancing the task revision.

### Common command validation

Enforce the field allowlists in Section 4 before applying a command. Reject
unknown/duplicate keys, system-managed fields, wrong types, unsupported nulls,
and empty patches. Validate the resulting definition and effective settings
atomically; no partial application. Enforce task ownership for every task command;
never trust owner/destination identifiers supplied in JSON. Schedule replacement
omits `cron` to remove recurrence and omits `once` (or uses `[]`) to remove explicit
dates. At least one source must remain.

Legacy positional `/create` returns JSON usage help and creates nothing. JSON
`/create` may still use a replied-to message from the same user when `prompt` is
omitted. Update requires an explicit `prompt` field to change it.

Confirm changes with task ID/revision, changed overrides or inheritance resets,
limits/count remaining, and up to three eligible local/UTC occurrences. Make clear
when an active run retains older settings. Default changes alone do not increment
task revisions; the defaults revision identifies the effective configuration.

### `/list [page] [compact]`

Show owner-scoped tasks as a compact table with ten tasks per page. Group rows by
task timezone and show zone/year once in each group; include the year in a cell
when its occurrence belongs to a different year. Columns are:

| ID | Status | Next | Prompt |
| --- | --- | --- | --- |
| a31f9c | executing | 05 Oct 09:00 | AI news… |
| b82d04 | active | 06 Oct 12:00 | Review release… |

- **Use `executing` in Status.** Next denotes the following scheduled occurrence,
  excluding the already claimed one; show `—` if none is eligible. A due/overdue
  task is not automatically executing. Label retry waits `retrying`.
- Derive display status from current occurrence/lease information, not Durable's
  generic running status (which includes timer waits). A stale/unconfirmed lease
  shows `recovering` or `unknown`; task lifecycle status remains separate. This is
  a snapshot when `/list` is requested, not a continuously updating display.
- Display a unique owner-scoped ID prefix, initially six characters and lengthened
  when necessary. `/get`, `/update`, and `/delete` accept a full ID or a unique
  prefix of at least six characters. Reject ambiguous prefixes; never guess.
- Use Telegram's native table with compact cells; allow date/time cell wrapping,
  normalize prompt whitespace, and truncate the preview to at most three words
  and 18 display characters with an ellipsis. `/get` preserves the full prompt.
  Paginate without splitting a task row; repeat headers and timezone labels.
- No API exposes the recipient's available pixel width. Validate supported mobile
  layouts and provide a stacked fallback for clients that cannot render the table;
  offer `/list <page> compact` to request it explicitly. Font size can still affect
  rendering. Do not promise a universal four-column width guarantee.
  ([Telegram compact tables](https://core.telegram.org/bots/api#inputrichblocktable))

## 2. Time model: one schedule in a named timezone

**User contract:** `cronExpression` + `timezone` defines when a task is due.
For example, `0 0 9 5 * *` in `Europe/Berlin` always means the fifth of the month
at 09:00 on Berlin's calendar, subject to the explicit DST policies below.
The timezone is part of the schedule, independent of the server or viewer.

Use one authoritative local calendar rule, one occurrence resolver,
and UTC execution records. Conversion back to local time is ordinary display
formatting; it is not a validation gate or part of recurrence calculation.

| Responsibility | Representation and rule |
| --- | --- |
| Task settings | Store the explicit cron and resolved IANA timezone with a schedule revision. The timezone is fixed for the task lifetime; timezone-data handling is defined below. `/get <task-id> explicit` preserves user-authored fields. |
| Occurrence resolver | Accept the rule, zone, and a UTC search boundary; return the next UTC instant or the latest due instant. Own all calendar matching and DST policy in this one component. |
| Execution | Persist `scheduledUtc`, contributing sources, and revision; use UTC for timers, ordering, waterline, expiration, and deduplication. |
| Presentation | Show the saved local rule and format resolved instants in the task timezone, including their offsets. Presentation never feeds back into scheduling. |

Resolve occurrences on demand through a tested timezone-aware adapter. Use the
timezone library's gap/ambiguity APIs and preserve the existing cron dialect.
There is no converted UTC cron, fixed-offset approximation, or manually shifted
day/month field. Retain local one-time inputs alongside their resolved UTC dates.
The same resolver powers previews, future timers, and latest-only catch-up.

Persist a planned UTC result before creating its timer. On wake-up, reselect the
latest due occurrence above the waterline and check limits before claiming it.
Once claimed, its UTC instant and settings remain fixed across retries.

This follows the calendar principle of retaining a local recurrence with a
zone reference to preserve local times. ([RFC 5545, recurrence rules](https://www.rfc-editor.org/rfc/rfc5545#section-3.8.5.3))

**Durable boundary:** resolve occurrences in an idempotent planning activity;
return persisted UTC results to the orchestrator. Timezone database access stays
outside orchestration replay. Next-run previews and catch-up use this same planner,
so they cannot interpret a rule differently from execution.

### Predictable behavior at boundaries

| Case | Policy |
| --- | --- |
| Midnight, month/year rollover | Match local calendar fields first, then convert the complete date-time. Never shift day/month fields manually. |
| Monthly day 29/30/31 absent from a month | Skip that invalid date; never clamp to month-end or move into the next month. |
| Cron time skipped by DST | Skip that occurrence; never move it to a different local time. |
| Cron time repeated by DST | Run once at the earlier UTC instant. Consume that local occurrence; never run its second offset interpretation. |
| Explicit date in a DST gap or repeated hour | Reject with a field error and ask for an unambiguous local time. |
| Half-hour / quarter-hour offsets | Resolve through timezone data, including the complete offset; no whole-hour assumptions. |

The gap and repeated-hour rules are explicit product policies, not defaults
inherited from a cron library. .NET exposes checks for
[invalid local times](https://learn.microsoft.com/en-us/dotnet/api/system.timezoneinfo.isinvalidtime?view=net-10.0)
and [ambiguous offsets](https://learn.microsoft.com/en-us/dotnet/api/system.timezoneinfo.getambiguoustimeoffsets?view=net-10.0).
A rule matching both day-of-month and weekday retains the existing **OR** semantics;
additional weekday matches can therefore occur independently of a missing month day.

Local calendar schedules keep the requested local hour as UTC offsets change.
A daily rule therefore does not promise a fixed 24-hour interval. Missing local
times are skipped and repeated local times run once. `timezone: "UTC"` supplies
UTC recurrence through the same resolver.

Acceptance examples: a monthly-first rule in Moscow stays on March 1 and
January 1 locally even when the execution instant falls in the previous UTC
month/year. Berlin's monthly-fifth 09:00 stays at 09:00 in January and July, while
its UTC execution hour changes. These are calendar expectations for the resolver.
The waterline may still consume missed occurrences without executing each one,
as defined in Section 3.

### Constraints and validation

- One cron expression plus at most ten explicit dates; require at least one
  source. Use the full existing six-field grammar in every supported timezone,
  with seconds exactly `0`. Reject unsupported syntax (`?`, `L`, `W`, `#`, year
  fields, macros). There are no conversion restrictions on multiple hours,
  weekday rules, or month boundaries.
- Accept `UTC` or a resolvable IANA ID; reject abbreviations and unknown zones.
  Never fall back to the server's local timezone. Date input is exactly
  `YYYY-MM-DDTHH:mm:00`, without offset, `Z`, or fractions; `timezone: "UTC"`
  uses that same input format.
- Validate cron syntax, zone availability, local calendar dates, supported time
  range, and explicit gap/ambiguity policy. Require a future resolved cron
  occurrence within the existing search horizon (about five years), even when
  explicit dates are also supplied. Invalid recurring dates are skipped.
- Verify the resolver against known calendar/DST examples. Its next result must
  be strictly after the supplied UTC boundary; its latest-due result must be the
  greatest eligible instant at or before `nowUtc`, above the waterline. Returned
  instants must be ordered and unique. Do not validate schedules by converting
  formatted display values back into execution input.
- New/replaced explicit dates must be unique and strictly future in UTC at commit.
  Apply the ten-entry limit before normalization, sort resolved dates, and merge collisions
  with cron. A new/changed expiration uses the same local format and DST validation,
  resolves to `expiresAtUtc`, and must be strictly future at commit. New/replaced
  explicit dates must precede that deadline. Require at least one eligible future
  occurrence when creating/reactivating a task. `maxOccurrences` must be an integer
  of at least 1 (no fractions, strings, or coercion). Unrelated updates retain
  existing pending dates; tightening a limit may intentionally end the task.
- Reject the whole command on failure. Return field path, stable error code, and
  correction: `timezone_invalid`, `cron_invalid`, `local_time_nonexistent`,
  `local_time_ambiguous`, `schedule_no_future_occurrence`, `schedule_date_invalid`,
  `expiration_invalid`, or `occurrence_limit_invalid`.
  Command redelivery reuses the committed definition and resolved results.

### Temporal naming and expiration

Use consistent names according to meaning: user-entered `expiresAt` and explicit
dates use the task timezone; internal `expiresAtUtc`, `scheduledUtc`, and
`waterlineUtc` are absolute UTC instants, serialized with `Z`. Local formatting is
presentation, not a second source of schedule truth. Keep the internal UTC names.

**Expiration:** resolve a submitted `expiresAt` once and keep that
absolute deadline until explicitly edited. DST/configuration changes do not extend
it. An unchanged value in a patch must not be re-resolved. Explicit `/get` preserves
the submitted local value; effective output renders the stored deadline in the task
zone. The task timezone cannot change. Deadline storage and execution state keep
UTC names internally; neither get mode adds these internal fields to settings.

### Immutable timezone

Resolve `timezone` at creation (`UTC` when omitted). `/update` may omit it or repeat
the stored ID, but must reject a different ID with `timezone_immutable`. Null is
invalid. A repeated ID is a no-op; it never recalculates dates or deadlines.
Changing the timezone always requires a new task, with new identity, memory,
waterline, and count. The original task remains until explicitly deleted.

Timezone editing and automatic timezone-data reconciliation are out of scope,
with no planned feature to introduce them later. Prompt, cron, explicit dates,
generation parameters, and stop limits remain editable.

Bundle one known timezone-data version and retain it across ordinary releases;
never silently switch to host-local timezone rules. Existing DST transitions in
that data work normally. Government rule changes can eventually make pinned data
differ from official local time. Treat a timezone-data upgrade as deliberate
operator maintenance, outside the command API and this implementation's automatic
workflows. It must not silently reinterpret existing task state.
([IANA explains why timezone data changes](https://data.iana.org/time-zones/tz-link.html))

## 3. Combined schedule, one waterline, and stop limits

Treat the schedule as the **union of cron occurrences resolved to UTC and explicit
UTC dates**. Coincident instants produce one occurrence.
([RFC 5545, recurrence dates](https://www.rfc-editor.org/rfc/rfc5545#section-3.8.5.2))

### One shared waterline

Persist one system-managed `waterlineUtc` per task. It is the **scheduled UTC time
through which work has been consumed**, including skipped catch-up occurrences.
Every instant **at or below** it is done for scheduling purposes. It is not the
worker's start/finish time and does not imply every earlier occurrence succeeded.
Initialize it to creation commit time so new tasks never backfill history.

Use this rule for both cron and explicit dates; no separate source cursors or
explicit-date completion flags are needed to decide what to run:

1. Resume an already claimed occurrence first, preserving its scheduled time and
   frozen settings. Retries never select a newer date.
2. Before claiming new work, check the stop limits. Capture `nowUtc` and find the
   **latest** merged scheduled instant in `(waterlineUtc, nowUtc]`. Run only that
   instant; all older due instants will be consumed with it. Exact collisions
   share one occurrence, one result, and one count increment.
3. If nothing is due, wait until the earliest future merged instant or expiration,
   whichever comes first. Recheck limits, revision, and latest due time on wake-up.
4. Claim the selected occurrence and reserve its count atomically. Serialize all
   work for a task. Keep the waterline unchanged while this occurrence is active;
   its existing receipt/claim identifies work to resume after a crash.
5. On terminal success **or exhausted failure**, atomically record the outcome
   and set `waterlineUtc = max(waterlineUtc, scheduledUtc)`. Then repeat. Transient
   failure leaves the waterline unchanged. A crash after commit must not rerun it.

Example: waterline `08:00Z`; due instants `09:00Z` (cron), `09:30Z` (explicit),
and `10:00Z` (cron); current time `10:20Z`. Execute only `10:00Z`, then advance
waterline to `10:00Z`. If it finishes at `10:40Z`, a newly due `10:30Z` remains
eligible for the next pass. Using finish time as the waterline would lose it.

Downtime and long runs can discard older explicit dates as well as
cron occurrences. This is intentional: keep the latest due work, with at most one
catch-up occurrence selected per pass. Skipped dates consume no occurrence count.
Historical receipts may describe outcomes but are not additional schedule cursors.

### Stop limits: first reached wins

Both limits are optional; by default there is no expiration or count limit.
A finite schedule still completes naturally when no eligible dates remain.

| Limit / event | Rule |
| --- | --- |
| `expiresAt` | Convert local input once to `expiresAtUtc`. Start no new occurrence when `nowUtc >= expiresAtUtc`; instants at or after that deadline are ineligible. After downtime beyond expiration, do not catch up even pre-expiration dates. |
| `maxOccurrences` | Lifetime maximum **started occurrences across cron and explicit dates**. Reserve one slot at the first successful claim; retries, collisions, and Telegram redelivery consume no extra slots. Terminal failures still count. |
| Both supplied | Start only while both allow it. Once either closes, no further starts. An already claimed occurrence and its retries may finish normally. |
| Completion | Once active work finishes, set `completed` with reason `expired`, `max_occurrences`, or `schedule_exhausted`. Retain the first stop reason reached; use expiration as the tie-breaker if both close in the same update. |
| Updating limits | Omitted fields retain values; `null` removes a limit. Increasing/removing a limit can reactivate a completed task if eligible future work remains. Lowering the count to/below the consumed count stops further starts immediately. Never reset the count or waterline. |

Keep `startedOccurrences` as system-managed accounting, not a second time cursor.
The execution claim transaction checks operation liveness, schedule revision,
waterline, current expiration, and available count together, then increments the
count only when creating a new occurrence. Resume the same receipt after a crash;
an abandoned claim must not consume another slot. These checks serialize races
between update/delete, limit changes, and execution admission.

An expiration-only update on an active task preserves the calendar rule and
waterline; wake the orchestration to replace its deadline timer. Reactivation
follows the activation-boundary rule below. Already-resolved expiration remains
an absolute UTC deadline when timezone rules change; resubmit `expiresAt` to change
it. Confirm the deadline in local time and UTC.

### Schedule updates and retained progress

Never move the waterline backward. Replacing a schedule discards old unstarted
dates; reactivating a completed task skips dates from its inactive period. Both
admit future work from the update commit time. When no run is active, atomically
advance `waterlineUtc` to at least that commit time. If a retained run is active,
defer this advance until its terminal commit, using the replacement schedule's
activation time as metadata. This is one waterline, with no per-source progress.

The explicit array remains capped at ten entries per replacement. Exact collisions
are allowed; duplicate instants within the array are rejected. Preserve the
existing cron day-of-month / weekday **OR** semantics. Reject invalid/no-future cron
rules even if explicit dates exist. Stop limits and the waterline always apply to
the merged schedule, not to each source independently.

## 4. Execution settings, updates, and extensibility

| Setting | Behavior |
| --- | --- |
| Memory | `IncludePreviousMessage` means the latest successful answer from this task, matching existing memory. `None` omits it from model input. Retain the latest successful answer for later re-enabling; changing a prompt does not erase memory. |
| Search / effort | Disabled search omits the tool and adjusts instructions/capabilities. Map `med` to provider `medium`; validate model support and reject unsupported combinations without downgrading. |

**Shared definition:** use a typed `TaskDefinition` containing prompt, schedule,
schedule timezone, and optional explicit `TaskParameters`. Share the field schema,
validation, and a single effective-settings resolver. Persist the explicit definition
separately from execution snapshots, with a schema version and task revision.
Keep runtime status, owner, task ID, `waterlineUtc`, and `startedOccurrences`
outside the editable settings contract and both get-mode JSON responses. They
remain internal state; `/list` and command confirmations expose relevant summaries.

### Field mutability and write permissions

Distinguish values that never change from values that only the system can change:

| Category | Fields | Rule |
| --- | --- | --- |
| Immutable task identity | `taskId`, `ownerId`, delivery `chatId`, `createdAtUtc` | Assign at creation; no rename, ownership transfer, or retargeting. Create a new task when identity/destination changes. |
| Immutable task setting | `timezone` | User selects at creation; later changes are rejected. |
| System-managed mutable state | `revision`, status, `waterlineUtc`, `startedOccurrences`, `updatedAtUtc` | Never accepted in create/update settings. Revision increments on an actual definition change; progress/count advance through execution. |
| Immutable occurrence snapshot | `scheduledUtc`, effective generation settings, captured defaults revision | Freeze when the occurrence is claimed/initialized; retries reuse it. |
| Editable task settings | Prompt, schedule, generation overrides, expiration, count limit | Retain current validation and update semantics. A fixed expiration instant is still editable through `expiresAt`. |

**Enforcement:**

- `/create` accepts only `prompt`, `schedule`, `timezone`, and `parameters` with
  their documented nested fields. Derive task ID, owner, destination chat, creation
  time, and initial runtime state on the server from trusted context.
- `/update` may change only `prompt`, `schedule.cron`, `schedule.once`, and the
  five documented `parameters` fields: `memoryMode`, `reasoningEffort`,
  `webSearch`, `expiresAt`, and `maxOccurrences`. Schedule fields still follow
  whole-schedule replacement semantics. This is an explicit allowlist, not a
  generic merge into a persisted operation record.
- The sole accepted immutable field in an update is `timezone` when it exactly
  repeats the stored ID. Treat it as a no-op assertion for explicit-JSON editing;
  it cannot mutate the task. A different value returns `timezone_immutable`.
- Reject task ID, owner, chat, creation/update timestamps, revision, status,
  waterline, count, occurrence snapshots, and other internal fields in either
  write command, even when their submitted values match storage. Return
  `field_read_only` with the field path; reject the entire command. Never silently
  ignore an attempted write to these fields.
- `revision` changes only when a stored definition changes. Identical input and
  command redelivery do not increment it; execution progress and global-default
  changes do not count as task-definition edits. `updatedAtUtc` records the last
  successful task-definition change. Separate receipt timestamps track execution.

These permissions apply to server handlers and persistence writes, not only to
Telegram input parsing. Dedicated lifecycle commands such as `/delete` remain
responsible for their own validated state transitions.

**Update boundary:** apply changes atomically to an owner-scoped task using an
ETag/revision check. Reject a concurrent edit with a retry message. Active tasks
are editable; completed tasks can resume through a replacement schedule or relaxed
limits if future work is eligible. Failed/deleted tasks require separate recovery
or creation. Telegram retries reuse the original command result.

**Running work:** freeze settings when a run initializes; that run and its retries
finish with the same snapshot. Schedule changes cancel unstarted work and calculate
future dates from the update commit time. Check the schedule revision atomically
before starting work; deduplicate by task and UTC instant across revisions.
Recheck that submitted dates remain future at commit; command redelivery reuses
the committed definition and result. Parameter/prompt-only changes preserve
schedule progress; stop-limit edits follow the admission rules above.

**Scheduler coordination:** wake the existing orchestration on schedule or limit
changes, cancel its old timer, and reload state. Save a pending notification
atomically with the update and retry until accepted: storage and Durable Functions
cannot be updated in one transaction.

**Adding a parameter:** add one typed field with its default, validation, and
documentation; carry it into the frozen execution settings and implement its
runtime mapping. Declare its inheritance/reset policy in the same schema used by
create/update and both get modes. Do not pass arbitrary provider JSON through.
This round supports only its new schema; old-data upgrade code is out of scope.

## 5. Deployment

Use a new business table name (e.g. `RecurringTaskDataV5`) and a new per-environment
Durable task hub (e.g. `RecurringTasksV5Dev` / `RecurringTasksV5Prod`). Azure creates
new storage resources under those names; no row migration, schema compatibility
reader, old-task recovery, or legacy command support is required.

Stop the old workers/pollers before cutover so they cannot keep executing tasks.
Deploy with the new names, start with empty tasks/receipts, discard queued pre-cutover
Telegram updates, and recreate test tasks explicitly. Update deployment and operator
scripts to target the new resources. Leave old storage unused; deletion is separate.
Changing only the business table would leave old orchestration messages/history
in the old task hub. ([Durable task hub state](https://learn.microsoft.com/en-us/azure/durable-task/common/durable-task-hubs))

## 6. Acceptance criteria

- **Commands and permissions:** JSON-only creation; reply-prompt creation; owner
  isolation; unknown/duplicate-key rejection; immutable timezone mismatch rejection
  and same-value no-op; submitted identity/system-managed fields fail atomically
  without altering settings, revision, progress, or ownership.
- **JSON and inheritance:** both get modes have the settings shape only; explicit
  output preserves omissions; effective output fills defaults without metadata;
  complete prompts survive JSON escaping and attachment delivery. Global changes
  affect inheriting tasks, overrides remain fixed, and null resets restore inheritance.
- **Updates:** unchanged explicit settings with past dates are a no-op; concrete
  effective values intentionally become overrides; omitted fields stay inherited.
  Concurrent edits conflict safely. Revision changes only for actual definition
  changes. Unrelated updates do not reset schedules, waterlines, or counts.
- **Time and scheduling:** known occurrence sequences across month/year boundaries,
  leap years, DST gaps/folds, and fractional-hour offsets; strictly ordered UTC
  results; latest-only catch-up across both sources; collision deduplication;
  equality at waterline and expiration boundaries; no replay after reactivation.
- **Execution and limits:** frozen settings across retries; success and terminal
  failure advance the waterline once; count reserved once per occurrence; expiration
  during execution/downtime; limits tightened/removed; recovery after crashes around
  claims, progress publication, update notification, and concurrent delete.
- **List and deployment:** compact/mobile layouts, stacked fallback, pagination,
  accurate activity states, prefix collisions, and operation on fresh table/hub
  resources with old workers stopped. No old-schema migration tests are required.

Related implementation context: [commands](TelegramCommands.md),
[scheduling](FunctionApp.md), [persistence](Persistence.md), [LLM](LLM.md).
