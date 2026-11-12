using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

// v0.5: undo (компенсирующая мутация) и goto (навигация по символам).

/// <summary>Undo: отменяет последние n правок; сам является новой правкой — undo(undo) = redo.</summary>
sealed class UndoTool : ITool {
    public string Name => "undo";
    public string Description =>
        "Undo the last n (default 1, max 20) undoable edits as a COMPENSATING mutation: new rev, recorded in history, safe with peers " +
        "(locations are re-found by content — peers' unrelated edits don't block). undo after undo naturally redoes. " +
        "kind=undo_conflict (nothing applied, all problems listed) if the exact text is gone or appears twice; " +
        "kind=nothing_to_undo when history has no invertible edits (edits over 200 lines per side are not invertible). dry_run → diffs only.";
    public string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string","description":"from a previous response; optional if path given"},"path":{"type":"string","description":"open-by-path: skip open entirely"},
          "n":{"type":"integer","description":"how many last edits to undo, default 1, max 20"},
          "expected_rev":{"type":"integer","description":"optional: omit = the last rev you observed (server tracks it)"},
          "dry_run":{"type":"boolean","description":"default false"}
        },"required":[]}
        """;

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var att = Sessions.Resolve(args.GetNullableString("session_id"), args.GetNullableString("path"));
        var s = att.Doc;
        int n = Math.Clamp(args.GetInt("n", 1), 1, 20);
        int expectedRev = args.GetInt("expected_rev", att.LastSeenRev); // v0.9: omit = последний увиденный rev
        bool dryRun = args.GetBool("dry_run");
        var outcome = await ToolOps.LockedAsync(s, () => s.Undo(n, expectedRev, dryRun, att.Id));
        att.LastSeenRev = outcome.Rev; // v0.9: ответ доставил состояние
        return ToolOps.Json(new BatchResult {
            Rev = outcome.Rev, TotalLines = outcome.TotalLines, Applied = outcome.Applied, DryRun = outcome.DryRun,
            Diffs = outcome.Diffs.ToArray(), Brackets = outcome.Brackets?.ToArray(),
        }, RpcJson.Default.BatchResult);
    }
}

/// <summary>Goto: поиск символов по имени (substring, ignore case) → kind/имя/строки/сигнатура. Номера строк = show/replace_lines.</summary>
sealed class GotoTool : ITool {
    public string Name => "goto";
    public string Description =>
        "Find code symbols by name (substring, case-insensitive): classes/methods/fields/tags with exact line numbers " +
        "(same numbering as show/replace_lines; feeds find-breadcrumbs). Warm the symbols cache first use per rev. " +
        "Returns up to 50 matches sorted by line.";
    public string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string","description":"from a previous response; optional if path given"},"path":{"type":"string","description":"open-by-path: skip open entirely"},
          "name":{"type":"string","description":"symbol name substring"},
          "docs_only":{"type":"boolean","description":"default false"}
        },"required":["name"]}
        """;

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var att = Sessions.Resolve(args.GetNullableString("session_id"), args.GetNullableString("path"));
        var s = att.Doc;
        Sessions.Touch(att);
        string name = args.GetString("name");
        if (name == "") throw new ToolException(ToolException.InvalidArgs, "name is required");
        bool docsOnly = args.GetBool("docs_only");
        var result = await ToolOps.LockedAsync(s, () => s.Symbols(docsOnly));

        var hits = new List<SymbolDto>();
        void Walk(IEnumerable<Core.SymbolScan.Symbol> symbols, string path) {
            foreach (var sym in symbols) {
                if (sym.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                    hits.Add(new SymbolDto {
                        Line = sym.Line, EndLine = sym.EndLine, Kind = sym.Kind,
                        Name = path + sym.Name, Sig = sym.Signature, Doc = sym.Doc,
                    });
                if (hits.Count < 200) Walk(sym.Children, path + sym.Name + ".");
            }
        }
        Walk(result.Roots, "");
        return ToolOps.Json(new GotoResult {
            Language = result.Language, Symbols = [.. hits.OrderBy(h => h.Line).Take(50)],
            Truncated = hits.Count > 50,
        }, RpcJson.Default.GotoResult);
    }
}
