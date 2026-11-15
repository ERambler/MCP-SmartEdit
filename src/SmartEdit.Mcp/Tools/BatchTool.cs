using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

// v0.3: атомарная серия правок одним вызовом.

sealed class BatchTool : ToolBase {
    public override string Name => "edit_batch";
    public override string Description =>
        "Atomic series of line edits in ONE call: edits=[{start,end,new_text,expect_old?}]. " +
        "All ranges and expect_old anchors are validated BEFORE anything applies — one bad edit rejects the whole batch (nothing applied, kind=batch_overlap/anchor_mismatch says which). " +
        "retry_on_stale=true (#1): on stale_rev auto-retry ONCE at the fresh rev — every edit must be provably safe (anchor intact, or range untouched with tracked line shifts); otherwise structured stale_conflict. " +
        "Line numbers are the CURRENT numbering for every edit: applied bottom-up, no shift math. " +
        "end = start-1 inserts before start. One rev bump total; response carries per-edit diffs in your order. dry_run=true → diffs only.";
    public override string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string","description":"from a previous response; optional if path given"},"path":{"type":"string","description":"open-by-path: skip open entirely"},
          "edits":{"type":"array","maxItems":200,"items":{"type":"object","properties":{
            "start":{"type":"integer","description":"first line, 1-based"},
            "end":{"type":"integer","description":"last line inclusive; end=start-1 → insert before start"},
            "new_text":{"type":"string"},
            "expect_old":{"type":"string","description":"optional anchor: exact current text of start..end; mismatch rejects the batch"}
          },"required":["start","end","new_text"]}},
          "expected_rev":{"type":"integer","description":"optional: omit = the last rev you observed (server tracks it)"},
          "retry_on_stale":{"type":"boolean","description":"auto-retry once on stale_rev when every edit is provably safe; default TRUE since v0.8 (pass false for the plain stale_rev)"},
          "dry_run":{"type":"boolean","description":"default false"}
        },"required":["edits"]}
        """;

    public override async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var att = Sessions.Resolve(args.GetNullableString("session_id"), args.GetNullableString("path"));
        var s = att.Doc;
        int expectedRev = args.GetInt("expected_rev", att.LastSeenRev); // v0.9: omit = последний увиденный rev
        bool dryRun = args.GetBool("dry_run");
        bool retryOnStale = args.GetBool("retry_on_stale", true); // v0.8: recovery по умолчанию (opt-out: false)
        var ops = new List<BatchOp>();
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("edits", out var arr) && arr.ValueKind == JsonValueKind.Array) {
            foreach (var e in arr.EnumerateArray()) {
                if (e.ValueKind != JsonValueKind.Object)
                    throw new ToolException(ToolException.InvalidArgs, "each edit must be an object {start,end,new_text,expect_old?}");
                ops.Add(new BatchOp(e.GetRequiredInt("start"), e.GetRequiredInt("end"),
                    e.GetString("new_text"), e.GetNullableString("expect_old")));
            }
        }
        var outcome = await LockedAsync(s, () => {
            try { return s.EditBatch(ops, expectedRev, dryRun, att.Id); }
            catch (ToolException tex) when (tex.Kind == ToolException.StaleRev && retryOnStale) {
                return StaleRecovery.RecoverBatch(s, att.Id, ops, expectedRev, dryRun);
            }
        });
        att.LastSeenRev = outcome.Rev; // v0.9: ответ доставил состояние
        return Json(new BatchResult {
            Rev = outcome.Rev, TotalLines = outcome.TotalLines, Applied = outcome.Applied, DryRun = outcome.DryRun,
            Diffs = outcome.Diffs.ToArray(), Brackets = outcome.Brackets?.ToArray(),
            Recovered = outcome.Recovered, Intervening = outcome.Intervening?.ToArray(),
            Diagnostics = outcome.Diags?.Select(d => new DiagDto { Line = d.Line, Code = d.Code, Message = d.Message }).ToArray(),
            Unsaved = s.Dirty, // v0.9.3: применено в RAM, НЕ сохранено — save(path)
            Blind = outcome.Blind, // #5
        }, RpcJson.Default.BatchResult);
    }
}
