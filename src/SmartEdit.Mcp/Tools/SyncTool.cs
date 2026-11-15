using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

/// <summary>v0.4: осознанная синхронизация перед большой правкой: peers + события с since_rev.</summary>
sealed class SyncTool : HiddenTool {
    public override string Name => "sync";
    public override string Description =>
        "Explicit collaboration sync: returns current rev, peers (id, note, pending edits, stale) and compact edit events since since_rev " +
        "(default: your last seen). Call before a big edit in a shared file to see who did what. No state change besides your last_seen.";
    public override string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string","description":"from a previous response; optional if path given"},"path":{"type":"string","description":"open-by-path: skip open entirely"},
          "since_rev":{"type":"integer","description":"default: your attachment's last seen rev"}
        },"required":[]}
        """;

    public async override Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var att = Sessions.Resolve(args.GetNullableString("session_id"), args.GetNullableString("path"));
        var s = att.Doc;
        Sessions.Touch(att);
        return await LockedAsync(s, () => {
            if (args.TryGetProperty("since_rev", out var sr) && sr.ValueKind == JsonValueKind.Number && sr.TryGetInt32(out var r))
                att.LastSeenRev = Math.Min(r, s.Rev);
            string[] events = Presence.EventsSince(s, att) ?? [];
            return Json(new SyncResult {
                Rev = s.Rev, TotalLines = s.TotalLines, Events = events,
                Peers = [.. Sessions.Peers(s).Select(p => new PeerDto {
                    SessionId = p.Id, Note = p.Note, Stale = p.Stale,
                    PendingEdits = s.PendingEdits.GetValueOrDefault(p.Id),
                })],
            }, RpcJson.Default.SyncResult);
        });
    }
}

public sealed class SyncResult {
    public int Rev { get; init; }
    public int TotalLines { get; init; }
    public string[] Events { get; init; } = [];
    public PeerDto[] Peers { get; init; } = [];
}
