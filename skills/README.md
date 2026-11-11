# Writing a skill that makes agents *confidently* use SmartEdit

A skill is not documentation — it is a **behavioral override**. Agents default to the tools they were
trained on (built-in Edit/Read). Your skill's only job is to win that competition at decision time.
This guide distills what actually worked in production (the companion
[`zcode-smartedit/SKILL.md`](zcode-smartedit/SKILL.md) is a working example, refined through a full
day of the agent dogfooding the editor on its own source code).

## The 7 rules we learned the hard way

### 1. The `description` frontmatter is a router, not a summary

Agents pick skills by matching the CURRENT situation against the description. Put concrete triggers
there — file size thresholds, action verbs, artifacts:

```yaml
description: Use the SmartEdit MCP tools (mcp__smartedit__*) INSTEAD of built-in Read/Edit
  for editing any file over ~150 lines, for multi-edit series, for exploring code structure
  (symbols/tree/goto), finding all occurrences with context, encoding work (cp1251/UTF-16),
  and exact arithmetic. Fires on file editing, large file refactoring, code navigation.
```

Rules of thumb: name the tools it replaces (`INSTEAD of built-in Read/Edit`), give a numeric
trigger (`~150 lines` — vague "large files" loses to habit), end with "Fires on …" action words.

### 2. Include a "when NOT" section — symmetric wins

Without it the agent either over-uses the tool (open/close ceremony for a 20-line one-off edit) or
under-uses it (falls back to built-in Edit on a 2000-line file after summarization). Keep both
lists short and mechanically checkable:

- **Use when:** file > ~150 lines · 2+ edits planned · structure navigation · encoding work · file dropped from context
- **Skip when:** file ≤ 150 lines · one-off 1–2 spot edits · file already fully in context · creating a new file

### 3. Teach the workflow as a numbered recipe, not prose

Agents follow numbered imperative steps far more reliably than paragraphs:

```
1. open(path) → sessionId, rev
2. goto(name) / find(regex, context=2) → line numbers
3. replace_lines(...) / edit_batch(edits[]) / replace_all(...)
4. save() — then close the session
```

### 4. Encode the failure modes as *branching rules*, not warnings

The single biggest confidence gain: tell the agent exactly what to do when things go wrong.
SmartEdit errors are machine-readable JSON — branch on `kind`:

```
stale_rev        → the error already carries current_rev; re-show the range and retry once
anchor_mismatch  → your line math was wrong; the error names the line; re-find, don't guess
peers_dirty      → another agent is mid-flight; sync() and coordinate, or force=true knowingly
too_large        → chunk via edit_batch
```

Phrased as "error → action", the agent recovers in ONE call instead of panicking or switching tools.

### 5. Call out the counter-intuitive semantics explicitly

Every tool has semantics that violate LLM priors. Name them in a "Semantics (critical)" section:
lines are 1-based inclusive; `new_text=''` deletes; trailing `\n` is ignored; `save` does NOT bump
rev; pass `expected_rev` from the previous response; read the `brackets` field in every mutation
response (unbalanced braces = your edit is broken). Anything you omit, the agent will re-derive
incorrectly under context pressure.

### 6. Add lifecycle hygiene rules

Session tools leak resources silently. Our hard-won lines: *close the session right after save*
(an open session holds the file via mmap — external writers fail with EBUSY); *if a file feels
locked, call `sessions` first*. These belong in the skill because the agent cannot discover them
from tool descriptions alone.

### 7. Keep it under ~60 lines

A skill is loaded into context — every line costs tokens on EVERY trigger. Cut explanations,
keep rules. If a rule needs justification, one short "why" clause inline, not a paragraph.

## Structure template

```markdown
---
name: smartedit
description: <router with concrete triggers + "Fires on …">
---
# one-paragraph value prop (the pain it removes)
## When <tool>            (mechanical checks)
## When built-in <other>  (mechanical checks)
## Workflow               (numbered imperative steps)
## Semantics (critical)   (counter-intuitive behaviors, error → action branches)
## Hygiene                (lifecycle rules: close, sessions, log for self-diagnosis)
```

## Anti-patterns (seen in the wild)

- **Marketing prose** ("powerful, efficient editing experience") — zero routing value
- **API reference copy** — the tool descriptions already are one; duplicate less, decide more
- **No error branches** — the agent's first stale_rev sends it back to built-in Edit forever
- **"When in doubt use X"** — doubt is exactly when habit wins; give a checkable condition instead
