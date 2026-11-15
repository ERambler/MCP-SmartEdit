using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

sealed class CheckTool : ToolBase {
    public override string Name => "check";
    public override string Description =>
        "Bracket balance {}[]() for code, <> for XML/HTML, on a line range (default: whole file). " +
        "Strings/comments/interpolation are excluded by a state machine (code inside {holes} of $\"…\" is kept); <> in C# is NOT checked (generics).";
    public override string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string","description":"from a previous response; optional if path given"},"path":{"type":"string","description":"open-by-path: skip open entirely"},
          "start":{"type":"integer"},
          "end":{"type":"integer"}
        },"required":[]}
        """;

    public override async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var s = Sessions.Resolve(args.GetNullableString("session_id"), args.GetNullableString("path")).Doc;
        int start = args.GetInt("start", 0);
        int end = args.GetInt("end", 0);
        var problems = await LockedAsync(s, () => s.CheckBrackets(start, end));
        return Json(new CheckResult { Problems = problems.ToArray() }, RpcJson.Default.CheckResult);
    }
}

public sealed class CheckResult {
    public string[] Problems { get; init; } = [];
}
