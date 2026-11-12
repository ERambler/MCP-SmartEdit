using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

// v0.2: структура кода (symbols), скобочный аудит (check), карта проекта по namespace (tree).

sealed class SymbolsTool : ITool {
    public string Name => "symbols";
    public string Description =>
        "Code structure of an open session (by extension): C# namespaces/classes/members with XML-doc summaries, " +
        "Python class/def with docstrings, JS/TS/Java/C++ functions and types, XML/HTML tag trees. " +
        "Line numbers map 1:1 to show/replace_lines. docs_only=true returns only documented symbols. Cache: recomputed on rev change.";
    public string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string","description":"from a previous response; optional if path given"},"path":{"type":"string","description":"open-by-path: skip open entirely"},
          "docs_only":{"type":"boolean","description":"only symbols with XML-doc/docstring (C#/Python), default false"}
        },"required":[]}
        """;

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var s = Sessions.Resolve(args.GetNullableString("session_id"), args.GetNullableString("path")).Doc;
        bool docsOnly = args.GetBool("docs_only");
        var result = await ToolOps.LockedAsync(s, () => s.Symbols(docsOnly));
        return ToolOps.Json(new SymbolsResult {
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

sealed class CheckTool : ITool {
    public string Name => "check";
    public string Description =>
        "Bracket balance {}[]() for code, <> for XML/HTML, on a line range (default: whole file). " +
        "Strings/comments/interpolation are excluded by a state machine (code inside {holes} of $\"…\" is kept); <> in C# is NOT checked (generics).";
    public string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string","description":"from a previous response; optional if path given"},"path":{"type":"string","description":"open-by-path: skip open entirely"},
          "start":{"type":"integer"},
          "end":{"type":"integer"}
        },"required":[]}
        """;

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var s = Sessions.Resolve(args.GetNullableString("session_id"), args.GetNullableString("path")).Doc;
        int start = args.GetInt("start", 0);
        int end = args.GetInt("end", 0);
        var problems = await ToolOps.LockedAsync(s, () => s.CheckBrackets(start, end));
        return ToolOps.Json(new CheckResult { Problems = problems.ToArray() }, RpcJson.Default.CheckResult);
    }
}


sealed class TreeTool : ITool {
    public string Name => "tree";
    public string Description =>
        "Project map for a directory: files (relative paths, lines, bytes), C# files grouped by namespace. " +
        "Skips bin/obj/.git/node_modules/.vs/packages/build/dist. pattern = optional file glob (e.g. '*.cs'). " +
        "recent_hours=N → only files modified within the last N hours: flat list, newest first, with modified_utc.";
    public string InputSchemaJson => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"directory to scan"},
          "pattern":{"type":"string","description":"file glob filter, default all"},
          "max_files":{"type":"integer","description":"default 1500"},
          "recent_hours":{"type":"integer","description":"only files modified within last N hours (flat, newest first)"}
        },"required":["path"]}
        """;

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        await Task.CompletedTask; // CPU--bound, без асинхронных ожиданий
        string root = args.GetString("path");
        if (root == "" || !Directory.Exists(root))
            throw new DirectoryNotFoundException($"directory not found: {root}");
        string pattern = args.GetString("pattern", "*");
        int maxFiles = Math.Clamp(args.GetInt("max_files", 1500), 1, 5000);
        int recentHours = Math.Clamp(args.GetInt("recent_hours", 0), 0, 24 * 90);
        var scan = ProjectScanner.Scan(System.IO.Path.GetFullPath(root), pattern, maxFiles, recentHours);
        return ToolOps.Json(new TreeResult {
            Root = scan.Root,
            TotalFiles = scan.TotalFiles,
            ListedFiles = scan.TotalFiles,
            SkippedDirs = scan.SkippedDirs,
            TotalBytes = scan.TotalBytes,
            Groups = scan.Groups.Select(g => new TreeGroupDto {
                Namespace = g.Namespace,
                Files = g.Files.Select(f => new TreeFileDto { Path = f.Path, Lines = f.Lines, Bytes = f.Bytes, ModifiedUtc = f.ModifiedUtc }).ToArray(),
            }).ToArray(),
        }, RpcJson.Default.TreeResult);
    }
}
