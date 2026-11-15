using System.Collections.Concurrent;

namespace SmartEdit.Mcp.Core;

/// <summary>Итог рендера карты: текст + счётчики для замков selftest.</summary>
public sealed record MapRender(string Text, int FilesTotal, int TypesShown, int TypesTotal, int SkippedDirs);

/// <summary>
/// #23 (v0.13.2): сборка project-map: файлы из ProjectScanner (инвентарь всегда полный) →
/// tree-sitter-экстракция (TreeSitterSymbols) с кэшем (path, mtime, size) → перескан только
/// изменившегося; save меняет mtime → кэш сам себя обновляет. Глобальный индекс простых имён
/// типов резолвит Bases в рёбра Type → Type(file:line).
/// </summary>
public static class MapBuilder {
    /// <summary>Выше — скелет не строим (инвентарь файла остаётся): гигантские генераты не стоят парсинга.</summary>
    public const long MaxMapBytes = 2L << 20;
    const int CacheCap = 1000;

    static readonly ConcurrentDictionary<string, CacheEntry> Cache = new();

    sealed record CacheEntry(DateTime Mtime, long Size, FileMap Map);

    public static MapRender Render(string root, int budgetTokens) {
        var scan = ProjectScanner.Scan(Path.GetFullPath(root), "*", 5000);
        var files = new List<(string Rel, int Lines, FileMap? Map)>(scan.TotalFiles);
        foreach (var g in scan.Groups)
            foreach (var f in g.Files)
                files.Add((f.Path.Replace('\\', '/'), f.Lines, GetMap(Path.Combine(scan.Root, f.Path))));

        // индекс простых имён → (rel, line): первый выигрывает (навигационная точность, не семантика)
        var index = new Dictionary<string, (string Rel, int Line)>(StringComparer.Ordinal);
        foreach (var (rel, _, map) in files) {
            if (map is null) continue;
            foreach (var t in map.Types)
                index.TryAdd(t.Name, (rel, t.Line));
        }

        int budgetChars = Math.Clamp(budgetTokens, 512, 8192) * 4; // токен ≈ 4 символа кода
        var r = MapRenderer.Render(files, index, budgetChars);
        return r with { SkippedDirs = scan.SkippedDirs };
    }

    /// <summary>Карта файла из кэша; mtime/size не совпали — перескан. Никогда не бросает.</summary>
    static FileMap? GetMap(string absPath) {
        if (!TreeSitterSymbols.Applies(absPath)) return null;
        try {
            var fi = new FileInfo(absPath);
            if (!fi.Exists || fi.Length > MaxMapBytes) return null;
            if (Cache.TryGetValue(absPath, out var hit) && hit.Mtime == fi.LastWriteTimeUtc && hit.Size == fi.Length)
                return hit.Map;
            string text = File.ReadAllText(absPath);
            var map = TreeSitterSymbols.Extract(absPath, text);
            if (Cache.Count > CacheCap) Cache.Clear(); // карта перечитывается за мс — проще снести, чем LRU
            Cache[absPath] = new CacheEntry(fi.LastWriteTimeUtc, fi.Length, map);
            return map;
        }
        catch (Exception ex) {
            Infra.Log.Warn($"map: {Path.GetFileName(absPath)} не прочитан: {ex.Message}");
            return null;
        }
    }

    /// <summary>Сброс записи кэша (например, после save из живой сессии).</summary>
    public static void Invalidate(string absPath) => Cache.TryRemove(absPath, out _);
}
