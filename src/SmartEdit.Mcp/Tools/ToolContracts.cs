using System.Text.Json;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

public interface ITool {
    string Name { get; }
    string Description { get; }
    /// <summary>JSON Schema входных аргументов (объект).</summary>
    string InputSchemaJson { get; }
    Task<string> ExecuteAsync(JsonElement args, CancellationToken ct);
}

public sealed class ToolRegistry {
    readonly Dictionary<string, ITool> _byName = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ITool tool) => _byName[tool.Name] = tool;

    public ToolsListResult ListTools() => new() {
        Tools = _byName.Values.Select(t => new ToolInfoDto {
            Name = t.Name,
            Description = t.Description,
            InputSchema = JsonDocument.Parse(t.InputSchemaJson).RootElement.Clone(),
        }).ToArray(),
    };

    public async Task<string> CallAsync(string name, JsonElement? args, CancellationToken ct) {
        if (!_byName.TryGetValue(name, out var tool))
            throw new KeyNotFoundException($"Неизвестный инструмент '{name}'. Доступны: {string.Join(", ", _byName.Keys)}");
        return await tool.ExecuteAsync(args ?? default, ct);
    }
}

public static class ArgsExtensions {
    public static string GetString(this JsonElement a, string name, string defaultValue = "") =>
        a.ValueKind == JsonValueKind.Object
        && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()! : defaultValue;

    public static string? GetNullableString(this JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object
        && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    public static int GetInt(this JsonElement a, string name, int defaultValue) =>
        a.ValueKind == JsonValueKind.Object
        && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n : defaultValue;

    public static int GetRequiredInt(this JsonElement a, string name) {
        if (a.ValueKind == JsonValueKind.Object
            && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
            return n;
        throw new ArgumentException($"missing required integer argument '{name}'");
    }

    public static bool GetBool(this JsonElement a, string name, bool defaultValue = false) {
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v)
            && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
            return v.ValueKind == JsonValueKind.True;
        return defaultValue;
    }
}
