using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartEdit.Mcp.Rpc;

// Все DTO результата протокола и инструментов. Под Native AOT рефлексионная сериализация
// недоступна — каждый тип явно перечислен в RpcJson (JsonSerializerContext), анонимные
// объекты запрещены. Имена полей в JSON — camelCase через naming policy контекста.

public sealed class InitializeResult {
    public string ProtocolVersion { get; set; } = "";
    public ServerCapabilitiesDto Capabilities { get; set; } = new();
    public ServerInfoDto ServerInfo { get; set; } = new();
    public string? Instructions { get; set; }
}

public sealed class ServerCapabilitiesDto {
    public ToolsCapabilityDto Tools { get; set; } = new() { ListChanged = false };
}

public sealed class ToolsCapabilityDto {
    public bool ListChanged { get; set; }
}

public sealed class ServerInfoDto {
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
}

public sealed class ToolsListResult {
    public ToolInfoDto[] Tools { get; set; } = [];
}

public sealed class ToolInfoDto {
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public JsonElement InputSchema { get; set; }
}

public sealed class CallToolResult {
    public TextContentDto[] Content { get; set; } = [];
    public bool IsError { get; set; }
}

public sealed class TextContentDto {
    public string Type => "text";
    public string Text { get; set; } = "";
}
