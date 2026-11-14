# SmartEdit MCP

**A safety-first text editor for LLM coding agents.** A zero-dependency [Model Context Protocol](https://modelcontextprotocol.io) stdio server: edits by **line number** (no byte-exact `old_string` to drift), **syntax-gated saves** (broken code never hits the disk), atomic multi-edit and multi-file transactions, undo, multi-agent collaboration — all with **range reads and diff responses**, so you never pay the whole file in tokens. Works with any MCP client: **ZCode, Claude Desktop, Cursor, Windsurf, Cline**, anything that speaks stdio MCP.

[![Version](https://img.shields.io/badge/version-v0.12.0-blue)](https://github.com/ERambler/MCP-SmartEdit/releases)
[![CI](https://img.shields.io/badge/CI-build%2Bsmoke-green)](https://github.com/ERambler/MCP-SmartEdit/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-10-purple)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)
[![MCP](https://img.shields.io/badge/MCP-stdio%20server-orange)](https://modelcontextprotocol.io)
[![Selftest](https://img.shields.io/badge/selftest-284%20checks%20ALL%20PASS-brightgreen)](#verification)

## The pain it kills

You gave an agent a 1500-line legacy file. It reads the whole thing (**12k tokens**), edits line 4500 — wait, that's a *different* file — it re-reads, `old_string` no longer matches, retry, retry, and then it saves code that **doesn't even parse** and walks away to run tests against the wrong disk state. Three failure classes, one root cause: the built-in editor is a text replacer with no memory, no validation and no idea of *lines*.

```text
built-in Edit            agent recomputes the file → old_string drifts → rejected → tokens burned
                         saves broken syntax → tests run against broken code → debug spiral
SmartEdit                find → show → replace_lines(41..52, "...") → save → syntax_gate
                         nothing to mismatch; broken code is REFUSED at the disk boundary
```

## Where it beats the built-in Edit

| Built-in Edit / str_replace / apply_patch | SmartEdit |
|---|---|
| byte-exact `old_string` that **drifts** after summarization | addresses lines by **number**, one integer rev guard + optional `expect_old` anchor |
| reads whole file to edit it | `show`/`find` read **ranges**; mmap up to ~2 GB UTF-8 |
| response = the new file (or silence) | response = unified **diff** + bracket diagnostics + rev |
| saves anything, including broken syntax | **syntax gate**: saves that introduce *new* parse errors are refused (`gateErrors[]` name the missing token) |
| last writer wins — silent clobber | named-offender conflicts, provably-safe auto-recovery, `peers_dirty`, cross-process mutex |
| no undo, no audit | **undo** (undo×2 = redo), shared journal every agent can grep |
| one edit at a time | atomic `edit_batch` (200 edits), multi-file transactions, replayable **recipes** |

## Token economy — the honest version

What actually saves tokens on large files:

- **Range reads instead of whole-file reads**: `show(start, end, max_chars)` reads exactly the lines you edit. Editing line 4500 of a 10k-line file never pays for lines 1–4499.
- **`find` returns edit-ready spans**: hits carry a `span {start, end}` + snippet — you edit the span directly, no follow-up `show` round-trip.
- **`code_outline` rides along with `open`**: top-50 symbols with line numbers for files ≤5k lines — the "read the tree → read the file" ceremony collapses into one call.
- **Diff responses**: every mutation returns a unified diff (±3 lines context), not the file.
- **`replace_content`**: exact-text anchors, no line math → fewer failed calls, fewer re-reads.
- **RAM staging**: edits accumulate in memory, `save` is explicit and rare — intermediate states cost zero round-trips.

What is *not* a token feature (so we don't claim it): the piece-table/mmap engine is **memory and speed** economy (flat RAM on 300 MB files, <1 ms per call) — it makes the editor possible, it doesn't shrink your prompt.

How to verify instead of trusting us: tokens-per-task (SmartEdit vs your built-in Edit on the same change), tool-calls-to-done, and edit success rate are all measurable from the shared journal (`log` tool greps itself). We dogfood daily; **post your numbers, we'll post ours**.

## The safety layer

1. **Syntax gate on `save`** (tree-sitter built in, no Node/Roslyn/plugins): `.cs .js .ts .tsx .py .sh` — a save that introduces **new** syntax errors vs baseline is refused with `syntax_gate`; pre-existing errors never block; a moved error isn't "new" (fingerprints, not coordinates); `missing ';' at 2:14` tells you exactly what to fix; `force=true` is the conscious override and still reports what slipped through.
2. **Anchors against your own wrong math**: `expect_old` (exact current text of the range) catches miscounted lines *before* the edit applies; `ambiguous_match`/`not_found` make text anchors unambiguous.
3. **Atomicity**: `edit_batch` — 200 edits, all validated before any apply, bottom-up (no shift math); `edit_transaction` — the same across files; `recipe_apply` — replay a saved refactor across N files with locate-all-then-apply semantics (divergence = precise report, nothing touched).
4. **Recovery**: stale-rev auto-recovery re-applies your edit *only when provably safe* (anchor intact or range untouched, shifts tracked); otherwise a structured `stale_conflict` with the current text and a retry hint. `undo` — compensating mutations, peer-safe.
5. **Collaboration guards**: one document per path, peers listed in `open`, conflicting edits name the offender, `peers_dirty` on save, cross-process mutex (two servers never clobber each other), a DIRTY file is OS-protected from external writes until you save.
6. **File birth inside the gate** (v0.12): `create(path, content)` — a phantom RAM session, nothing on disk until `save`, advisory syntax check of the seed right in the response, first save gates hard.
7. **Crash safety**: save = temp file + atomic rename (+ `.smartedit-bak`); unsaved work is shadowed to disk; encodings (cp1251/UTF-16) round-trip — `save` keeps the original encoding by default.

## The 60-second tour

```jsonc
// path-first: no open ceremony, every tool takes path directly
show(path: "C:\\src\\BigFile.cs", start: 41, end: 52)          // range read, numbered lines
find(path, regex: "EditSession\\(", context: 2)                 // hits with spans → edit without re-reading
replace_lines(path, start: 41, end: 52, new_text: "...",
              expect_old: "...exact current text...")           // anchored, atomic
// -> { rev: 1, diff: "@@ ...", diagnostics: [], unsaved: true } // bracket-clean proof, nag until save
save(path)                                                       // syntax gate runs HERE
// -> { gate: "pass" }  ...or syntax_gate with gateErrors[] if you broke something

edit_batch(path, edits: [ {start:10,end:12,new_text:"...",expect_old:"..."},
                          {start:900,end:899,new_text:"inserted"} ])  // 200 max, all-or-nothing
undo(path, n: 1)                                                 // compensating edit
create(path: "C:\\src\\New.cs", content: "class N { }")          // phantom until save
recipe_apply(name: "priv-to-readonly", params: {...}, paths: [...]) // zero-token replay, N files
```

Every error is JSON with a machine `kind` (`stale_rev`, `anchor_mismatch`, `syntax_gate`, `disk_conflict`, `peers_dirty`…) plus `current_rev` and a retry hint — branch on kinds, never parse prose.

## Performance (measured on the release binary)

| Operation | Result |
|---|---|
| Cold start to full `tools/list` (R2R single-file) | **~220 ms** |
| `open` 300 MB / 4.9 M lines (parallel line index) | **1.2 s** |
| `find` regex over 300 MB | **0.6 s** |
| `save` 300 MB (streamed, xxHash64 on the fly) | **1.5 s** |
| Syntax gate, typical file (tree-sitter, lazy grammars) | **0.2–30 ms** |
| Typical tool call | **< 1 ms** server-side (ms-precision in the journal) |

Memory stays flat on giants: memory-mapped base + piece-table edits; `save` streams.

## Install

**v0.12.1+ ships Native AOT builds — zero runtime dependencies.** Primary artifact: `SmartEdit-AOT-win-x64-vX.Y.Z.zip` (~10 MB exe + grammar dlls, self-contained native binary). For Ubuntu: a `.deb` package. A slim framework-dependent zip (~1.4 MB exe, needs [.NET 10](https://dotnet.microsoft.com/download/dotnet/10.0)) stays as an alternative.

| Artifact | OS | Size | Needs |
|---|---|---|---|
| `SmartEdit-AOT-win-x64-*.zip` | Windows x64 | ~10 MB + dlls | nothing |
| `smartedit-mcp_*_amd64.deb` | Ubuntu x64 | ~12 MB | nothing (CI-tested on Ubuntu 24.04; **not yet field-tested** — honest) |
| `SmartEdit-win-x64-*.zip` | Windows x64 | ~1.4 MB + dlls | .NET 10 runtime |

> `exe smoke` (live-server suite) is framework-dependent-build only — under Native AOT it prints an honest refusal (reflection JSON); `selftest` runs fully everywhere, including AOT and Ubuntu.

### Option A — one-liner for agents (PowerShell)

```powershell
irm https://raw.githubusercontent.com/ERambler/MCP-SmartEdit/main/install/install.ps1 | iex
```

Downloads the latest release to `%LOCALAPPDATA%\SmartEdit\bin`, runs the selftest, prints ready-to-paste client configs.

### Option B — manual

1. Download the artifact you want from [Releases](https://github.com/ERambler/MCP-SmartEdit/releases), unzip (e.g. into `%LOCALAPPDATA%\SmartEdit\bin`; Ubuntu: `sudo dpkg -i smartedit-mcp_*_amd64.deb`, binary at `/usr/bin/smartedit-mcp`)
2. Sanity check: `SmartEdit.Mcp.exe selftest` — must print `ALL PASS` (works on the AOT build and on Ubuntu too)
3. Register with your MCP client (below)

### Client configs

```jsonc
// ZCode: ~/.zcode/cli/config.json          // Claude Desktop: %APPDATA%\Claude\claude_desktop_config.json
{ "mcp": { "servers":  { "smartedit": { "command": "C:\\Users\\YOU\\AppData\\Local\\SmartEdit\\bin\\SmartEdit.Mcp.exe" } } } }
// Cursor / Windsurf / any stdio client: same shape under mcpServers
```

- New server registration = **new chat session** (no hot reload). The exe is locked while a session uses it — close chats or `Stop-Process -Name SmartEdit.Mcp` before upgrading (or click OK on the built-in update dialog).
- Optional companion skill (usage protocol: when-SmartEdit vs when-Edit, the find→show→replace loop): copy [`skills/zcode-smartedit/SKILL.md`](skills/zcode-smartedit/SKILL.md) to `~/.zcode/skills/smartedit/SKILL.md`. Authoring your own for another client? [`skills/README.md`](skills/README.md) is a field-tested guide.

### Environment variables

| Variable | Effect |
|---|---|
| `SMARTEDIT_DATA_DIR` | data dir (default `%LOCALAPPDATA%\SmartEdit`); the shared journal `mcp.log` lives there |
| `SMARTEDIT_LOG=debug` | verbose protocol logging (method/id/size only — payloads never enter the journal) |
| `SMARTEDIT_TELEMETRY=off` | disable telemetry (`tool … → ok` lines); errors keep being logged — that's diagnostics |
| `SMARTEDIT_NO_UPDATE_CHECK=1` | disable the startup update check |

## Verification

- **284 selftest checks** (exit 0/1, CI gate on every push): five-language literal fixtures, anchored diagnostics, stale-recovery scenarios, save-torture (byte-exact disk verification after every save), the piece-table corruption regression, `create` semantics, encodings, collaboration, undo, 300 MB big-file flow, full protocol round-trip, corpus fuzz (every repo .cs file must pass the audit).
- **67 live smoke checks** (~1 s, CI gate on build *and* on the published release binary): real server over stdio, every tool happy-path + edges, plus a **drunk-LLM pack** — wrong line math, ancient revs, hallucinated session ids, garbage-typed args, 50K-char edits, double-edits. The server must help, not crash.
- Per-call milliseconds are written to the shared journal — audit performance yourself.

## Milestones

Brief — the full history lives in [CHANGELOG.md](CHANGELOG.md).

- **v0.12** — `create()`: file birth inside the syntax gate (phantom RAM session, disk untouched until save)
- **v0.11** — `recipe_save`/`recipe_apply` (replayable refactors, `{{param}}` anchors); attention tracking (`blind` warnings on unseen-range edits); **#19 root cause fixed** (piece-table slice reads — every path now sees the same document, locked by regression tests)
- **v0.10** — built-in **tree-sitter syntax gate** on save (.cs/.js/.ts/.tsx/.py/.sh); live smoke suite in CI
- **v0.9** — path-first API (the `open` ritual is dead); multi-file `edit_transaction`; cross-process mutex; `replace_content`; corpus fuzz
- **v0.8** — honest literal-aware bracket diagnostics in every edit response; `help`; clean sessions never hold files
- **v0.7** — stale-rev auto-recovery, save-torture lock, telemetry hygiene

## Roadmap

Deprecation wave in flight: 7 zero-use tools leave `tools/list` (issue #22, telemetry-driven). Next candidates: cross-platform single-file builds, a public tokens-per-edit benchmark vs built-in editors. Known limits: Python indentation slips past the gate; HTML is not gated (lenient by nature); Windows-first distribution.

## License

[MIT](LICENSE) © ERambler
