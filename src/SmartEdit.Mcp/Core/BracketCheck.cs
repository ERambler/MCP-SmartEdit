using SmartEdit.Mcp.Core.SymbolScan;

namespace SmartEdit.Mcp.Core;

/// <summary>
/// Баланс скобок {} [] () для кода и &lt;&gt;-тегов для XML/HTML (по консилиуму:
/// &lt;&gt; в C# — lost cause из-за generics, проверяем только пары {}[]()); строки/комментарии/
/// интерполяция исключает стейт-машина CodeStrip (скобки кода внутри {дырок} $"…" считаются).
/// Строки подаются лениво (lineAt): полный чек гиганта не материализует файл в RAM.
/// Отчёт — до limit проблем: «line N: 'X' не закрыта / лишняя».
/// </summary>
public static class BracketCheck {
    public static List<string> Check(Func<int, string> lineAt, string path, int fromLine, int toLine,
        bool wholeFile = false, int limit = 5) {
        bool markup = SymbolDispatcher.KindOf(path) == SymbolLang.Markup;
        var problems = new List<string>();
        var stack = new Stack<(char Open, int Line)>();
        bool inBlockComment = false, inVerbatim = false;

        for (int i = fromLine; i <= toLine; i++) {
            string raw = lineAt(i);
            string trimmed = raw.TrimStart();
            if (trimmed.StartsWith("//")) continue;
            string code = CodeStrip.Strip(raw, ref inBlockComment, ref inVerbatim);
            for (int j = 0; j < code.Length; j++) {
                char c = code[j];
                if (c is '{' or '[' or '(' || (markup && c == '<' && TagAhead(code, j))) {
                    stack.Push((c, i));
                }
                else if (c is '}' or ']' or ')' || (markup && c == '>' && TagBehind(code, j))) {
                    char expected = c switch { '}' => '{', ']' => '[', ')' => '(', _ => '<' };
                    if (stack.Count > 0 && stack.Peek().Open == expected) stack.Pop();
                    else if (problems.Count < limit) problems.Add($"line {i}: unexpected '{c}'");
                }
                if (problems.Count >= limit) return problems;
            }
        }
        // неучтённые открытия: локальный диапазон может «забыть» закрывающие из соседних строк —
        // репортим только когда диапазон покрывает весь файл
        if (wholeFile) {
            foreach (var (open, line) in stack)
                if (problems.Count < limit) problems.Add($"line {line}: '{open}' not closed");
        }
        return problems;
    }

    static bool TagAhead(string code, int j) =>
        j + 1 < code.Length && (char.IsLetter(code[j + 1]) || code[j + 1] == '/' || code[j + 1] == '!' || code[j + 1] == '?');

    static bool TagBehind(string code, int j) =>
        j > 0 && (char.IsLetterOrDigit(code[j - 1]) || code[j - 1] == '-' || code[j - 1] == '_' || code[j - 1] == ':' || code[j - 1] == '.');
}
