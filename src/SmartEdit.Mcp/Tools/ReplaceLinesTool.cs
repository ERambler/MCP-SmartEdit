using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

sealed class ReplaceLinesTool : ToolBase {
    public override string Name => "replace_lines";
    public override string Description =>
        "Replace lines start..end (1-based, inclusive) with new_text lines. new_text='' deletes the range; a lone trailing newline is ignored. " +
        "expected_rev (optional: omit = the last rev you observed) must equal the current rev; on mismatch the error carries current_rev. " +
        "retry_on_stale=true (#1): on stale_rev auto-retry ONCE at the fresh rev when it is provably safe — expect_old anchor still matches, or the range was untouched by the intervening edits (line shifts tracked); response gains recovered=true + intervening=[what others did]. Never blind: changed range → structured stale_conflict with the current text of the range. " +
        "expect_old (optional anchor): exact current text of the range — guards against wrong-line math (kind=anchor_mismatch names the line). " +
        "dry_run=true returns the diff without applying. For 2+ edits use edit_batch.";
    public override string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string","description":"from a previous response; optional if path given"},"path":{"type":"string","description":"open-by-path: skip open entirely"},
          "start":{"type":"integer"},
          "end":{"type":"integer"},
          "new_text":{"type":"string","description":"replacement lines ('' = delete)"},
          "expect_old":{"type":"string","description":"optional anchor: exact current text of start..end"},
          "expected_rev":{"type":"integer","description":"optional: omit = the last rev you observed (server tracks it)"},
          "retry_on_stale":{"type":"boolean","description":"auto-retry once on stale_rev when provably safe (anchor intact or range untouched); default false"},
          "dry_run":{"type":"boolean","description":"default false"}
        },"required":["start","end","new_text"]}
        """;

    public override async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var att = Sessions.Resolve(args.GetNullableString("session_id"), args.GetNullableString("path"));
        var s = att.Doc;
        int start = args.GetRequiredInt("start");
        int end = args.GetRequiredInt("end");
        string newText = args.GetString("new_text");
        int expectedRev = args.GetInt("expected_rev", att.LastSeenRev); // v0.9: omit = последний увиденный rev
        bool dryRun = args.GetBool("dry_run");
        string? expectOld = args.GetNullableString("expect_old");
        bool retryOnStale = args.GetBool("retry_on_stale", true); // v0.8: recovery по умолчанию (opt-out: false)
        // retry живёт ВНУТРИ блокировки: между проверкой и повторным применением rev не двинется
        var outcome = await LockedAsync(s, () => {
            try { return s.ReplaceLines(start, end, newText, expectedRev, dryRun, expectOld, att.Id); }
            catch (ToolException tex) when (tex.Kind == ToolException.StaleRev && retryOnStale) {
                return StaleRecovery.RecoverReplace(s, att.Id, start, end, newText, expectedRev, dryRun, expectOld);
            }
        });
        att.LastSeenRev = outcome.Rev; // v0.9: ответ доставил состояние
        return Json(new MutationResult {
            Rev = outcome.Rev, TotalLines = outcome.TotalLines, Diff = outcome.Diff, DryRun = outcome.DryRun,
            Brackets = outcome.Brackets?.ToArray(),
            Recovered = outcome.Recovered, Intervening = outcome.Intervening?.ToArray(),
            Diagnostics = outcome.Diags?.Select(d => new DiagDto { Line = d.Line, Code = d.Code, Message = d.Message }).ToArray(),
            Unsaved = s.Dirty, // v0.9.3: применено в RAM, НЕ сохранено — не забудь save(path)
            Blind = outcome.Blind, // #5: правка вслепую (только при open(attention:true))
        }, RpcJson.Default.MutationResult);
    }
}
