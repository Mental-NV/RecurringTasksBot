# LLM

One contract: `ILlmExecutor.ExecuteAsync(LlmRequest)`. The application
builds the complete message list; the selected adapter
(`OpenRouterLlmExecutor` or `DeepSeekLlmExecutor`) serializes it
verbatim and never injects a second system prompt.

## Profiles and selection

`RecurringTasksBot:Llm:ActiveProfile` selects one named profile from
`RecurringTasksBot:Llm:Profiles` (see
[Configuration](Configuration.md)). The selector switches adapter,
endpoint, model, and credential together through the shared
`LlmAdapterFactory`; the host and the smoke tool resolve the same
path. Production selects `DeepSeek`; switching profiles requires
draining retained generation work first (frozen
claims keep prompt and settings but not provider, model, credential,
or budget).

## Message order and roles

Built by `ExecutionMessageBuilder.BuildMessages`
(`Application/ExecutionContext.cs`):

- Without memory: `system` (effective instruction), then `user` (current
  occurrence envelope).
- With a previous successful reply: `system`, `user` (archived-occurrence
  metadata), `assistant` (previous answer), `user` (current envelope).

The system instruction is `RecurringTaskSystemPrompt.Render`
(`Application/ExecutionContext.cs`): template `recurring-task-v2` plus
the operator `Llm:SystemInstruction` suffix. The effective instruction,
model, declared context, and memory mode are frozen into the persisted
context row at generation time, so retries reuse the identical snapshot.

The current envelope supplies the task's IANA timezone in
`execution_context.schedule_timezone`. Context timestamps remain UTC; the
system instruction requires conversion to that timezone for displayed dates
and times, including references to previous occurrences and relative dates
such as "today". Conversion uses the offset at each instant, including daylight
saving time and calendar date changes, unless the task requests another timezone.

## Memory selection

`Memory:Mode` is `PreviousSuccessfulReply` or `None`. Only the single
most recent successful reply is archived per operation; failed runs never
publish memory. Memory rows live while their operation exists and are
removed only with deleted-operation cleanup.

## Budgets

`Application/ExecutionOptions.cs` (operator-settable; see
[Configuration](Configuration.md)):

- Answer text target 24,000 chars; answers must fit Telegram's 32,768
  rich-text ceiling or the attempt fails visibly — never truncated.
- Answer source bound 131,072 chars (`LlmRequest.MaxSourceScalars`);
  streaming readers enforce it while accumulating (`answer_source_limit`).
- Declared context 1,048,576 tokens with envelope/search reserves;
  `ContextBudget` checks the byte-based estimate before sending.
- Completion budget 131,072 tokens via `max_completion_tokens`.
- 2 generation retries (`GenerationPolicy.MaxRetriesAfterInitial`);
  `AnswerIncomplete` and `SourceLimit` are terminal, never retried.
- Single request timeout 480s; must stay under the 510s activity budget
  (`ExecutionLimits.MaxLlmTimeout`), enforced at startup.

## Reasoning and search mapping

The shared `Llm:ReasoningEffort` (`Maximum`, normalizing to task token
`max`) maps per adapter. OpenRouter sends `reasoning.effort`
(`med` → `medium`) with `exclude: true`; direct DeepSeek sends
`thinking: {type: enabled}` plus `output_config.effort` on its
low/high/max scale (`med`, `high`, `xhigh` → `high`).

OpenRouter search uses the provider tool (`max_tool_calls` =
`MaxSearches` 8, up to 5 results per call, 40 total) with
`engine: parallel`, `mode: fast` (see
[Configuration](Configuration.md) for overrides, pricing, and
rollback). DeepSeek search sends one `web_search_20250305` tool with
`max_uses` = profile `MaxSearches`; per-search and total server
result-count controls have no DeepSeek equivalent and are never sent.
These are upper bounds, not required usage; each LLM request may
issue multiple searches. Fast-mode language coverage is unspecified
upstream — verify multilingual quality with a live smoke check after
rollout.

For DeepSeek search responses only the final answer segment after the
last server-tool/result block is kept; source metadata comes from
citations on that segment alone (`LlmSource` entries, capped by
`MaxTotalResults`, same `SearchUsed` source-presence meaning as
OpenRouter). Model-authored text and inline links are preserved
without appended citation rendering.

## Transport and errors

SSE transport with keep-alives; the full answer is collected before
delivery and a disconnected stream is retried, never sent partially.
`LlmFailureKind`: `Transient` (retryable), `Permanent`, `EmptyResponse`,
terminal `AnswerIncomplete` / `SourceLimit`. Provider errors inside HTTP
200 bodies classify as failures. Connection failures record transport
category and elapsed time separately from timeouts. Logs never contain
prompts, answers, history, or tokens.

## Smoke test

`scripts/smoke-llm-dev.sh` (via `tools/LlmSmoke`) runs one bounded live
request with the selected profile's development key: `--prompt`,
`--previous-reply`, `--model`, `--config-dir`, `--max-source-scalars`,
`--max-requests` flags, or `SMOKE_PROMPT_FILE` for the prompt file.
`--validate-config` checks non-secret structure and reference syntax
for the selected profile without requiring its key or network access.
`--execute` verifies a complete answer with valid delivery leaves; the
citation check applies to search-enabled runs only (a source-presence
indicator, not proof of executed search). Prints diagnostics/usage
only. Ordinary CI makes no live or paid calls.

## Data sent externally

Only the prompt text and scheduling context leave the system — never
credentials, other users' data, or internal state. Provider/model
changes apply to new attempts; persisted results deliver unchanged.
