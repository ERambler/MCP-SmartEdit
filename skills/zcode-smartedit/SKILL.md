---
name: smartedit
description: Use the SmartEdit MCP tools (mcp__smartedit__*) INSTEAD of built-in Read/Edit for ALL code work — project orientation (tree FIRST), creating new files (create, not Write), editing any file over ~150 lines, multi-edit series, mass renames, finding occurrences with context, encoding work (cp1251/UTF-16). Fires on file editing, code navigation, understanding a codebase, large-file refactoring. DEFAULT editor for code files — built-in Edit only for trivial one-liners.
---

# SmartEdit — the default line-based editor

Built-in Edit needs a byte-exact `old_string` — on big files it breaks (drift after context
summarization). SmartEdit edits by **line numbers** — nothing to mismatch. The file lives in the
server's RAM, not in your context. Every edit answers back: diff, **anchored diagnostics**,
and auto-recovery if a peer raced you.

## Lifecycle (mandatory — this is THE workflow)

0. **New project/chat → `tree(path)` FIRST.** One call = the whole picture: directories, types with
   inheritance edges resolved to `file:line`, top members with doc lines, `path:line` anchors that plug
   straight into `show`/`goto`. Skeleton is budget-capped (`budget_tokens`, default 2048, max 8192 for
   big repos; the tail honestly says `… +N types`). Do NOT orient by reading files whole —
   that is the 30–100K-token habit this call replaces.
1. **Drill in**: `goto(path, name)` for a symbol, `find(path, regex, context=2)` for usages,
   `show(path, start..end)` for the exact lines. Never read a file whole when a range or the map answers it.
2. **Create a file → `create(path, content)`, NOT Write.** Phantom RAM session: disk untouched until
   `save`; the seed is syntax-checked right in the response (advisory), first save gates hard.
   (Write touches disk immediately and bypasses the gate.)
3. **Edit**: exact text known → `replace_content` (no line math, uniqueness anchors it);
   series in one file → `edit_batch`; mass rename → `replace_all`; one logical change across 2+ files →
   `edit_transaction`.
4. **`save(path)` is ALWAYS the closer.** Edits live in server RAM; the syntax gate runs here.
   Any response nagging `unsaved: true` = save before switching to bash/git/tests.
5. **No rituals**: no `open` before editing (path-first everywhere), no `close` after (clean sessions
   self-evict after 30 min idle / 16-doc LRU; dirty ones are NEVER evicted).
   `open` only for extras (outline/encoding/note); `close` only to force-drop unsaved edits.

## When SmartEdit (default for code)

- **Orientation in a known OR unknown project**: `tree(path)` — deep skeleton by default (v0.13.2)
- Any file **> ~150 lines** — the session amortizes across the edit series
- **New files**: `create(path, content)` — RAM until save, gate-checked
- **Edit series** in one file: 2+ edits → `edit_batch` (atomic, no shift math)
- **Mass rename**: `replace_all` (literal, not regex, one rev)
- **All occurrences**: `find(regex, context=2)` — snippets + breadcrumb `Ns.Class.Method` + edit-ready spans
- **Encodings**: cp1251/UTF-16 (`open` with `encoding`), `file_info`, `convert` (by name); `save` keeps encoding
- File **dropped from context** after summarization — don't re-read: `find(path, …)` / `goto(path, …)` → edit
- Giants up to ~2 GB UTF-8 (mmap): `show`/`find` read only what is asked
- **Undo a bad edit**: `undo(n, expected_rev)` (undo twice = redo)
- **What did I / others do**: `log(grep=..., errors_only=true)` — shared journal (by name)

## When built-in Edit

- File ≤ 150 lines, one-off single-line tweak, file already in context — and nothing else:
  new files go through `create`, code edits through SmartEdit

## Workflow

1. Orient: `tree(path)` — skeleton + inheritance + anchors (Lifecycle step 0)
2. Read what you'll touch: `show(path, start..end)` / `find(path, regex, context=2)` / `goto(path, name)` — path directly, no open (v0.9)
3. One edit: `replace_lines(path, start, end, new_text, expect_old?)`;
   have the EXACT text instead? `replace_content(path, find, replace)` — no line numbers, must be unique;
   boilerplate not unique? bound the scope with `start_line/end_line` (v0.9.3);
   2+ edits in one file: `edit_batch(path, edits[])` — all ranges/anchors validated BEFORE applying, no shift math;
   rename: `replace_all(path, find, replace)`;
   2+ FILES: `edit_transaction(steps[])` — all-or-nothing across files
4. `save(path)` — gate runs HERE; keeps encoding by default; rev does NOT change on save

## Hard rules (non-negotiable)

