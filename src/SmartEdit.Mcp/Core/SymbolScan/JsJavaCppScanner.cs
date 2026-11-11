using System.Text.RegularExpressions;

namespace SmartEdit.Mcp.Core.SymbolScan;

/// <summary>
/// JS/TS, Java, C/C++: грубее C#-сканера (по консилиуму — навигационная точность).
/// Классы/функции/методы по регекспам + глубина {}; JSDoc/JavaDoc первая строка как doc.
/// </summary>
public static partial class JsJavaCppScanner {
    const int SigMax = 120;

    [GeneratedRegex(@"^(?:export\s+)?(?:default\s+)?(?:abstract\s+)?(?:public\s+|private\s+|protected\s+|internal\s+|static\s+|final\s+|sealed\s+|virtual\s+|override\s+|async\s+|inline\s+|constexpr\s+|extern\s+)*(class|struct|interface|enum)\s+([A-Za-z_]\w*)")]
    private static partial Regex TypeDecl();

    [GeneratedRegex(@"^(?:export\s+)?(?:default\s+)?(?:async\s+)?(?:function\s*\*?\s+)([A-Za-z_$][\w$]*)\s*[\(<]")]
    private static partial Regex JsFunc();

    [GeneratedRegex(@"(?:(?:public|private|protected|internal|static|final|sealed|virtual|override|async|inline|constexpr|extern)\s+)+[\w:<>\[\],.*&\s]+\b([A-Za-z_~][\w]*)\s*\(")]
    private static partial Regex MethodLike();

    [GeneratedRegex(@"^(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=\s*(?:async\s*)?\(")]
    private static partial Regex JsArrow();

    public static SymbolScanResult ScanJs(IReadOnlyList<string> lines) => ScanGeneric(lines, "javascript");
    public static SymbolScanResult ScanJava(IReadOnlyList<string> lines) => ScanGeneric(lines, "java");
    public static SymbolScanResult ScanCpp(IReadOnlyList<string> lines) => ScanGeneric(lines, "cpp");

    static SymbolScanResult ScanGeneric(IReadOnlyList<string> lines, string lang) {
        var roots = new List<Symbol>();
        var usings = new List<string>();
        var stack = new Stack<(int Depth, Symbol Sym)>();
        var sink = roots;
        int depth = 0;
        bool inBlockComment = false, inVerbatim = false;
        bool topOnly = true; // import/using — только сверху
        string? pendingDoc = null;

        for (int i = 0; i < lines.Count; i++) {
            string raw = lines[i];
            string trimmed = raw.TrimStart();
            if (trimmed.Length == 0) continue;

            if (trimmed.StartsWith("/**") || trimmed.StartsWith("/*") || inBlockComment) {
                string inner = trimmed.TrimStart('/', '*').Trim();
                if (pendingDoc is null && inner.Length > 0 && !inner.StartsWith("*")) pendingDoc = Short(inner);
                bool bc = inBlockComment;
                _ = CodeStrip.Strip(raw, ref bc, ref inVerbatim);
                inBlockComment = bc;
                continue;
            }
            if (trimmed.StartsWith("//") || trimmed.StartsWith("*")) continue;

            string code = CodeStrip.Strip(raw, ref inBlockComment, ref inVerbatim).Trim();
            if (code.Length == 0) continue;

            if (topOnly && (code.StartsWith("import ") || code.StartsWith("using ") || code.StartsWith("#include"))) {
                usings.Add(Short(code));
                continue;
            }
            topOnly = false;

            int delta = TextUtil.CountBraces(code);
            int line = i + 1;

            var td = TypeDecl().Match(code);
            var jf = JsFunc().Match(code);
            var ja = JsArrow().Match(code);
            var ml = MethodLike().Match(code);
            if (td.Success && code.Contains('{')) {
                var sym = new Symbol(line, line, td.Groups[1].Value, td.Groups[2].Value, TrimSig(code.TrimEnd('{').Trim()), pendingDoc, []);
                pendingDoc = null;
                sink.Add(sym);
                stack.Push((depth, sym));
                sink = sym.Children;
            }
            else if (jf.Success) {
                var sym = new Symbol(line, line, "function", jf.Groups[1].Value, TrimSig(code.TrimEnd('{').Trim()), pendingDoc, []);
                pendingDoc = null;
                sink.Add(sym);
                if (code.Contains('{')) { stack.Push((depth, sym)); sink = sym.Children; }
            }
            else if (ja.Success) {
                var sym = new Symbol(line, line, "function", ja.Groups[1].Value, TrimSig(code), pendingDoc, []);
                pendingDoc = null;
                sink.Add(sym);
            }
            else if (ml.Success && stack.Count > 0 && depth == stack.Peek().Depth + 1 && code.Contains('(')) {
                var sym = new Symbol(line, line, "method", ml.Groups[1].Value, TrimSig(code.TrimEnd('{').Trim()), pendingDoc, []);
                pendingDoc = null;
                stack.Peek().Sym.Children.Add(sym);
                if (code.Contains('{') && !code.EndsWith(';')) { stack.Push((depth, sym)); sink = sym.Children; }
            }

            depth += delta;
            while (stack.Count > 0 && depth <= stack.Peek().Depth) {
                var (_, s) = stack.Pop();
                s.EndLine = Math.Max(s.EndLine, line);
            }
            if (stack.Count == 0) sink = roots; else if (stack.Peek().Depth + 1 == depth) sink = stack.Peek().Sym.Children;
        }
        return new(roots, lang, usings, []);
    }

    static string TrimSig(string s) => TextUtil.Truncate(s, SigMax);
    static string Short(string s) => TextUtil.Truncate(s, 100);
}
