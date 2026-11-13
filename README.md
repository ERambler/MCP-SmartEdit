# SmartEdit MCP

**Line-based text editor for LLM agents.** A zero-dependency [Model Context Protocol](https://modelcontextprotocol.io) stdio server that lets coding agents edit files by **line numbers** — no byte-exact `old_string` to drift out of sync, no whole files loaded into context. Works with any MCP client: **ZCode, Claude Desktop, Cursor, Windsurf, Cline** and anything else that speaks stdio MCP.

[![Version](https://img.shields.io/badge/version-v0.9.3-blue)](https://github.com/ERambler/MCP-SmartEdit/releases)
[![CI](https://github.com/ERambler/MCP-SmartEdit/actions/workflows/ci.yml/badge.svg)](https://github.com/ERambler/MCP-SmartEdit/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-10-purple)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)
[![MCP](https://img.shields.io/badge/MCP-stdio%20server-orange)](https://modelcontextprotocol.io)
[![Selftest](https://img.shields.io/badge/selftest-237%20checks%20ALL%20PASS-brightgreen)](#verification)

```text
the #1 failure mode of built-in Edit tools:
agent recomputes the file -> old_string no longer matches -> edit rejected -> tokens burned

SmartEdit: find -> show -> replace_lines(41..52, "...", expected_rev=17) -> done.
Nothing to mismatch. Ever. And if you break a bracket doing it -
the response itself tells you the line where the unclosed literal was OPENED.
```

---

## Why it exists

| Built-in Edit / str_replace / apply_patch | SmartEdit |
|---|---|
| needs byte-exact `old_string` that **drifts** after context summarization | addresses lines by **number**, one integer `rev` guard |
| whole file in context to edit it | `show`/`find` read only what is asked; mmap up to **~2 GB UTF-8** |
| last writer wins — silent clobbering | named-offender conflicts, provably-safe **auto-recovery**, all-or-nothing batches |
| errors are prose | errors are **JSON** with machine `kind`, `current_rev`, `retry_hint` — even the current text of the disputed range |
| no undo, no audit | **undo** (compensating mutations), shared cross-project **journal** every agent can read |

## Killer features

### 1. Diagnostics in every edit response — anchored, machine-readable
Each `replace_lines` / `edit_batch` returns `diagnostics: [{line, code, message}]` built on a **universal literal-aware lexer** (C#, Python, JS/TS, Java, C/C++). It understands raw strings `"""`/`$$"""`, triple-quoted Python, JS template literals with nested `${}`, Java text blocks, C++ `R"delim(...)delim"` — so brackets **stop lying about string contents**. And when a literal is left unterminated, the anchor points at the **line where it was opened**, not where the pain is felt. Codes: `bracket_mismatch`, `never_closed`, `stray_closer`, `unterminated_literal`, `unterminated_comment`, `unterminated_hole`.

### 2. Stale-rev auto-recovery — concurrency without the ceremony
Someone else edited the file while your `rev` aged? `retry_on_stale` (ON by default) re-applies your edit **once, only when provably safe**: the `expect_old` anchor still matches, or the range was untouched (line shifts are tracked — the apply lands at the *shifted* coordinates). The response tells you `recovered: true` and exactly what others did meanwhile. Never blind: a semantically changed range returns a structured `stale_conflict` carrying the current text of the range plus a `retry_hint`.

### 3. All-or-nothing editing
- **`edit_batch`** — up to 200 edits in one atomic call: every range and anchor validated *before* anything applies; applied bottom-up, no shift math; one rev bump.
- **`edit_transaction` (v0.9)** — the same guarantee across files: every step validated on every file before anything applies; one bad anchor aborts the whole transaction with `transaction_aborted` naming the file. RAM-atomic by design, `save` explicit.
- **`replace_content` (v0.9.1)** — edit by exact text, no line numbers: the snippet must occur exactly once (`not_found` / `ambiguous_match` with the matching lines otherwise). Field-driven: line-math errors were the #1 failure in real agent sessions.
- **`expect_old` anchors** — exact-content guard that catches the agent's own wrong-line math (rev only catches *external* drift).
- **`undo`** — compensating mutations: new rev, peer-safe, `undo` after `undo` redoes.
- **`undo`** — compensating mutations: new rev, peer-safe, `undo` after `undo` redoes.

### 4. Syntax gate on save (v0.10)
A tree-sitter engine is built into the core (no plugins, no Node, no Roslyn — grammars load lazily per language, single-digit ms) and checks `.cs .js .ts .tsx .py .sh` on every `save`: **the save is blocked with `syntax_gate` if the edit introduced NEW syntax errors**. Pre-existing errors never block (baseline absorbs them); a shifted or moved error is not "new" (fingerprints, not coordinates). `missing` diagnostics name the absent token — `;`, `)`, `fi` — with 1-based row/col. `force=true` publishes anyway and the response lists exactly what slipped through (`gate: pass|forced|off|degraded`, `gateErrors[]`). A wedged parser can't hang the server: hard per-parse budget; 3 strikes → the language's gate auto-disables until restart (`off_disabled`). Known limits (honest): Python indentation slips through; HTML is not gated (lenient by nature).

### 5. Structure vision, free of round-trips
`open` on a code file (≤5k lines) returns `code_outline` — top-50 symbols with lines — no extra `symbols` call needed. `symbols`/`goto`/`tree` go deeper when asked.

### 6. Multi-agent collaboration
One shared document per file path: every editor sees one piece table and one rev. `open(path, note)` lists peers; a conflicting edit names **who** changed **what** and **when**; `save` refuses with `peers_dirty` while others have unsaved work. Idle attachments are garbage-collected after 30 minutes.

### 7. Zero-ritual operability
- **Path-first (v0.9)**: every session tool takes `path` directly — `show(path, 1..N) → replace_lines(path, ...) → save(path)`. `open` is optional, `expected_rev` is optional (omit = the last rev you observed). Zero preamble.
- **`log`** — the shared journal of *all* sessions and processes (every call, every error kind, per-pid, rotated at 5 MB) the agent can grep to retrace its own steps. Test traffic is isolated to a temp dir — the journal reflects real usage only.
- **Update check** — on startup the server asks GitHub (background, never blocks stdio); a native dialog shows the release notes; **OK installs** the update in place; **Cancel** never nags about that version again.
- **`calc`** — exact arithmetic (hex/bin/`^`/`!`/functions); LLMs should not do mental math.

### Also on board
- **Crash-safe save**: `save` writes to a temp file and atomically renames (`.smartedit-bak` kept) — power loss mid-save never corrupts the original.
- **Cross-process guard (v0.9.2)**: two SmartEdit instances never fight over a file — the second gets an explicit `cross_process_lock` at first touch (pids are in the shared journal), instead of an opaque EBUSY at save time.
- Encoding safety (cp1251/UTF-16 open + round-trip, `convert`, save keeps the original encoding by default), `replace_all` with refuse-entirely semantics, `file_info`, `sessions` diagnostics, insert into empty files via `[1..0]`.

**Ships with a battle-tested agent skill** (the usage protocol that makes agents confidently prefer the right tool — hard rules, when-SmartEdit vs when-Edit, the find→show→replace loop): [skills/zcode-smartedit/SKILL.md](skills/zcode-smartedit/SKILL.md), with a guide to authoring your own: [skills/README.md](skills/README.md).

## Performance (measured, see [Verification](#verification))

| Operation | Result |
|---|---|
| Cold start to full `tools/list` (R2R single-file) | **~220 ms** |
| `open` 300 MB / 4.9 M lines (parallel line index) | **1.2 s** |
| `find` regex over 300 MB | **0.6 s** |
| `save` 300 MB (streamed, xxHash64 on the fly) | **1.5 s** |
| Typical tool call | **< 1 ms** server-side (ms-precision in the journal) |

Memory stays flat on giants: memory-mapped base + piece-table edits; `save` streams; diagnostics stream.

## Install

**Requirement: [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0)** — the single framework-dependent artifact (~1.4 MB exe). Windows x64 today; the code is pure BCL — `dotnet publish -r linux-x64` should work, feedback welcome.

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
- The exe is **locked while a session uses it**: to upgrade, close chats / `Stop-Process -Name SmartEdit.Mcp`, overwrite, reopen — or just click OK on the update dialog, it handles this for you.
- **Locks (v0.8.2+)**: a CLEAN session never holds its file between tool calls — git/formatters/scripts write freely. Only a DIRTY session (unsaved edits) holds the file until save/close — that's protection for the edit base, call `save` or `close(discard)` to release. If a file feels locked, `sessions` shows who.
- Optional companion skill (usage protocol, smart vs built-in edit): copy [`skills/zcode-smartedit/SKILL.md`](skills/zcode-smartedit/SKILL.md) to `~/.zcode/skills/smartedit/SKILL.md`. **Writing your own skill for another agent/client?** Read [`skills/README.md`](skills/README.md) first — a field-tested guide on authoring skills that make agents confidently prefer the right tool.

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
| `SMARTEDIT_TELEMETRY=off` | disable telemetry (`tool … → ok` lines); errors keep being logged — that's diagnostics, not telemetry |
| `SMARTEDIT_NO_UPDATE_CHECK=1` | disable the startup update check (same as the `--no-update` command-line arg) |

## The 60-second tour

```jsonc
// 1. open — session, rev, code outline, peers (multi-agent aware)
open(path: "C:\\src\\BigFile.cs", note: "refactoring utils")
// -> { sessionId: "sab12...", rev: 0, totalLines: 84213, codeOutline: [...], peers: [...] }

// 2. navigate — regex with context snippets + symbol breadcrumbs
find(sessionId, regex: "EditSession\\(", context: 2)

// 3. edit — by line numbers, anchored, atomic
replace_lines(sessionId, start: 41, end: 52, new_text: "...", expected_rev: 0,
              expect_old: "...exact current text of lines 41..52...")
// -> { rev: 1, diff: "...", diagnostics: [] }   // diagnostics [] = brackets clean
//    peer raced you? recovered: true + intervening: [...] — or a structured stale_conflict

// 4. many edits at once — validated before applied, no shift math, one rev bump
edit_batch(sessionId, edits: [
  { start: 10, end: 12, new_text: "...", expect_old: "..." },
  { start: 900, end: 899, new_text: "inserted" }   // end=start-1 -> insertion
], expected_rev: 1)

// 5. oops — undo is a compensating edit (undo twice = redo)
undo(sessionId, n: 1, expected_rev: 2)

// 6. persist — keeps the original encoding; refuses while peers have unsaved edits
save(sessionId)   // peers_dirty if someone else is mid-flight; force:true publishes all
```

Every mutation response carries: new `rev`, unified `diff`, structured `diagnostics` with anchors, and (in shared files) the `events` you missed. Every error is JSON with a machine-readable `kind`.
## Verification

- `SmartEdit.Mcp.exe selftest` — 237 checks: five-language literal fixtures (raw strings, f-strings, template literals, C++ raw), diagnostics anchors, stale-recovery scenarios (anchor/untouched/shifted/hijacked), save-torture (mutation stream with byte-exact disk verification after every save), encodings, collaboration, undo, 300 MB big-file flow, full protocol round-trip, corpus fuzz (every repo .cs file must pass the bracket audit). Exit code 0/1 — used as the CI gate on every push and tag.
- Cold-start and per-call timings measured on the release binary; per-call milliseconds are written to the shared journal so anyone can audit performance.

## Changelog

Full history in [CHANGELOG.md](CHANGELOG.md). Recent: **v0.9.3** unsaved nag in every response + `replace_content` scope bounds + `server_version` in errors; **v0.9.2** cross-process guard + corpus fuzz; **v0.9.1** `replace_content` (edit by exact text) + honest force-hint; **v0.9** path-first API (the `open` ritual is dead) + `edit_transaction` (#3) + lexer fix; **v0.8.2** unlock-when-clean; **v0.8.1** self-documenting API (`help`); **v0.8** honest brackets + anchored diagnostics; **v0.7** stale-rev auto-recovery, telemetry hygiene, session GC.

## License

[MIT](LICENSE) © ERambler
