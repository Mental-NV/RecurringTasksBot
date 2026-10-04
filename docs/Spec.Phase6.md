# Phase 6 implementation plan: LLM profiles and direct DeepSeek

Status: ready for implementation; this document does not claim the code is implemented.  
Updated: 2026-10-04.  

## 0. Execution instructions

Implement this plan end to end. Sections 1–10 define the required behavior;
section 11 gives executable validation steps and the handoff checklist. All
provider details needed for the initial implementation are included here.
External links are provenance only and are not prerequisites. Use repository
code for existing behavior and bounded live requests to investigate provider
compatibility; do not stop merely because browsing is unavailable.

The direct DeepSeek + native-search POC passed, and the user accepted its cost
saving. Do not recreate the deleted POC, re-prove savings, implement billing
analysis, or work on the separate natural-language-commands proposal.
Preserve existing functionality; new code needed to translate DeepSeek's
protocol is required, but unrelated feature expansion is out of scope.

### Ordered work packages

Complete the packages in order. Keep tests beside the behavior they verify;
file/type names for new internal helpers are suggestions, not extra public APIs.
Do not commit secrets, live response bodies, or scratch probes. Preserve
unrelated working-tree changes and existing staged work.

| Step | Implementation work | Completion check |
| --- | --- | --- |
| 1. Establish baseline | Read the configuration loader, host DI, both OpenRouter readers, task defaults/planner, occurrence generator, and smoke tool. Run the current offline gates in section 11; distinguish pre-existing failures. | Existing contracts and failures are understood. No provider behavior is changed yet. |
| 2. Unify configuration | Implement sections 3–5 in `Infrastructure/Configuration/AppConfiguration.cs`, a small environment-reference resolver, and common appsettings. Bind shared settings once, select a named profile, derive `TaskDefaults`, and reject obsolete keys. | Configuration/default-inheritance tests pass for both profiles, including inactive missing secrets. |
| 3. Preserve application behavior | Update `Application/TaskDefinition.cs`, `Features/Tasks/TaskGetHandler.cs`, default injection/call sites, and `Features/ExecuteOccurrence/OccurrenceGenerator.cs`. Preserve claims, prompt/memory, result/receipt schemas, and delivery. | Effective `/get`, new claims, frozen claims, and search-disabled messages agree. |
| 4. Implement provider compatibility | Adapt OpenRouter option binding without changing its wire format/readers. Add `DeepSeekOptions`, request builder, JSON/SSE readers, and `DeepSeekLlmExecutor` under `Infrastructure/Llm/DeepSeek/`. Apply section 7 and its embedded fixtures. | Offline provider tests cover request/response mapping and existing source/usage semantics. |
| 5. Compose and probe | Add one shared Infrastructure factory, update `FunctionAppServices.cs` and `tools/LlmSmoke/Program.cs`, and remove fixed OpenRouter checks from local scripts. Use the production adapter for live development checks. | Selector switches adapter/model/endpoint/key together; offline smoke requires no secret; live smoke works for search on and off. |
| 6. Migrate deployment/docs | Update Bicep, parameter resolution, workflow inputs, shell tests, and owning docs per section 9. | Published configuration contains both profiles; selected credentials are checked without exposing them; offline deployment tests pass. |
| 7. Verify and hand off | Run section 11, exercise the development bot with real LLM requests, clean up only your test tasks, and report results. | All functional acceptance checks pass or any external blocker is explicitly identified. Production still selects OpenRouter unless separately changed by the operator. |

Baseline files use `src/RecurringTasksBot.*` and tests under
`tests/RecurringTasksBot.Tests`. Do not create a second scheduler, command
contract, persistence format, agent framework, or live CI suite.

