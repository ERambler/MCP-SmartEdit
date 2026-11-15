using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

/// <summary>Goto: поиск символов по имени (substring, ignore case) → kind/имя/строки/сигнатура. Номера строк = show/replace_lines.</summary>
sealed class GotoTool : ToolBase {
    public override string Name => "goto";
    public override string Description =>
        "Find code symbols by name (substring, case-insensitive): classes/methods/fields/tags with exact line numbers " +
        "(same numbering as show/replace_lines; feeds find-breadcrumbs). Warm the symbols cache first use per rev. " +
        "Returns up to 50 matches sorted by line.";
    public override string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string","description":"from a previous response; optional if path given"},"path":{"type":"string","description":"open-by-path: skip open entirely"},
          "name":{"type":"string","description":"symbol name substring"},
          "docs_only":{"type":"boolean","description":"default false"}
        },"required":["name"]}
        """;

    public override async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var att = Sessions.Resolve(args.GetNullableString("session_id"), args.GetNullableString("path"));
        var s = att.Doc;
        Sessions.Touch(att);
        string name = args.GetString("name");
        if (name == "") throw new ToolException(ToolException.InvalidArgs, "name is required");
        bool docsOnly = args.GetBool("docs_only");
        var result = await LockedAsync(s, () => s.Symbols(docsOnly));

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
        var shown = hits.OrderBy(h => h.Line).Take(50).ToList();
        if (s.Attention)
            await LockedAsync(s, () => {
                foreach (var h in shown) // #5: goto привёл к символу — сам символ увиден (тела нет)
                    s.MarkSeen(att.Id, h.Line, Math.Max(h.Line, h.EndLine), "goto");
                return 0;
            });
        return Json(new GotoResult {
            Language = result.Language, Symbols = [.. shown],
            Truncated = hits.Count > 50,
        }, RpcJson.Default.GotoResult);
    }
}

/// <summary>v0.5: символы по имени (goto) — строки совпадают с show/replace_lines.</summary>
public sealed class GotoResult {
    public string Language { get; init; } = "";
    public SymbolDto[] Symbols { get; init; } = [];
    public bool Truncated { get; init; }
}
