using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

sealed class RecipeApplyTool : ToolBase {
    public override string Name => "recipe_apply";
    public override string Description =>
        "#8: replay a recipe across 1..16 files atomically. Phase A locates EVERY step's anchor in EVERY file first; " +
        "any divergence (not_found/ambiguous/stale) → precise report {file, step, reason}, NOTHING applied. " +
        "Phase B applies per file (atomic edit_batch, RAM-staging — save(path) when happy, or save:true best-effort per file).";
    public override string InputSchemaJson => """
        {"type":"object","properties":{
          "name":{"type":"string"},
          "paths":{"type":"array","minItems":1,"maxItems":16,"items":{"type":"string"}},
          "params":{"type":"object","description":"{{placeholder}} → string value; must cover every placeholder"},
          "dry_run":{"type":"boolean","description":"default false: locate + diffs only, nothing applied"},
          "save":{"type":"boolean","description":"default false (RAM-staging); true = save each file best-effort after apply"}
        },"required":["name","paths"]}
        """;

    public override async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        string name = args.GetString("name", "");
        var recipe = Recipes.Load(name);
        var paths = new List<string>();
        if (!args.TryGetProperty("paths", out var pj2) || pj2.ValueKind != JsonValueKind.Array)
            throw new ToolException("invalid_args", "paths array is required");
        foreach (var e in pj2.EnumerateArray()) paths.Add(e.GetString()!);
        bool dryRun = args.GetBool("dry_run");
        bool save = args.GetBool("save");

        var p = new Dictionary<string, string>();
        if (args.TryGetProperty("params", out var pj) && pj.ValueKind == JsonValueKind.Object)
            foreach (var kv in pj.EnumerateObject())
                p[kv.Name] = kv.Value.ValueKind == JsonValueKind.String ? (kv.Value.GetString() ?? "") : kv.Value.GetRawText();
        var missing = Recipes.CollectPlaceholders(recipe.Steps).Where(k => !p.ContainsKey(k)).ToList();
        if (missing.Count > 0)
            throw new ToolException("missing_params", $"recipe '{name}' needs params: {string.Join(", ", missing.OrderBy(x => x))}");

        // Фаза А: найти якоря всех шагов во всех файлах ДО применения (all-or-nothing)
        var plan = new List<(Attachment Att, List<Core.BatchOp> Ops, int Rev)>();
        foreach (var path in paths) {
            var att = Sessions.Resolve(null, path);
            var s = att.Doc;
            var ops = await LockedAsync(s, () => {
                var acc = new List<Core.BatchOp>();
                foreach (var (i, step) in recipe.Steps.Index()) {
                    var (start, end) = Recipes.LocateUnique(s, Recipes.Resolve(step.Find, p), out int count) ?? (0, 0);
                    if (count == 0)
                        throw new ToolException("recipe_diverged", $"FILE={path} STEP={i} not_found — anchor absent; fix the file or the recipe");
                    if (count > 1)
                        throw new ToolException("recipe_diverged", $"FILE={path} STEP={i} ambiguous — anchor occurs {count}×; extend find to be unique");
                    acc.Add(new Core.BatchOp(start, end, Recipes.Resolve(step.Replace, p), null));
                }
                return acc;
            });
            plan.Add((att, ops, att.LastSeenRev));
        }

        // Фаза Б: применить (per-file атомарно; RAM-staging)
        var files = new List<RecipeFileDto>();
        foreach (var (att, ops, rev) in plan) {
            var s = att.Doc;
            var outcome = await LockedAsync(s, () => s.EditBatch(ops, rev, dryRun, att.Id));
            att.LastSeenRev = outcome.Rev;
            var f = new RecipeFileDto {
                Path = s.Path, Rev = outcome.Rev, TotalLines = outcome.TotalLines, Applied = outcome.Applied,
                Diffs = outcome.Diffs, Unsaved = s.Dirty,
            };
            if (save && !dryRun) {
                try { s.Save(false, "keep", false, att.Id); f.SavedOk = true; }
                catch (ToolException ex) { f.SavedOk = false; f.SaveError = $"{ex.Kind}: {ex.Message}"; }
            }
            files.Add(f);
        }
        return Json(new RecipeApplyResult {
            Name = name, DryRun = dryRun, Applied = files.Sum(f => f.Applied), Files = [.. files],
            UnsavedFiles = dryRun ? 0 : files.Count(f => f.Unsaved),
        }, RpcJson.Default.RecipeApplyResult);
    }
}

public sealed class RecipeFileDto {
    public string Path { get; init; } = "";
    public int Rev { get; init; }
    public int TotalLines { get; init; }
    public int Applied { get; init; }
    public List<string> Diffs { get; init; } = [];
    public bool Unsaved { get; init; }                  // RAM-staging: save(path) когда доволен
    public bool? SavedOk { get; set; }                 // save:true → статус (null = save не запрашивался); мутируется после save
    public string? SaveError { get; set; }
}

public sealed class RecipeApplyResult {
    public string Name { get; init; } = "";
    public bool DryRun { get; init; }
    public int Applied { get; init; }                   // шагов применено (0 при diverged)
    public RecipeFileDto[] Files { get; init; } = [];
    public int UnsavedFiles { get; init; }
    /// <summary>Не null, когда якорь рецепта разошёлся с файлом: {file, step, reason, detail}. Ничего не применено.</summary>
    public RecipeDivergedDto? Diverged { get; init; }
}

public sealed class RecipeDivergedDto {
    public string File { get; init; } = "";
    public int Step { get; init; }                      // индекс шага (0-based)
    public string Reason { get; init; } = "";           // not_found | ambiguous | stale
    public string Detail { get; init; } = "";
}