## 1. Required outcome and scope

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
agent loop, storage schema changes, new citation-processing or formatting
features beyond existing behavior, billing/cost evaluation, and changing
production defaults before functional validation. Preserve existing functionality
and make DeepSeek compatible with its application-visible contracts. Protocol
translation needed for the new provider is in scope; unrelated feature expansion
is not. Scheduling, ownership, previous-answer memory mechanics, canonical answer
persistence, and Telegram delivery retain their existing contracts.

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
| Earlier proposal requires cost comparisons and billing validation | Accept the user-confirmed POC result that direct DeepSeek search saves cost; no further cost evaluation is in scope. |

The production Bicep currently says non-secret LLM options come from published
appsettings files. Preserve that model; do not duplicate every LLM option into
deployment parameters based on older documentation suggesting otherwise.

## 3. Configuration to implement

Use the following complete common `appsettings.json` content for
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
          "MaxSearches": 8,
          "MaxTotalResults": 40
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
Reject OpenRouter-only `SearchEngine`, `SearchMode`, and `MaxResultsPerSearch`
options in a selected DeepSeek profile. Retain `MaxTotalResults` for both
profiles: it already caps the returned `Sources` collection. In DeepSeek it is
only a local metadata cap, not a server search parameter. Profile-level
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
The requested DeepSeek environment variable also appears in merged configuration
as `RecurringTasksBot:Llm:DeepSeek_ApiKey`; allow that path as an environment
source, but read it only through the selected profile's macro. Do not reject
these two credential-source paths through a blanket unknown-root-key check.
No root credential may implicitly supply another profile's request.

## 4. Environment-reference resolution

Implement a small `EnvironmentReferenceResolver` in Infrastructure, used only
for `Llm:Profiles:<selected>:ApiKey` in this change. There is no global JSON
substitution: prompts, system instructions, endpoints, and other settings are
never interpreted as expressions. Existing Telegram/storage secret loading
is unchanged.

Contract:

- Accept exactly one whole-value reference matching
  `\A%([A-Za-z_][A-Za-z0-9_]*)%\z` in .NET (whole string, including no trailing newline). No embedded substitutions, escaping, defaults,
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
Use `HttpClient` and `System.Text.Json`, following the existing adapter; no new
provider SDK is necessary. Pass normalized defaults to application consumers
explicitly; do not introduce an Application dependency on Infrastructure.
Keep configuration structural validation separate from credential resolution
so tests/offline tools can build requests without reading secrets.
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

The current smoke tool also hardcodes `OpenRouterRequestBuilder` and constructs
messages without passing effective search settings; replace both with the
selected production path. Its current success predicate unconditionally
requires `result.SearchUsed`. Make that check scenario-aware: search-disabled
runs require a complete answer and valid delivery leaves, not citation metadata.
For a research smoke prompt keep the existing citation check, but label it as a
citation check, not proof of executed search. Missing metadata must not cause the
runtime to reject an otherwise complete answer or synthesize sources. Validate
native search separately in the test session from provider tool blocks; do not
add a production telemetry field or repurpose `SearchUsed`.

## 7. Direct DeepSeek adapter and native search

### POC result and compatibility boundary

The user confirmed that the standalone direct DeepSeek web-search POC passed
and established the cost saving. Accept that result for this specification;
no billing analysis, cost comparison, or additional proof of savings is required.
The temporary POC script and instructions have been removed. Remaining gates
cover application integration and functional correctness only.

