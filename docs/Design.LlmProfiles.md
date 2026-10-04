# LLM profiles, unified task defaults, and direct DeepSeek search

Status: proposed; implementation is outside this document change.  
Date: 2026-10-04.  
Consolidates the earlier direct DeepSeek proposal's adapter, search,
cost-validation, and rollout requirements.

## 1. Decision and scope

Remove the `RecurringTasksBot:TaskDefaults` configuration section. Derive task
defaults from the existing effective `Memory:Mode`, `Llm:ReasoningEffort`, and
`Llm:SearchEnabled` settings. Keep the application `TaskDefaults` record as a
derived value so task parsing, planning, and persistence need minimal changes.

Introduce named profiles under `RecurringTasksBot:Llm:Profiles` and select one
with `RecurringTasksBot:Llm:ActiveProfile`. Each profile owns its provider,
endpoint, model, credential reference, and provider-specific search controls.
Add a direct DeepSeek adapter behind the existing `ILlmExecutor` interface;
retain OpenRouter as an independent adapter and the initial selected profile.
Switching the selector switches the complete provider connection together.

Keep shared task defaults and application execution budgets at their existing
paths. They apply to whichever LLM profile is selected. This avoids moving every
setting, duplicating task defaults in each profile, or introducing profile
inheritance. Here, an **environment profile** means `appsettings.Development.json`
or another environment overlay; an **LLM profile** means a named provider
connection inside `Llm:Profiles`.

Use `%ENVIRONMENT_VARIABLE_NAME%` references for profile API keys. Resolve the
selected profile's reference against the process environment at startup.
The DeepSeek variable name is exactly `RecurringTasksBot__Llm__DeepSeek_ApiKey`;
preserve the single underscore before `ApiKey`.

Out of scope: per-task provider selection, automatic failover, provider routing,
hot reload, a generic expression language, external search services, a client
agent loop, storage schema changes, and changing production defaults before
validation. Scheduling, ownership, previous-answer memory mechanics, canonical
answer persistence, and Telegram delivery retain their existing contracts.

## 2. Current behavior and decisions that change it

Repository inspection found the following differences between the two current
sources of defaults:

| Setting | Current `TaskDefaults` | Existing authoritative setting after this change | Effective task value |
| --- | --- | --- | --- |
| Memory | `None` | `Memory:Mode = PreviousSuccessfulReply` | `IncludePreviousMessage` |
| Reasoning | `low` | `Llm:ReasoningEffort = Maximum` | `max` |
| Search | `true` | `Llm:SearchEnabled = true` | `true` |

This is an intentional behavior change for **all future, unclaimed occurrences
with inherited settings**, including existing tasks. It is not merely a JSON
cleanup. Previous-reply memory and higher reasoning can increase token use.
Explicit task settings and already persisted occurrence claims retain their
values. Do not silently preserve the removed `None`/`low` defaults elsewhere.

Other contradictions are resolved as follows:

| Earlier proposal or implementation | Decision |
| --- | --- |
| Direct DeepSeek becomes the default immediately after development work | Ship both profiles with OpenRouter selected; promote DeepSeek only after the acceptance gates. |
| Reuse one selected-provider API-key slot for both providers | Each profile has its own environment reference. Preserve the existing OpenRouter variable and use the requested DeepSeek variable. |
| Select by `Llm:Provider` and edit endpoint/model/key separately | Select by `Llm:ActiveProfile`; `Provider` is a field inside the selected profile. |
| `OpenRouterOptions.Validate()` rejects `SearchEnabled=false` | Search is a default capability, not a mandatory global setting. Accept `false`; explicit task overrides still win. |
| New tasks default to low reasoning | New claims inherit the unified setting, currently `Maximum`, normalized to `max`. Low reasoning becomes an explicit tuning decision. |
| Published search prices imply guaranteed savings | Treat lower cost as a hypothesis; measure total billed cost per completed occurrence. |

The production Bicep currently says non-secret LLM options come from published
appsettings files. Preserve that model; do not duplicate every LLM option into
deployment parameters based on older documentation suggesting otherwise.

## 3. Proposed configuration contract

The following is the proposed complete common `appsettings.json` content for
the keys currently present there. Environment-specific infrastructure values
remain in their existing files and deployment settings.

