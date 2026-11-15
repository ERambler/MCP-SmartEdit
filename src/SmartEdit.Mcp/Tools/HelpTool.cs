using System.Text.Json;

namespace SmartEdit.Mcp.Tools;

/// <summary>
/// v0.8.1: самодокументируемый API — одна страница: карта инструментов, семантика диапазонов,
/// rev-протокол, error kinds → действия. Консилиум: «самый дешёвый токен-выигрыш — один вызов
/// вместо 5 раундов тыка». Плюс приглашение на первый tools/call в сессии (см. McpProtocolHandler).
/// </summary>
sealed class HelpTool : HiddenTool {
    public override string Name => "help";
    public override string Description =>
        "ONE page you can trust: tool map (one line each), [start..end] semantics (1-based inclusive, end=start-1 inserts, [1..0] = empty file), " +
        "the rev protocol, and every error kind with the exact next action. Call it once at session start instead of reverse-engineering the API.";
    public override string InputSchemaJson => """{"type":"object","properties":{},"required":[]}""";

    public override Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        const string page = """
SMARTEDIT — GOLDEN RULES
- lines are 1-based, INCLUSIVE; end = start-1 => INSERT before start; empty file: replace_lines(1, 0, text)
- path-first (v0.9): EVERY tool accepts path instead of session_id — open is optional, no ritual; both given => ambiguous_target
- expected_rev is OPTIONAL (omit = the last rev you observed — the server tracks it); pin it to assert no peer raced you
- expect_old = exact current text of the range — your guard against your own wrong-line math
- stale rev auto-recovers by default (response: recovered:true + intervening) — usually you do nothing
- v0.9.3: every edit/show response on a DIRTY document carries unsaved:true — SAVE before running external tools (git/tests/bash see the OLD disk code)
- v0.11 #5: open(path, attention:true) tracks which lines you've SEEN (show/find/goto); editing an unseen range returns
  a non-blocking blind warning ("lines 4..4 not seen — nearest seen: 1..2 via show; show them first") — opt-in, per document
- errors are JSON {kind,...} — branch on kind, never parse prose; machine retry: {strategy: reissue_with_rev|reanchor|reread_range, start?, end?}

TOOL MAP
navigate : open(path, note?) → sessionId, rev, code_outline(≤5k-line code, +total/truncated), peers | show(start,end,max_chars?)
          create(path, content?, create_parents?) — NEW file as a RAM session (#21): nothing on disk until save; advisory
          syntax check of the seed in the response; FIRST save gates hard (fix in RAM, save once; force = conscious);
          existing file/session → already_exists; create_parents=true makes dirs at save
          find(regex, context:N) → lines+snippets+breadcrumbs+span{start,end} (edit the span, no follow-up show) | symbols / goto / tree | file_info
edit     : replace_lines(start,end,new_text,expected_rev,expect_old?) | insert_after(line,text,expected_rev)
          edit_batch(edits≤200, atomic, bottom-up = no shift math) | replace_all(literal, refuse-if-too-many)
          edit_transaction(steps[{path|session_id, edits}], save?) — MULTI-FILE all-or-nothing: every range/anchor
          validated on all files BEFORE anything applies; one bad step aborts everything (kind=transaction_aborted)
          replace_content(find, replace) — EXACT text, no line numbers; must be unique (0 → not_found, >1 → ambiguous_match)
          undo(n, expected_rev) — undo×2 = redo, peer-safe compensating mutations
lifecycle: save(keeps encoding; force=true publishes peers' edits too) | close(discard?) | sessions | sync
ops      : check(bracket audit) | log(tail/grep/errors_only — shared journal) | calc(exact math) | convert(encoding)
recipes : recipe_save(name, steps[{find,replace}], description?) — persist a validated refactor with {{param}} placeholders;
           recipe_apply(name, params, paths[1..16], dry_run?) replays it by TEXT anchors (unique occurrence each) —
           phase A locates every anchor in every file BEFORE applying; divergence → recipe_diverged {file, step}, nothing applied;
           RAM-staging (save per file, or save:true best-effort). recipe_list() shows what exists.
gate     : save on .cs/.js/.ts/.tsx/.py/.sh is SYNTAX-GATED (v0.10, built-in tree-sitter): blocked with syntax_gate
           if YOUR edit added NEW syntax errors vs baseline (old ones never block, moved ones aren't new);
           gateErrors[] names the missing token (;/)/fi) at 1-based row:col; result carries gate: pass|forced|off|degraded

ERROR KINDS → ACTION
stale_rev        → already auto-retried (see recovered/intervening); if you passed retry_on_stale:false → re-issue with current_rev
stale_conflict   → the range changed under you: show(range from retry_hint), then re-apply with the fresh rev
anchor_mismatch  → your line math is off — the error names the line, found vs expected; fix coords, don't force
invalid_range / batch_overlap → fix coordinates; ranges in one batch must be disjoint
unknown_session  → session gone (GC after 30 min idle?): open again
peers_dirty      → someone mid-flight: wait, or save(force=true) publishes THEIR edits too
cross_process_lock→ ANOTHER SmartEdit process owns this file (two servers overwrite each other's saves): close it (pids in log) and retry
disk_conflict    → file changed on disk since open: SHOW the range and RE-APPLY on top; force=true DISCARDS the on-disk version
not_found / ambiguous_match (replace_content) → copy the snippet exactly from a fresh show/find / extend it to be unique
too_large        → over a cap: split the work
syntax_gate      → your edit added NEW syntax errors (gateErrors[] lists them, missing names the token): FIX them;
                   force=true only if the block is wrong (cascade/#if) — it publishes and still reports gateErrors
syntax_gate_degraded→ checker timed out/crashed (never hangs the server): retry, or force=true to save unchecked
diagnostics (in every edit response): [{line, code, message}] — bracket_mismatch, never_closed, stray_closer,
unterminated_literal/comment/hole; unterminated_literal anchors the OPENING line. [] = clean.

QUICKSTART
open(path) → find(regex, context:2) → edit_batch(edits, expected_rev, expect_old anchors) → save → close
single file, zero ceremony (v0.9): show(path, 1..N) → replace_lines(path, ...) → save(path) — open never needed
(Disk philosophy: NOTHING touches the disk except save — edit freely, undo if wrong, save when happy.
 Locks: a DIRTY session holds its file (protection); a CLEAN session never does — between tool calls the handle is released, so git/scripts/formatters can write freely. External change while released → next call transparently starts fresh (rev=0); file deleted → disk_conflict with retry_hint.)
""";
        return Task.FromResult(page);
    }
}
