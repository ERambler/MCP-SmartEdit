using System.Text.Json.Serialization;
using SmartEdit.Mcp.Tools;

namespace SmartEdit.Mcp.Rpc;

/// <summary>
/// Source-generated сериализация — единственный вариант под Native AOT (рефлексия
/// обрезается). Каждый сериализуемый тип обязан быть перечислен здесь явно.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(RpcRequest))]
[JsonSerializable(typeof(RpcResponse))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(ToolsListResult))]
[JsonSerializable(typeof(CallToolResult))]
[JsonSerializable(typeof(EmptyResult))]
[JsonSerializable(typeof(Tools.OpenResult))]
[JsonSerializable(typeof(Tools.ShowResult))]
[JsonSerializable(typeof(Tools.FindResult))]
[JsonSerializable(typeof(Tools.MutationResult))]
[JsonSerializable(typeof(Tools.SaveResult))]
[JsonSerializable(typeof(Tools.CloseResult))]
[JsonSerializable(typeof(Tools.SymbolsResult))]
[JsonSerializable(typeof(Tools.SymbolDto))]
[JsonSerializable(typeof(Tools.TreeResult))]
[JsonSerializable(typeof(Tools.TreeGroupDto))]
[JsonSerializable(typeof(Tools.TreeFileDto))]
[JsonSerializable(typeof(Tools.FileInfoResult))]
[JsonSerializable(typeof(Tools.ConvertResult))]
[JsonSerializable(typeof(Tools.CalcResult))]
[JsonSerializable(typeof(Tools.CheckResult))]
[JsonSerializable(typeof(Tools.LogResult))]
[JsonSerializable(typeof(Tools.GotoResult))]
[JsonSerializable(typeof(Tools.BatchResult))]
[JsonSerializable(typeof(Tools.ReplaceAllResult))]
[JsonSerializable(typeof(ToolErrorPayload))]
[JsonSerializable(typeof(Tools.SessionsResult))]
[JsonSerializable(typeof(Tools.SessionDto))]
[JsonSerializable(typeof(Tools.PeerDto))]
[JsonSerializable(typeof(Tools.SyncResult))]
public sealed partial class RpcJson : JsonSerializerContext { }

public sealed class EmptyResult { }

/// <summary>Структурированная ошибка инструмента: текст ответа с isError=true — этот JSON.</summary>
public sealed class ToolErrorPayload {
    public string Kind { get; set; } = "";
    public string Message { get; set; } = "";
    public int? CurrentRev { get; set; }
    public int? TotalLines { get; set; }
}
