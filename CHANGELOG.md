# Changelog

## v0.5.1 (2026-11-11)

- **Fixed**: UTF-8 detection falsely rejected valid files when a multi-byte (e.g. Cyrillic) character started exactly at the 4096-byte sample boundary — `open` and post-save reload failed with a wrong "not valid UTF-8". Detection now uses an exception-free prefix scanner (boundary truncation allowed, broken continuation inside = honest NotUtf8). Regression-tested.

## v0.5.0 (2026-11-11)

- **`undo(n)`** — undo as a *compensating mutation*: new rev, inverse ops recorded in history (so `undo` after `undo` naturally redoes); locations re-found by content, peers' unrelated edits don't block; `undo_conflict` when the text is gone or ambiguous (nothing applied, all problems listed); edits over 200 lines per side are not invertible (`nothing_to_undo`)
- **`goto(name)`** — find symbols by name substring with exact line numbers
- **`log(tail/grep/level/errors_only)`** — the shared journal of ALL server processes (every tool call, error+kind, per-pid) exposed to the agent itself as an emergency handle; interprocess mutex, rotation at 5 MB (`mcp.log.1/.2`)
- Telemetry v2: millisecond timings, args summary (session, file, counters, feature flags `expect_old`/`dry_run`/`note`/`force`) in the journal
- Structure: collaboration model extracted to `Sessions.cs`; undo/goto in `NavTools.cs`
- Reviewed with external LLM consultants (DeepSeek R1 + ChatGPT, parallel): undo ranked P1 by both; macros rejected ("the model IS the macro executor"), background timers rejected for a no-push stdio server

## v0.4.0 (2026-11-11)

- **Multi-agent collaboration**: one shared document per file path, N attachments (`open` reuses the document, `reused:true`); single piece table and rev space for all editors
- **Named offenders**: `stale_rev` errors say who changed what and when — `(r17 by s3: replace 41..52 @ 12:33)`
- **Presence**: `open(path, note)` lists peers (id, note, pending edits, stale marker) + recent edits; `show`/`find` append missed `events` only when the caller fell behind (zero token tax otherwise); `sync(since_rev)` for explicit resync
- **`peers_dirty` save guard**: save refuses while other sessions have unsaved edits; `force=true` publishes everything (one document = one truth)
- `close` = detach (document lives while peers are attached); liveness via last-seen + 30-min stale marker
- Architecture reviewed with DeepSeek R1: 6/10 as proposed, 8/10 after revisions

## v0.3.1 (2026-11-11)

- **`sessions`** tool — diagnostics: every open document and attachment (the mmap lock makes forgotten sessions visible)
- **`close(discard=true)` keeps the shadow snapshot** on disk (path returned in the response) — unsaved work recoverable by copying over the file; previously discard deleted it irrecoverably

## v0.3.0 (2026-11-11)

- **`edit_batch`** — atomic multi-edit in one call: all ranges and `expect_old` anchors validated BEFORE anything applies (one bad edit rejects the batch, all mismatches reported at once); applied bottom-up so line numbers need no shift math; `end = start-1` inserts; single rev bump
- **`replace_all`** — literal (non-regex) whole-file replace; refuses entirely above `max_lines` (partial rename breaks code)
- **`expect_old`** anchors on `replace_lines` — content verification against the agent's own line math (`anchor_mismatch` names the line)
- **Structured errors**: tool errors are JSON `{"kind","message","current_rev","total_lines"}` — kinds: `stale_rev | anchor_mismatch | invalid_range | invalid_args | unknown_session | disk_conflict | dirty_close | too_large | bad_regex | batch_overlap`
- `save` no longer bumps rev (save does not renumber lines)
- `check` streams (full-file audit up to 200k lines, RAM flat); `tree(recent_hours=N)` lists recently modified files
- CodeStrip: interpolation-aware `$"…"` stripping (holes are code, `{{ }}` escaped) — removed the whole-line skip that broke multi-line calls
- JSON-RPC: malformed input gets a spec-compliant `-32700` response with `id:null`

## v0.2.0 (2026-11-11)

- Structure vision: **`symbols`** (C#, Python, JS/TS, Java, C/C++, XML/HTML — line numbers match show/replace), `find(context=N)` snippets + symbol breadcrumbs, **`check`** bracket audit, **`tree`** project map (C# namespace groups), `file_info`, `convert` (re-encode in place), `calc`
- `save` default encoding changed to **keep** (never mangle encodings implicitly)
- Shadow snapshots after each mutation (≤32 MB files); atomic save (temp+rename) with `.smartedit-bak` backup and mtime/size conflict detection

## v0.1.0 (2026-11-10)

- Core: `open/show/find/replace_lines/insert_after/save/close`; rev-based optimistic concurrency; mmap + piece table + parallel line index (1.2 s open / 0.6 s find / 1.5 s save on 300 MB); UTF-8 of any size, cp1251/UTF-16 via explicit encoding; selftest suite
