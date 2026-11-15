using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

sealed class RecipeSaveTool : ToolBase {
    public override string Name => "recipe_save";
    public override string Description =>
        "#8: persist a validated refactor as a named recipe — ordered steps {find, replace} with {{param}} placeholders. " +
        "Replay via recipe_apply(name, params, paths): each find must occur EXACTLY ONCE in every target (text anchors, no line numbers).";
    public override string InputSchemaJson => """
        {"type":"object","properties":{
          "name":{"type":"string","description":"[a-z0-9._-], ≤64 chars"},
          "steps":{"type":"array","minItems":1,"maxItems":50,"items":{"type":"object","properties":{
            "find":{"type":"string","description":"exact anchor text (may be multi-line, may contain {{param}})"},
            "replace":{"type":"string","description":"replacement, reuses {{param}}"} },"required":["find","replace"]}},
          "description":{"type":"string","description":"what this recipe does (shown in recipe_list)"}
        },"required":["name","steps"]}
        """;

    public override Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        string name = args.GetString("name", "").ToLowerInvariant();
        var steps = new List<Recipes.Step>();
        if (!args.TryGetProperty("steps", out var sj) || sj.ValueKind != JsonValueKind.Array)
            throw new ToolException("invalid_args", "steps array is required");
        foreach (var st in sj.EnumerateArray())
            steps.Add(new Recipes.Step(st.GetString("find"), st.GetString("replace")));
        var r = Recipes.Save(name, args.GetNullableString("description") ?? "", steps);
        return Task.FromResult(Json(new RecipeSaveResult {
            Name = r.Name, Steps = r.Steps.Count,
            Placeholders = [.. Recipes.CollectPlaceholders(r.Steps).OrderBy(x => x)],
            Path = Path.Combine(Recipes.Dir, r.Name + ".json"),
        }, RpcJson.Default.RecipeSaveResult));
    }
}

public sealed class RecipeSaveResult {
    public string Name { get; init; } = "";
    public int Steps { get; init; }
    public string[] Placeholders { get; init; } = [];   // {{param}} собранные из find/replace
    public string Path { get; init; } = "";             // где лежит JSON рецепта
}
