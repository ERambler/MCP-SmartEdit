# SmartEdit MCP

**Line-based text editor for LLM agents.** A zero-dependency [Model Context Protocol](https://modelcontextprotocol.io) stdio server that lets coding agents edit files by **line numbers** — no byte-exact `old_string` to drift out of sync, no whole files loaded into context. Works with any MCP client: **ZCode, Claude Desktop, Cursor, Windsurf, Cline** and anything else that speaks stdio MCP.

[![Version](https://img.shields.io/badge/version-v0.8.0-blue)](https://github.com/ERambler/MCP-SmartEdit/releases)
[![CI](https://github.com/ERambler/MCP-SmartEdit/actions/workflows/ci.yml/badge.svg)](https://github.com/ERambler/MCP-SmartEdit/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-10-purple)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)
[![MCP](https://img.shields.io/badge/MCP-stdio%20server-orange)](https://modelcontextprotocol.io)
[![Selftest](https://img.shields.io/badge/selftest-%7E190%20checks%20ALL%20PASS-brightgreen)](#verification)

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
- **`expect_old` anchors** — exact-content guard that catches the agent's own wrong-line math (rev only catches *external* drift).
- **`undo`** — compensating mutations: new rev, peer-safe, `undo` after `undo` redoes.

### 4. Structure vision, free of round-trips
`open` on a code file (≤5k lines) returns `code_outline` — top-50 symbols with lines — no extra `symbols` call needed. `symbols`/`goto`/`tree` go deeper when asked.

### 5. Multi-agent collaboration
One shared document per file path: every editor sees one piece table and one rev. `open(path, note)` lists peers; a conflicting edit names **who** changed **what** and **when**; `save` refuses with `peers_dirty` while others have unsaved work. Idle attachments are garbage-collected after 30 minutes.

### 6. Zero-ritual operability
- **`log`** — the shared journal of *all* sessions and processes (every call, every error kind, per-pid, rotated at 5 MB) the agent can grep to retrace its own steps. Test traffic is isolated to a temp dir — the journal reflects real usage only.
- **Update check** — on startup the server asks GitHub (background, never blocks stdio); a native dialog with release notes; **OK silently installs** the update in place; **Cancel** never nags about that version again. Flavor-preserving: framework-dependent updates to fd, self-contained to sc.
- **`calc`** — exact arithmetic (hex/bin/`^`/`!`/functions); LLMs should not do mental math.

### Also on board
Encoding safety (cp1251/UTF-16 open + round-trip, `convert`, save keeps the original encoding by default), `replace_all` with refuse-entirely semantics, `file_info`, `sessions` diagnostics, insert into empty files via `[1..0]`.

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

**Requirement: [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0)** (framework-dependent single-file exe, ~1.4 MB) — or pick the **self-contained** `SmartEdit-sc-win-x64-*.zip` asset (zero runtime dependencies; `install.ps1` chooses it automatically when no runtime is installed). Windows x64 today; the code is pure BCL — `dotnet publish -r linux-x64` should work, feedback welcome.

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
- An open session holds its file `FileShare.Read` — external writers get EBUSY until `close`. Intentional; call `sessions` if a file feels locked.
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

- `SmartEdit.Mcp.exe selftest` — ~190 checks: five-language literal fixtures (raw strings, f-strings, template literals, C++ raw), diagnostics anchors, stale-recovery scenarios (anchor/untouched/shifted/hijacked), save-torture (mutation stream with byte-exact disk verification after every save), encodings, collaboration, undo, 300 MB big-file flow, full protocol round-trip. Exit code 0/1 — used as the CI gate on every push and tag.
- Cold-start and per-call timings measured on the release binary; per-call milliseconds are written to the shared journal so anyone can audit performance.

## Changelog

Full history in [CHANGELOG.md](CHANGELOG.md). Recent: **v0.8** honest brackets + anchored diagnostics + recovery by default; **v0.7** stale-rev auto-recovery, telemetry hygiene, session GC; **v0.6** update check with silent self-install, code outline in `open`, CI gate, self-contained asset.

## License

[MIT](LICENSE) © ERambler
