---
name: smartedit
description: Use the SmartEdit MCP tools (mcp__smartedit__*) INSTEAD of built-in Read/Edit for editing any file over ~150 lines, for multi-edit series, for exploring code structure (symbols/tree/goto), finding all occurrences with context, encoding work (cp1251/UTF-16), and exact arithmetic. Fires on file editing, large file refactoring, code navigation, calculations.
---

# SmartEdit — line-based editor for big files

Built-in Edit needs a byte-exact `old_string` — on big files it breaks (drift after context
summarization). SmartEdit edits by **line numbers** — nothing to mismatch. The file lives in the
server's RAM, not in your context.

## When SmartEdit

- File **> ~150 lines** — almost always (open amortizes across the edit series)
- **Edit series** in one file: 2+ edits → `edit_batch` (atomic, no shift math)
- **Mass rename**: `replace_all` (literal, not regex, one rev)
- **Structure**: `symbols` / `goto(name)` / `tree` (C#/py/js/java/cpp/xml/html)
- **All occurrences**: `find(regex, context=2)` — snippets + breadcrumb `Ns.Class.Method`
- **Encodings**: cp1251/UTF-16 (`open` with `encoding`), `file_info`, `convert`; `save` defaults to keep
- File **dropped from context** after summarization — don't re-read: `open → symbols/find → edit`
- Giants up to ~2 GB UTF-8 (mmap): `show`/`find` read only what is asked
- **Exact math**: `calc` (hex/bin, ^, !, functions). Never mental arithmetic.
- **Undo a bad edit**: `undo(n, expected_rev)` (undo twice = redo)
- **What did I / others do**: `log(grep=..., errors_only=true)` — shared journal; `sessions` — who holds which file

## When built-in Edit

- File ≤ 150 lines, one-off 1–2 spot edits, file already in context
- Creating a new file (Write)

## Workflow

1. `open(path)` → sessionId, rev=0, outline/preview (repeat open = attach to the shared document)
2. `goto(name)` / `find(regex, context=2)` → locate lines
3. One edit: `replace_lines(start, end, new_text, expected_rev, expect_old?)`;
   2+ edits: `edit_batch(edits[])` — all ranges/anchors validated BEFORE applying, bottom-up, one rev bump;
   rename: `replace_all(find, replace)`
4. `save()` — keeps encoding by default; rev does NOT change on save

## Semantics (critical)

- Lines are 1-based inclusive; `new_text=''` deletes the range; a lone trailing `\n` is ignored
- Pass `expected_rev` from the previous response; `save` does not bump it
- `expect_old` — anchor against your own line math (mismatch → `anchor_mismatch` names the line)
- Errors are JSON: `{"kind":"stale_rev|anchor_mismatch|invalid_range|batch_overlap|too_large|…","current_rev":N}` — branch on `kind`
- Every mutation returns `diff` + `brackets` (unbalanced braces/parens — read it!)
- Multi-agent: stale errors name the offender; `save` → `peers_dirty` while others are mid-flight; `sync` before big edits in shared files
- Close sessions when done (mmap holds the file); `close` right after `save`
