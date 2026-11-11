---
name: smartedit
description: Use the SmartEdit MCP tools (mcp__smartedit__*) INSTEAD of built-in Read/Edit for editing any file over ~150 lines, for multi-edit series, for exploring code structure (symbols/tree/goto), finding all occurrences with context, encoding work (cp1251/UTF-16), and exact arithmetic. Fires on file editing, large file refactoring, code navigation, calculations. DEFAULT editor for code files — built-in Edit only for trivial one-liners.
---

# SmartEdit — the default line-based editor

Built-in Edit needs a byte-exact `old_string` — on big files it breaks (drift after context
summarization). SmartEdit edits by **line numbers** — nothing to mismatch. The file lives in the
server's RAM, not in your context. Every edit answers back: diff, **anchored diagnostics**,
and auto-recovery if a peer raced you.

## When SmartEdit (default for code)

- Any file **> ~150 lines** — open amortizes across the edit series
- **Edit series** in one file: 2+ edits → `edit_batch` (atomic, no shift math)
- **Mass rename**: `replace_all` (literal, not regex, one rev)
- **Structure**: `open` returns `code_outline` (top-50 symbols, ≤5k-line code files) — no extra call;
  deeper: `symbols` / `goto(name)` / `tree`
- **All occurrences**: `find(regex, context=2)` — snippets + breadcrumb `Ns.Class.Method`
- **Encodings**: cp1251/UTF-16 (`open` with `encoding`), `file_info`, `convert`; `save` defaults to keep
- File **dropped from context** after summarization — don't re-read: `open → code_outline/find → edit`
- Giants up to ~2 GB UTF-8 (mmap): `show`/`find` read only what is asked
- **Exact math**: `calc` (hex/bin, ^, !, functions). Never mental arithmetic.
- **Undo a bad edit**: `undo(n, expected_rev)` (undo twice = redo)
- **What did I / others do**: `log(grep=..., errors_only=true)` — shared journal; `sessions` — who holds which file

## When built-in Edit

- File ≤ 150 lines, one-off single-line tweak, file already in context
- Creating a new file (Write)

## Workflow

1. `open(path)` → sessionId, rev, code_outline, peers (repeat open = attach to the shared document)
2. `goto(name)` / `find(regex, context=2)` → locate lines
3. One edit: `replace_lines(start, end, new_text, expected_rev, expect_old?)`;
   2+ edits: `edit_batch(edits[])` — all ranges/anchors validated BEFORE applying, bottom-up, one rev bump;
   rename: `replace_all(find, replace)`
4. `save()` — keeps encoding by default; rev does NOT change on save

## Semantics (critical)

- Lines are 1-based inclusive; `new_text=''` deletes the range; a lone trailing `\n` is ignored
- Pass `expected_rev` from the previous response; `save` does not bump it
- `expect_old` — anchor against your own line math (mismatch → `anchor_mismatch` names the line)
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
- Close sessions when done; `close` right after `save` (clean sessions no longer lock files — v0.8.2 — but closing frees RAM)

## If something looks wrong (abnormal — not a ritual)

The engine's guarantees are test-locked: every save publishes exactly the current rev (byte-verified
by the save-torture check in selftest), diagnostics can't false-alarm on string contents. So treat
these as REGRESSIONS, not as weather:

- `save` result differs from what the session shows on disk → `log(grep="update:")` to capture evidence, re-`open`, report
- `show` returning text that contradicts the last diff → `close` the session, `open` fresh, report with journal lines

Do not build habits around them — grep-after-save and reopen-on-suspicion are incident protocol,
not everyday steps.
