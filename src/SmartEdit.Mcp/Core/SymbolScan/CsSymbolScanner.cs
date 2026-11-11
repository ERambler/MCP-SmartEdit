using System.Text;
using System.Text.RegularExpressions;

namespace SmartEdit.Mcp.Core.SymbolScan;

/// <summary>
/// C#-структура: namespace/типы/члены с номерами строк, XML-doc (первая содержательная строка
/// ///-блока), using-список. Регексп+глубина {} — навигационная точность, не компиляторная
/// (известно: partial-дубликаты, редкие expression-bodied; консилиум 2026-11-11: достаточно для агента).
/// </summary>
public static partial class CsSymbolScanner {
    const int SigMax = 120;

    static readonly string[] TypeMods = ["public", "internal", "protected", "private", "static", "sealed",
        "abstract", "partial", "readonly", "ref", "unsafe", "new", "file", "required", "volatile", "extern", "async"];

    static readonly HashSet<string> Control = ["if", "for", "foreach", "while", "switch", "catch", "finally",
        "using", "lock", "return", "throw", "else", "do", "try", "yield", "await", "fixed", "get", "set",
        "init", "add", "remove", "case", "default", "break", "continue", "goto", "checked", "unchecked", "when"];

    [GeneratedRegex(@"^(?:(?:public|internal|protected|private|static|sealed|abstract|partial|readonly|ref|unsafe|new|file|required|volatile|extern|async)\s+)*(class|interface|struct|record|record\s+struct|record\s+class|enum)\s+([A-Za-z_@][\w]*(?:\s*<[^>{]*>)?)")]
    private static partial Regex TypeDecl();

    [GeneratedRegex(@"^namespace\s+([\w.]+)")]
    private static partial Regex NamespaceDecl();

    [GeneratedRegex(@"^using\s+(?:static\s+)?([\w.*]+|\w+\s*=\s*[\w.<>]+)")]
    private static partial Regex UsingDecl();

    [GeneratedRegex(@"^event\s+(?:static\s+)?([\w<>\[\],.?]+)\s+([A-Za-z_@]\w*)")]
    private static partial Regex EventDecl();

    [GeneratedRegex(@"([A-Za-z_@]\w*)\s*(?:<[^>]*>)?\s*\(")]
    private static partial Regex NameBeforeParen();

    [GeneratedRegex(@"([A-Za-z_@]\w*)\s*(\{|=>)")]
    private static partial Regex NameBeforeProp();

    public static SymbolScanResult Scan(IReadOnlyList<string> lines) {
        var usings = new List<string>();
        var warnings = new List<string>();
        var roots = new List<Symbol>();
        var topTypes = roots;
        string? fileNs = null;
        int fileNsLine = 0;

        var stack = new Stack<(int Depth, Symbol Sym)>();
        int depth = 0;
        var lex = new LexerState(); // v0.8: профильный лексер (raw """/$$""" больше не ломают скан)
        var pendingDoc = new StringBuilder();

        for (int i = 0; i < lines.Count; i++) {
            string raw = lines[i];
            string trimmed = raw.TrimStart();

            // XML-doc копим до следующей декларации (сырой текст, конвертация в summary на месте)
            if (trimmed.StartsWith("///")) { AppendDoc(pendingDoc, trimmed[3..]); continue; }
            if (trimmed.StartsWith("//") || trimmed.Length == 0) continue;

            string code = LiteralLexer.Strip(raw, lex, LiteralLexer.CSharp).TrimEnd();
            string trimmedCode = code.Trim();
            if (trimmedCode.Length == 0) continue;

            // using — только пока не встретились типы (using-statement внутри кода не декларация)
            if (stack.Count == 0 && roots.Count == 0 && fileNs is null) {
                var u = UsingDecl().Match(trimmedCode);
                if (u.Success && trimmedCode.Contains(';')) { usings.Add(u.Groups[1].Value.Trim()); }
            }

            var ns = NamespaceDecl().Match(trimmedCode);
            if (ns.Success) {
                var sym = new Symbol(i + 1, lines.Count, "namespace", ns.Groups[1].Value, TrimSig(trimmedCode.TrimEnd('{', ';').Trim()), TakeDoc(pendingDoc), []);
                bool fileScoped = trimmedCode.EndsWith(';');
                if (fileScoped) { fileNs = ns.Groups[1].Value; fileNsLine = i + 1; }
                else {
                    roots.Add(sym);
                    topTypes = sym.Children;
                    stack.Push((depth + TextUtil.CountBraces(code), sym)); // строка заканчивается '{'
                }
                depth += TextUtil.CountBraces(code);
                continue;
            }

            var td = TypeDecl().Match(trimmedCode);
            if (td.Success && LineOpensBlock(code)) {
                string kw = Regex.Replace(td.Groups[1].Value, @"\s+", " ");
                string name = td.Groups[2].Value.Replace(" ", "");
                int start = i;
                (int endLine, string sig) = JoinDecl(lines, ref i, trimmedCode);
                var sym = new Symbol(start + 1, endLine, kw, name, TrimSig(sig), TakeDoc(pendingDoc), []);
                topTypes.Add(sym);
                stack.Push((depth, sym)); // depth внутри типа = depth+1 после '{' на этой строке
                depth += TextUtil.CountBraces(code);
                continue;
            }

            // члены — если вершина стека type-like и строка на глубине типа+1
            // (LineOpensBlock не требуется: сигнатура может быть многострочной)
            if (stack.Count > 0 && IsTypeLike(stack.Peek().Sym.Kind) && depth == stack.Peek().Depth + 1) {
                var member = MatchMember(trimmedCode);
                if (member is not null) {
                    int start = i;
                    (int endLine, string sig) = JoinDecl(lines, ref i, trimmedCode);
                    var sym = new Symbol(start + 1, endLine, member.Value.Kind, member.Value.Name, TrimSig(sig), TakeDoc(pendingDoc), []);
                    stack.Peek().Sym.Children.Add(sym);
                    if (member.Value.OpensScope) {
                        stack.Push((depth, sym));
                        depth += TextUtil.CountBraces(code);
                        continue;
                    }
                    depth += TextUtil.CountBraces(code);
                    continue;
                }
            }

            // глубина и закрытие узлов
            depth += TextUtil.CountBraces(code);
            while (stack.Count > 0 && depth <= stack.Peek().Depth) {
                var (_, sym) = stack.Pop();
                sym.EndLine = Math.Max(sym.EndLine, i + 1);
            }
            pendingDoc.Clear();
        }

        if (fileNs is not null && roots.Count > 0 && roots[0].Kind != "namespace")
            return new([new(fileNsLine, lines.Count, "namespace", fileNs, $"namespace {fileNs}", null, roots)], "csharp", usings, warnings);
        return new(roots, "csharp", usings, warnings);
    }

