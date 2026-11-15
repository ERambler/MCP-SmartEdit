using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

sealed class ShowTool : ToolBase {
    public override string Name => "show";
    public override string Description =>
        "Return numbered lines start..end (1-based, inclusive). Show a range right before editing it. max_chars caps the output (default 20000, max 200000).";
    public override string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string","description":"from a previous response; optional if path given"},"path":{"type":"string","description":"open-by-path: skip open entirely"},
          "start":{"type":"integer","description":"first line, 1-based"},
          "end":{"type":"integer","description":"last line, inclusive"},
          "max_chars":{"type":"integer","description":"output budget, default 20000"}
        },"required":["start","end"]}
        """;

    public override async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var att = Sessions.Resolve(args.GetNullableString("session_id"), args.GetNullableString("path"));
        var s = att.Doc;
        Sessions.Touch(att);
        int start = args.GetRequiredInt("start");
        int end = args.GetRequiredInt("end");
        int maxChars = Math.Clamp(args.GetInt("max_chars", 20_000), 1_000, 200_000);
        var (text, truncated) = await LockedAsync(s, () => {
            var r = s.Show(start, end, maxChars);
            s.MarkSeen(att.Id, start, Math.Min(end, s.TotalLines), "show"); // #5: показанное = увиденное
            return r;
        });
        string[]? events = Presence.EventsSince(s, att); // компактные события, если отстал по rev
        return Json(new ShowResult {
            Rev = s.Rev, TotalLines = s.TotalLines, Start = start, End = end, Text = text, Truncated = truncated,
            Events = events,
            Unsaved = s.Dirty, // v0.9.3: есть несохранённые правки — save(path) перед git/тестами/bash
        }, RpcJson.Default.ShowResult);
    }
}

public sealed class ShowResult {
    public int Rev { get; init; }
    public int TotalLines { get; init; }
    public int Start { get; init; }
    public int End { get; init; }
    public string Text { get; init; } = "";
    public bool Truncated { get; init; }
    public string[]? Events { get; init; } // v0.4: чужие правки с твоего last_seen (null = не отставал)
    public bool Unsaved { get; init; }     // v0.9.3 (Claude): в документе есть НЕСОХРАНЁННЫЕ правки — save(path) перед внешними тулами (git/тесты/bash)
}
