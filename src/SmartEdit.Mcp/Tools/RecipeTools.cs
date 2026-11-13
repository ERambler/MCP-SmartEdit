using System.Text.Json;
using System.Text.RegularExpressions;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Infra;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Infra;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

/// <summary>
/// #8 (v0.11): parameterized edit recipes — переиспользуемые проверенные рефакторинги.
/// Рецепт = упорядоченные шаги {find, replace} с плейсхолдерами {{param}}; replay = текстовые
/// якоря (уникальное вхождение каждого find), фаза поиска по ВСЕМ файлам до применения
/// (all-or-nothing), затем атомарный edit_batch на файл. Дивергенция — точный отчёт, ничего не применено.
/// </summary>
public static partial class Recipes {
    [GeneratedRegex(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}")]
    private static partial Regex Placeholder();

    static readonly Regex NameRx = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.Compiled);

    public sealed record Step(string Find, string Replace);
    public sealed record Recipe(string Name, string Description, string CreatedAt, List<Step> Steps);

    public static string Dir => Path.Combine(AppPaths.DataRoot, "recipes");

    public static HashSet<string> CollectPlaceholders(IEnumerable<Step> steps) =>
        [.. steps.SelectMany(s => Placeholder().Matches(s.Find + "\n" + s.Replace)).Select(m => m.Groups[1].Value)];

    /// <summary>Подставляет параметры; пустой плейсхолдер {{x}} запрещён (ловится CollectPlaceholders).</summary>
    public static string Resolve(string text, Dictionary<string, string> p) =>
        Placeholder().Replace(text, m => p.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    public static Recipe Load(string name) {
        if (!NameRx.IsMatch(name)) throw new ToolException("invalid_name", $"recipe name must match {NameRx}");
        string p = Path.Combine(Dir, name + ".json");
        if (!File.Exists(p)) throw new ToolException("recipe_not_found", $"no recipe '{name}' — recipe_list() shows what exists");
        return JsonSerializer.Deserialize<Recipe>(File.ReadAllText(p), RpcJson.Default.Recipe)
               ?? throw new ToolException("recipe_not_found", $"recipe '{name}' is corrupt (unparsable JSON)");
    }

    public static Recipe Save(string name, string description, List<Step> steps) {
        if (!NameRx.IsMatch(name)) throw new ToolException("invalid_name", $"recipe name must match {NameRx} (got '{name}')");
        if (steps.Count is 0 or > 50) throw new ToolException("invalid_args", "steps must contain 1..50 items");
        foreach (var (i, s) in steps.Index()) {
            if (s.Find.Length == 0) throw new ToolException("invalid_args", $"steps[{i}].find is empty");
            if (s.Find == s.Replace) throw new ToolException("invalid_args", $"steps[{i}]: find == replace (no-op step)");
        }
        Directory.CreateDirectory(Dir);
        var r = new Recipe(name, description, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm"), steps);
        File.WriteAllText(Path.Combine(Dir, name + ".json"), JsonSerializer.Serialize(r, RpcJson.Default.Recipe));
        return r;
    }

    public static List<(string Name, string Description, int Steps)> List() {
        Directory.CreateDirectory(Dir);
        return [.. Directory.EnumerateFiles(Dir, "*.json").Select(f => {
            try { var r = JsonSerializer.Deserialize<Recipe>(File.ReadAllText(f), RpcJson.Default.Recipe); return (r!.Name, r.Description, r.Steps.Count); }
            catch { return (Path.GetFileNameWithoutExtension(f), "(corrupt)", 0); }
        }).OrderBy(t => t.Item1)];
    }

    /// <summary>Локатор многострочного якоря: (startLine, endLine) уникального вхождения; null/счётчик при дивергенции.</summary>
    public static (int Start, int End)? LocateUnique(EditSession s, string needle, out int count) {
        count = 0;
        (int, int)? hit = null;
        if (needle.Length == 0 || s.TotalLines == 0) return null;
        var lines = new List<string>(s.TotalLines);
        var starts = new int[s.TotalLines + 1]; // смещения в склеенном тексте (по \n)
        int off = 0;
        for (int i = 1; i <= s.TotalLines; i++) {
            lines.Add(s.GetLineText(i));
            starts[i - 1] = off;
            off += s.GetLineText(i).Length + 1;
        }
        string hay = string.Join("\n", lines);
        int idx = hay.IndexOf(needle, StringComparison.Ordinal);
        while (idx >= 0) {
            count++;
            if (count == 1) {
                int li = Array.BinarySearch(starts, 0, s.TotalLines, idx);
                if (li < 0) li = ~li - 1;
                int endOff = idx + needle.Length - 1;
                int lj = Array.BinarySearch(starts, 0, s.TotalLines, endOff);
                if (lj < 0) lj = ~lj - 1;
                hit = (li + 1, lj + 1);
            }
            if (count > 1) break;
            idx = hay.IndexOf(needle, idx + 1, StringComparison.Ordinal);
        }
        return count == 1 ? hit : null;
    }
}

sealed class RecipeSaveTool : ITool {
    public string Name => "recipe_save";
    public string Description =>
        "#8: persist a validated refactor as a named recipe — ordered steps {find, replace} with {{param}} placeholders. " +
        "Replay via recipe_apply(name, params, paths): each find must occur EXACTLY ONCE in every target (text anchors, no line numbers).";
    public string InputSchemaJson => """
        {"type":"object","properties":{
          "name":{"type":"string","description":"[a-z0-9._-], ≤64 chars"},
          "steps":{"type":"array","minItems":1,"maxItems":50,"items":{"type":"object","properties":{
            "find":{"type":"string","description":"exact anchor text (may be multi-line, may contain {{param}})"},
            "replace":{"type":"string","description":"replacement, reuses {{param}}"} },"required":["find","replace"]}},
          "description":{"type":"string","description":"what this recipe does (shown in recipe_list)"}
        },"required":["name","steps"]}
        """;

    public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        string name = args.GetString("name", "").ToLowerInvariant();
        var steps = new List<Recipes.Step>();
        if (!args.TryGetProperty("steps", out var sj) || sj.ValueKind != JsonValueKind.Array)
            throw new ToolException("invalid_args", "steps array is required");
        foreach (var st in sj.EnumerateArray())
            steps.Add(new Recipes.Step(st.GetString("find"), st.GetString("replace")));
        var r = Recipes.Save(name, args.GetNullableString("description") ?? "", steps);
        return Task.FromResult(ToolOps.Json(new RecipeSaveResult {
            Name = r.Name, Steps = r.Steps.Count,
            Placeholders = [.. Recipes.CollectPlaceholders(r.Steps).OrderBy(x => x)],
            Path = Path.Combine(Recipes.Dir, r.Name + ".json"),
        }, RpcJson.Default.RecipeSaveResult));
    }
}

sealed class RecipeListTool : ITool {
    public string Name => "recipe_list";
    public string Description => "#8: list saved recipes (name, description, steps).";
    public string InputSchemaJson => """{"type":"object","properties":{},"required":[]}""";
    public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var list = Recipes.List();
        return Task.FromResult(ToolOps.Json(new RecipeListResult {
            Recipes = [.. list.Select(t => {
                var loaded = Recipes.Load(t.Item1);
                var ph = Recipes.CollectPlaceholders(loaded.Steps);
                return new RecipeDto { Name = t.Item1, Description = t.Item2, Steps = t.Item3, Placeholders = [.. ph.OrderBy(x => x)] };
            })],
        }, RpcJson.Default.RecipeListResult));
    }
}