```json
{
  "RecurringTasksBot": {
    "TableName": "RecurringTaskDataV5",
    "Memory": {
      "Mode": "PreviousSuccessfulReply"
    },
    "Llm": {
      "ActiveProfile": "OpenRouter",
      "ReasoningEffort": "Maximum",
      "RequestTimeoutSeconds": 480,
      "CompletionTokenBudget": 131072,
      "TargetAnswerTextChars": 24000,
      "MaxAnswerSourceChars": 131072,
      "DeclaredContextTokens": 1048576,
      "SearchContextReserveTokens": 65536,
      "ContextEnvelopeReserveTokens": 8192,
      "GenerationRetries": 2,
      "SearchEnabled": true,
      "SystemInstruction": "",
      "Profiles": {
        "OpenRouter": {
          "Provider": "OpenRouter",
          "BaseUrl": "https://openrouter.ai/api/v1",
          "Model": "deepseek/deepseek-v4.1-flash",
          "ApiKey": "%RecurringTasksBot__Llm__ApiKey%",
          "SearchEngine": "parallel",
          "SearchMode": "fast",
          "MaxSearches": 8,
          "MaxResultsPerSearch": 5,
          "MaxTotalResults": 40
        },
        "DeepSeek": {
          "Provider": "DeepSeek",
          "BaseUrl": "https://api.deepseek.com/anthropic",
          "Model": "deepseek-flash",
          "ApiKey": "%RecurringTasksBot__Llm__DeepSeek_ApiKey%",
          "MaxSearches": 8
        }
      }
    }
  }
}
```

### Ownership and precedence

1. Keep configuration source order: common JSON, environment JSON, process
   environment variables. Later sources override the same configuration path.
2. Read shared options and the final `Llm:ActiveProfile`. Resolve the named
   profile from the already merged configuration; do not overlay its properties
   onto the root `Llm` section.
3. Validate shared values and the selected profile, normalize task defaults,
   and resolve only the selected credential. Capture an immutable configuration
   for the host lifetime. A change requires a restart/redeployment.

Example selector and profile-specific override:

```text
RecurringTasksBot__Llm__ActiveProfile=DeepSeek
RecurringTasksBot__Llm__Profiles__DeepSeek__MaxSearches=5
```

The following paths keep their existing meaning and override shared defaults
for either provider:

```text
RecurringTasksBot__Memory__Mode
RecurringTasksBot__Llm__ReasoningEffort
RecurringTasksBot__Llm__SearchEnabled
RecurringTasksBot__Llm__CompletionTokenBudget
```

Profile names are dictionary identifiers; initially ship `OpenRouter` and
`DeepSeek`. Lookup follows .NET configuration's case-insensitive semantics.
`Provider` is the adapter discriminator, restricted to those two values.
Require an explicit, nonblank selector and an existing profile; never fall back
to OpenRouter for an invalid selector or a missing DeepSeek credential.

Require the selected profile's provider, absolute HTTPS base URL, model, API-key
reference, and applicable search limits. Validate positive limits even when
the shared search default is false: a task can explicitly enable search.
Reject OpenRouter-only options in a selected DeepSeek profile. Profile-level
copies of shared settings such as `ReasoningEffort` and `SearchEnabled` are
unsupported and must produce a configuration error, not silently do nothing.
Inactive profiles do not require credentials or provider-specific validation;
syntactically invalid JSON still fails normal configuration loading.

Application budgets remain common in this minimum design. Validate their
compatibility with the selected model's confirmed limits before promotion;
never silently clamp them. Changing to a future model with different limits
requires updating the shared deployment budget too. Model-specific budget
profiles are a possible later extension, not necessary for these two initial
connections.

### Removing obsolete paths

Remove `TaskDefaults` entirely, including its configurable `Revision`. Move
`Llm:Provider`, `BaseUrl`, `Model`, `SearchEngine`, `SearchMode`, `MaxSearches`,
`MaxResultsPerSearch`, and `MaxTotalResults` into their named profiles. Reject
these obsolete root settings and any remaining `TaskDefaults` settings at
startup with a path-only migration error. This prevents stale environment
overrides from appearing to work. Do not maintain parallel compatibility
readers that recreate ambiguous precedence.

