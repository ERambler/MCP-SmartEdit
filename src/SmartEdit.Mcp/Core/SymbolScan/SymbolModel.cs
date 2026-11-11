namespace SmartEdit.Mcp.Core.SymbolScan;

/// <summary>Символ кода: узел дерева (namespace/class/method/…), номера строк — логические, 1-based.</summary>
public sealed record Symbol {
    public int Line { get; }
    public int EndLine { get; set; } // сканеры уточняют при закрытии блока
    public string Kind { get; }
    public string Name { get; }
    public string Signature { get; }
    public string? Doc { get; }
    public List<Symbol> Children { get; init; }

    public Symbol(int line, int endLine, string kind, string name, string signature, string? doc, List<Symbol> children) {
        Line = line;
        EndLine = endLine;
        Kind = kind;
        Name = name;
        Signature = signature;
        Doc = doc;
        Children = children;
    }

    /// <summary>Плоский список + карта родителей (для breadcrumb Ns.Class.Member).</summary>
    public static void Flatten(List<Symbol> roots, List<Symbol> sink, Dictionary<Symbol, Symbol?> parents) {
        foreach (var c in roots) {
            sink.Add(c);
            parents[c] = null;
            FlattenInner(c, sink, parents);
        }
    }

    static void FlattenInner(Symbol node, List<Symbol> sink, Dictionary<Symbol, Symbol?> parents) {
        foreach (var c in node.Children) {
            sink.Add(c);
            parents[c] = node;
            FlattenInner(c, sink, parents);
        }
    }

    /// <summary>Полный путь охватывающего символа для строки: Namespace.Class.Method.</summary>
    public static string? EnclosingPath(List<Symbol> flat, Dictionary<Symbol, Symbol?> parents, int line) {
        Symbol? best = null;
        int bestSpan = int.MaxValue;
        foreach (var s in flat) {
            if (s.Line <= line && line <= s.EndLine) {
                int span = s.EndLine - s.Line;
                if (span < bestSpan) { best = s; bestSpan = span; }
            }
        }
        if (best is null) return null;
        var chain = new List<string>();
        for (Symbol? cur = best; cur is not null; cur = parents.TryGetValue(cur, out var p) ? p : null)
            chain.Add(cur.Name);
        chain.Reverse();
        return string.Join(".", chain);
    }
}

public sealed record SymbolScanResult(List<Symbol> Roots, string Language, List<string> Usings, List<string> Warnings);

/// <summary>Диспетчер по расширению: C#, Python, JS/TS, Java, C/C++, XML/HTML/MD.</summary>
public static class SymbolDispatcher {
    public static bool IsCode(string path) => KindOf(path) is not SymbolLang.Text;

    public static SymbolLang KindOf(string path) => (Path.GetExtension(path).ToLowerInvariant()) switch {
        ".cs" => SymbolLang.CSharp,
        ".py" or ".pyw" => SymbolLang.Python,
        ".js" or ".mjs" or ".cjs" or ".jsx" or ".ts" or ".tsx" => SymbolLang.JsTs,
        ".java" => SymbolLang.Java,
        ".cpp" or ".cc" or ".cxx" or ".c" or ".h" or ".hpp" or ".hh" or ".hxx" => SymbolLang.Cpp,
        ".xml" or ".xaml" or ".csproj" or ".props" or ".targets" or ".config" or ".resx" or ".svg" or ".html" or ".htm" => SymbolLang.Markup,
        _ => SymbolLang.Text,
    };

    public static SymbolScanResult Scan(string path, IReadOnlyList<string> lines) => KindOf(path) switch {
        SymbolLang.CSharp => CsSymbolScanner.Scan(lines),
        SymbolLang.Python => PySymbolScanner.Scan(lines),
        SymbolLang.JsTs => JsJavaCppScanner.ScanJs(lines),
        SymbolLang.Java => JsJavaCppScanner.ScanJava(lines),
        SymbolLang.Cpp => JsJavaCppScanner.ScanCpp(lines),
        SymbolLang.Markup => MarkupScanner.Scan(lines),
        _ => new([], "text", [], []),
    };
}

public enum SymbolLang { CSharp, Python, JsTs, Java, Cpp, Markup, Text }

/// <summary>Общие утилиты текста сканеров: обрезка с многоточием и баланс {} строки.</summary>
internal static class TextUtil {
    /// <summary>Длина ≤ max, иначе max-1 символов + '…'.</summary>
    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>Баланс фигурных скобок строки (после CodeStrip — литералы уже вырезаны).</summary>
    public static int CountBraces(string code) {
        int d = 0;
        foreach (char c in code) { if (c == '{') d++; else if (c == '}') d--; }
        return d;
    }
}
