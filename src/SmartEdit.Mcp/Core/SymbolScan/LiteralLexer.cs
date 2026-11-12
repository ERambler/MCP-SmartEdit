using System.Text;

namespace SmartEdit.Mcp.Core.SymbolScan;

// ── v0.8: универсальный лексер литералов/комментариев (консилиум deepseek+google+qwen, 2026-11-11) ──
// Канон: Roslyn ScanRawStringLiteral (run-length кавычек) + interpolationStack (дырки),
// TextMate begin/end/patterns (декларативные формы), tree-sitter lexer-state-as-int.
// Один Scanner + профили языков; скобки считаются ТОЛЬКО в Code и HoleCode.

/// <summary>Форма литерала. Raw-серии (C# """/Py triple/Java text block): MinRun>=3,
/// длина серии фиксируется в состоянии при открытии и закрывается серией той же длины.</summary>
public sealed record LitForm(
    string Open,                     // первый символ(ы) открытия после префикса
    string Close,                    // закрытие (для raw = тот же символ, реальная длина в состоянии)
    char? Escape = null,             // '\' экранирует следующий символ
    bool DoubleEscape = false,       // close×2 = литеральный символ (@"..." → "")
    bool Multiline = false,
    int MinRun = 0,                  // >=3 → raw-серия кавычек
    string[]? Prefixes = null,       // "$", "@", "f", "rf"… — матчатся longest-first
    int BraceRun = 0,                // фиксированный множитель дырки (f-string → 1)
    bool BraceRunFromDollars = false,// C# raw: множитель = число '$' перед серией
    bool DoubleEscapeBrace = false,  // '{{'/'}}' вне дырки = литеральная скобка
    string? HoleOpen = null,         // "${" (JS-шаблоны)
    bool FormsInsideHole = false,    // внутри дырки разрешены вложенные формы/комментарии
    bool CppRaw = false,             // R"delim(...) — делимитер читается при открытии
    int CloseRunFixed = 0);          // >0: делимитер РОВНО столько кавычек (Py/Java triple; C# raw — жадная серия)

public sealed record LanguageProfile(
    string[] LineComments,
    (string Open, string Close)[] BlockComments,
    LitForm[] Forms);

/// <summary>Межстрочное состояние: Kind {Code, LineComment, BlockComment, Literal, HoleCode} + параметры открытой формы.</summary>
public sealed class LexerState {
    public const byte Code = 0, LineComment = 1, BlockComment = 2, Literal = 3, HoleCode = 4;
    public byte Kind = Code;
    public int FormId = -1;
    public int CloseRun;        // raw: длина серии кавычек открытия
    public int Dollars;         // raw: множитель скобок дырки (число '$')
    public int HoleDepth;       // >0 — в дырке, глубина { }
    public string? CppDelim;    // C++ raw: делимитер
    public int BlockIdx;
    public int OpenLine;        // строка, где открылся текущий литерал/комментарий — якорь диагностики
    /// <summary>Стек контекстов дырок (глубина ≤3): вложенная форма в ${/{hole} сохраняет контекст и возвращает его при закрытии.</summary>
    public List<(byte Kind, int FormId, int CloseRun, int Dollars, int HoleDepth, string? CppDelim)>? Ctx;
    public LexerState Clone() => (LexerState)MemberwiseClone();

    /// <summary>Войти в форму/комментарий ИЗ дырки — сохранить контекст (консилиум: interpolationStack).</summary>
    public void PushCtx() {
        Ctx ??= new(3);
        Ctx.Add((Kind, FormId, CloseRun, Dollars, HoleDepth, CppDelim));
    }

    /// <summary>Закрыть форму: если под нами дырка — вернуть её контекст, иначе — код.</summary>
    public byte PopCtxOrCode() {
        if (Ctx is { Count: > 0 }) {
            var f = Ctx[^1];
            Ctx.RemoveAt(Ctx.Count - 1);
            (Kind, FormId, CloseRun, Dollars, HoleDepth, CppDelim) = f;
            return Kind;
        }
        return Code;
    }
}

public static class LiteralLexer {