- **SAVE is mandatory.** Edits live in server RAM — unsaved edits are LOST after 30 min idle or a server
  restart. Finish every edit series with `save(path)`. Every response nags `unsaved: true` until you do.
- **Edit only what you just SAW.** After any intervening edits (yours or a peer's): `find` → `show` → replace.
  NEVER edit by remembered line numbers — line math after shifts is the #1 error source in the field.
- **All ranges in `edit_batch`/`edit_transaction` refer to the ORIGINAL state** (no shift math); when you have
  the exact text, prefer `replace_content` — the uniqueness check anchors it for you.
- **`force=true` DISCARDS the on-disk version** (`disk_conflict`/`peers_dirty`). Default: `show` the range and
  re-apply on top of what's there.
- New chat/session → `show`/`find` FIRST (the rev chain starts fresh).
- **`save` on code files is syntax-gated (v0.10)**: `.cs .js .ts .tsx .py .sh` — blocked with `syntax_gate`
  if YOUR edit introduced NEW syntax errors (old ones never block; moved ones aren't "new").
  `missing` errors name the absent token (`;`, `)`, `fi`) at 1-based row/col. Fix them; `force=true`
  only when the block is wrong (cascade/`#if`) — the response lists `gateErrors` either way.

## Semantics (critical)

- Lines are 1-based inclusive; `new_text=''` deletes the range; a lone trailing `\n` is ignored
- `expect_old` — anchor against your own line math (mismatch → `anchor_mismatch` names the line)
- **Every response on a dirty document carries `unsaved: true`** (v0.9.3) — external tools (git/tests/bash)
  see the OLD disk code until `save`; when you see it, save before switching to bash
- **`diagnostics: [{line, code, message}]`** in every mutation response (v0.8): honest bracket balance
  across C#/Python/JS/Java/C++ literals (raw `"""`, f-strings, template `${}`, `R"()"`);
  `unterminated_literal` anchors at the OPENING line — fix before saving, no extra call needed
- **Stale rev auto-recovers by default** (v0.8): provably-safe retry lands at shifted coordinates and
  returns `recovered: true` + `intervening: [...]`; a changed range → `stale_conflict` with the current
  text of the range + `retry_hint` (show it, then re-apply). Pass `retry_on_stale:false` for the plain error
- Errors are JSON: `{"kind":"stale_rev|stale_conflict|anchor_mismatch|invalid_range|batch_overlap|too_large|…","current_rev":N}` — branch on `kind`
- Empty file: `replace_lines(1, 0, text)` inserts the first lines (same `end=start-1` convention as batch)
- Multi-agent: stale errors name the offender; `save` → `peers_dirty` while others are mid-flight; idle attachments auto-detach after 30 min
- **Locks (v0.8.2)**: clean sessions NEVER hold files between tool calls — git/scripts/formatters can write freely; a DIRTY session holds its file until save/close (protection). External change while released → next call starts fresh (rev=0); deletion → `disk_conflict`
- **Path-first (v0.9)**: every session tool takes `path` directly — `open` is never a prerequisite.
  Single file: `show(path, 1..N) → find(path, …) → replace_lines(path, …) → save(path) → close(path)`.
  `open(path)` only when you want its extras: code_outline preview, `note`, explicit `encoding`.
  Both `session_id` AND `path` in one call → `ambiguous_target`
- Multi-file refactor (2+ files, one logical change): `edit_transaction(steps=[{path, edits[]}], save?)` —
  all-or-nothing (one bad anchor anywhere → `transaction_aborted`, NOTHING applied); `save` defaults
  to false (RAM-staging): inspect diffs, then `save` each path (or pass `save:true` for best-effort per-file)
- `expected_rev` is OPTIONAL everywhere: omit = the last rev you observed (the server tracks it); pass it
  explicitly only to assert no peer raced you. Path-mode needs zero rev bookkeeping
- **Lifecycle is automatic** (v0.13.2): clean sessions never hold files and self-evict (30 min idle /
  16-doc LRU, dirty never evicted); `close` (hidden, by name) is the manual valve to force-drop unsaved
  edits — a shadow backup keeps them recoverable

## If something looks wrong (abnormal — not a ritual)

The engine's guarantees are test-locked: every save publishes exactly the current rev (byte-verified
by the save-torture check in selftest), diagnostics can't false-alarm on string contents. So treat
these as REGRESSIONS, not as weather:

- `save` result differs from what the session shows on disk → `log(grep="update:")` to capture evidence, re-`open`, report
- `show` returning text that contradicts the last diff → `close` the session, `open` fresh, report with journal lines

Do not build habits around them — grep-after-save and reopen-on-suspicion are incident protocol,
not everyday steps.