DeepSeek documents an Anthropic-compatible endpoint, the `deepseek-flash`
model name, streaming, API-key authentication, and server-search response
blocks. Sources checked on 2026-10-04:
[DeepSeek compatibility](https://api-docs.deepseek.com/guides/anthropic_api/)
and [native search integration](https://api-docs.deepseek.com/quick_start/agent_integrations/claude_code/).

Validate tool handling, enforcement of `max_uses`, and completion signals in
integration testing before enabling DeepSeek in production. The
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

When effective search is enabled, include this tool declaration with
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
server result-count controls. Apply the existing `MaxTotalResults` cap only to
extracted source metadata locally; do not send it as a DeepSeek search option.
Only server-side web search is exposed; never execute a returned client tool.

### Embedded protocol reference (no browsing required)

The following examples are synthetic, minimized protocol fixtures, not recorded
POC responses. Native search is POC-confirmed; exact live streaming, citation,
and error details still need integration verification. If DeepSeek returns a
compatible field variation, inspect it locally, add a sanitized regression
fixture, and adapt the reader without changing application contracts. Report
unsupported required behavior rather than silently disabling it.

#### Complete request example

Headers: `Content-Type: application/json`, `x-api-key: <resolved DeepSeek key>`,
`anthropic-version: 2023-06-01`. Send to the endpoint given above. Values below
are deliberately small test values; production values come from configuration.
There is no required beta header, OpenRouter bearer header, or separate search
API key. `stream: false` requests the equivalent JSON response for a bounded
protocol check; the production adapter requests `stream: true`.

```json
{
  "model": "deepseek-flash",
  "max_tokens": 4096,
  "stream": true,
  "system": "<existing fully rendered system instruction>",
  "thinking": {"type": "enabled"},
  "output_config": {"effort": "low"},
  "messages": [
    {"role": "user", "content": [{"type": "text", "text": "<existing occurrence envelope>"}]}
  ],
  "tools": [{"type": "web_search_20250305", "name": "web_search", "max_uses": 2}]
}
```

For memory, preserve the existing user/assistant/user sequence after moving
only the leading system message. Previous assistant content is the saved answer
text, not prior protocol tool or thinking blocks. Do not use SDK model aliases
or silently replace a configured model when a request fails.

#### JSON search response and metadata

```json
{
  "id": "msg_fixture",
  "type": "message",
  "role": "assistant",
  "model": "deepseek-flash",
  "content": [
    {"type": "text", "text": "Searching."},
    {"type": "server_tool_use", "id": "search_1", "name": "web_search", "input": {"query": "example"}},
    {"type": "web_search_tool_result", "tool_use_id": "search_1", "content": [
      {"type": "web_search_result", "url": "https://example.com/", "title": "Example", "encrypted_content": "opaque"}
    ]},
    {"type": "text", "text": "See [Example](https://example.com/).", "citations": [
      {"type": "web_search_result_location", "url": "https://example.com/", "title": "Example", "encrypted_index": "opaque", "cited_text": "Example"}
    ]}
  ],
  "stop_reason": "end_turn",
  "usage": {"input_tokens": 120, "output_tokens": 35}
}
```

Interpret `web_search_tool_result.content` as a results array or an error object.
An empty array is a successful empty search. Read source metadata from the final
text's `citations`, not the results array. Ignore opaque encrypted fields.
This fixture yields the final text, one `LlmSource`, token counts 120/35,
`SearchResults=1`, and both `SearchUsed` fields true. Removing `citations` yields
zero sources/count and false flags while the answer remains valid.
Reference: [search wire format](https://platform.claude.com/docs/en/agents-and-tools/tool-use/web-search-tool).

#### SSE event reference

Events end at a blank line; join multiple `data:` lines with newline before
JSON parsing. Ignore comment/keepalive lines. Content block indexes connect
start, delta, and stop events. `message_delta.usage` values are cumulative.
Source: [streaming protocol](https://platform.claude.com/docs/en/build-with-claude/streaming).

```text
event: message_start
data: {"type":"message_start","message":{"id":"msg_fixture","type":"message","role":"assistant","model":"deepseek-flash","content":[],"stop_reason":null,"usage":{"input_tokens":120,"output_tokens":1}}}

event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello."}}

event: content_block_stop
data: {"type":"content_block_stop","index":0}

event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":35}}

event: message_stop
data: {"type":"message_stop"}

```

This fixture yields `AnswerText="Hello."`, token counts 120/35, no sources,
and false search flags. Do not reset input tokens to zero when a later event
omits that field, or add 35 to the initial output count of 1.

| Event/delta | Adapter action |
| --- | --- |
| `message_start` | Initialize message metadata and usage; require a single message. |
| `content_block_start` | Open the indexed block, including any initial text or citation array. Search/result blocks use the same shapes as the JSON fixture. |
| `text_delta` | Append `delta.text` to that text block using the existing Unicode accumulator. |
| `input_json_delta` | Treat `delta.partial_json` as tool input, never answer text. No client tool execution. |
| `thinking_delta`, `signature_delta` | Ignore for answer/source output; do not log their values. |
| `content_block_stop` | Close the indexed block. Reject missing starts or deltas after closure. |
| `message_delta` | Update present stop/usage fields, retaining absent fields. |
| `message_stop` | Finalize only after all blocks close and completion checks pass. |
| `ping` | Ignore. |
| `error` | Classify the nested `error.type`; never deliver accumulated partial text. |

In a text block, `citations_delta` supplies one `delta.citation` to append to
that block's citation metadata. Its web citation object has the same fields as
the JSON fixture. Example:

```json
{"type":"content_block_delta","index":3,"delta":{"type":"citations_delta","citation":{"type":"web_search_result_location","url":"https://example.com/","title":"Example"}}}
```

Handle citations already present on block start too, using the existing stream
URL deduplication rule. Source: [citation delta protocol](https://platform.claude.com/docs/en/build-with-claude/citations).

Implementation decisions: use one semantic reader for assembled JSON/SSE
blocks, with mode-specific source duplicate handling. Concatenate adjacent
final text blocks verbatim (no invented separators), trim only through existing
canonicalization, and keep only citations belonging to those final text blocks.
On each search/tool boundary discard preceding candidate answer text and its
citations. Unknown optional properties can be ignored; unknown content/delta
shapes that prevent safe answer reconstruction fail with a sanitized protocol
error. Never treat EOF or OpenRouter's `[DONE]` as `message_stop`.

Map `usage.input_tokens` to `PromptTokens` and `usage.output_tokens` to
`CompletionTokens`. Initialize missing counters to zero and replace only
present counters on later events. Do not add cache, internal-search, or other
fields to these counters. That preserves the existing usage contract without
introducing new accounting logic.

#### Provider errors and compatibility policy

HTTP failures use `GenerationPolicy.ClassifyHttpStatus` and
`GenerationPolicy.ParseRetryAfter`. A JSON/SSE top-level error has shape:

```json
{"type":"error","error":{"type":"overloaded_error","message":"<omit from logs>"}}
```

A server-search failure can appear inside an HTTP 200 response:

```json
{"type":"web_search_tool_result","tool_use_id":"search_1","content":{"type":"web_search_tool_result_error","error_code":"max_uses_exceeded"}}
```

Use this explicit adapter mapping; it is a local failure-policy decision:

| Signal | Existing failure kind / safe summary |
| --- | --- |
| `rate_limit_error`, `overloaded_error`, `api_error`; search `too_many_requests` or `unavailable` | `Transient`; sanitized type/code and `Retry-After` if present. |
| `authentication_error`, `permission_error`, `invalid_request_error`, `not_found_error`, `request_too_large`; search `invalid_tool_input`, `query_too_long`, `request_too_large` | `Permanent`. |
| Search `max_uses_exceeded` | `Permanent`, summary `search_budget_exhausted`. |
| Unknown in-body search error | `Permanent`, summary `unsupported_search_error`; do not publish an apparently successful answer. |
| Unknown top-level error | `Transient`, summary `provider_error_unknown`; existing application retry bounds apply. |
| `max_tokens` stop | `AnswerIncomplete`, summary `answer_incomplete`. |
| Missing final stop, malformed/truncated stream | `Transient`, matching the existing OpenRouter incomplete-stream policy. |
| Confirmed `end_turn` with empty answer | `EmptyResponse`, matching existing application retries. |
| `pause_turn`, client `tool_use`, unsupported stop reason or unsupported content semantics | `AnswerIncomplete`, summary `answer_incomplete`; no continuation or tool execution. |

Do not add new failure enums, receipt fields, or persistence schema versions.
Existing `LlmExecutionException.Summary` can carry a short safe code. Caller
cancellation propagates; only the adapter's own deadline becomes a transient
timeout. Use a linked cancellation token for connect and body consumption.

### Response handling

Implement the Anthropic SSE lifecycle with indexed content blocks, deltas,
usage updates, errors, and a validated final message stop. Support JSON through
the same semantic reader. Do not reuse OpenRouter's `choices`, `annotations`,
or `[DONE]` reader. Validate block ordering and completeness; transport EOF
alone is not a successful answer.

Exclude thinking and tool payloads from the answer. For search responses,
retain the final answer text segment after the last server-tool/result block,
rather than intermediate search narration. Validate this segmentation against
recorded DeepSeek fixtures; if the response has no completed final answer
segment, fail instead of publishing narration. For responses without tools,
concatenate answer text blocks in order.

Response formatting, including visible links and citations, remains the
responsibility of the LLM and system prompt plus the existing Telegram delivery
pipeline. Preserve model-authored final answer text in `AnswerText`, subject to
existing canonicalization and size checks. Keep the existing source-extraction
functionality as separate metadata; do not add citation rendering or append a
source list to the answer.

The compatibility baseline below is established by the current
[OpenRouter JSON helpers](../src/RecurringTasksBot.Infrastructure/Llm/OpenRouter/OpenRouterJson.cs),
[JSON reader](../src/RecurringTasksBot.Infrastructure/Llm/OpenRouter/OpenRouterResponseReader.cs),
[stream reader](../src/RecurringTasksBot.Infrastructure/Llm/OpenRouter/OpenRouterStreamReader.cs),
and [transport tests](../tests/RecurringTasksBot.Tests/OpenRouterTransportTests.cs).

| Existing behavior | DeepSeek compatibility requirement |
| --- | --- |
| Citation metadata becomes `LlmSource(Title, Url)` entries; only URLs with case-insensitive `http://` or `https://` prefixes are accepted. | Map equivalent provider-returned citation metadata to `Sources`, preserving titles and the same URL filtering. Do not force `Sources` to be empty when supported metadata exists. |
| Sources are capped by `MaxTotalResults`; streaming deduplicates by exact URL, while the JSON helper currently retains duplicates. | Preserve the corresponding source-cap and duplicate-handling behavior in each response mode; do not broaden deduplication or URL validation as part of this migration. |
| `LlmUsage.SearchResults` equals the retained `Sources.Count`. Both `SearchUsed` fields equal `Sources.Count > 0`. | Keep these existing meanings for DeepSeek, including their limitations. Do not replace them with counts of retrieved pages or actual search calls. |
| Token counts populate existing usage and receipt fields; missing counters default to zero. Streaming uses reported usage values rather than summing repeated cumulative totals. | Translate DeepSeek's reported input/output usage into the existing fields with equivalent missing-value and cumulative-update handling. |
| Source metadata is separate from `AnswerText`; delivery does not append it to the answer. | Preserve this separation. Do not render structured citations, rewrite authored links, or insert missing links. |

Only extract citations identified by the provider's response metadata; do not
scan answer text for URLs or treat every search result as a cited source. If
citation metadata is absent, return an empty source list under the existing
contract. Verify the mapping with representative DeepSeek fixtures; do not
invent metadata to fill a provider gap. The legacy `SearchUsed` field is a
source-presence indicator, not authoritative proof of a web-search execution.
Keep that behavior for compatibility without adding a new evidence field.

Enforce the existing `MaxSourceScalars` answer-text bound during accumulation
and preserve completed-answer persistence before delivery. Retain existing
error sanitization and handling of thinking/tool payloads. No additional
configurable frame/block limits or metadata-retention subsystem are introduced
by this migration.

Preserve `LlmResult`, `LlmUsage`, and receipt schemas. No new counters,
search-evidence records, usage-completeness diagnostics, billing reconciliation,
or persisted accounting are required. Existing OpenRouter functionality stays
intact; adapting the DeepSeek wire format to these contracts is required
compatibility work, not a new user-facing feature.

### Completion and failure policy

Accept only a complete nonempty answer with validated completed-turn semantics
(initially `end_turn`). Handle in-body errors even when HTTP status is 200.

| Condition | Mapping / action |
| --- | --- |
| Network interruption, provider timeout, HTTP 429/temporary 5xx, temporary search unavailability | `Transient`; existing bounded application retry policy; honor `Retry-After`. |
| Authentication, insufficient credits, invalid request/query, unsupported model/tool | `Permanent`; sanitized error, no retry. |
| Confirmed output length limit or unsupported completion semantics | Terminal `AnswerIncomplete`; no truncation or partial delivery. |
| Interrupted/malformed stream or missing final stop | `Transient`, preserving existing incomplete-stream retry behavior. |
| Completed but empty answer | `EmptyResponse`, preserving existing application retries. |
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
If continuation is implemented later, it must share the original deadline,
aggregate output budget, and remaining search allowance, rather than starting
an unbounded agent loop.

## 8. Functional validation and rollout

The successful POC and its cost-saving result are accepted. Rollout depends on
functional integration tests, not further cost evaluation or provider comparisons.

1. Implement unified defaults and both profiles, initially selecting OpenRouter.
   Record the changed inherited behavior explicitly and verify that task
   overrides and frozen claims remain authoritative.
2. In development, select DeepSeek with the configured 8-search and 131,072-token
   ceilings. Confirm that the chosen model accepts the configured output/context
   limits and that representative tasks complete. If a limit is incompatible,
   choose an explicit supported setting; never silently clamp it.
3. Preserve the current reasoning and answer-size defaults. Lower reasoning,
   search limits, or completion budgets are optional future configuration choices,
   not experiments or acceptance requirements in this change. Reject incomplete
   answers rather than truncating them.
4. After the functional gates below pass, drain generation, apply the selector,
   restart, and monitor completion, errors, and latency. Roll back the selector
   to OpenRouter if needed; restore any shared settings changed for compatibility.
   Keep both credentials available during rollout for provider rollback.

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
  Use `__DEEPSEEK_API_KEY__` as its template placeholder. Keep
  `llmActiveProfile` as the static `OpenRouter` value in the parameter template
  (the deployment selector's canonical location); pass it through Bicep instead
  of introducing another mandatory workflow variable.
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
2. Derived defaults are `IncludePreviousMessage`, `max`, `true` for the target
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
joining, credential isolation, JSON/SSE contract compatibility, indexed block assembly,
final-segment selection, preserved model-authored answer text and inline links
without appended citations, citation metadata mapped to `Sources`, HTTP(S)
prefix filtering, the existing source cap and per-mode duplicate handling,
missing citation metadata, thinking exclusion, existing source-count/search-flag
semantics, token mapping and missing-usage defaults, in-body search errors,
search-budget failure,
`pause_turn`, unexpected client tools, incomplete streams, cancellation,
timeouts/`Retry-After`, and existing answer/source-list bounds. Use recorded sanitized
fixtures and fake transports, with no live CI requests or Telegram sends.

### Bounded live development gate

Using the development secret, exercise English and Russian current-information
tasks, timezone-sensitive dates, previous-reply memory, search disabled, and
representative long answers. Confirm actual server-side search, `max_uses`
enforcement, completion semantics, supported model limits, and latency within
the activity budget. Verify that the final model-authored text reaches the
existing delivery pipeline without new adapter-added source formatting and
that available citation metadata populates the existing source/usage contracts.
Document remaining compatibility findings. Promote when representative tasks complete
with acceptable functional behavior; otherwise keep OpenRouter selected until
the integration issues are resolved. No cost or billing gate applies.

## 11. Validation runbook and definition of done

Run commands from the repository root. Required tools are the .NET 10 SDK,
Bash, and Python 3 for existing shell scripts/tests. Live bot testing additionally
needs Azure Functions Core Tools (`func`), development storage, a development
Telegram bot, and the known development test chat/driver. Use the supplied
environment; do not install a new provider SDK or a browsing tool.

### Offline gates

```sh
dotnet restore RecurringTasksBot.sln
dotnet build RecurringTasksBot.sln --configuration Release --no-restore
dotnet test RecurringTasksBot.sln --configuration Release --no-build
bash tests/scripts/test-launch-local.sh
bash tests/scripts/test-deploy-config.sh
dotnet build tools/LlmSmoke/LlmSmoke.csproj --configuration Release
dotnet publish src/RecurringTasksBot.FunctionApp/RecurringTasksBot.FunctionApp.csproj \
  --configuration Release --no-restore --output /tmp/recurringtasksbot-phase6-publish
```

Inspect published `appsettings.json` for both profiles and macro strings, with
no embedded credentials. Run structural validation for both profiles without
requiring either key:

```sh
RecurringTasksBot__Llm__ActiveProfile=OpenRouter \
  dotnet run --project tools/LlmSmoke/LlmSmoke.csproj --configuration Release --no-build -- \
  --config-dir src/RecurringTasksBot.FunctionApp --validate-config
RecurringTasksBot__Llm__ActiveProfile=DeepSeek \
  dotnet run --project tools/LlmSmoke/LlmSmoke.csproj --configuration Release --no-build -- \
  --config-dir src/RecurringTasksBot.FunctionApp --validate-config
```

Update `ConfigurationTests`, `FunctionHostTests`, `GenerationPolicyTests`,
`LlmRequestContractTests`, task/default tests, and shell tests as needed. Add
`DeepSeekTransportTests` with fake `HttpMessageHandler` responses and the
embedded JSON/SSE fixtures. Do not remove existing OpenRouter regression tests
or weaken assertions unrelated to intentional configuration/default changes.
Run the full gates once after the final edits; repeat affected checks if fixes
are needed. No paid calls belong in these gates.

### Live adapter checks

Credentials are process environment variables, never command arguments:

| Variable | Used by |
| --- | --- |
| `RecurringTasksBot__Llm__DeepSeek_ApiKey` | Direct DeepSeek profile |
| `RecurringTasksBot__Llm__ApiKey` | OpenRouter profile |
| `RecurringTasksBot__Telegram__BotToken` | Development bot testing only |
| `RecurringTasksBot__Telegram__WebhookSecret` | Development local webhook |
| `RecurringTasksBot__AzureWebJobsStorage` | Development task/Durable storage |

Verify presence without printing values. Do not substitute credentials from
another application automatically. Missing live prerequisites are an external
test blocker, not a reason to abandon implementation or offline verification.
Keep a short human-readable test record (scenario, outcome, observed limitation)
in the final handoff; no new runtime evidence or billing subsystem is needed.

After adapting the smoke tool in step 5, run the actual production adapter:

```sh
RecurringTasksBot__Llm__ActiveProfile=DeepSeek \
  dotnet run --project tools/LlmSmoke/LlmSmoke.csproj --configuration Release --no-build -- \
  --config-dir src/RecurringTasksBot.FunctionApp --execute --max-requests 1 \
  --prompt 'Search the live web for the latest stable Python release on python.org. State its version and release date with the official link.'

RecurringTasksBot__Llm__ActiveProfile=DeepSeek \
RecurringTasksBot__Llm__SearchEnabled=false \
  dotnet run --project tools/LlmSmoke/LlmSmoke.csproj --configuration Release --no-build -- \
  --config-dir src/RecurringTasksBot.FunctionApp --execute --max-requests 1 \
  --prompt 'Compute 17 times 19. Do not search the web.'
```

Each command requests one generation; keep the default zero adapter retries.
The first uses normal configured budgets and the production system instruction.
The second must complete with 323 and no search tool in the request. Do not
change checked-in defaults to make a test pass. A missing citation in a live
response is not proof of failed search: inspect the protocol in the local test
session and apply the existing metadata contract described in section 7.

For remaining scenarios, reuse the same command with the stated prompt/option:

| Scenario | Test input / check |
| --- | --- |
| Russian research and dates | Ask in Russian for a recent Python release, its date, and an official link; verify a completed Russian answer and the occurrence's supplied date/time context. |
| Previous-answer memory | Supply `--previous-reply 'The previous run selected the marker ALPHA.'` and ask which marker the previous run selected; verify ALPHA is available with memory enabled and omitted from messages when `Memory:Mode=None`. |
| Long answer | Request a substantial multi-section explanation with a list and a link; verify the existing delivery plan remains valid without truncation. Use offline fixtures for exact size boundaries. |
| JSON vs SSE | Exercise the JSON reader with a bounded direct development request using the request example and `stream:false`; the normal adapter covers SSE. Use a temporary local probe if necessary, remove it afterward, and do not retain a second product execution path. |
| Search limit | Temporarily override `RecurringTasksBot__Llm__Profiles__DeepSeek__MaxSearches=1` for a focused search. Confirm the request carries `max_uses:1` and inspect observed server calls locally; do not assert that observing one call alone proves exhaustion handling. Use the embedded error fixture for deterministic exhaustion behavior. |
| OpenRouter regression | Select OpenRouter and run a focused research smoke with its existing credential. Verify its request/response behavior, source cap, and delivery plan are retained. |

Do not fetch provider docs or calculate prices to finish these checks. Inspect
live protocol differences using public prompts only. Convert relevant structure
into synthetic/sanitized offline fixtures; do not commit raw thinking, queries,
retrieved content, credentials, chat IDs, or opaque encrypted payloads.

### Live development-bot check

Start the updated application with the development profile and DeepSeek selected:

```sh
export RecurringTasksBot__Llm__ActiveProfile=DeepSeek
./scripts/launch-local.sh
```

The launcher verifies development bot/storage identity. Keep those guards.
Use an already configured test driver or, in another terminal, development
polling through the existing webhook path:

```sh
./scripts/poll-dev.sh --timeout 20
```

Polling deletes the development bot's webhook while active; use only the test
bot. If a tunnel was in use, restore its prior development webhook afterward
with `scripts/register-webhook-dev.sh <previous-development-tunnel-url>`.

In the known development test chat, submit this current JSON command syntax:

```text
/create {"prompt":"Search the web for the latest stable Python release on python.org; include the version, date, and official link.","schedule":{"cron":"0 * * * * *"},"timezone":"Europe/Moscow","parameters":{"maxOccurrences":1}}
```

Use the returned task ID for `/get <id> effective`; verify inherited
`IncludePreviousMessage`, `max`, and `webSearch:true`. Wait for the occurrence's
single completed answer, check ordinary rich-text/link delivery, then issue
`/delete <id>`. Run a second one-occurrence task with
`parameters.webSearch=false` and the arithmetic prompt. For memory, run a
bounded two-occurrence task asking each run to identify its previous answer;
verify the second receives the first successful reply. Check provider/model
receipt metadata reflects DeepSeek. Delete only test tasks created by this run.

Use actual test-chat access or the configured development driver, not an
invented chat ID. If that prerequisite is missing, finish live adapter tests
and report the bot-delivery check as blocked rather than claiming it ran.
After tests, stop only the host/poller started for this work, restore temporary
environment overrides and development routing, and leave shared data intact.
Do not deploy to production as part of this implementation handoff.

### Completion report

The executing agent must provide:

- Changed files and confirmation that defaults/profiles/credentials share one
  resolution path between host and smoke tool.
- Offline test and publish results, including any pre-existing failures.
- Live scenarios actually run, selected model/provider, outcomes, and any
  observed protocol incompatibility. Distinguish live observations from fixtures.
- Confirmation that source extraction/counts, answer formatting, persistence,
  and OpenRouter behavior retain the compatibility rules in section 7.
- Required environment/deployment migration and any operator action remaining;
  keep production selection at OpenRouter and describe the DeepSeek selector.

Implementation is complete when the functional gates pass and no required code,
tests, or current documentation remain unfinished. An unavailable credential or
test chat must be reported explicitly as unverified live coverage. No cost
comparison, new citation feature, production deployment, or billing report is
part of the definition of done.