    public static readonly LanguageProfile CSharp = new(
        ["//"], [("/*", "*/")],
        [
            new("\"", "\"", Escape: '\\'),
            new("'", "'", Escape: '\\'),
            new("@\"", "\"", DoubleEscape: true, Multiline: true, Prefixes: ["@"]),
            new("\"", "\"", Escape: '\\', Prefixes: ["$", "$@", "@$"], BraceRun: 1, DoubleEscapeBrace: true),
            new("\"", "\"", MinRun: 3, Multiline: true, Prefixes: ["", "$", "$$", "$$$"], BraceRunFromDollars: true),
        ]);

    public static readonly LanguageProfile Python = new(
        ["#"], [],
        [
            new("\"\"\"", "\"\"\"", Multiline: true, MinRun: 3, CloseRunFixed: 3, Prefixes: ["", "r", "b", "rb", "u"]),
            new("'''", "'''", Multiline: true, MinRun: 3, CloseRunFixed: 3, Prefixes: ["", "r", "b", "rb", "u"]),
            new("\"", "\"", Escape: '\\', Prefixes: ["", "r", "b", "rb", "u"]),
            new("\"", "\"", Escape: '\\', Prefixes: ["f", "F", "rf", "fr"], BraceRun: 1, DoubleEscapeBrace: true, FormsInsideHole: true),
            new("'", "'", Escape: '\\', Prefixes: ["", "r", "b"]),
            new("'", "'", Escape: '\\', Prefixes: ["f", "F", "rf", "fr"], BraceRun: 1, DoubleEscapeBrace: true, FormsInsideHole: true),
        ]);

    public static readonly LanguageProfile JsTsJava = new(      // Java text block: делимитер ровно 3
        ["//"], [("/*", "*/")],
        [
            new("\"", "\"", Escape: '\\'),
            new("'", "'", Escape: '\\'),
            new("`", "`", Escape: '\\', Multiline: true, HoleOpen: "${", FormsInsideHole: true),
            new("\"", "\"", MinRun: 3, CloseRunFixed: 3, Multiline: true, Escape: '\\'),
        ]);

    public static readonly LanguageProfile Cpp = new(
        ["//"], [("/*", "*/")],
        [
            new("\"", "\"", Escape: '\\'),
            new("'", "'", Escape: '\\'),
            new("R\"", "\")", Multiline: true, CppRaw: true),
        ]);

    public static readonly LanguageProfile Plain = new(["//"], [("/*", "*/")], []);

    /// <summary>Профиль по расширению (симметрично SymbolDispatcher.KindOf).</summary>
    public static LanguageProfile ForPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch {
        ".cs" => CSharp,
        ".py" or ".pyw" => Python,
        ".js" or ".mjs" or ".cjs" or ".jsx" or ".ts" or ".tsx" or ".java" => JsTsJava,
        ".cpp" or ".cc" or ".cxx" or ".c" or ".h" or ".hpp" or ".hh" or ".hxx" => Cpp,
        _ => Plain,
    };

