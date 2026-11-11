using SmartEdit.Mcp.Core.SymbolScan;

namespace SmartEdit.Mcp.Core;

/// <summary>
/// Баланс скобок {} [] () для кода и &lt;&gt;-тегов для XML/HTML (по консилиуму:
/// &lt;&gt; в C# — lost cause из-за generics, проверяем только пары {}[]()); строки/комментарии/
/// интерполяция исключает стейт-машина CodeStrip (скобки кода внутри {дырок} $"…" считаются).
/// Строки подаются лениво (lineAt): полный чек гиганта не материализует файл в RAM.
/// Алгоритм — канонический стек пар с позицией (как в линтерах/редакторах): каждый отчёт несёт
/// ЯКОРЬ — строку открывающей скобки. Несовпадение пары: якорь на открытую + pop для ресинка
/// (иначе одна ошибка каскадирует на весь остаток файла). Незакрытые в EOF — в порядке исходника:
/// внешняя первой (Stack перечисляет новейшие первыми → разворачиваем).
/// Отчёт — до limit проблем. v0.8: структурные diagnostics (Line/Code/Message) + незакрытые
/// литералы/комментарии/дырки — лексер на EOF репортит СТРОКУ ОТКРЫТИЯ.
/// </summary>
public static class BracketCheck {

    /// <summary>Структурная диагностика для ответов инструментов: якорная строка + машинный код + текст.</summary>
    public sealed record Diag(int Line, string Code, string Message);

    public static List<string> Check(Func<int, string> lineAt, string path, int fromLine, int toLine,
        bool wholeFile = false, int limit = 5)
        => [.. Diagnostics(lineAt, path, fromLine, toLine, wholeFile, limit).Select(d => $"line {d.Line}: {d.Message}")];

    /// <summary>
    /// Киллерфича v0.8: (1) баланс скобок с якорями, (2) НЕЗАКРЫТЫЕ литералы/комментарии/интерполяции —
    /// состояние лексера на EOF указывает на строку открытия. wholeFile only для (2).
    /// </summary>
    public static List<Diag> Diagnostics(Func<int, string> lineAt, string path, int fromLine, int toLine,
        bool wholeFile = false, int limit = 5) {
        bool markup = SymbolDispatcher.KindOf(path) == SymbolLang.Markup;
        var problems = new List<Diag>();
        var stack = new Stack<(char Open, int Line)>();
        // v0.8: универсальный профильный лексер (консилиум-дизайн): raw/triple/template/f-string/R"()"
        var profile = LiteralLexer.ForPath(path);
        var lex = new LexerState();

        for (int i = fromLine; i <= toLine; i++) {
            string raw = lineAt(i);
            string trimmed = raw.TrimStart();
            if (!markup && trimmed.StartsWith("//")) continue;
            string code = markup ? raw : LiteralLexer.Strip(raw, lex, profile, i);
            for (int j = 0; j < code.Length; j++) {
                char c = code[j];
                if (c is '{' or '[' or '(' || (markup && c == '<' && TagAhead(code, j))) {
                    stack.Push((c, i));
                }
                else if (c is '}' or ']' or ')' || (markup && c == '>' && TagBehind(code, j))) {
                    char expected = c switch { '}' => '{', ']' => '[', ')' => '(', _ => '<' };
                    if (stack.Count == 0) {
                        if (problems.Count < limit)
                            problems.Add(new Diag(i, "stray_closer", $"'{c}' closes nothing (no '{expected}' open above)"));
                    }
                    else if (stack.Peek().Open == expected) {
                        stack.Pop();
                    }
                    else {
                        var (open, openLine) = stack.Pop(); // ресинк: одна ошибка не должна каскадить
                        if (problems.Count < limit)
                            problems.Add(new Diag(i, "bracket_mismatch",
                                $"'{c}' here, but '{open}' from line {openLine} is still open — missing '{Closer(open)}'?"));
                    }
                }
                if (problems.Count >= limit) return problems;
            }
        }
        // неучтённые открытия: локальный диапазон может «забыть» закрывающие из соседних строк —
        // репортим только когда диапазон покрывает весь файл; внешняя незакрытая — первой
        if (wholeFile) {
            foreach (var (open, line) in stack.Reverse())
                if (problems.Count < limit)
                    problems.Add(new Diag(line, "never_closed", $"'{open}' opened here is never closed (missing '{Closer(open)}')"));
            // (2) незакрытые литералы/комментарии/дырки: якорь = строка ОТКРЫТИЯ (лексер помнит её)
            if (!markup) {
                if (lex.Kind == LexerState.Literal)
                    problems.Add(new Diag(lex.OpenLine, "unterminated_literal", "string literal opened here is never closed"));
                if (lex.Kind == LexerState.HoleCode)
                    problems.Add(new Diag(lex.OpenLine, "unterminated_hole", "interpolation hole '{' opened here is never closed"));
                if (lex.Kind == LexerState.BlockComment)
                    problems.Add(new Diag(lex.OpenLine, "unterminated_comment", "'/*' opened here is never closed"));
            }
        }
        return problems;
    }

    static char Closer(char open) => open switch { '{' => '}', '[' => ']', '(' => ')', _ => '>' };

    static bool TagAhead(string code, int j) =>
        j + 1 < code.Length && (char.IsLetter(code[j + 1]) || code[j + 1] == '/' || code[j + 1] == '!' || code[j + 1] == '?');

    static bool TagBehind(string code, int j) =>
        j > 0 && (char.IsLetterOrDigit(code[j - 1]) || code[j - 1] == '-' || code[j - 1] == '_' || code[j - 1] == ':' || code[j - 1] == '.');
}
