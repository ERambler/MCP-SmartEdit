using System.Text.Json;
using System.Text.RegularExpressions;
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