    /// <summary>
    /// Прогнать строку: литералы/комментарии → пробелы (длины сохранены), код — как есть,
    /// код интерполяционных дырок — как есть (его скобки настоящие). lineNo — для якоря OpenLine.
    /// </summary>
    public static string Strip(string line, LexerState st, LanguageProfile p, int lineNo = 0) {
        var sb = new StringBuilder(line.Length);
        int i = 0;
        while (i < line.Length) {
            switch (st.Kind) {
                case LexerState.LineComment:
                    sb.Append(' ', line.Length - i);
                    st.Kind = st.HoleDepth > 0 ? LexerState.HoleCode : LexerState.Code; // комментарий кончился вместе со строкой
                    return sb.ToString();

                case LexerState.BlockComment: {
                    var close = p.BlockComments[st.BlockIdx].Close;
                    int end = line.IndexOf(close, i, StringComparison.Ordinal);
                    if (end < 0) { sb.Append(' ', line.Length - i); return sb.ToString(); }
                    sb.Append(' ', end + close.Length - i);
                    i = end + close.Length;
                    st.Kind = st.PopCtxOrCode();
                    continue;
                }

                case LexerState.Literal: {
                    var f = p.Forms[st.FormId];
                    // escape
                    if (f.Escape is char esc && line[i] == esc) { sb.Append("  "); i += 2; continue; }
                    // удвоенный close = литерал (@"")
                    if (f.DoubleEscape && line[i] == f.Close[0] && MatchesAt(line, i + 1, f.Close[0])) { sb.Append("  "); i += 2; continue; }
                    // raw: серия >= CloseRun
                    if (f.MinRun >= 3 && line[i] == f.Close[0]) {
                        int run = RunAt(line, i, f.Close[0]);
                        if (run >= st.CloseRun) { sb.Append(' ', st.CloseRun); i += st.CloseRun; st.Kind = st.PopCtxOrCode(); continue; }
                    }
                    // C++ raw: ")delim"
                    if (f.CppRaw && line[i] == ')' && st.CppDelim is string delim && MatchesAt(line, i, ")" + delim + "\"")) {
                        sb.Append(' ', delim.Length + 3); i += delim.Length + 3; st.Kind = st.PopCtxOrCode(); continue;
                    }
                    // открытие дырки (только с нулевой глубиной); без $-префикса скобки raw — литеральный текст
                    if (st.HoleDepth == 0) {
                        int d = f.BraceRunFromDollars ? st.Dollars : f.BraceRun;
                        if (f.HoleOpen is string ho && MatchesAt(line, i, ho)) {
                            sb.Append(' ', ho.Length); i += ho.Length;
                            st.Kind = LexerState.HoleCode; st.HoleDepth = 1;
                            continue;
                        }
                        if (d > 0 && line[i] == '{') {
                            int run = RunAt(line, i, '{');
                            if (f.DoubleEscapeBrace && run >= 2 * d && run % (2 * d) == 0) {
                                sb.Append(' ', 2 * d); i += 2 * d; continue; // '{{' — литеральная скобка
                            }
                            if (run >= d && run % d == 0) {
                                sb.Append(' ', d); i += d;
                                st.Kind = LexerState.HoleCode; st.HoleDepth = 1;
                                continue;
                            }
                        }
                    }
                    // обычное закрытие
                    if (f.MinRun < 3 && !f.CppRaw && line[i] == f.Close[0]
                        && (f.Close.Length == 1 || MatchesAt(line, i, f.Close))) {
                        sb.Append(' ', f.Close.Length); i += f.Close.Length;
                        st.Kind = st.PopCtxOrCode();
                        continue;
                    }
                    sb.Append(' ');
                    i++;
                    continue;
                }

                case LexerState.HoleCode: {
                    var f = p.Forms[st.FormId];
                    int d = f.HoleOpen is not null ? 1 : (f.BraceRunFromDollars ? st.Dollars : f.BraceRun);
                    // закрытие дырки: для D>1 — серия D '}', для D=1/${ — одиночная '}' на глубине 1
                    if (line[i] == '}') {
                        int run = RunAt(line, i, '}');
                        if (st.HoleDepth == 1 && run >= d) {
                            sb.Append(' ', d); i += d;
                            st.HoleDepth = 0; st.Kind = LexerState.Literal;
                            continue;
                        }
                        st.HoleDepth--;
                        sb.Append(line[i]); i++;
                        continue;
                    }
                    if (line[i] == '{') st.HoleDepth++;
                    // вложенные формы в дырке: JS `${`x`}`, Py 3.12 f"{f'{x}'}", комментарии
                    if (f.FormsInsideHole && (TryEnterForm(line, ref i, p, st, sb, lineNo) || TryEnterComment(line, ref i, p, st, sb, lineNo)))
                        continue;
                    sb.Append(line[i]);
                    i++;
                    continue;
                }

                default: { // Code
                    if (TryEnterForm(line, ref i, p, st, sb, lineNo)) continue;
                    if (TryEnterComment(line, ref i, p, st, sb, lineNo)) continue;
                    sb.Append(line[i]);
                    i++;
                    continue;
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>longest-prefix выбор формы (префикс+open+серия); сырые серии требуют run>=MinRun.</summary>
    static bool TryEnterForm(string line, ref int i, LanguageProfile p, LexerState st, StringBuilder sb, int lineNo) {
        bool fromHole = st.Kind == LexerState.HoleCode;
        // кадр снимаем ДО мутаций: TryEnterForm ниже обнуляет HoleDepth (баг: пушили уже обнулённое)
        var frame = (st.Kind, st.FormId, st.CloseRun, st.Dollars, st.HoleDepth, st.CppDelim);
        int best = -1, bestLen = 0, bestRun = 0, bestHoles = 0;
        int bestCloseRun = 0, bestDollars = 0;
        string? bestDelim = null;
        for (int id = 0; id < p.Forms.Length; id++) {
            var f = p.Forms[id];
            var prefixes = f.Prefixes is { Length: > 0 } ? f.Prefixes : [""];
            foreach (var pre in prefixes) {
                if (pre.Length > 0 && !MatchesAt(line, i, pre)) continue;
                int at = i + pre.Length;
                if (at >= line.Length || line[at] != f.Open[0]) continue;
                if (f.Open.Length > 1 && !MatchesAt(line, at + 1, f.Open[1..])) continue;

                int run = f.MinRun >= 3 ? RunAt(line, at, f.Open[0]) : f.Open.Length;
                if (f.MinRun >= 3 && run < f.MinRun) continue; // тройная кавычка требует серию
                int closeRun = f.CloseRunFixed > 0 ? f.CloseRunFixed : (f.MinRun >= 3 ? run : 0);

                int consume = pre.Length + (f.MinRun >= 3 ? closeRun : f.Open.Length);
                int holes = f.BraceRun > 0 || f.HoleOpen is not null || f.BraceRunFromDollars ? 1 : 0;
                // сортировка кандидатов: длина → minRun → наличие дырок (специфика побеждает)
                if (best < 0 || consume > bestLen
                    || (consume == bestLen && f.MinRun > bestRun)
                    || (consume == bestLen && f.MinRun == bestRun && holes > bestHoles)) {
                    best = id; bestLen = consume; bestRun = f.MinRun; bestHoles = holes;
                    bestCloseRun = closeRun;
                    bestDollars = f.BraceRunFromDollars ? pre.Length : 0;
                    bestDelim = null;
                    if (f.CppRaw) {
                        int paren = line.IndexOf('(', at);
                        bestDelim = paren > at + 1 ? line[(at + 2)..paren] : ""; // между R" и (
                        bestLen = paren > at ? paren - i + 1 : consume;
                    }
                }
            }
        }
        if (best < 0) return false;
        st.Kind = LexerState.Literal;
        st.FormId = best;
        st.CloseRun = bestCloseRun;
        st.Dollars = bestDollars;
        st.HoleDepth = 0;
        st.CppDelim = bestDelim;
        st.OpenLine = lineNo;
        if (fromHole) { st.Ctx ??= new(3); st.Ctx.Add(frame); } // кадр ДО мутаций — контекст дырки под стеком
        sb.Append(' ', Math.Min(bestLen, line.Length - i));
        i += Math.Min(bestLen, line.Length - i); // курсор вызывающего цикла — за делимитер
        return true;
    }

    static bool TryEnterComment(string line, ref int i, LanguageProfile p, LexerState st, StringBuilder sb, int lineNo) {
        bool fromHole = st.Kind == LexerState.HoleCode;
        foreach (var lc in p.LineComments)
            // line-комментарий кончается на ТОЙ ЖЕ строке: состояние не переключаем
            // (v0.9 fix: Kind=LineComment пережидал конец строки и ПОЛНОСТЬЮ съедал СЛЕДУЮЩУЮ строку)
            if (MatchesAt(line, i, lc)) { sb.Append(' ', line.Length - i); i = line.Length; return true; }
        for (int b = 0; b < p.BlockComments.Length; b++)
            if (MatchesAt(line, i, p.BlockComments[b].Open)) {
                if (fromHole) st.PushCtx();
                st.Kind = LexerState.BlockComment; st.BlockIdx = b; st.OpenLine = lineNo;
                sb.Append(' ', p.BlockComments[b].Open.Length);
                i += p.BlockComments[b].Open.Length;
                return true;
            }
        return false;
    }

    static int RunAt(string s, int i, char c) {
        int n = 0; while (i + n < s.Length && s[i + n] == c) n++;
        return n;
    }

    static bool MatchesAt(string s, int i, char c) => i < s.Length && s[i] == c;

    static bool MatchesAt(string s, int i, string lit) =>
        i >= 0 && i + lit.Length <= s.Length && s.AsSpan(i, lit.Length).SequenceEqual(lit);
}
