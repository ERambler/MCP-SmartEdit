using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

/// <summary>Undo: отменяет последние n правок; сам является новой правкой — undo(undo) = redo.</summary>
sealed class UndoTool : ToolBase {
    public override string Name => "undo";
    public override string Description =>
        "Undo the last n (default 1, max 20) undoable edits as a COMPENSATING mutation: new rev, recorded in history, safe with peers " +
        "(locations are re-found by content — peers' unrelated edits don't block). undo after undo naturally redoes. " +
        "kind=undo_conflict (nothing applied, all problems listed) if the exact text is gone or appears twice; " +
        "kind=nothing_to_undo when history has no invertible edits (edits over 200 lines per side are not invertible). dry_run → diffs only.";
    public override string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string","description":"from a previous response; optional if path given"},"path":{"type":"string","description":"open-by-path: skip open entirely"},
          "n":{"type":"integer","description":"how many last edits to undo, default 1, max 20"},
          "expected_rev":{"type":"integer","description":"optional: omit = the last rev you observed (server tracks it)"},
          "dry_run":{"type":"boolean","description":"default false"}
        },"required":[]}
        """;

    public override async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var att = Sessions.Resolve(args.GetNullableString("session_id"), args.GetNullableString("path"));
        var s = att.Doc;
        int n = Math.Clamp(args.GetInt("n", 1), 1, 20);
        int expectedRev = args.GetInt("expected_rev", att.LastSeenRev); // v0.9: omit = последний увиденный rev
        bool dryRun = args.GetBool("dry_run");
        var outcome = await LockedAsync(s, () => s.Undo(n, expectedRev, dryRun, att.Id));
        att.LastSeenRev = outcome.Rev; // v0.9: ответ доставил состояние
        return Json(new BatchResult {
            Rev = outcome.Rev, TotalLines = outcome.TotalLines, Applied = outcome.Applied, DryRun = outcome.DryRun,
            Diffs = outcome.Diffs.ToArray(), Brackets = outcome.Brackets?.ToArray(),
            Unsaved = s.Dirty, // v0.9.3
        }, RpcJson.Default.BatchResult);
    }
}
