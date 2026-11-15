using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

sealed class InsertAfterTool : ToolBase {
    public override string Name => "insert_after";
    public override string Description =>
        "Insert text after line N (line=0 inserts at top). Same rev contract and dry_run as replace_lines.";
    public override string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string","description":"from a previous response; optional if path given"},"path":{"type":"string","description":"open-by-path: skip open entirely"},
          "line":{"type":"integer","description":"0 = at top"},
          "text":{"type":"string"},
          "expected_rev":{"type":"integer","description":"optional: omit = the last rev you observed (server tracks it)"},
          "dry_run":{"type":"boolean","description":"default false"}
        },"required":["line","text"]}
        """;

    public override async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var att = Sessions.Resolve(args.GetNullableString("session_id"), args.GetNullableString("path"));
        var s = att.Doc;
        int line = args.GetRequiredInt("line");
        string text = args.GetString("text");
        int expectedRev = args.GetInt("expected_rev", att.LastSeenRev); // v0.9: omit = последний увиденный rev
        bool dryRun = args.GetBool("dry_run");
        var outcome = await LockedAsync(s, () => s.InsertAfter(line, text, expectedRev, dryRun, att.Id));
        att.LastSeenRev = outcome.Rev; // v0.9: ответ доставил состояние
        return Json(new MutationResult {
            Rev = outcome.Rev, TotalLines = outcome.TotalLines, Diff = outcome.Diff, DryRun = outcome.DryRun,
            Brackets = outcome.Brackets?.ToArray(),
            Unsaved = s.Dirty, // v0.9.3: применено в RAM, НЕ сохранено — не забудь save(path)
            Blind = outcome.Blind, // #5
        }, RpcJson.Default.MutationResult);
    }
}
