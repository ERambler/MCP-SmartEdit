# Changelog

## v0.8.1 (2026-11-12)

- **`help` tool — self-documenting API** (consilium: "the cheapest token win — one call instead of 5 rounds of reverse-engineering"): one compact page with the tool map, `[start..end]` semantics, the rev protocol, and every error kind → exact next action. Plus a one-time invitation: `initialize` instructions point to it, and the FIRST tools/call of a process gets a second content block with the tip (never again in that session)
- **(#1) Structured retry + unified error envelope**: every error payload now echoes `session_id` and carries machine-actionable `retry: {strategy: reissue_with_rev|reanchor|reread_range, start?, end?}` alongside the prose hint
- **(#2) `code_outline` honesty**: open responses add `code_outline_total` + `code_outline_truncated` — the agent always knows whether it saw the whole file or just the top-50
- **(#3) `find` span**: each hit with a snippet carries `span: {start, end}` — edit that range directly, no follow-up `show` round-trip
- Selftests: 4 help checks + 3 polish checks; ~208 total, ALL PASS

## v0.8.0 (2026-11-11)

- **Universal literal lexer** (consilium-designed): ONE core + per-language profiles — C# raw `"""`/`$$"""`, Python triple/f-strings, JS template literals with nested `${}`, Java text blocks, C++ `R"delim(...)delim"`. Brackets stop lying about string contents; interpolation-hole code keeps its real braces (context stack for nested forms)
- **Structured `diagnostics` in every edit response**: `[{line, code, message}]` with machine codes (`bracket_mismatch`, `never_closed`, `stray_closer`, `unterminated_literal/comment/hole`); unterminated literals anchor at the OPENING line
- **`retry_on_stale` defaults to true** — provably-safe stale-rev recovery without asking

## v0.7.3 (2026-11-11)

- **Telemetry hygiene** (#10): `selftest` runs redirect their data dir (journal, declined-marker, downloads) to a temp folder — the shared production `mcp.log` now reflects real agent usage only (today's raw stats were poisoned by ~40 test runs: 6.1 % apparent errors vs 3.7 % real)
- **Empty-file insert** (#11): `replace_lines` with `start=1, end=0` on a 0-line file now inserts the first lines (same `end=start-1` convention as `edit_batch`) — previously the natural call was rejected with `invalid_range`
- **Session GC** (#12): `open` opportunistically detaches stale (>30 min silent) attachments with no pending edits (journaled per eviction); the shared document is kept — week-long servers stop accumulating abandoned attachments (16 leaked today)
- **Flavor-preserving updates** (#13): the updater detects its own install flavor — `fd` (framework-dependent) / `sc` (self-contained) / `aot` — and only offers a matching asset: an SC install can no longer miss updates or be downgraded to a runtime-dependent binary; when the AOT asset ships, AOT updates AOT→AOT
 
## v0.7.2 (2026-11-11)

- **save-torture guard**: two real-world incidents today showed `save` publishing the PREVIOUS rev's content (duplicate lines on disk while the in-memory document was correct). A deterministic (seeded) torture test now runs a shuffled stream of replace/batch/insert/delete/undo mutations with a save + byte-exact disk↔document comparison after EVERY step — 23 mutations / 29 saves, all byte-identical. The current engine is clean; the artifacts traced back to a legacy server binary still running from before the v0.7 rework. The guard locks the property forever

## v0.7.1 (2026-11-11)

- **Bracket check anchors**: `check` reports now anchor to the OPENING bracket's line (stack-of-pairs with positions, the canonical linter approach): a mismatched closer says `line 5: '}' here, but '(' from line 4 is still open — missing ')'?'`, an unclosed opener says `line 1: '{' opened here is never closed (missing '}')` — at its own opening line, outermost first, with the missing closer named. A mismatch now resyncs the stack (one error no longer cascades through the rest of the file); a stray closer says `closes nothing`. Selftested (mismatch anchor, own-line report, source order, stray closer)
- **Fixed**: `insert_after` diff context showed the line ABOVE the anchor and hid the anchor line itself (`Context(line-2,2)` off-by-one — the diff displayed an irrelevant older line); now the line you insert after is always in the context. Selftested

## v0.7.0 (2026-11-11)

- **`retry_on_stale`** (#1, P0 per telemetry — stale_rev was 8.6 % of replaces / 64 % of all errors): `replace_lines` and `edit_batch` accept `retry_on_stale: true` — on `stale_rev` they auto-retry **once**, and only when it is provably safe: the `expect_old` anchor still matches at the fresh rev, or the range was untouched by the intervening edits (line shifts are tracked through structured spans now recorded in the mutation history; the apply lands at the *shifted* coordinates). The response carries `recovered: true` + `intervening: ["r5 s876b8ca7: insert_after 9 (+1 lines)", …]` — what others did meanwhile. **Never a blind retry**: a semantically changed range returns a structured `stale_conflict` with the current text of the range in `current_text` ("both versions" without a separate `show`) and a `retry_hint`. Error payloads gained `retry_hint`/`current_text` fields. Undo/replace_all/unknown spans are conservatively unsafe. Without the flag the behavior is unchanged. Selftested: anchor intact, untouched range, hijacked range (conflict), top-insert shift math, whole-batch recovery, flag-off pass-through
- AOT experiment fix: `-p:SelfContained=true` added (NETSDK1102 — AOT requires self-contained even though the csproj Release profile pins framework-dependent)

## v0.6.3 (2026-11-11)

- **Self-contained asset** (#7): every release now ships `SmartEdit-sc-win-x64-vX.Y.Z.zip` (~85 MB single exe, no .NET runtime needed) alongside the framework-dependent one. `install.ps1` picks it automatically when no .NET 10 runtime is present — no more hard failure on runtime-less machines. The SC binary runs the full selftest gate in CI before release
- **AOT experiment** (#7): CI gains a non-blocking `aot-experiment` job (tag builds) that compiles Native AOT on the runner's MSVC linker and runs selftest — result feeds the decision to ship an AOT asset

## v0.6.2 (2026-11-11)

- **Code outline in the `open` response** (#2): opening a code file (C#, Python, JS/TS, Java, C/C++, markup) of ≤5000 lines now returns `code_outline` — the top 50 symbols (kind/name/line) — and warms the symbols cache (recomputed only on rev change). Kills the separate exploratory `symbols` call that telemetry showed agents skipping (3 uses / 86 opens). Non-code and oversized files omit the field. Selftested (symbols present, non-code null, >5k gate)
- **CI** (#6): GitHub Actions workflow — every push/PR runs `dotnet build -c Release` + the full `selftest` gate; a `v*` tag additionally publishes the win-x64 zip and attaches it to a **draft release** (a human publishes after review). Release builds are no longer hand-rolled

## v0.6.1 (2026-11-11)

- **Auto-install**: OK in the update dialog now *installs* the update — no user action beyond the click. The zip is downloaded to a temp dir, extracted, and the running binary replaces itself: Windows uses the rename dance (a running exe can't be overwritten but can be *renamed* → current file becomes `.exe.old`, the new exe takes its place, `.old` is cleaned up on the next start; rollback if the swap fails), Linux atomically overwrites its own binary. The new version takes effect on the next server start (a live process can't hot-swap). On failure the zip path is logged for a manual update. Dialog text now says "Install". Selftested (`ReplaceBinary` dance on temp files)

## v0.6.0 (2026-11-11)

- **Update check**: on startup the server fetches the GitHub latest release (background thread, 5 s timeout — stdio is never blocked). A newer version triggers a native system dialog with the release notes and OK/Cancel: **OK** downloads the release zip to `<DataRoot>\downloads` and opens the folder; **Cancel** is remembered per-version — a declined version is never offered again, the next release will ask. Windows: `MessageBoxW`; Linux: `zenity`/`kdialog`/`xmessage` under DISPLAY/WAYLAND_DISPLAY, headless → journal notice only. Kill switch: `--no-update` arg or `SMARTEDIT_NO_UPDATE_CHECK=1`. Selftested (version compare, decline memory, dialog text, kill switch); the full popup→download flow was verified live

## v0.5.3 (2026-11-11)

- **Security**: `SMARTEDIT_LOG=debug` no longer writes raw JSON-RPC lines into the shared journal — tool-call arguments (file contents, edit texts, notes — potentially logins/tokens) were landing in the machine-wide `mcp.log`, readable via the `log` tool from any chat/project. Debug lines now carry method/id/length only (`← id=7 method=tools/call len=15234`); payloads never enter the journal. Selftested (`debug-brief …`)

## v0.5.2 (2026-11-11)

- **Security**: log injection (log forging) fixed — `Log.Write` now escapes `\r`/`\n`/`\t`, ANSI ESC and other control chars (plus U+2028/2029) in every message; one journal entry is exactly one line. Client-controlled data reaches the journal via tool names, `path` file-name parts (`Path.GetFileName` does not strip `\n`), `session_id`s, and exception texts — a raw newline there could forge entries that the `log` tool later shows to agents in *other* chats/projects (the journal is machine-wide shared). Covered by a selftest (`log: sanitize … log forging impossible`); unhandled-exception logging switched from `{ex}` (multi-line stack) to `ex.Message`
- **`SMARTEDIT_TELEMETRY=off`** (also `0`/`false`): opt-out of telemetry — the `tool … → ok` lines with args summary. Error lines stay (diagnostics, not telemetry)

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
