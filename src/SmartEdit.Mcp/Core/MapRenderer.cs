using System.Text;

namespace SmartEdit.Mcp.Core;

/// <summary>
/// #23 (v0.13.2): рендер project-map в aider-скелет: каталоги первичны (как лежит репо, цепочки
/// одиночных подкаталогов сворачиваются — «src/SmartEdit.Mcp», а не «src» → «SmartEdit.Mcp»),
/// неймспейс чипом, якоря path:line = нумерации show/replace_lines. Инвентарь файлов всегда
/// полный; скелет — под бюджет токенов (≈4 символа на токен), хвост честный: «… +N types»
/// с подсказкой, как увидеть больше. using-рёбра (#23 фаза 1.5): строка «uses:» на каталог —
/// «кто кем питается» на уровне модулей.
/// </summary>
public static class MapRenderer {
    const int TopN = 6;

    public static MapRender Render(List<(string Rel, int Lines, FileMap? Map)> files,
        Dictionary<string, (string Rel, int Line)> typeIndex, int budgetChars) {
        var sb = new StringBuilder(4096);
        int typesShown = 0, skeletonChars = 0, typesTotal = 0;
        bool budgetCut = false;
        var langCounts = new Dictionary<string, int>();
        foreach (var f in files) {
            if (f.Map is not null) typesTotal += f.Map.Types.Count;
            string lang = f.Map?.Lang ?? LangOf(f.Rel);
            langCounts[lang] = langCounts.GetValueOrDefault(lang) + 1;
        }

        var chips = new List<string>(); // каталоги без кода — одной строкой внизу
        foreach (var cat in BuildCatalogs(files)) {
            bool hasCode = cat.Files.Any(f => f.Map is { Types.Count: > 0 });
            long ln = cat.Files.Sum(f => f.Lines);
            if (!hasCode) {
                chips.Add($"{Disp(cat)} ({cat.Files.Count}, {K(ln)} ln)");
                continue;
            }
            sb.AppendLine($"{Disp(cat)} ({cat.Files.Count} files, {K(ln)} ln)");
            foreach (var f in cat.Files.OrderByDescending(f => f.Lines)) {
                int firstLine = f.Map is { Types.Count: > 0 } ? f.Map.Types[0].Line : 0;
                string anchor = firstLine > 0 ? $"{f.Rel}:{firstLine}" : f.Rel;
                string nsChip = f.Map?.Namespace is { Length: > 0 } ns ? $"  ns:{ns}" : "";
                sb.AppendLine($"  {anchor} [{f.Lines}]{nsChip}");

                if (f.Map is not { Types.Count: > 0 } || budgetCut) continue;
                var block = RenderSkeleton(f.Map, typeIndex);
                if (skeletonChars + block.Length > budgetChars) { budgetCut = true; continue; }
                sb.Append(block);
                skeletonChars += block.Length;
                typesShown += f.Map.Types.Count;
            }
            AppendUses(sb, cat);
        }

        if (typesShown < typesTotal)
            sb.AppendLine($"… +{typesTotal - typesShown} types — raise budget_tokens (max 8192) or deep=false for the plain list");
        if (chips.Count > 0)
            sb.AppendLine(string.Join(" · ", chips));
        sb.AppendLine("langs: " + string.Join(" ", langCounts.OrderByDescending(p => p.Value).Take(6)
            .Select(p => $"{p.Key}={p.Value}")));
        return new MapRender(sb.ToString(), files.Count, typesShown, typesTotal, SkippedDirs: 0);
    }

    // ── скелет файла: типы с рёбрами наследования, топ-члены с doc ───────────

    static StringBuilder RenderSkeleton(FileMap map, Dictionary<string, (string Rel, int Line)> index) {
        var sb = new StringBuilder(256);
        foreach (var t in map.Types) {
            sb.Append("    ").Append(t.Kind).Append(' ').Append(t.Name);
            if (t.Bases.Count > 0) {
                sb.Append(" : ").Append(string.Join(", ", t.Bases.Select(b =>
                    index.TryGetValue(b, out var hit) ? $"{b}({hit.Rel}:{hit.Line})" : b)));
            }
            sb.AppendLine();
            foreach (var m in TopMembers(t.Members)) {
                sb.Append("      ").Append(m.Name);
                if (m.Args.Length > 0) sb.Append('(').Append(m.Args).Append(')');
                if (m.Doc is { Length: > 0 }) sb.Append(" — ").Append(m.Doc);
                sb.AppendLine();
            }
            if (t.Members.Count > TopN) sb.AppendLine($"      … +{t.Members.Count - TopN} more");
        }
        return sb;
    }

