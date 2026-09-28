# Backlog

## Future ideas

- [ ] [UI/UX] Send long responses as Markdown file attachments, with only a
  short summary in the message.
- [ ] [UI/UX] Build a friendly web UI for managing recurrent tasks.
  Investigate cheap or free Azure hosting; consider a second Function
  App for UI backend operations designed for minimal invocation to
  control billing.
- [ ] [UI/UX] Ability to call commands through regular conversation (execute the commands as tool calling though LLM)
- [x] [UI/UX] A command to update existing recurring task (any available parameter, e.g. prompt, schedule, reasoning effort, etc)

- [ ] [UI/UX] CLI access (depends on authenticaion and security)
- [ ] [Security] Check user ownership (permission) when delete a recurring task
- [ ] [Security] Authentication though JWT tokens for external clients (CLI, Web UI)
- [ ] [Architecture] Hexagon architecute (ports, adapters) with different clients (Telegram Bot, Web UI, CLI)
- [x] More rich ability to set recurring task:
  - [x] one time schedules, up to 10
  - [x] ability to set any timezone, UTC by default
  - [x] set parameters as JSON
  - [x] [parameter] MemoryMode = (IncludePreviousMessage, None)
  - [x] [parameter] set a reasoning effort (low, med, high, xhigh, max), low by default
  - [ ] [parameter] choose a LLM model (through a profile name)
  - [x] [parameter] enable/disable web search (enabled by default)

- [ ] Let agent to schedule future onetime occurence atomaticaly (tool calling) through user promt
- [ ] Monetization using Telegram Start
- [ ] Billing for real money usage
- [ ] Limits based on available credits (Telegram starts)
- [x] Switch from "exa" serach engine to "prallel"
- [ ] Adding observability through App Insights
- [ ] Different LLM profiles and a default profile (OpenRouter provider)
- [ ] Support different LLM providers (direct DeepSeek, direct OpenAI, Azure)
