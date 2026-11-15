using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

sealed class SessionsTool : HiddenTool {
    public override string Name => "sessions";
    public override string Description =>
        "List open documents and every attachment (diagnostics): path, shared rev, dirty, per-editor id/note/pending edits/stale marker. " +
        "Use when a file feels locked (open sessions hold it FileShare.Read — external writes fail) or before barging into a busy file.";
    public override string InputSchemaJson => """{"type":"object","properties":{},"required":[]}""";

    public override Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
        => Task.FromResult(Json(new SessionsResult {
            Sessions = [.. Sessions.Map.Values.OrderBy(a => a.Doc.Path).ThenBy(a => a.Id).Select(a => new SessionDto {
                SessionId = a.Id, Path = a.Doc.Path, Rev = a.Doc.Rev, Dirty = a.Doc.Dirty,
                TotalLines = a.Doc.TotalLines, Bytes = a.Doc.Bytes,
                Note = a.Note, PendingEdits = a.Doc.PendingEdits.GetValueOrDefault(a.Id), Stale = a.Stale,
            })],
        }, RpcJson.Default.SessionsResult));
}

public sealed class SessionsResult {
    public SessionDto[] Sessions { get; init; } = [];
}

public sealed class SessionDto {
    public string SessionId { get; init; } = "";
    public string Path { get; init; } = "";
    public int Rev { get; init; }
    public bool Dirty { get; init; }
    public int TotalLines { get; init; }
    public long Bytes { get; init; }
    public string? Note { get; init; }        // v0.4
    public int PendingEdits { get; init; }    // несохранённые правки этого подключения
    public bool Stale { get; init; }
}
