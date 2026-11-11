using System.Text.RegularExpressions;

namespace SmartEdit.Mcp.Core;

public sealed record ProjectFile(string Path, int Lines, long Bytes, DateTime? ModifiedUtc = null);
public sealed record ProjectGroup(string Namespace, List<ProjectFile> Files);
public sealed record ProjectScanResult(string Root, int TotalFiles, int SkippedDirs, long TotalBytes, List<ProjectGroup> Groups);

/// <summary>
/// Карта каталога-проекта: BFS-обход (bin/obj/.git/node_modules/… пропускаются), для .cs —
/// namespace из первых 300 строк, количество строк — потоковый счёт 0x0A (конвенция LineScanner).
/// recentHours &gt; 0 — режим «недавно изменённые»: только файлы новее cutoff, плоский список
/// (без namespace-групп), сортировка по mtime по убыванию, заполнен ModifiedUtc.
/// Вынесено из tree-инструмента: Core не знает про сериализацию.
/// </summary>
public static partial class ProjectScanner {
    public static ProjectScanResult Scan(string root, string pattern, int maxFiles, int recentHours = 0) {
        bool recent = recentHours > 0;
        DateTime cutoff = recent ? DateTime.UtcNow.AddHours(-recentHours) : DateTime.MaxValue;

        var files = new List<(string Rel, string Abs, long Bytes, DateTime Mtime)>();
        int skippedDirs = 0;
        var queue = new Queue<string>();
        queue.Enqueue(root);
        while (queue.Count > 0 && files.Count < maxFiles) {
            string dir = queue.Dequeue();
            foreach (var sub in Directory.EnumerateDirectories(dir)) {
                string name = Path.GetFileName(sub);
                if (name is "bin" or "obj" or ".git" or "node_modules" or ".vs" or "packages" or "build" or "dist" or ".idea" or "__pycache__" or ".venv" or "venv") {
                    skippedDirs++;
                    continue;
                }
                queue.Enqueue(sub);
            }
            foreach (var f in Directory.EnumerateFiles(dir, pattern)) {
                var fi = new FileInfo(f);
                if ((fi.Attributes & FileAttributes.Hidden) != 0) continue;
                if (recent && fi.LastWriteTimeUtc < cutoff) continue;
                files.Add((Path.GetRelativePath(root, f), f, fi.Length, fi.LastWriteTimeUtc));
                if (files.Count >= maxFiles) break;
            }
        }

        long totalBytes = 0;
        foreach (var f in files) totalBytes += f.Bytes;

        if (recent) {
            var list = files.OrderByDescending(f => f.Mtime)
                .Select(f => new ProjectFile(f.Rel, CountLinesFast(f.Abs, f.Bytes), f.Bytes, f.Mtime))
                .ToList();
            return new ProjectScanResult(root, files.Count, skippedDirs, totalBytes, [new ProjectGroup("", list)]);
        }

        var groups = new Dictionary<string, List<ProjectFile>>();
        foreach (var (rel, abs, bytes, _) in files) {
            string ns = "";
            if (abs.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) ns = ReadNamespace(abs) ?? "";
            groups.TryAdd(ns, []);
            groups[ns].Add(new ProjectFile(rel, CountLinesFast(abs, bytes), bytes));
        }

        return new ProjectScanResult(root, files.Count, skippedDirs, totalBytes,
            groups.OrderByDescending(g => g.Key.Length == 0).ThenBy(g => g.Key)
                .Select(g => new ProjectGroup(g.Key, g.Value.OrderBy(f => f.Path).ToList()))
                .ToList());
    }

    static string? ReadNamespace(string path) {
        try {
            using var sr = new StreamReader(path);
            for (int i = 0; i < 300; i++) {
                string? line = sr.ReadLine();
                if (line is null) break;
                var m = NamespaceRegex().Match(line);
                if (m.Success) return m.Groups[1].Value;
            }
        }
        catch (IOException) { }
        return null;
    }

    static int CountLinesFast(string path, long bytes) {
        if (bytes == 0) return 0;
        int lines = 0;
        byte last = 0;
        try {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16);
            byte[] buf = new byte[1 << 20];
            int n;
            while ((n = fs.Read(buf)) > 0) {
                for (int i = 0; i < n; i++) if (buf[i] == 0x0A) lines++;
                last = buf[n - 1];
            }
            if (last != 0x0A) lines++; // хвост без завершающего перевода (конвенция LineScanner)
        }
        catch (IOException) { return 0; }
        return lines;
    }

    [GeneratedRegex(@"^\s*namespace\s+([\w.]+)")]
    private static partial Regex NamespaceRegex();
}
