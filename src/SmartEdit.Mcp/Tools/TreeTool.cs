using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

sealed class TreeTool : ToolBase {
    public override string Name => "tree";
    public override string Description =>
        "Project map for a directory: files (relative paths, lines, bytes), C# files grouped by namespace. " +
        "Skips bin/obj/.git/node_modules/.vs/packages/build/dist. pattern = optional file glob (e.g. '*.cs'). " +
        "recent_hours=N → only files modified within the last N hours: flat list, newest first, with modified_utc.";
    public override string InputSchemaJson => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"directory to scan"},
          "pattern":{"type":"string","description":"file glob filter, default all"},
          "max_files":{"type":"integer","description":"default 1500"},
          "recent_hours":{"type":"integer","description":"only files modified within last N hours (flat, newest first)"}
        },"required":["path"]}
        """;

    public override async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        await Task.CompletedTask; // CPU-bound, без асинхронных ожиданий
        string root = args.GetString("path");
        if (root == "" || !Directory.Exists(root))
            throw new DirectoryNotFoundException($"directory not found: {root}");
        string pattern = args.GetString("pattern", "*");
        int maxFiles = Math.Clamp(args.GetInt("max_files", 1500), 1, 5000);
        int recentHours = Math.Clamp(args.GetInt("recent_hours", 0), 0, 24 * 90);
        var scan = ProjectScanner.Scan(System.IO.Path.GetFullPath(root), pattern, maxFiles, recentHours);
        return Json(new TreeResult {
            Root = scan.Root,
            TotalFiles = scan.TotalFiles,
            ListedFiles = scan.TotalFiles,
            SkippedDirs = scan.SkippedDirs,
            TotalBytes = scan.TotalBytes,
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
