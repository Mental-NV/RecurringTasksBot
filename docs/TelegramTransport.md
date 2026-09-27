# Telegram transport

One contract: `ITelegramTransport.SendAsync(chatId, TelegramPayload)`
sends exactly one payload in exactly one HTTP request and returns the
confirmed message ID. Any failure throws; the caller owns chunking,
fallback transitions, and retries.

## Payload kinds

`TelegramPayload(Kind, Content)` with kind `Markdown`, `LiteralRich`,
or `LiteralPlain`:

- `Markdown` — `rich_message.markdown` with the composed answer; plan
  leaves capped at 128 (`ExecutionLimits.MaxPlanLeaves`).
- `LiteralRich` — literal rich fallback when Markdown is rejected.
- `LiteralPlain` — conservative plain chunks (4,096 units,
  `TelegramLimits.PlainFallbackMaxUnits`) when the method is
  unavailable or rich content is rejected.

New answers are delivered natively without wrappers or appended
sources. An oversized answer fails visibly; new answers are never
substring-truncated.

## Chunking

Literal text splits in Unicode scalar (`Rune`) units with a 512-scalar
newline window (`LiteralMessageChunker`); splits never separate a
surrogate pair and prefer newline boundaries. Limits are measured in
scalars, never UTF-8 bytes or UTF-16 units.

## Rejection classification

`TelegramClassification(Disposition, Category)`:

- `ContentRejection` → deterministic fallback (Markdown to literal
  rich; literal-size rejection to conservative chunks).
- `UnknownMethod` → plain fallback.
- `RateLimited` / `Transient` → retryable via orchestration timers.
- `PermanentRecipient` / `TerminalFailure` → terminal, recorded on the
  occurrence.

## Acknowledgement ambiguity

A send whose HTTP result is lost is ambiguous: the message may exist
without a confirmed ID. Redelivery is keyed on the persisted receipt,
not on resending blindly — duplicate occurrence deliveries resolve to
`SkippedDuplicate`.
