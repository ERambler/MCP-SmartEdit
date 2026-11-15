using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

sealed class CloseTool : ToolBase {
    public override string Name => "close";
    public override string Description => "Close a session. discard=true drops unsaved edits (otherwise it refuses while dirty).";
    public override string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string","description":"from a previous response; optional if path given"},"path":{"type":"string","description":"open-by-path: skip open entirely"},
          "discard":{"type":"boolean","description":"default false"}
        },"required":[]}
        """;

    public override async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        // v0.9: close по пути тоже (Resolve): гасит path-attachment этого документа
        var att = Sessions.Resolve(args.GetNullableString("session_id"), args.GetNullableString("path"));
        string id = att.Id;
        bool discard = args.GetBool("discard");
        var doc = att.Doc;
        // detach: не последний — просто отключение (документ жив у peers); последний — гашение документа
        var (wasDirty, disposed, shadow) = await LockedAsync(doc, () => Sessions.Detach(id, discard));
        if (disposed) doc.Dispose(); // семафор гасим уже после Release
        return Json(new CloseResult {
            Closed = true, Dirty = wasDirty,
            // dirty+discard последним закрывающим оставил shadow — несохранённое можно вернуть копированием поверх
            Shadow = shadow,
        }, RpcJson.Default.CloseResult);
    }
}

public sealed class CloseResult {
    public bool Closed { get; init; }
    public bool Dirty { get; init; }
    public string? Shadow { get; init; } // путь к shadow, если discard грязной сессии оставил страховку на диске
}
