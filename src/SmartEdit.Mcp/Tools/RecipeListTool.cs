using System.Text.Json;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

sealed class RecipeListTool : ToolBase {
    public override string Name => "recipe_list";
    public override string Description => "#8: list saved recipes (name, description, steps).";
    public override string InputSchemaJson => """{"type":"object","properties":{},"required":[]}""";
    public override Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var list = Recipes.List();
        return Task.FromResult(Json(new RecipeListResult {
            Recipes = [.. list.Select(t => {
                var loaded = Recipes.Load(t.Item1);
                var ph = Recipes.CollectPlaceholders(loaded.Steps);
                return new RecipeDto { Name = t.Item1, Description = t.Item2, Steps = t.Item3, Placeholders = [.. ph.OrderBy(x => x)] };
            })],
        }, RpcJson.Default.RecipeListResult));
    }
}

public sealed class RecipeDto {
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public int Steps { get; init; }
    public string[] Placeholders { get; init; } = [];
}

public sealed class RecipeListResult {
    public RecipeDto[] Recipes { get; init; } = [];
}
