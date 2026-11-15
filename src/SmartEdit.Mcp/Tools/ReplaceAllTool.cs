using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

sealed class ReplaceAllTool : ToolBase {
    public override string Name => "replace_all";
    public override string Description =>
        "Replace a literal substring in EVERY line of the session (all occurrences per line; plain text, NOT regex — no escaping needed). " +
        "The perfect rename primitive: one call, one rev bump. Refuses with kind=too_large if more than max_lines lines match " +
        "(default 200, max 2000) — a partial rename would break the code; narrow or chunk via edit_batch. " +
        "Returns occurrences + changed line numbers + per-line diffs. dry_run=true → preview only.";
    public override string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string","description":"from a previous response; optional if path given"},"path":{"type":"string","description":"open-by-path: skip open entirely"},
          "find":{"type":"string","description":"literal text to find (not regex)"},
          "replace":{"type":"string"},
          "max_lines":{"type":"integer","description":"cap on changed lines, default 200, max 2000; above → refuse"},
          "expected_rev":{"type":"integer","description":"optional: omit = the last rev you observed (server tracks it)"},
          "dry_run":{"type":"boolean","description":"default false"}
        },"required":["find","replace"]}
        """;

    public override async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var att = Sessions.Resolve(args.GetNullableString("session_id"), args.GetNullableString("path"));
        var s = att.Doc;
        string find = args.GetString("find");
        string replace = args.GetString("replace");
        int maxLines = Math.Clamp(args.GetInt("max_lines", 200), 1, 2000);
        int expectedRev = args.GetInt("expected_rev", att.LastSeenRev); // v0.9: omit = последний увиденный rev
        bool dryRun = args.GetBool("dry_run");
        var outcome = await LockedAsync(s, () => s.ReplaceAll(find, replace, maxLines, expectedRev, dryRun, att.Id));
        att.LastSeenRev = outcome.Rev; // v0.9: ответ доставил состояние
        return Json(new ReplaceAllResult {
            Rev = outcome.Rev, TotalLines = outcome.TotalLines, Occurrences = outcome.Occurrences,
            Lines = outcome.Lines.ToArray(), DryRun = outcome.DryRun, Brackets = outcome.Brackets?.ToArray(),
            Unsaved = s.Dirty, // v0.9.3
        }, RpcJson.Default.ReplaceAllResult);
    }
}

public sealed class ReplaceAllResult {
    public int Rev { get; init; }
    public int TotalLines { get; init; }
    public int Occurrences { get; init; }
    public int[] Lines { get; init; } = [];
    public bool DryRun { get; init; }
    public string[]? Brackets { get; init; }
    public bool Unsaved { get; init; }             // v0.9.3
}
