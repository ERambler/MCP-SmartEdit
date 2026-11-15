using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

sealed class TreeTool : ToolBase {
    public override string Name => "tree";
    public override string Description =>
        "Use this FIRST to understand project structure/architecture. deep=true (default): ONE token-budgeted aider-style " +
        "skeleton — types with inheritance edges (Base resolved to file:line), top members with doc lines, exact path:line anchors " +
        "(same numbering as show/replace_lines — saw it = edit it), catalogs as the repo lies + langs header. " +
        "Do NOT use it to read file bodies — show() is for that. Auto-falls-back to the flat list when pattern/recent_hours given " +
        "(point query — skeleton not needed): files (relative paths, lines, bytes), C# grouped by namespace, skips bin/obj/.git/node_modules/.vs/packages/build/dist.";
    public override string InputSchemaJson => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"directory to scan"},
          "deep":{"type":"boolean","description":"default true: skeleton + inheritance + path:line anchors; false = flat file list"},
          "budget_tokens":{"type":"integer","description":"skeleton token budget, default 2048 (512..8192); the file inventory is always complete"},
          "pattern":{"type":"string","description":"file glob filter, default all; non-default disables deep"},
          "max_files":{"type":"integer","description":"default 1500"},
          "recent_hours":{"type":"integer","description":"only files modified within last N hours (flat, newest first); disables deep"}
        },"required":["path"]}
        """;

    public override async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        await Task.CompletedTask; // CPU-bound, без асинхронных ожиданий
        string root = args.GetString("path");
        if (root == "" || !Directory.Exists(root))
            throw new DirectoryNotFoundException($"directory not found: {root}");
        root = System.IO.Path.GetFullPath(root);
        string pattern = args.GetString("pattern", "*");
        int maxFiles = Math.Clamp(args.GetInt("max_files", 1500), 1, 5000);
        int recentHours = Math.Clamp(args.GetInt("recent_hours", 0), 0, 24 * 90);
        // #23: авто-откат deep при точечном запросе (pattern/recent_hours) — скелет не нужен
        bool deep = args.GetBool("deep", true) && pattern == "*" && recentHours == 0;

        if (deep) {
            int budgetTokens = Math.Clamp(args.GetInt("budget_tokens", 2048), 512, 8192);
            var map = MapBuilder.Render(root, budgetTokens);
            return Json(new TreeResult {
                Root = root,
                TotalFiles = map.FilesTotal,
                ListedFiles = map.FilesTotal,
                SkippedDirs = map.SkippedDirs,
                TotalBytes = 0,
                Deep = true,
                Map = map.Text,
            }, RpcJson.Default.TreeResult);
        }

        var scan = ProjectScanner.Scan(root, pattern, maxFiles, recentHours);
        return Json(new TreeResult {
            Root = scan.Root,
            TotalFiles = scan.TotalFiles,
            ListedFiles = scan.TotalFiles,
            SkippedDirs = scan.SkippedDirs,
            TotalBytes = scan.TotalBytes,
            Deep = false,
            Groups = scan.Groups.Select(g => new TreeGroupDto {
                Namespace = g.Namespace,
                Files = g.Files.Select(f => new TreeFileDto { Path = f.Path, Lines = f.Lines, Bytes = f.Bytes, ModifiedUtc = f.ModifiedUtc }).ToArray(),
            }).ToArray(),
        }, RpcJson.Default.TreeResult);
    }
}

public sealed class TreeResult {
    public string Root { get; init; } = "";
    public int TotalFiles { get; init; }
    public int ListedFiles { get; init; }
    public int SkippedDirs { get; init; }
    public long TotalBytes { get; init; }
    public TreeGroupDto[] Groups { get; init; } = [];
    /// <summary>#23 (v0.13.2): фактический режим — deep авто-откатывается при pattern/recent_hours.</summary>
    public bool Deep { get; init; }
    /// <summary>Скелет-карта (deep=true): каталоги, типы с рёбрами наследования, path:line якоря.</summary>
    public string? Map { get; init; }
}

public sealed class TreeGroupDto {
    public string Namespace { get; init; } = ""; // "" = без namespace / не C#
    public TreeFileDto[] Files { get; init; } = [];
}
public sealed class TreeFileDto {
    public string Path { get; init; } = "";
    public int Lines { get; init; }
    public long Bytes { get; init; }
    public DateTime? ModifiedUtc { get; init; } // заполняется только в recent-режиме tree
}
