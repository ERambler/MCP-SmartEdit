using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartEdit.Mcp.Rpc;

/// <summary>Входящее JSON-RPC 2.0 сообщение. Id == null означает notification (ответ не нужен).</summary>
public sealed class RpcRequest {
    [JsonPropertyName("jsonrpc")] public string? JsonRpc { get; set; }
    [JsonPropertyName("id")] public JsonElement? Id { get; set; }
    [JsonPropertyName("method")] public string? Method { get; set; }
    [JsonPropertyName("params")] public JsonElement? Params { get; set; }
}

public sealed class RpcResponse {
    [JsonPropertyName("jsonrpc")] public string JsonRpc => "2.0";
    // Never: id обязателен в ответе даже null (parse error без id в запросе, спека JSON-RPC 2.0)
    [JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public JsonElement? Id { get; set; }
    [JsonPropertyName("result")] public JsonElement? Result { get; set; }
    [JsonPropertyName("error")] public RpcError? Error { get; set; }
    // ВАЖНО: никаких посторонних свойств на верхнем уровне — клиенты (ZCode) валидируют
    // envelope JSON-RPC строго (zod .strict()), и лишний ключ роняет каждый ответ
    // (продакшн-урок SupportOnlineChat 0.3.1).
}

public sealed class RpcError {
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}
