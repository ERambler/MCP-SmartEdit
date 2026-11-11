using System.Text.RegularExpressions;

namespace SmartEdit.Mcp.Core.SymbolScan;

/// <summary>
/// XML/HTML/XAML и прочая разметка: дерево тегов с номерами строк + баланс тегов.
/// HTML: void-элементы (br, img…) и самозакрытые не открывают узел.
/// </summary>
public static partial class MarkupScanner {
    const int MaxNodes = 2000;

    [GeneratedRegex(@"<\s*(/?)\s*([A-Za-z_][\w.:-]*)")]
    private static partial Regex Tag();

    static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase) {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param",
        "source", "track", "wbr", "!DOCTYPE", "?xml",
    };

    public static SymbolScanResult Scan(IReadOnlyList<string> lines) {
        var roots = new List<Symbol>();
        var stack = new Stack<Symbol>();
        var sink = roots;
        var warnings = new List<string>();
        int nodes = 0;

        for (int i = 0; i < lines.Count; i++) {
            foreach (Match m in Tag().Matches(lines[i])) {
                bool closing = m.Groups[1].Value == "/";
                string name = m.Groups[2].Value;
                if (name is "!DOCTYPE" or "?xml") continue;
                // самозакрыт, если до ближайшей '>' идёт '/'
                int gt = lines[i].IndexOf('>', m.Index);
                bool self = gt > m.Index && lines[i][gt - 1] == '/';

                if (closing) {
                    if (stack.Count > 0 && string.Equals(stack.Peek().Name, name, StringComparison.OrdinalIgnoreCase)) {
                        var s = stack.Pop();
                        s.EndLine = i + 1;
                        sink = stack.Count > 0 ? stack.Peek().Children : roots;
                    }
                    else if (warnings.Count < 10) warnings.Add($"line {i + 1}: closing </{name}> matches nothing");
                    continue;
                }
                if (self || VoidElements.Contains(name)) continue;
                if (++nodes > MaxNodes) { if (warnings.Count < 10) warnings.Add("markup tree truncated at 2000 nodes"); return new(roots, "markup", [], warnings); }
                var sym = new Symbol(i + 1, i + 1, "tag", name, "", null, []);
                sink.Add(sym);
                stack.Push(sym);
                sink = sym.Children;
            }
        }
        foreach (var s in stack) if (warnings.Count < 10) warnings.Add($"line {s.Line}: <{s.Name}> not closed");
        return new(roots, "markup", [], warnings);
    }
}