One deliberate exception is `Llm:ApiKey`: the retained OpenRouter environment
variable naturally appears at this path through `AddEnvironmentVariables`.
It is not read as a generic selected-provider credential and must not be
rejected as an obsolete key. It is used only through the OpenRouter macro.
No root credential may implicitly supply a DeepSeek request.

## 4. Environment-reference resolution

Implement a small `EnvironmentReferenceResolver` in Infrastructure, used only
for `Llm:Profiles:<selected>:ApiKey` in this change. There is no global JSON
substitution: prompts, system instructions, endpoints, and other settings are
never interpreted as expressions. Existing Telegram/storage secret loading
is unchanged.

Contract:

- Accept exactly one whole-value reference matching
  `^%([A-Za-z_][A-Za-z0-9_]*)%$`. No embedded substitutions, escaping, defaults,
  configuration-key lookups, shell evaluation, or recursive expansion.
- Require this reference form for profile API keys, including environment
  overrides of the `ApiKey` configuration path. Actual secrets live only in
  the referenced environment variable; literal keys in JSON are invalid.
- Read the captured name with `Environment.GetEnvironmentVariable(name)`
  (inject the lookup function for tests). Use the exact configured spelling;
  do not translate `__` to `:` or alter the name or underscores.
- Missing, empty, or whitespace-only values fail host startup before any
  provider call. Pass any nonblank resolved value unchanged as an opaque
  credential; percent signs in it do not trigger another lookup.
- Errors may identify the selected profile, configuration path, and referenced
  variable name. Never include the resolved value, authorization headers, raw
  provider error bodies, or a configuration dump. Hold the resolved key outside
  printable option records and diagnostics.
- Resolve no inactive-profile secrets. An OpenRouter-only deployment starts
  without a DeepSeek key, and vice versa. Credential rotation requires restart.

Do not use a permissive expansion function that leaves a missing `%NAME%`
unchanged: the placeholder must never reach an HTTP authentication header.

## 5. Deriving task defaults and preserving occurrences

Construct `TaskDefaults` once from the same validated shared options used by
execution and the selected adapter. Keep its name and consumers, but remove
its independent configuration reader and runtime reliance on the old static
`TaskDefaults.Default` (`None`/`low`). Test fixtures can supply explicit values.
Production handlers must receive the derived record through DI; optional
handler fallbacks must not reintroduce the old defaults.

| Shared configuration | Normalization for task defaults |
| --- | --- |
| `Memory:Mode = PreviousSuccessfulReply` | `TaskMemoryModes.IncludePreviousMessage` |
| `Memory:Mode = None` | `TaskMemoryModes.None` |
| `Llm:ReasoningEffort = Maximum` | `TaskReasoningEfforts.Max` (`max`) |
| `low`, `med`, `high`, `xhigh`, `max` | Preserve the canonical task token |
| `Llm:SearchEnabled` | Boolean copied to `WebSearch` |

Accept the legacy `Maximum` spelling case-insensitively at the configuration
boundary; task JSON keeps its current validation and vocabulary. Unknown
memory/effort values fail startup. Missing shared values may use the current
code defaults (`PreviousSuccessfulReply`, `Maximum`, `true`) from one shared
reader; explicit blank or malformed values must not silently become defaults.

For each setting independently, resolution is:

```text
existing occurrence claim -> reuse its frozen value
new occurrence           -> explicit task override ?? derived shared default
```

`null` or omitted task parameters continue to mean inheritance. `/get` effective
output and new claims use the same derived defaults. Do not backfill existing
task rows with old defaults. Keep `ExecutionOptions.MemoryMode` in its existing
vocabulary and map only at the task boundary, avoiding a stored-data migration.

Keep `OccurrenceClaim.DefaultsRevision` as provenance. Derive its value as
`defaults-v2:` followed by lowercase SHA-256 hex over UTF-8 compact JSON with
fixed property order `memoryMode`, `reasoningEffort`, `webSearch`, using the
normalized values and a JSON boolean. Identical effective defaults yield the
same revision; changing any of them changes it. Secrets, profile identity, and
transport settings are excluded. Existing revision strings remain readable
and are never recomputed for retained claims. This is a defaults fingerprint,
not a complete execution-profile snapshot.

