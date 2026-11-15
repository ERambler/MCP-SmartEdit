using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Infra;
using SmartEdit.Mcp.Tools;

namespace SmartEdit.Mcp.Rpc;

/// <summary>Диспетчер MCP-методов: initialize, ping, tools/list, tools/call.</summary>
public sealed class McpProtocolHandler(ToolRegistry tools) {
    public const string ServerVersion = "0.13.2";
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
            // v0.13.2: инструкции = обязательный жизненный цикл (тот же, что в skills/zcode-smartedit/SKILL.md;
            // держать синхронно). initialize отдаёт их КАЖДОМУ хосту — «скилл добавляется сам», ноль файлов.
            Instructions = "SmartEdit — line-based code editor; the disk is touched ONLY by save. THE LIFECYCLE: " +
                "(1) ORIENT: tree(path) first — deep project skeleton: directories, types with inheritance, " +
                "path:line anchors that plug straight into show/goto; budget_tokens caps it (default 2048). " +
                "Don't read files whole to learn a project. " +
                "(2) DRILL IN: goto(path, name), find(path, regex, context=2), show(path, start, end) — ranges, not whole files. " +
                "(3) NEW FILE: create(path, content) — RAM until first save, seed syntax-checked in the response. " +
                "(4) EDIT LADDER: replace_content (exact text, no line math) → edit_batch (2+ edits, atomic) → " +
                "replace_all (rename) → edit_transaction (multi-file, all-or-nothing). " +
                "(5) save(path) ALWAYS closes a series — syntax gate (.cs/.js/.ts/.tsx/.py/.sh) refuses NEW errors; " +
                "a response with unsaved:true means save before bash/git. " +
                "No rituals: no open (every tool takes path), no close (clean sessions self-evict). " +
                "Errors are JSON {kind,...}: stale_rev/anchor_mismatch/syntax_gate/disk_conflict/... — branch on kind. " +
                "help() (by name) maps every tool and error → exact next action.",
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
            return (Elem(WithFirstCallHint(ToolResult(text, isError: false), name), RpcJson.Default.CallToolResult), null);
        }
        catch (ToolException tex) {
            // ошибка инструмента = result с isError=true (спека MCP); текст — МАШИННЫЙ JSON
            Log.Error($"tool {name}{ArgsBrief(args)} → {tex.Kind}: {tex.Message}");
            return (Elem(ToolResult(JsonSerializer.Serialize(
                new ToolErrorPayload {
                    Kind = tex.Kind, Message = tex.Message, CurrentRev = tex.CurrentRev, TotalLines = tex.TotalLines,
                    RetryHint = tex.RetryHint, CurrentText = tex.CurrentText,
                    SessionId = args?.TryGetProperty("session_id", out var sid) == true && sid.ValueKind == JsonValueKind.String ? sid.GetString() : null,
                    Retry = tex.RetryStrategy is null ? null : new RetryDto { Strategy = tex.RetryStrategy, Start = tex.RetryStart, End = tex.RetryEnd },
                    ServerVersion = ServerVersion, // v0.9.3: версия в каждой ошибке
                },
                RpcJson.Default.ToolErrorPayload), isError: true), RpcJson.Default.CallToolResult), null);
        }
        catch (Exception ex) {
            Log.Error($"tool {name}{ArgsBrief(args)} → ошибка: {ex.Message}");
            return (Elem(ToolResult(JsonSerializer.Serialize(
                new ToolErrorPayload { Kind = "error", Message = ex.Message, ServerVersion = ServerVersion },
                RpcJson.Default.ToolErrorPayload), isError: true), RpcJson.Default.CallToolResult), null);
        }
    }

    static int _toolCalls; // счётчик для однократного приглашения help (v0.8.1)

    /// <summary>Первый tools/call в жизни процесса (если это не сам help) получает вторую
    /// content-строку с приглашением — агент гарантированно увидит, дальше тишина.</summary>
    static CallToolResult WithFirstCallHint(CallToolResult r, string toolName) {
        if (Interlocked.Increment(ref _toolCalls) != 1 || toolName == "help") return r;
        r.Content = [.. r.Content, new TextContentDto {
            Text = "SmartEdit tip (shown once): tree(path) renders the project skeleton FIRST — types + inheritance + path:line anchors that plug straight into show/replace_lines. help() has the full tool map + every error kind → exact action.",
        }];
        return r;
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
