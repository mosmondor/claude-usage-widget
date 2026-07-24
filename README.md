# Claude Usage Widget

A tiny always-on-top Windows desktop widget that shows your **Claude Code / Claude subscription usage** at a glance:

- **Plan limits** — the same numbers as Claude Code's `/usage`:
  - **Session** (rolling 5-hour window) — % used + reset countdown
  - **Weekly · all models** — % used + reset
  - **Weekly · per model** (e.g. Fable) — % used + reset
  - **Extra credits** (if enabled)
- **This month, per model** — token volume + a rough **API-equivalent** cost estimate
- **Today** — total tokens + estimate

Borderless floating card, drag with the left mouse button, right-click (or the tray icon) for the menu.

<p align="center"><img src="docs/screenshot.png" alt="Claude Usage Widget" width="300"></p>

## How it works

Two independent local data sources — nothing is uploaded anywhere:

1. **Plan limits** come from Claude Code's own usage endpoint,
   `GET https://api.anthropic.com/api/oauth/usage`, authenticated with the OAuth
   access token that Claude Code stores in `~/.claude/.credentials.json`.
   The widget reads that token **locally at runtime** and sends it only to
   Anthropic's usage endpoint (exactly like Claude Code does). It is never
   stored, logged, or transmitted anywhere else.
2. **Per-model token counts** are computed by scanning your local Claude Code
   transcripts in `~/.claude/projects/**/*.jsonl` (the `message.usage` fields).
   Records are de-duplicated globally by message id (Claude Code writes the same
   message into multiple files) and filtered to Claude models only.

## Requirements

- Windows
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (LTS)
- Claude Code installed and logged in (for the plan-limit numbers)

## Build & run

```sh
dotnet build ClaudeUsageWidget.sln -c Release
dotnet test  ClaudeUsageWidget.sln -c Release
# then run ClaudeUsageWidget/bin/Release/net8.0-windows/ClaudeUsageWidget.exe
```

To start it automatically at login, drop a shortcut to the exe into
`shell:startup`.

## Project layout

- **`ClaudeUsageWidget.Core`** — parsing, pricing, de-duplication and the month-scoped
  transcript store. No UI dependency, fully unit-tested.
- **`ClaudeUsageWidget`** — the WinForms tray + floating-card UI (`net8.0-windows`).
- **`ClaudeUsageWidget.Tests`** — xUnit tests (parsing, dates/timezone, pricing, de-dup, cache).

### De-duplication rule

Claude Code writes the same assistant message into several transcript files, and retries can
report different token counts for one message id. To stay order-independent, when several records
share a message id the **canonical** one is the record with the greatest total tokens (ties broken
by output, then input, then cache-read, then cache-write). Records without an id are all kept.

## Caveats

- **The `$` figures are a rough, notional API-equivalent estimate — not a bill.**
  On a Pro/Max subscription you pay a flat fee; the `$` shown is roughly "what
  this would cost on the pay-per-token API". Because most agentic usage is
  cache reads, the totals look large but cost little.
- **Fable's public price isn't known**, so it is priced at the Opus tier, which
  makes the estimate run high. For an authoritative dollar figure use
  [`ccusage`](https://github.com/ryoppippi/ccusage). Adjust the rates in
  `Pricing.For(...)` if you want.
- The plan-limit endpoint is **undocumented / internal** and may change at any
  time. This is a personal convenience tool; use at your own risk.

## License

MIT — see [LICENSE](LICENSE).
