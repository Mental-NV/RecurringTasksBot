# Documentation

Find guidance by task. Each page owns its topic; pages link instead of
copying configuration tables or procedures.

## Operate

- [Setup](Setup.md) — prerequisites, secrets, local launch, polling/tunnel
  alternatives, verification, shutdown.
- [Deployment](Deployment.md) — Azure resources, OIDC setup, workflow
  triggers, the single deploy flow, webhook registration,
  failure handling.
- [Runbook](Runbook.md) — diagnostics, stranded starts, delivery/LLM
  failures, leases, cleanup/dry-run/retention, restart recovery.
- [Monitoring](Monitoring.md) — metric/log checks and temporary queue and
  scale-controller diagnostic captures; deployment setup is in Deployment.
- [CI](CI.md) — offline commands, test organization, gates.

## Understand

- [Architecture](Architecture.md) — project direction, runtime flow,
  responsibility boundaries, how to add a command or provider adapter,
  non-goals.
- [TelegramCommands](TelegramCommands.md) — command syntax, validation,
  receipts, redelivery, HTTP outcomes.
- [LLM](LLM.md) — message roles/order, system prompt, budgets, errors,
  smoke test, data sent externally.
- [Persistence](Persistence.md) — entity/key schema, state/claim
  transitions, transactions, integrity errors, cleanup rules.
- [FunctionApp](FunctionApp.md) — bindings/routes, DI lifecycle,
  recurrence state and continuation, activity outcomes, timeouts.
- [TelegramTransport](TelegramTransport.md) — payload kinds, chunking,
  rejection classification, fallbacks.
- [Configuration](Configuration.md) — canonical setting inventory.

## Plan

- [Phase 6: LLM profiles and direct DeepSeek](Spec.Phase6.md) — implementation plan for unified
  task defaults, provider selection, environment references, native search,
  functional validation, and rollout.
- [Natural-language commands](NaturalLanguageCommands.md) — proposed LLM
  interpretation, guardrails, confirmation, conversation state, and framework choices.
- [Backlog](Backlog.md) — open scale/security questions and future ideas.
- [History](History/README.md) — frozen historical specs (not current
  guidance), including the completed `Spec.Phase5.md`.

## Authority and maintenance

Current docs describe the code as implemented. When behavior changes, update
the owning page in the same change and keep its regression test alongside.
Do not write "Phase N does X; Phase M changes Y" in current docs. No
secrets or user content in examples.
