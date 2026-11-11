using System.Text;

namespace SmartEdit.Mcp.Core;

/// <summary>Минимальный unified diff: мы знаем заменяемый диапазон, LCS не нужен.</summary>
public static class Differ {
    public static string Unified(
        IReadOnlyList<string> before, IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines,
        IReadOnlyList<string> after, int startLine, int cap = 200) {
        var rows = before.Select(l => (" ", l))
            .Concat(oldLines.Select(l => ("-", l)))
            .Concat(newLines.Select(l => ("+", l)))
            .Concat(after.Select(l => (" ", l)));
        var sb = new StringBuilder();
        int anchor = Math.Max(1, startLine - before.Count);
        int oldSpan = before.Count + oldLines.Count + after.Count;
        int newSpan = before.Count + newLines.Count + after.Count;
        sb.Append($"@@ -{anchor},{oldSpan} +{anchor},{newSpan} @@\n");
        int shown = 0;
        foreach (var (prefix, line) in rows) {
            if (shown++ == cap) {
                sb.Append($"… [diff truncated at {cap} lines]\n");
                break;
            }
            sb.Append(prefix).Append(line).Append('\n');
        }
        return sb.ToString();
    }
}
