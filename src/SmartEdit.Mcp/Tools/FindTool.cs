using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

sealed class FindTool : ToolBase {
    public override string Name => "find";
    public override string Description =>
        "Regex search across session lines after after_line. Returns line numbers + short previews + where (breadcrumb Ns.Class.Member " +
        "if symbols were requested). context=N adds a numbered snippet around each hit (all occurrences view). Feed line numbers into replace_lines/insert_after.";
    public override string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string","description":"from a previous response; optional if path given"},"path":{"type":"string","description":"open-by-path: skip open entirely"},
          "regex":{"type":"string","description":".NET regex"},
          "after_line":{"type":"integer","description":"skip lines up to this one (default 0)"},
          "max_hits":{"type":"integer","description":"default 20, max 200"},
          "context":{"type":"integer","description":"lines around each hit for snippets, default 0, max 10"},
          "ignore_case":{"type":"boolean","description":"default false"}
        },"required":["regex"]}
        """;

    public override async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var att = Sessions.Resolve(args.GetNullableString("session_id"), args.GetNullableString("path"));
        var s = att.Doc;
        Sessions.Touch(att);
        var rx = CompileRegex(args.GetString("regex"), args.GetBool("ignore_case"));
        int afterLine = args.GetInt("after_line", 0);
        int maxHits = Math.Clamp(args.GetInt("max_hits", 20), 1, 200);
        int context = Math.Clamp(args.GetInt("context", 0), 0, 10);
        var (hits, truncated) = await LockedAsync(s, () => {
            var r = s.Find(rx, afterLine, maxHits, context);
            foreach (var h in r.Hits) // #5: сниппет find = увиденное; без context — сама строка хита
                s.MarkSeen(att.Id, h.Snippet is null ? h.Line : h.SpanStart, h.Snippet is null ? h.Line : h.SpanEnd, "find");
            return r;
        });
        string[]? events = Presence.EventsSince(s, att);
        return Json(new FindResult {
            Rev = s.Rev,
            Hits = hits.Select(h => new HitDto {
                Line = h.Line, Preview = h.Preview, Snippet = h.Snippet, Where = h.Where,
                Span = h.Snippet is null ? null : new SpanDto { Start = h.SpanStart, End = h.SpanEnd },
            }).ToArray(),
            Truncated = truncated,
            Events = events,
        }, RpcJson.Default.FindResult);
    }
}

public sealed class FindResult {
    public int Rev { get; init; }
    public HitDto[] Hits { get; init; } = [];
    public bool Truncated { get; init; }
    public string[]? Events { get; init; } // v0.4: чужие правки с твоего last_seen
}

public sealed class HitDto {
    public int Line { get; init; }
    public string Preview { get; init; } = "";
    public string? Snippet { get; init; }
    public string? Where { get; init; } // breadcrumb Ns.Class.Method (если symbols-кэш прогрет)
    public SpanDto? Span { get; init; }  // v0.8.1 (#3): строки, которые покрывает snippet — редактируй без follow-up show
}

/// <summary>Диапазон строк (1-based, включительно).</summary>
public sealed class SpanDto {
    public int Start { get; init; }
    public int End { get; init; }
}