    /// <summary>Топ-N членов: методы/ctor/def важнее, с doc — важнее, дальше по порядку объявления (v1-ранжирование).</summary>
    static List<MapMember> TopMembers(List<MapMember> members) =>
        [.. members.OrderByDescending(m => m.Kind is "method" or "ctor" or "def" ? 1 : 0)
                   .ThenByDescending(m => m.Doc is null ? 0 : 1)
                   .ThenBy(m => m.Line)
                   .Take(TopN)];

    // ── using-рёбра (#23 фаза 1.5): «кто кем питается» ───────────────────────

    static void AppendUses(StringBuilder sb, Catalog cat) {
        var counts = new Dictionary<string, int>();
        foreach (var f in cat.Files) {
            if (f.Map is null) continue;
            foreach (var u in f.Map.Usings) {
                if (u.StartsWith("System") || u.StartsWith("Microsoft")) continue;
                counts[u] = counts.GetValueOrDefault(u) + 1;
            }
        }
        if (counts.Count == 0) return;
        var top = counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key).Take(3)
            .Select(p => $"{p.Key} ×{p.Value}");
        sb.AppendLine("  uses: " + string.Join(" · ", top));
    }

    // ── каталоги: дерево по сегментам пути + сворачивание одиночных цепочек ──

    sealed class Catalog {
        public required string Name; // полный относительный путь каталога
        public List<(string Rel, int Lines, FileMap? Map)> Files = [];
        public Dictionary<string, Catalog> Children = new(StringComparer.Ordinal); // ключ — имя ребёнка
    }

    static List<Catalog> BuildCatalogs(List<(string Rel, int Lines, FileMap? Map)> files) {
        var byDir = new Dictionary<string, Catalog>(StringComparer.Ordinal) { [""] = new Catalog { Name = "" } };

        Catalog Node(string dir) {
            if (byDir.TryGetValue(dir, out var n)) return n;
            var parent = Node(DirOf(dir));
            n = new Catalog { Name = dir };
            parent.Children[dir] = n;
            byDir[dir] = n;
            return n;
        }

        foreach (var f in files)
            Node(DirOf(f.Rel)).Files.Add(f);

        // свернуть: каталог без файлов с единственным ребёнком принимает его имя, файлы и внуков
        bool changed = true;
        while (changed) {
            changed = false;
            foreach (var node in byDir.Values.ToList()) {
                if (node.Files.Count != 0 || node.Children.Count != 1) continue;
                var (childName, child) = node.Children.First();
                node.Name = child.Name;
                node.Files.AddRange(child.Files);
                node.Children.Clear();
                foreach (var (grandName, grand) in child.Children) node.Children[grandName] = grand;
                byDir.Remove(childName);
                changed = true;
            }
        }

        var result = new List<Catalog>();
        void Collect(Catalog c) {
            if (c.Files.Count > 0) result.Add(c);
            foreach (var ch in c.Children.Values) Collect(ch);
        }
        Collect(byDir[""]);
        return [.. result.OrderByDescending(c => c.Files.Count)];
    }

    static string DirOf(string rel) => Path.GetDirectoryName(rel)!.Replace('\\', '/');

    /// <summary>Имя каталога для вывода: корень — «.», остальные с «/».</summary>
    static string Disp(Catalog c) => c.Name.Length == 0 ? "." : c.Name + "/";

    static string LangOf(string rel) {
        string e = Path.GetExtension(rel).TrimStart('.').ToLowerInvariant();
        return e.Length > 0 ? e : "noext";
    }

    static string K(long ln) => ln >= 1000 ? $"{ln / 1000.0:0.#}K" : ln.ToString();
}
