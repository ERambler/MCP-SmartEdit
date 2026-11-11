using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Infra;
using SmartEdit.Mcp.Tools;

namespace SmartEdit.Mcp.Rpc;

/// <summary>Диспетчер MCP-методов: initialize, ping, tools/list, tools/call.</summary>
public sealed class McpProtocolHandler(ToolRegistry tools) {
    public const string ServerVersion = "0.8.0";
    public const string ProtocolVersion = "2025-06-18";

    /// <summary>
    /// Телеметрия — строки «tool … → ok» с краткой подписью аргументов. Отключается
    /// переменной окружения SMARTEDIT_TELEMETRY=off (также 0/false). Ошибки остаются: это диагностика, не телеметрия.
    /// </summary>
    static readonly bool TelemetryEnabled =
        Environment.GetEnvironmentVariable("SMARTEDIT_TELEMETRY") is not { } t
        || !(t.Equals("off", StringComparison.OrdinalIgnoreCase) || t == "0" || t.Equals("false", StringComparison.OrdinalIgnoreCase));

    public async Task<(JsonElement? Result, RpcError? Error)> HandleAsync(RpcRequest req) {
        switch (req.Method) {
            case "initialize":
                return (Elem(Initialize(req), RpcJson.Default.InitializeResult), null);
            case "ping":
                return (Elem(new EmptyResult(), RpcJson.Default.EmptyResult), null);
            case "tools/list":
                return (Elem(tools.ListTools(), RpcJson.Default.ToolsListResult), null);
            case "tools/call":
                return await CallToolAsync(req);
            default:
                // notifications/initialized, notifications/cancelled и прочие уведомления молча игнорируем
                if (req.Id is null) return (null, null);
                return (null, new RpcError { Code = -32601, Message = $"Method not found: {req.Method}" });
        }
    }

    static InitializeResult Initialize(RpcRequest req) {
        // отвечаем версией протокола клиента, если она строка; иначе нашей последней
        string? clientVersion = null;
        if (req.Params is { ValueKind: JsonValueKind.Object } p
            && p.TryGetProperty("protocolVersion", out var pv)
            && pv.ValueKind == JsonValueKind.String)
            clientVersion = pv.GetString();
        return new InitializeResult {
            ProtocolVersion = clientVersion ?? ProtocolVersion,
            ServerInfo = new ServerInfoDto { Name = "smartedit", Version = ServerVersion },
            Instructions = "Line-based text editor + code structure vision. open(path) → session_id, rev, usings, outline. " +
                "symbols(session_id) → code structure with line numbers; docs_only=true → documented only. " +
                "show(start,end) → numbered lines; find(regex, context=N) → line numbers + snippets + breadcrumbs. " +
                "Mutations: replace_lines/insert_after (rev guard, dry_run, optional expect_old anchor), " +
                "edit_batch (atomic multi-edit, one rev bump, bottom-up — no shift math), " +
                "replace_all (literal rename across the file). Errors come as JSON {kind, message, current_rev?}: " +
                "stale_rev/anchor_mismatch/invalid_range/batch_overlap/too_large/... — branch on kind, not prose. " +
                "save() keeps the original encoding, atomic with backup; rev does NOT change on save. " +
                "check() → bracket audit (full file up to 200k lines). " +
                "tree(dir) → project map (recent_hours=N → recently modified only). file_info/convert/calc as utilities.",
        };
    }

    async Task<(JsonElement? Result, RpcError? Error)> CallToolAsync(RpcRequest req) {
        if (req.Params is not { ValueKind: JsonValueKind.Object } ps
            || !ps.TryGetProperty("name", out var nameEl)
            || nameEl.ValueKind != JsonValueKind.String)
            return (null, new RpcError { Code = -32602, Message = "Invalid params for tools/call" });

        var args = ps.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object ? a : (JsonElement?)null;
        string name = nameEl.GetString()!;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try {
            string text = await tools.CallAsync(name, args, CancellationToken.None);
            if (TelemetryEnabled) Log.Info($"tool {name}{ArgsBrief(args)} → ok за {sw.Elapsed.TotalMilliseconds:0}мс");
            return (Elem(ToolResult(text, isError: false), RpcJson.Default.CallToolResult), null);
        }
        catch (ToolException tex) {
            // ошибка инструмента = result с isError=true (спека MCP); текст — МАШИННЫЙ JSON
            Log.Error($"tool {name}{ArgsBrief(args)} → {tex.Kind}: {tex.Message}");
            return (Elem(ToolResult(JsonSerializer.Serialize(
                new ToolErrorPayload { Kind = tex.Kind, Message = tex.Message, CurrentRev = tex.CurrentRev, TotalLines = tex.TotalLines, RetryHint = tex.RetryHint, CurrentText = tex.CurrentText },
                RpcJson.Default.ToolErrorPayload), isError: true), RpcJson.Default.CallToolResult), null);
        }
        catch (Exception ex) {
            Log.Error($"tool {name}{ArgsBrief(args)} → ошибка: {ex.Message}");
            return (Elem(ToolResult(JsonSerializer.Serialize(
                new ToolErrorPayload { Kind = "error", Message = ex.Message },
                RpcJson.Default.ToolErrorPayload), isError: true), RpcJson.Default.CallToolResult), null);
        }
    }

    static CallToolResult ToolResult(string text, bool isError) => new() {
        Content = [new TextContentDto { Text = text }],
        IsError = isError,
    };

    static JsonElement Elem<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.SerializeToElement(value, typeInfo);

    /// <summary>
    /// Краткая подпись аргументов для журнала (телеметрия): session_id + 2-3 скаляра.
    /// Тексты правок/пути целиком НЕ пишутся — только имя файла и счётчики.
    /// </summary>
    static string ArgsBrief(JsonElement? args) {
        if (args is not { ValueKind: JsonValueKind.Object } a) return "";
        var parts = new List<string>();
        if (a.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String)
            parts.Add(sid.GetString()![..Math.Min(4, sid.GetString()!.Length)]);
        if (a.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
            parts.Add(System.IO.Path.GetFileName(p.GetString()!));
        foreach (var key in new[] { "start", "end", "line", "n", "edits", "tail" }) {
            if (a.TryGetProperty(key, out var v)) {
                if (v.ValueKind == JsonValueKind.Array) { parts.Add($"{key}×{v.GetArrayLength()}"); break; }
                if (v.ValueKind is JsonValueKind.Number) { parts.Add(v.ToString()); break; }
            }
        }
        foreach (var flag in new[] { "expect_old", "dry_run", "note", "force" })
            if (a.TryGetProperty(flag, out _)) parts.Add(flag);
        return parts.Count > 0 ? "(" + string.Join(" ", parts) + ")" : "";
    }
}