sealed class RecipeApplyTool : ITool {
    public string Name => "recipe_apply";
    public string Description =>
        "#8: replay a recipe across 1..16 files atomically. Phase A locates EVERY step's anchor in EVERY file first; " +
        "any divergence (not_found/ambiguous/stale) → precise report {file, step, reason}, NOTHING applied. " +
        "Phase B applies per file (atomic edit_batch, RAM-staging — save(path) when happy, or save:true best-effort per file).";
    public string InputSchemaJson => """
        {"type":"object","properties":{
          "name":{"type":"string"},
          "paths":{"type":"array","minItems":1,"maxItems":16,"items":{"type":"string"}},
          "params":{"type":"object","description":"{{placeholder}} → string value; must cover every placeholder"},
          "dry_run":{"type":"boolean","description":"default false: locate + diffs only, nothing applied"},
          "save":{"type":"boolean","description":"default false (RAM-staging); true = save each file best-effort after apply"}
        },"required":["name","paths"]}
        """;

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
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
            var ops = await ToolOps.LockedAsync(s, () => {
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
            var outcome = await ToolOps.LockedAsync(s, () => s.EditBatch(ops, rev, dryRun, att.Id));
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
        return ToolOps.Json(new RecipeApplyResult {
            Name = name, DryRun = dryRun, Applied = files.Sum(f => f.Applied), Files = [.. files],
            UnsavedFiles = dryRun ? 0 : files.Count(f => f.Unsaved),
        }, RpcJson.Default.RecipeApplyResult);
    }
}
