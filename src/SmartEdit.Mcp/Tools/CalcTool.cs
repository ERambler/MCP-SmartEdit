using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

sealed class CalcTool : HiddenTool {
    public override string Name => "calc";
    public override string Description =>
        "Exact calculator (LLMs are bad at arithmetic): + - * / % ^ (power, right-assoc), unary minus, postfix ! factorial, " +
        "hex 0x…, binary 0b…, scientific 1e-3, functions sin cos tan asin acos atan atan2 sqrt abs ln log log2 log10 exp " +
        "round floor ceil min max pow, constants pi e tau. Integer results also shown as hex.";
    public override string InputSchemaJson =>
        """{"type":"object","properties":{"expression":{"type":"string"}},"required":["expression"]}""";

    public override Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        string expr = args.GetString("expression");
        if (expr == "") throw new ArgumentException("expression is required");
        string value = Calc.Evaluate(expr);
        return Task.FromResult(Json(new CalcResult { Expr = expr, Value = value }, RpcJson.Default.CalcResult));
    }
}

public sealed class CalcResult {
    public string Expr { get; init; } = "";
    public string Value { get; init; } = "";
}