Fix the current generation fallback `overrides?.SearchEnabled ?? true` so prompt
construction uses the same effective search value as request serialization.
Resolve non-null reasoning/search values before building messages and issuing
`LlmRequest`, using frozen overrides when present and derived defaults otherwise.
Direct adapter calls with null request overrides use the same normalized shared
defaults. A task with `webSearch=false` omits search tools; a task with `true`
enables them even when the global default is false. Enabling search does not
require every answer to perform a search.

Claims freeze task settings; frozen contexts also retain prompt/memory data.
Neither currently freezes provider, model, credential, or completion budget.
Keep this limitation explicit. Drain retained generation work, including queued
retries, before switching profiles or budgets. Already persisted answers can
resume delivery without regeneration. Provider-stable retries across arbitrary
configuration changes would require a separate versioned profile snapshot.

## 6. Composition and implementation boundaries

Use an explicit two-provider switch, not a provider registry framework.

| Area | Necessary change |
| --- | --- |
| `Infrastructure/Configuration/AppConfiguration.cs` | Read shared options and selected profile once; derive defaults; detect obsolete settings; call the credential resolver only for execution/startup. |
| Infrastructure LLM composition helper | Construct the selected adapter from `HttpClient`, validated options, and resolved credential; shared by host and smoke tool. |
| `FunctionApp/FunctionAppServices.cs` | Register exactly one selected `ILlmExecutor`; inject its provider/model metadata and derived defaults into execution. |
| `Infrastructure/Llm/OpenRouter/*` | Bind profile transport/search options plus shared defaults; retain wire protocol and allow search-disabled execution. |
| `Infrastructure/Llm/DeepSeek/*` | Add options, executor, request builder, response/stream readers, and shared block helpers only where useful. |
| Application defaults/generation paths | Normalize defaults and eliminate conflicting fallbacks; preserve task and execution interfaces. |
| `tools/LlmSmoke/Program.cs` | Use the same selector, defaults, resolver, and adapter factory; remove direct OpenRouter construction and fixed key lookup. |

Keep provider options in Infrastructure and `ExecutionOptions` in Application.
Preserve `ILlmExecutor`, `LlmRequest`, `LlmResult`, and persistence codecs for
this migration. Provider/model fields in receipts describe the selected
execution, not a hardcoded OpenRouter value. Log the selected profile name and
non-secret effective settings; never serialize the credential-bearing object.

The offline smoke mode and `--validate-config` validate non-secret structure
and reference syntax without requiring the referenced secret or making an API
call. Clearly label this as structural validation. Host startup and smoke
`--execute` additionally require the selected secret. Keep the existing bounded
request count and no-Telegram behavior. The selector environment variable is
sufficient; no new CLI selection flag is required.

## 7. Direct DeepSeek adapter and native search

### Evidence and compatibility boundary

The user confirmed that the standalone direct DeepSeek web-search POC passed.
Its temporary script and instructions have been removed. This confirms the
initial capability check; the broader compatibility, billing, and rollout gates
below still apply.

