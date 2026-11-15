using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

sealed class SymbolsTool : HiddenTool {
    public override string Name => "symbols";
    public override string Description =>
        "Code structure of an open session (by extension): C# namespaces/classes/members with XML-doc summaries, " +
        "Python class/def with docstrings, JS/TS/Java/C++ functions and types, XML/HTML tag trees. " +
        "Line numbers map 1:1 to show/replace_lines. docs_only=true returns only documented symbols. Cache: recomputed on rev change.";
    public override string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string","description":"from a previous response; optional if path given"},"path":{"type":"string","description":"open-by-path: skip open entirely"},
          "docs_only":{"type":"boolean","description":"only symbols with XML-doc/docstring (C#/Python), default false"}
        },"required":[]}
        """;

    public async override Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var s = Sessions.Resolve(args.GetNullableString("session_id"), args.GetNullableString("path")).Doc;
        bool docsOnly = args.GetBool("docs_only");
        var result = await LockedAsync(s, () => s.Symbols(docsOnly));
        return Json(new SymbolsResult {
            Language = result.Language,
            Usings = result.Usings.ToArray(),
            Warnings = result.Warnings.ToArray(),
            Symbols = ToDto(result.Roots),
        }, RpcJson.Default.SymbolsResult);
    }

    static SymbolDto[] ToDto(List<Core.SymbolScan.Symbol> symbols)
        => symbols.Select(ToDto).ToArray();

    static SymbolDto ToDto(Core.SymbolScan.Symbol s) => new() {
        Line = s.Line, EndLine = s.EndLine, Kind = s.Kind, Name = s.Name, Sig = s.Signature, Doc = s.Doc,
        Children = s.Children.Count == 0 ? [] : ToDto(s.Children),
    };
}

public sealed class SymbolsResult {
    public string Language { get; init; } = "";
    public string[] Usings { get; init; } = [];
    public string[] Warnings { get; init; } = [];
    public SymbolDto[] Symbols { get; init; } = [];
}
