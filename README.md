# SmartEdit MCP

**Line-based text editor for LLM agents.** A zero-dependency [Model Context Protocol](https://modelcontextprotocol.io) stdio server that lets coding agents edit files by **line numbers** — no byte-exact `old_string` to drift out of sync, no whole files loaded into context. Works with any MCP client: **ZCode, Claude Desktop, Cursor, Windsurf, Cline** and anything else that speaks stdio MCP.

[![Version](https://img.shields.io/badge/version-v0.6.1-blue)](https://github.com/ERambler/MCP-SmartEdit/releases)
[![.NET](https://img.shields.io/badge/.NET-10-purple)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)
[![MCP](https://img.shields.io/badge/MCP-stdio%20server-orange)](https://modelcontextprotocol.io)
[![Selftest](https://img.shields.io/badge/selftest-%7E130%20checks%20ALL%20PASS-brightgreen)](#verification)

```text
the #1 failure mode of built-in Edit tools:
agent recomputes the file -> old_string no longer matches -> edit rejected -> tokens burned

SmartEdit: find -> show -> replace_lines(41..52, "…", expected_rev=17) -> done.
Nothing to mismatch. Ever.
```

---

## Why it exists

| Built-in Edit / str_replace / apply_patch | SmartEdit |
|---|---|
| needs byte-exact `old_string` that **drifts** after context summarization | addresses lines by **number**, one integer `rev` guard |
| whole file in context to edit it | `show`/`find` read only what is asked; mmap up to **~2 GB UTF-8** |
| last writer wins — silent clobbering | named-offender conflicts: `stale_rev: (r17 by s3: replace 41..52 @ 12:33)` |
| errors are prose | errors are **JSON**: `{"kind":"stale_rev","message":"…","current_rev":17}` |
| no undo, no audit | **undo** (compensating mutations), shared cross-project **log** every agent can read |

## Feature highlights (20 tools)

- **Core editing** — `open` / `show` / `find` (regex + breadcrumbs) / `replace_lines` / `insert_after` / `save` / `close`
- **`edit_batch`** — N edits in ONE atomic call: all ranges and anchors validated *before* anything applies (one bad edit rejects the whole batch), applied bottom-up so line numbers need no shift math, single rev bump
- **`replace_all`** — literal (non-regex) rename across the whole file; refuses *entirely* if too many lines match (a partial rename would break code)
- **`expect_old` anchors** — optional exact-content guard on any edit; catches the agent's own wrong-line math (rev only catches *external* drift)
- **`undo`** — undo as a *compensating mutation*: new rev, recorded in history, safe with peers (locations re-found by content). `undo` after `undo` naturally redoes
- **Multi-agent collaboration** — one shared document per file path: every editor sees one piece table and one rev; `open(path, note="why I'm here")` lists peers and recent edits; a stale edit names **who** changed **what** and **when**; `save` refuses with `peers_dirty` while other sessions have unsaved work
- **Structure vision** — `symbols` / `goto` (C#, Python, JS/TS, Java, C/C++, XML/HTML), `tree` project map (C# namespaces), `check` bracket audit (interpolation-aware)
- **Encoding safety (Windows-first)** — cp1251 / UTF-16 open + round-trip, `file_info`, `convert`; save keeps the original encoding by default
- **Operability** — `log`: the shared journal of *all* sessions and processes (every call, every error kind, per-pid, rotated at 5 MB) that the agent itself can read and grep to retrace its own steps; `sessions` diagnostics
- **`calc`** — exact arithmetic (hex/bin/`^`/`!`/functions); LLMs should not do mental math

## Performance (measured, see [Verification](#verification))

| Operation | Result |
|---|---|
| Cold start to full `tools/list` (R2R single-file) | **~220 ms** |
| `open` 300 MB / 4.9 M lines (parallel line index) | **1.2 s** |
| `find` regex over 300 MB | **0.6 s** |
| `save` 300 MB (streamed, xxHash64 on the fly) | **1.5 s** |
| Typical tool call | **< 1 ms** server-side (ms-precision in the journal) |

Memory stays flat on giants: memory-mapped base + piece-table edits; `save` streams; bracket audit streams.

## Field telemetry & research

One day of dogfooding (the agent developing SmartEdit *through* SmartEdit), harvested from the shared journal:

- 496 ok / 25 error calls; `replace_lines` 37 %, session lifecycle 43 %, `find`+`show` 15 %
- **`stale_rev` was 8.6 % of replaces** (64 % of all errors) — roadmapped as P0: stale-recovery auto-retry with anchor revalidation
- `symbols` used on only 3 of 86 opens despite being a flagship — v0.6 plan: code outline directly in the `open` response
- The analysis uncovered and fixed a real v0.2-era bug: UTF-8 detection falsely rejected files whose Cyrillic landed exactly on the 4096-byte sample boundary ([fix](src/SmartEdit.Mcp/Core/EncodingDetect.cs), regression-tested)

Design decisions were cross-reviewed with independent LLM consultants (DeepSeek R1 deep-think + ChatGPT, in parallel): the collaboration architecture scored 6/10 as first proposed and **8/10 after revisions** (global-rev guard kept, `peers_dirty` save guard added, adaptive presence/events instead of a per-response token tax, soft range-claims deferred). Closest alternative: [tumf/mcp-text-editor](https://github.com/tumf/mcp-text-editor) (hash-anchored patches) — SmartEdit differs by hybrid rev+anchor concurrency, structure vision, multi-agent presence and compensating undo.

## Install

**Requirement: [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0)** (framework-dependent single-file exe, ~1.4 MB). Windows x64 today; the code is pure BCL — `dotnet publish -r linux-x64` should work, feedback welcome.

### Option A — one-liner for agents (PowerShell)

```powershell
irm https://raw.githubusercontent.com/ERambler/MCP-SmartEdit/main/install/install.ps1 | iex
```

Downloads the latest release to `%LOCALAPPDATA%\SmartEdit\bin`, runs the selftest, prints ready-to-paste client configs.

### Option B — manual

1. Download `SmartEdit-win-x64-vX.Y.Z.zip` from [Releases](https://github.com/ERambler/MCP-SmartEdit/releases), unzip (e.g. into `%LOCALAPPDATA%\SmartEdit\bin`)
2. Sanity check: `SmartEdit.Mcp.exe selftest` — must print `ALL PASS` (exit code 0, CI-friendly)
3. Register with your MCP client (below)

### ZCode (user scope) — notes from our own bruises

Config: `~/.zcode/cli/config.json` (create the `mcp` section if missing):

```json
{
  "mcp": {
    "servers": {
      "smartedit": { "command": "C:\Users\YOU\AppData\Local\SmartEdit\bin\SmartEdit.Mcp.exe", "args": [] }
    }
  }
}
```

Learned the hard way:

- **A skill file is NOT a server registration.** The skill only teaches *when* to call the tools; without the `mcp.servers` entry the `mcp__smartedit__*` tools never appear.
- New server = **new chat session** (no hot reload). Restart the client or open a new conversation after editing the config.
- The exe is **locked while a session uses it**: to upgrade, close chats / `Stop-Process -Name SmartEdit.Mcp`, overwrite, reopen.
- An open session holds its file `FileShare.Read` — external writers get EBUSY until `close`. Intentional; call `sessions` if a file feels locked.
- Optional companion skill (usage protocol, smart vs built-in edit): copy [`skills/zcode-smartedit/SKILL.md`](skills/zcode-smartedit/SKILL.md) to `~/.zcode/skills/smartedit/SKILL.md`. **Writing your own skill for another agent/client?** Read [`skills/README.md`](skills/README.md) first — a field-tested guide on authoring skills that make agents confidently prefer the right tool (routing descriptions, error→action branches, counter-intuitive semantics, lifecycle hygiene).

### Claude Desktop

`%APPDATA%\Claude\claude_desktop_config.json`:

```json
{ "mcpServers": { "smartedit": { "command": "C:\Users\YOU\AppData\Local\SmartEdit\bin\SmartEdit.Mcp.exe" } } }
```

### Cursor / Windsurf / any stdio MCP client

`~/.cursor/mcp.json` (or your client's equivalent) — same shape:

```json
{ "mcpServers": { "smartedit": { "command": "C:\Users\YOU\AppData\Local\SmartEdit\bin\SmartEdit.Mcp.exe", "args": [] } } }
```

The server speaks standard MCP (initialize / tools/list / tools/call) over stdio with **no client-specific assumptions** — if your client supports MCP stdio servers, it supports SmartEdit.

### Environment variables

| Variable | Effect |
|---|---|
| `SMARTEDIT_DATA_DIR` | data dir (default `%LOCALAPPDATA%\SmartEdit`); the shared journal `mcp.log` lives there |
| `SMARTEDIT_LOG=debug` | verbose protocol logging (method/id/size only — tool-call payloads never enter the journal) |
| `SMARTEDIT_TELEMETRY=off` | disable telemetry (`tool … → ok` lines with args summary); `0`/`false` also accepted. Errors keep being logged — that's diagnostics, not telemetry |
| `SMARTEDIT_NO_UPDATE_CHECK=1` | disable the startup update check (same as the `--no-update` command-line arg) |

### Update check

On startup the server asks GitHub for the latest release (background thread, 5 s timeout, never blocks stdio). If a newer version exists it shows a native system dialog — release notes + **OK/Cancel**. **OK installs the update with no further user action**: the zip is downloaded, extracted, and the binary replaces itself in place (Windows rename dance with `.exe.old` cleanup on next start; Linux atomic self-overwrite) — the new version takes effect on the next server start. **Cancel** remembers your choice and never asks about *that* version again (the next release will ask). Windows uses `MessageBoxW`; Linux uses `zenity`/`kdialog`/`xmessage` when a graphical session is available — otherwise the update notice goes to the shared journal (visible to the agent via `log`). Opt out entirely with `--no-update` or `SMARTEDIT_NO_UPDATE_CHECK=1`.

## The 60-second tour

```jsonc
// 1. open — session, rev, outline, peers (multi-agent aware)
open(path: "C:\\src\\BigFile.cs", note: "refactoring utils")
// -> { sessionId: "sab12…", rev: 0, totalLines: 84213, peers: [...], recent: [...] }

// 2. navigate — regex with context snippets + symbol breadcrumbs
find(sessionId, regex: "EditSession\\(", context: 2)

// 3. edit — by line numbers, anchored, atomic; diff + bracket audit come back in the response
replace_lines(sessionId, start: 41, end: 52, new_text: "…", expected_rev: 0,
              expect_old: "…exact current text of lines 41..52…")

// 4. many edits at once — validated before applied, no shift math, one rev bump
edit_batch(sessionId, edits: [
  { start: 10, end: 12, new_text: "…", expect_old: "…" },
  { start: 400, end: 400, new_text: "…" },
  { start: 900, end: 899, new_text: "inserted" }   // end=start-1 -> insertion
], expected_rev: 1)

// 5. oops — undo is a compensating edit (undo twice = redo)
undo(sessionId, n: 1, expected_rev: 2)

// 6. persist — keeps the original encoding; refuses while peers have unsaved edits
save(sessionId)   // peers_dirty if someone else is mid-flight; force:true publishes all
```

Every mutation response carries: new `rev`, unified `diff`, bracket problems, and (in shared files) the `events` you missed. Every error is JSON with a machine-readable `kind`.

## Verification

- `SmartEdit.Mcp.exe selftest` — ~130 checks: encodings (cp1251/UTF-16 round-trips, BOM, the 4096-boundary regression), rev-guard, anchors, batch atomicity, undo/redo/conflict, collaboration (reuse, named offender, peers_dirty, detach), log handle, 300 MB big-file flow, full protocol round-trip. Exit code 0/1 — used as the CI gate.
- Cold-start and per-call timings measured on the release binary; per-call milliseconds are written to the shared journal (`log` tool) so anyone can audit performance.

## Changelog

See [CHANGELOG.md](CHANGELOG.md). Highlights: v0.2 structure vision -> v0.3 batch/anchors/JSON errors -> v0.4 multi-agent collaboration -> v0.5 undo/goto/shared log + telemetry v2 -> v0.5.1 UTF-8 boundary fix -> v0.5.2 log-injection hardening + telemetry opt-out -> v0.5.3 debug-payload masking -> v0.6.0 startup update check -> v0.6.1 silent self-install.

## Roadmap & TODO

Tracked in [GitHub Issues](https://github.com/ERambler/MCP-SmartEdit/issues) — pick one and contribute:

- [#1 stale-rev auto-recovery](https://github.com/ERambler/MCP-SmartEdit/issues/1) — retry-once with anchor revalidation (**P0** per telemetry: 8.6 % of replaces hit stale_rev)
- [#2 code outline in `open` response](https://github.com/ERambler/MCP-SmartEdit/issues/2) — adoption fix for structure vision (3 uses / 86 opens)
- [#3 multi-file transactions](https://github.com/ERambler/MCP-SmartEdit/issues/3) — `edit_transaction` across sessions, commit-all-or-nothing
- [#6 CI: build + selftest gate + release artifact](https://github.com/ERambler/MCP-SmartEdit/issues/6)
- [#7 self-contained & Native AOT builds](https://github.com/ERambler/MCP-SmartEdit/issues/7) — drop the .NET 10 runtime requirement
- [#5 attention tracking](https://github.com/ERambler/MCP-SmartEdit/issues/5) — warn on edits to ranges the agent never read ("you're editing blind at line 812")
- [#8 parameterized edit recipes](https://github.com/ERambler/MCP-SmartEdit/issues/8) — replayable validated refactors across N files (LLM-consultant idea)

## License

[MIT](LICENSE) © ERambler