DeepSeek documents an Anthropic-compatible endpoint, the `deepseek-flash`
model name, streaming, API-key authentication, and server-search response
blocks. Its integration guide describes native search and additional token
costs for search summarization. These support the proposed adapter, but do not
establish complete Anthropic search parity or measured savings.
Sources checked on 2026-10-04:
[DeepSeek compatibility](https://api-docs.deepseek.com/guides/anthropic_api/)
and [native search integration](https://api-docs.deepseek.com/quick_start/agent_integrations/claude_code/).

Validate the exact tool declaration, enforcement of `max_uses`, citation
shape, completion signals, and returned usage with the development key before
enabling DeepSeek in production. The
[Anthropic search reference](https://platform.claude.com/docs/en/agents-and-tools/tool-use/web-search-tool)
is a wire-format reference, not proof that DeepSeek supports every option.
Do not assume newer search-tool versions, filters, or client continuation work.

### Request mapping

POST to `https://api.deepseek.com/anthropic/v1/messages`. Compose the configured
base URL and `/v1/messages` once, tolerating a trailing slash. Send the resolved
DeepSeek key in `x-api-key`, JSON content type, and
`anthropic-version: 2023-06-01`. DeepSeek documents the version header as ignored.
Never forward OpenRouter authentication or gateway-specific headers.

Move the existing leading system message into top-level `system`; retain all
remaining message roles, order, and content as text blocks. Support the current
`system,user` and `system,user,assistant,user` shapes. Reject unsupported role
shapes rather than dropping content or adding another system instruction.

Map the application completion budget to `max_tokens`, request streaming,
enable thinking, and set `output_config.effort`. Preserve task vocabulary and
translate only at the provider boundary:

| Canonical task effort | OpenRouter wire value (existing behavior) | DeepSeek wire value |
| --- | --- | --- |
| `low` | `low` | `low` |
| `med` | `medium` | `high` |
| `high` | `high` | `high` |
| `xhigh` | `xhigh` | `high` |
| `max` | `max` | `max` |

The DeepSeek mapping deliberately collapses effort levels to its documented
scale; a configuration value of `Maximum` has already normalized to `max`.
See [DeepSeek thinking controls](https://api-docs.deepseek.com/guides/thinking_mode/).

When effective search is enabled, include this proposed tool declaration with
`max_uses` taken from the selected profile's `MaxSearches`:

```json
{
  "tools": [
    { "type": "web_search_20250305", "name": "web_search", "max_uses": 8 }
  ]
}
```

When disabled, omit `tools` entirely. Omit OpenRouter's `reasoning.exclude`,
`max_completion_tokens`, `max_tool_calls`, engine/mode, and result-count
parameters. DeepSeek has no confirmed equivalents for per-search or total
result-count controls. Local source-list limits do not limit server cost.
Only server-side web search is exposed; never execute a returned client tool.

### Response and citation handling

Implement the Anthropic SSE lifecycle with indexed content blocks, deltas,
usage updates, errors, and a validated final message stop. Support JSON through
the same semantic reader. Do not reuse OpenRouter's `choices`, `annotations`,
or `[DONE]` reader. Validate block ordering and completeness; transport EOF
alone is not a successful answer.

Exclude thinking and tool payloads from the answer. For search responses,
retain the final answer segment after the last server-tool/result block,
including its associated citations, rather than intermediate search narration.
Validate this segmentation against recorded DeepSeek fixtures; if the response
has no completed final answer segment, fail instead of publishing narration.
For responses without tools, concatenate answer text blocks in order.

Extract cited absolute HTTP(S) URLs and titles into deduplicated `LlmSource`
entries. Preserve authored inline links; where structured citations lack an
inline link, render returned URLs beside the associated text before constructing
the canonical `AnswerText`. Escape titles/links for the existing answer format.
This is necessary because delivery consumes `AnswerText`, not `Sources`.
Never fabricate URLs or treat every retrieved result as a citation.

Enforce `MaxSourceScalars` while accumulating the answer and after citation
rendering. Independently bound SSE frames, block count, and retained tool data
with documented parser limits and oversize fixtures. Discard unnecessary raw
payloads; do not log search queries, retrieved content, thinking, or opaque
blocks. Do not deliver any partial output before completed-answer persistence.

Record evidence of search execution from server-tool blocks or confirmed usage
fields, not from citations alone. Count successful retrieved results separately
from cited sources. Aggregate documented usage without double-counting final
cumulative counters. Missing counters mean unknown, not free execution.
Keep existing `LlmUsage`/receipt fields readable; if their non-null numeric
fields require zero for unavailable counters, mark usage incomplete in separate
non-secret diagnostics and exclude those zeros from cost evidence. Richer
persisted accounting is deferred.

### Completion and failure policy

Accept only a complete nonempty answer with validated completed-turn semantics
(initially `end_turn`). Handle in-body errors even when HTTP status is 200.

| Condition | Mapping / action |
| --- | --- |
| Network interruption, provider timeout, HTTP 429/temporary 5xx, temporary search unavailability | `Transient`; existing bounded application retry policy; honor `Retry-After`. |
| Authentication, insufficient credits, invalid request/query, unsupported model/tool | `Permanent`; sanitized error, no retry. |
| Output length limit or incomplete final answer | Terminal `AnswerIncomplete`; no truncation or partial delivery. |
| Answer exceeds source limit | Terminal `SourceLimit`. |
| Search budget exhausted | Terminal `Permanent` with a dedicated sanitized `search_budget_exhausted` code; no retry to reset the allowance. |
| `pause_turn`, unexpected client `tool_use`, or unknown completion semantics | Terminal `AnswerIncomplete` with a specific safe code until explicitly supported. |
| Successful search with no results | Not a transport failure; allow a complete answer explaining verification limits. |

No HTTP retries inside the adapter. Retain the configured 480-second request
deadline per generation attempt and the existing application retry policy.
The deadline covers response streaming and any provider-internal search work
within that request; it is not a total deadline across Durable retries. Keep
the existing 510-second configured timeout ceiling and 540-second activity
budget checks. Propagate caller cancellation without disguising it as success.

Search failure must not silently produce an apparently verified research answer.
Retries may repeat billed work: the per-request search/token ceilings are not
an occurrence-wide monetary cap. If continuation is implemented later, it must
share the original deadline, aggregate output budget, and remaining search
allowance, rather than starting an unbounded agent loop.

## 8. Cost experiment and rollout

Separate three changes when measuring outcomes: deduplicating defaults,
substituting providers, and reducing budgets. Otherwise increased reasoning or
memory could hide search savings, or smaller output could look like a provider
cost improvement.

1. Implement unified defaults and both profiles, initially selecting OpenRouter.
   Record the changed inherited behavior explicitly. Establish an OpenRouter
   baseline with the new shared defaults and representative explicit overrides.
2. In development, select DeepSeek with matching effective task settings,
   8 searches, and the existing 131,072 completion-token ceiling. Confirm the
   chosen model accepts the configured output/context limits. A rejected limit
   blocks this comparison until an explicit compatible budget is chosen for
   both sides; never silently reduce only one side.
3. After compatibility passes, evaluate a cheaper development configuration:
   shared `Llm:ReasoningEffort=low`, shared `CompletionTokenBudget=16384`, and
   `Profiles:DeepSeek:MaxSearches=5`. Use inherited or deliberately matched task
   settings; existing explicit/frozen efforts remain authoritative. Do not
   reduce the 24,000-character answer target or use 4,096 output tokens as a
   universal cap. Reject incomplete answers rather than truncating them.
4. Compare total billed cost per successfully completed occurrence, including
   failed attempts, retries, cache effects, and internal search-summary work.
   Record latency, completion rate, citation usefulness, and source quality too.
   Reconcile returned usage with provider billing/balance changes on isolated
   bounded runs. Partial counters are insufficient evidence of savings.
5. Promote only after the gates below pass. Drain generation, apply the selector
   and any intentionally evaluated budget changes, restart, and monitor.
   Roll back the selector to OpenRouter; restore baseline shared budgets as
   well if the experiment changed them. Keep both credentials available during
   the rollout window for a selector-only provider rollback.

The earlier proposal's exact prices and arithmetic examples are intentionally
not normative here. Consult the current
[DeepSeek pricing](https://api-docs.deepseek.com/quick_start/pricing/) and
[OpenRouter search catalog](https://openrouter.ai/tool/web-search/) when running
the experiment, and retain dated billing evidence. Native search avoids adding
an application-managed search integration; it does not prove a zero search
surcharge or guaranteed savings. Do not shift user schedules to chase pricing.

## 9. Deployment, tooling, and documentation migration

Implement configuration and deployment changes together; no stored-data
migration is required.

- Update common JSON and any environment overrides to the new paths. Keep
  environment-specific non-LLM settings unchanged. Ensure both profile
  definitions are included in the published appsettings artifact.
- Keep `RecurringTasksBot__Llm__ApiKey` as the OpenRouter secret name. Add the
  DeepSeek secret exactly as `RecurringTasksBot__Llm__DeepSeek_ApiKey`. Do not
  rename it to resemble the new configuration path.
- In `infra/main.bicep`, retain the existing secure `llmApiKey` parameter for
  OpenRouter with a corrected description; add secure `deepSeekApiKey` and
  non-secret `llmActiveProfile` parameters. Permit an absent/empty inactive
  credential, and provision nonempty credentials under their exact names.
  Always provision `RecurringTasksBot__Llm__ActiveProfile` explicitly. The
  initial parameter-template selector is `OpenRouter`.
- Extend `infra/main.parameters.json`, `scripts/resolve-deploy-parameters.sh`,
  and `.github/workflows/deploy-production.yml` with the DeepSeek secret input
  and selector. Preserve existing OpenRouter workflow wiring; add
  `DEPLOY_DEEPSEEK_API_KEY` from the new repository/environment secret.
  Resolve an absent inactive key to an empty secure parameter, not an unresolved
  placeholder. Require the selected key before deployment; preserve supplied
  inactive credentials for rollback. Never print resolved parameter contents.
- Update `scripts/launch-local.sh` and `scripts/smoke-llm-dev.sh` to remove their
  unconditional OpenRouter-key requirement. Let the shared .NET resolver own
  selected-provider validation; avoid a second profile/macro parser in shell.
- Update `tests/scripts/test-deploy-config.sh` and local-launch tests for both
  selections, exact environment spelling, optional inactive credentials, and
  secret-free output. Verify deployment does not accidentally erase a supplied
  rollback credential.
- During implementation, update the owning current docs: `Configuration.md`,
  `LLM.md`, `Setup.md`, `Deployment.md`, `Runbook.md`, `Architecture.md`, relevant
  task-command inheritance descriptions, and root `README.md`. Correct comments
  claiming the `TaskDefaults` section is authoritative. Keep historical specs
  historical. Until implementation, current runtime docs remain unchanged.

## 10. Acceptance criteria

### Offline configuration and application tests

1. Checked-in JSON has no `TaskDefaults` section or root provider connection
   fields. Exactly one authoritative path supplies each shared task default.
2. Derived defaults are `IncludePreviousMessage`, `max`, `true` for the proposed
   common file. Invalid modes/efforts/booleans and explicit blank values fail.
3. Common JSON < environment JSON < environment variables holds for shared
   fields, selector, and profile fields. Unknown/blank selectors fail without
   fallback; obsolete paths produce actionable path-only errors.
4. Both profiles select their own endpoint, model, adapter, and key. Switching
   only `ActiveProfile` switches all four. Inactive missing keys do not fail
   startup; selected missing keys do. The old OpenRouter key cannot satisfy
   DeepSeek authentication.
5. Reference tests cover exact DeepSeek spelling, malformed/embedded references,
   missing/empty/whitespace values, literal-key rejection, one lookup only, and
   no resolved secrets in errors, logging, or serialized options.
6. Explicit task overrides and `null` inheritance behave consistently in `/get`,
   claims, message construction, and adapters. `SearchEnabled=false` starts
   successfully; task `true` still enables search and task `false` omits it.
7. Existing claims retain frozen defaults/revision after a configuration change;
   new claims use derived defaults and deterministic revised fingerprints.
   Legacy memory tokens and stored rows remain readable without rewriting.
8. Host DI and smoke execution use the same factory. Offline smoke needs no
   secret/network; execution requires only the selected secret. Existing
   OpenRouter contract tests remain green except intentional default/validation
   assertions updated for this specification.

### Offline DeepSeek transport tests

Cover role/memory translation, all effort mappings, search on/off, endpoint
joining, credential isolation, JSON/SSE equivalence, indexed block assembly,
final-segment selection, citations in delivered `AnswerText`, thinking exclusion,
usage aggregation/incompleteness, in-body search errors, search-budget failure,
`pause_turn`, unexpected client tools, incomplete streams, cancellation,
timeouts/`Retry-After`, and all answer/frame/block bounds. Use recorded sanitized
fixtures and fake transports, with no live CI requests or Telegram sends.

### Bounded live development gate

Using the development secret, exercise English and Russian current-information
tasks, timezone-sensitive dates, previous-reply memory, search disabled, and
representative long answers. Confirm actual server-side search, `max_uses`
enforcement, completion semantics, usable citations, supported model limits,
latency within the activity budget, and measured billing including internal
work. Document the result of each compatibility assumption above. Promote only
when representative tasks complete with acceptable quality and demonstrated
cost benefit against the matched OpenRouter baseline; otherwise keep OpenRouter
selected and retain DeepSeek as an unpromoted profile.
