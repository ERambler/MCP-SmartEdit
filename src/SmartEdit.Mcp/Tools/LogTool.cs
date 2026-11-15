using System.Text.Json;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

/// <summary>
/// v0.5: экстремная ручка — общий журнал сервера (все чаты/проекты/процессы): хвост, фильтры, grep.
/// Агент сам себе «прокручивает» прошлое: что делал, что упало и с каким kind.
/// </summary>
sealed class LogTool : ToolBase {
    public override string Name => "log";
    public override string Description =>
        "Emergency handle: the SHARED server log across all chats/projects/processes (every tool call, error+kind, per-pid). " +
        "tail=N (default 50, max 500) returns the LAST matching lines; level=INFO|WARN|ERROR filters; grep=substring filter; " +
        "errors_only=true → [ERROR] only. Rotates at 5MB (mcp.log.1/.2 kept). Use to retrace your own past actions or diagnose a weird session.";
    public override string InputSchemaJson => """
        {"type":"object","properties":{
          "tail":{"type":"integer","description":"last N matching lines, default 50, max 500"},
          "level":{"type":"string","enum":["INFO","WARN","ERROR"],"description":"filter by level"},
          "grep":{"type":"string","description":"substring filter"},
          "errors_only":{"type":"boolean","description":"shortcut for level=ERROR"}
        },"required":[]}
        """;

    public override Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        int tail = Math.Clamp(args.GetInt("tail", 50), 1, 500);
        string? level = args.GetNullableString("level")?.ToUpperInvariant();
        if (level is not ("INFO" or "WARN" or "ERROR" or null)) level = null;
        string? grep = args.GetNullableString("grep");
        bool errorsOnly = args.GetBool("errors_only");
        if (errorsOnly) level = "ERROR";

        bool Match(string line) =>
            (level is null || line.Contains($"[{level}]"))
            && (grep is null || line.Contains(grep, StringComparison.OrdinalIgnoreCase));

        // хронология: архивы старее живого журнала; держим только хвост подходящих (RAM O(tail))
        var keep = new Queue<string>();
        long scanned = 0;
        for (int a = SmartEdit.Mcp.Infra.Log.KeepArchives; a >= 0; a--) {
            string path = SmartEdit.Mcp.Infra.Log.ArchivePath(a);
            if (!File.Exists(path)) continue;
            try {
                foreach (var line in File.ReadLines(path)) {
                    scanned++;
                    if (!Match(line)) continue;
                    keep.Enqueue(line);
                    if (keep.Count > tail) keep.Dequeue();
                }
            }
            catch (IOException) { }
        }
        return Task.FromResult(Json(new LogResult {
            Path = SmartEdit.Mcp.Infra.Log.ArchivePath(0),
            LinesScanned = scanned,
            Lines = [.. keep],
        }, RpcJson.Default.LogResult));
    }
}

/// <summary>v0.5: хвост общего журнала сервера (все процессы/проекты).</summary>
public sealed class LogResult {
    public string Path { get; init; } = "";
    public long LinesScanned { get; init; }
    public string[] Lines { get; init; } = [];
}
