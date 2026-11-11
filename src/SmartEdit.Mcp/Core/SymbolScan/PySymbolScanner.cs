using System.Text.RegularExpressions;

namespace SmartEdit.Mcp.Core.SymbolScan;

/// <summary>
/// Python: indent-сканер — самый надёжный из эвристических (консилиум).
/// class / def / async def с телом до строки той же или меньшей вложенности; docstring — первая строка.
/// </summary>
public static partial class PySymbolScanner {
    const int SigMax = 120;

    [GeneratedRegex(@"^(async\s+def|def|class)\s+([A-Za-z_]\w*)\s*(\(.*?\))?\s*:")]
    private static partial Regex Decl();

    public static SymbolScanResult Scan(IReadOnlyList<string> lines) {
        var roots = new List<Symbol>();
        // стек: (indent, symbol); children родителя — куда добавлять
        var stack = new Stack<(int Indent, Symbol Sym)>();
        var sink = roots;
        var usings = new List<string>();
        bool seenCode = false;
        string? pendingDoc = null;

        for (int i = 0; i < lines.Count; i++) {
            string raw = lines[i];
            string trimmed = raw.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            // импорты — только сверху файла до первой декларации
            if (!seenCode && (trimmed.StartsWith("import ") || trimmed.StartsWith("from "))) {
                usings.Add(TrimSig(trimmed.TrimEnd()));
                continue;
            }
            seenCode = true;

            int indent = raw.Length - raw.TrimStart(' ', '\t').Length;

            // закрытие охватывающих блоков: отступ перестал покрывать
            while (stack.Count > 0 && indent <= stack.Peek().Indent) {
                var (_, s) = stack.Pop();
                s.EndLine = Math.Max(s.EndLine, i); // последняя строка тела — предыдущая
            }
            sink = stack.Count > 0 ? stack.Peek().Sym.Children : roots;

            var m = Decl().Match(trimmed);
            if (m.Success) {
                string kind = m.Groups[1].Value.Contains("class") ? "class" : "def";
                string name = m.Groups[2].Value + (kind == "def" ? m.Groups[3].Value : "");
                string sig = TrimSig(TrimToCol80(raw));
                string? doc = pendingDoc;
                pendingDoc = null;
                var sym = new Symbol(i + 1, lines.Count, kind, name, sig, doc ?? FirstDocstring(lines, i + 1), []);
                sink.Add(sym);
                stack.Push((indent, sym));
                continue;
            }
            pendingDoc = null;
        }
        foreach (var (_, s) in stack) s.EndLine = Math.Min(s.EndLine, Math.Max(lines.Count, 1));
        return new(roots, "python", usings, []);
    }

    /// <summary>Первая строка docstring-тела ("""…""" / '…'), без кавычек.</summary>
    static string? FirstDocstring(IReadOnlyList<string> lines, int bodyIndex) {
        if (bodyIndex >= lines.Count) return null;
        string t = lines[bodyIndex].Trim();
        if (!t.StartsWith("\"\"\"") && !t.StartsWith("'''")) return null;
        string inner = t.Trim(['"', '\'']);
        if (inner.Length == 0 && bodyIndex + 1 < lines.Count) inner = lines[bodyIndex + 1].Trim();
        return inner.Length == 0 ? null : (inner.Length <= 100 ? inner : inner[..100] + "…");
    }

    static string TrimSig(string s) => TextUtil.Truncate(s, SigMax);
    static string TrimToCol80(string s) => s.Length <= 80 ? s.TrimEnd() : s[..80].TrimEnd();
}
