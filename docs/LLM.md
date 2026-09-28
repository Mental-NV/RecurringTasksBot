# LLM

One contract: `ILlmExecutor.ExecuteAsync(LlmRequest)`. The application
builds the complete message list; the OpenRouter adapter
(`OpenRouterLlmExecutor`) serializes it verbatim and never injects a
second system prompt.

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

`Llm:ReasoningEffort` maps to the request `reasoning.effort` (default
`Maximum`). Search uses the provider tool (`max_tool_calls` =
`MaxSearches` 8, up to 5 results per call, 40 total) with
`engine: parallel`, `mode: fast` (see
[Configuration](Configuration.md) for overrides, pricing, and
rollback). These are upper bounds, not required usage; each LLM
request may issue multiple searches. Fast-mode language coverage is
unspecified upstream — verify multilingual quality with a live smoke
check after rollout.

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
request with the development key: `--prompt`, `--previous-reply`,
`--model`, `--config-dir`, `--max-source-scalars`, `--max-requests`
flags, or `SMOKE_PROMPT_FILE` for the prompt file. It verifies a
current-information answer with source links, reasoning excluded from
the response, and composition into valid rich-message parts. Prints
diagnostics/usage only. Ordinary CI makes no live or paid calls.

## Data sent externally

Only the prompt text and scheduling context leave the system — never
credentials, other users' data, or internal state. Provider/model
changes apply to new attempts; persisted results deliver unchanged.