    static bool IsTypeLike(string kind) => kind is "namespace" or "class" or "interface" or "struct"
        or "record" or "record struct" or "record class" or "enum";

    static bool LineOpensBlock(string code) => code.Contains('{') || code.EndsWith(';') || code.Contains("=>");

    /// <summary>Сигнатура может быть многострочной: дотянуть до '{' или ';' (максимум 5 строк).</summary>
    static (int EndLine, string Sig) JoinDecl(IReadOnlyList<string> lines, ref int i, string first) {
        if (first.Contains('{') || first.Contains(';') || first.Contains("=>")) {
            string s = first.TrimEnd('{', ';').Trim();
            return (i + 1, s);
        }
        var sig = new StringBuilder(first);
        int endLine = i + 1;
        for (int k = 1; k <= 5 && i + k < lines.Count; k++) {
            string next = LiteralLexer.Strip(lines[i + k], new LexerState(), LiteralLexer.CSharp).Trim();
            sig.Append(' ').Append(next);
            endLine = i + k + 1;
            if (next.Contains('{') || next.Contains(';') || next.Contains("=>")) { i += k; return (endLine, sig.ToString().TrimEnd('{', ';').Trim()); }
        }
        return (endLine, sig.ToString());
    }

    /// <summary>Член типа: method/ctor/property/field/event. Управляющие конструкции отсеяны.</summary>
    static (string Kind, string Name, bool OpensScope)? MatchMember(string code) {
        string t = code;
        int eq = t.IndexOf('=');
        // event
        var ev = EventDecl().Match(t);
        if (ev.Success) return ("event", ev.Groups[2].Value, t.Contains('{') && !t.EndsWith(';'));

        // метод/ctor: имя перед '(' до '=' (лямбды/присваивания — не декларации)
        string head = eq >= 0 ? t[..eq] : t;
        if (head.Contains('(')) {
            var m = NameBeforeParen().Match(head);
            if (m.Success) {
                string name = m.Groups[1].Value;
                if (Control.Contains(name)) return null;
                string before = head[..m.Index].Trim();
                if (before.Length == 0) return null; // вызов вида Foo(...)
                return ("method", name, !t.EndsWith(';'));
            }
        }

        // property: Имя { / Имя => ; field: Имя [= ...]; — после отсечения '='-хвоста
        string decl = eq >= 0 ? t[..eq] : t;
        var pp = NameBeforeProp().Match(decl);
        if (pp.Success) {
            string name = pp.Groups[1].Value;
            if (Control.Contains(name)) return null;
            if (decl.EndsWith("=>")) return ("property", name, false); // expression-bodied: тело до ';'
            return ("property", name, true);
        }
        int semi = decl.IndexOf(';');
        if (semi > 0) {
            var words = decl[..semi].Trim().Split(' ');
            if (words.Length >= 2 && !Control.Contains(words[^1]) && !words[^1].Contains('('))
                return ("field", words[^1].TrimEnd(';'), false);
        }
        return null;
    }

    static string TrimSig(string s) => TextUtil.Truncate(s, SigMax);

    static void AppendDoc(StringBuilder sb, string line) {
        if (sb.Length < 2000) sb.AppendLine(line.Trim());
    }

    /// <summary>Первая содержательная строка ///-блока: текст <summary> без XML-тегов.</summary>
    static string? TakeDoc(StringBuilder pending) {
        if (pending.Length == 0) return null;
        foreach (var line in pending.ToString().Split('\n')) {
            var s = line.Trim().TrimStart('/');
            s = Regex.Replace(s, @"</?\w+[^>]*>", "").Trim();
            if (s.Length > 0) { pending.Clear(); return s.Length <= 100 ? s : s[..100] + "…"; }
        }
        pending.Clear();
        return null;
    }
}
