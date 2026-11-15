using System.Text.RegularExpressions;
using TreeSitter;

namespace SmartEdit.Mcp.Core;

/// <summary>
/// #23 (v0.13.2): извлечение символов для project-map парсером tree-sitter — те же грамматики,
/// что возит синтакс-гейт (.cs/.js/.ts/.tsx/.py). Решение 2026-11-15: дерево вместо регэкспа —
/// базы наследования это готовые узлы (base_list/superclasses/class_heritage), а не разбор двоеточий.
/// Экстент узла гарантированно прав (строки/комментарии/where-клаuses не мешают); интерпретация
/// внутри узла — текстовая. XML-doc привязывается по строкам (дерево комментарии с декларациями не связывает).
/// </summary>
public static partial class TreeSitterSymbols {
    static readonly object LoadLock = new();
    static readonly Dictionary<string, Engine?> Engines = new();

    sealed class Engine {
        public required Parser Parser { get; init; }
        public required SemaphoreSlim Lock { get; init; }
    }

    static readonly Dictionary<string, string> LangByExt = new(StringComparer.OrdinalIgnoreCase) {
        [".cs"] = "C-Sharp",
        [".js"] = "JavaScript", [".mjs"] = "JavaScript", [".cjs"] = "JavaScript",
        [".ts"] = "TypeScript", [".tsx"] = "TSX",
        [".py"] = "Python", [".pyw"] = "Python",
    };

    public static bool Applies(string path) => LangByExt.ContainsKey(Path.GetExtension(path));

    /// <summary>Карта файла (типы/члены/базы/using); пустая — если грамматика не загрузилась. Никогда не бросает.</summary>
    public static FileMap Extract(string path, string text) {
        if (!LangByExt.TryGetValue(Path.GetExtension(path), out var langName)) return new(path, "text", null, [], []);
        Engine? eng;
        lock (LoadLock) {
            if (!Engines.TryGetValue(langName, out eng)) {
                try {
                    var lang = new Language(langName); // здесь грузится нативная dll грамматики
                    eng = new Engine { Parser = new Parser(lang), Lock = new SemaphoreSlim(1) };
                }
                catch (Exception ex) {
                    Infra.Log.Warn($"map extractor: грамматика {langName} не загрузилась: {ex.Message}");
                    eng = null;
                }
                Engines[langName] = eng;
            }
        }
        if (eng is null) return new(path, langName, null, [], []);

        try {
            eng.Lock.Wait();
            try {
                using var tree = eng.Parser.Parse(text);
                if (tree is null) return new(path, langName, null, [], []);
                var docs = CollectComments(tree.RootNode); // /// и /** по строкам
                return Path.GetExtension(path) switch {
                    ".cs" => ExtractCs(path, tree.RootNode, docs),
                    ".py" or ".pyw" => ExtractPy(path, tree.RootNode),
                    _ => ExtractTs(path, tree.RootNode),
                };
            }
            finally { eng.Lock.Release(); }
        }
        catch (Exception ex) {
            Infra.Log.Warn($"map extractor: parse {Path.GetFileName(path)} упал: {ex.Message}");
            return new(path, langName, null, [], []);
        }
    }

    // ── C# ──────────────────────────────────────────────────────────────────

    static FileMap ExtractCs(string path, Node root, Dictionary<int, string> docs) {
        string? ns = null;
        var usings = new List<string>();
        var types = new List<MapType>();
        WalkCs(root, 0, usings, ref ns, types, docs);
        return new FileMap(path, "cs", ns, usings, types);
    }

    /// <summary>Один проход: using-и, namespace-чип, типы (включая вложенные, до глубины 3).</summary>
    static void WalkCs(Node n, int depth, List<string> usings, ref string? ns, List<MapType> types, Dictionary<int, string> docs) {
        foreach (var c in n.Children) {
            if (!c.IsNamed) continue;
            switch (c.Type) {
                case "using_directive": {
                    var uname = c.GetChildForField("name");
                    if (uname is { IsNamed: true }) usings.Add(uname.Text);
                    break;
                }
                case "file_scoped_namespace_declaration" or "namespace_declaration" when ns is null: {
                    var nname = c.GetChildForField("name");
                    if (nname is { IsNamed: true }) ns = nname.Text;
                    break;
                }
                case "class_declaration" or "record_declaration" or "interface_declaration"
                     or "struct_declaration" or "enum_declaration" when depth <= 3:
                    types.Add(CsType(c, docs));
                    break;
            }
            WalkCs(c, depth + 1, usings, ref ns, types, docs);
        }
    }

    static MapType CsType(Node n, Dictionary<int, string> docs) {
        string kind = n.Type == "record_declaration" ? "record"
            : n.Type == "interface_declaration" ? "interface"
            : n.Type == "struct_declaration" ? "struct"
            : n.Type == "enum_declaration" ? "enum" : "class";
        var bases = new List<string>();
        var baseList = ChildByType(n, "base_list"); // поле НЕ ИМЕНОВАНО в грамматике — по типу узла
        if (baseList is { IsNamed: true })
            foreach (var b in SplitTopLevel(baseList.Text.TrimStart(':')))
                bases.Add(SimpleName(b));

        var members = new List<MapMember>();
        var body = n.GetChildForField("body");
        if (body is { IsNamed: true })
            foreach (var m in body.Children) {
                if (!m.IsNamed) continue;
                MapMember? mm = m.Type switch {
                    "method_declaration" => new MapMember(Row(m), "method", NameOf(m), ArgsOf(m.GetChildForField("parameters")), null),
                    "constructor_declaration" => new MapMember(Row(m), "ctor", NameOf(m), ArgsOf(m.GetChildForField("parameters")), null),
                    "property_declaration" => new MapMember(Row(m), "property", NameOf(m), "", null),
                    "field_declaration" => CsField(m),
                    _ => null,
                };
                if (mm is { } v) members.Add(v with { Doc = DocJustAbove(docs, v.Line) });
            }
        return new MapType(kind, NameOf(n), Row(n), (int)n.EndPosition.Row + 1, bases, members, DocJustAbove(docs, Row(n)));
    }

    static MapMember? CsField(Node m) {
        // field_declaration → variable_declaration → variable_declarator(name); берём первый декларатор
        foreach (var vd in m.Children)
            if (vd.Type == "variable_declaration")
                foreach (var d in vd.Children)
                    if (d.Type == "variable_declarator")
                        return new MapMember(Row(m), "field", NameOf(d), "", null);
        return null;
    }

    // ── Python ──────────────────────────────────────────────────────────────

    static FileMap ExtractPy(string path, Node root) {
        var usings = new List<string>();
        var types = new List<MapType>();
        WalkPy(root, usings, types, depth: 0);
        return new FileMap(path, "py", null, usings, types);
    }

    static void WalkPy(Node n, List<string> usings, List<MapType> types, int depth) {
        foreach (var c in n.Children) {
            if (!c.IsNamed) continue;
            if (c.Type is "import_statement" or "import_from_statement" or "future_import_statement") {
                usings.Add(SimpleName(c.Text.Split(" import")[0].Replace("from ", "").Replace("import ", "").Trim()));
                continue;
            }
            if (c.Type == "class_definition") {
                var supers = c.GetChildForField("superclasses");
                var bases = new List<string>();
                if (supers is { IsNamed: true })
                    foreach (var b in SplitTopLevel(supers.Text.Trim('(', ')')))
                        bases.Add(SimpleName(b.Split('=')[0]));
                var body = c.GetChildForField("body");
                var members = new List<MapMember>();
                if (body is { IsNamed: true })
                    foreach (var m in body.Children) {
                        if (m.Type != "function_definition") continue;
                        members.Add(new MapMember(Row(m), "def", NameOf(m),
                            PyArgs(m.GetChildForField("parameters")), null));
                    }
                types.Add(new MapType("class", NameOf(c), Row(c), (int)c.EndPosition.Row + 1, bases, members, FirstStringDoc(body)));
                // без углубления: вложенные классы редки, а методы уже сняты
                continue;
            }
            if (c.Type == "function_definition" && depth == 0)
                types.Add(new MapType("def", NameOf(c), Row(c), (int)c.EndPosition.Row + 1, [],
                    [new MapMember(Row(c), "def", NameOf(c), PyArgs(c.GetChildForField("parameters")), null)], null));
        }
        // углубление нужно только для top-level функции/классов в составных узлах (module → ...)
        if (depth == 0)
            foreach (var c in n.Children)
                if (c.IsNamed && c.Type != "class_definition" && c.Type != "function_definition"
                    && !c.Type.Contains("import"))
                    WalkPy(c, usings, types, depth + 1);
    }

    static string PyArgs(Node? parameters) {
        if (parameters is not { IsNamed: true }) return "";
        var names = new List<string>();
        foreach (var p in SplitTopLevel(parameters.Text.Trim('(', ')'))) {
            var nm = p.Split(':')[0].Split('=')[0].Trim();
            if (nm.Length > 0 && nm != "self" && nm != "cls") names.Add(nm.Split(' ')[^1]);
        }
        return string.Join(", ", names);
    }

    /// <summary>docstring py: первый string-узел тела блока.</summary>
    static string? FirstStringDoc(Node? body) {
        if (body is not { IsNamed: true }) return null;
        foreach (var c in body.Children)
            if (c.IsNamed) return c.Type == "string" ? TrimDoc(c.Text.Trim('\'', '"')) : null;
        return null;
    }

    // ── TS/TSX/JS ───────────────────────────────────────────────────────────

    static FileMap ExtractTs(string path, Node root) {
        var usings = new List<string>();
        var types = new List<MapType>();
        WalkTs(root, usings, types, depth: 0);
        string lang = Path.GetExtension(path) switch { ".ts" => "ts", ".tsx" => "tsx", _ => "js" };
        return new FileMap(path, lang, null, usings, types);
    }

    static void WalkTs(Node n, List<string> usings, List<MapType> types, int depth) {
        foreach (var c in n.Children) {
            if (!c.IsNamed) continue;
            if (c.Type == "import_statement" && depth == 0) {
                var src = c.GetChildForField("source");
                if (src is { IsNamed: true }) usings.Add(src.Text.Trim('\'', '"'));
            }
            else if (c.Type == "class_declaration") {
                var heritage = ChildByType(c, "class_heritage"); // поле не именовано — по типу узла
                var bases = new List<string>();
                if (heritage is { IsNamed: true })
                    foreach (var b in SplitTopLevel(heritage.Text.Replace("extends", " ").Replace("implements", " ")))
                        bases.Add(SimpleName(b));
                var body = c.GetChildForField("body");
                var members = new List<MapMember>();
                if (body is { IsNamed: true })
                    foreach (var m in body.Children) {
                        if (!m.IsNamed) continue;
                        if (m.Type == "method_definition")
                            members.Add(new MapMember(Row(m), "method", NameOf(m), TsArgs(m.GetChildForField("parameters")), null));
                        else if (m.Type is "property_definition" or "public_field_definition" or "property_declaration")
                            members.Add(new MapMember(Row(m), "property", NameOf(m), "", null));
                    }
                types.Add(new MapType("class", NameOf(c), Row(c), (int)c.EndPosition.Row + 1, bases, members, null));
            }
            else if (c.Type == "function_declaration" && depth == 0)
                types.Add(new MapType("def", NameOf(c), Row(c), (int)c.EndPosition.Row + 1, [],
                    [new MapMember(Row(c), "def", NameOf(c), TsArgs(c.GetChildForField("parameters")), null)], null));
        }
        if (depth == 0)
            foreach (var c in n.Children)
                if (c.IsNamed && c.Type != "class_declaration" && c.Type != "function_declaration"
                    && c.Type != "import_statement")
                    WalkTs(c, usings, types, depth + 1);
    }

    static string TsArgs(Node? parameters) {
        if (parameters is not { IsNamed: true }) return "";
        var names = new List<string>();
        foreach (var p in SplitTopLevel(parameters.Text.Trim('(', ')')))
            names.Add(SimpleName(p.Split(':')[0]));
        return string.Join(", ", names);
    }

    // ── общие помощники ──────────────────────────────────────────────────────

    /// <summary>Все /// и /** комментарии файла, ключ — строка (1-based). Клаusage дерева не связывает.</summary>
    static Dictionary<int, string> CollectComments(Node root) {
        var docs = new Dictionary<int, string>();
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0) {
            var n = stack.Pop();
            if (n.Type == "comment") {
                string t = n.Text;
                if (t.StartsWith("///") || t.StartsWith("/**"))
                    docs[(int)n.StartPosition.Row + 1] = TrimDoc(t.StartsWith("///") ? t[3..] : t.Trim('/', '*'));
            }
            foreach (var c in n.Children) stack.Push(c);
        }
        return docs;
    }

    /// <summary>Комментарий непосредственно над декларацией (цепочка ///-строк допустима).</summary>
    static string? DocJustAbove(Dictionary<int, string> docs, int row) {
        int r = row - 1;
        while (docs.ContainsKey(r)) r--;               // докатываемся до начала цепочки
        int top = r + 1;
        return top < row ? docs.GetValueOrDefault(top) // была цепочка
                         : docs.GetValueOrDefault(row - 1); // одиночная строка прямо над
    }

    static string TrimDoc(string s) {
        s = DocTags().Replace(s.Trim(), "").Trim(); // XML-теги срезаем: <summary>X</summary> → X
        return s.Length <= 90 ? s : s[..89] + "…";
    }

    [GeneratedRegex(@"</?\w+[^>]*>")]
    private static partial Regex DocTags();

    /// <summary>Первый ребёнок по ТИПУ узла — для неименованных полей (base_list, class_heritage).</summary>
    static Node? ChildByType(Node n, string type) {
        foreach (var c in n.Children)
            if (c.Type == type) return c;
        return null;
    }

    static string NameOf(Node n) {
        var name = n.GetChildForField("name");
        if (name is { IsNamed: true }) return name.Text;
        foreach (var c in n.Children)
            if (c.IsNamed && (c.Type.EndsWith("identifier") || c.Type.EndsWith("declarator")))
                return c.Text;
        return "?";
    }

    static string ArgsOf(Node? parameters) {
        if (parameters is not { IsNamed: true }) return "";
        var names = new List<string>();
        foreach (var p in SplitTopLevel(parameters.Text.Trim('(', ')')))
            names.Add(p.Trim().Split(' ')[^1].Split(':')[0].Split('=')[0].Trim());
        return string.Join(", ", names);
    }

    /// <summary>Разделение по запятым уровня 0 (вложенность &lt;&gt; и () учтена). Экстент узла уже верен —
    /// это интерпретация, не распознавание.</summary>
    static List<string> SplitTopLevel(string s) {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++) {
            char c = s[i];
            if (c is '<' or '(' or '[') depth++;
            else if (c is '>' or ')' or ']') depth--;
            else if (c == ',' && depth == 0) { parts.Add(s[start..i].Trim()); start = i + 1; }
        }
        if (s.Length - start > 0) {
            var last = s[start..].Trim();
            if (last.Length > 0) parts.Add(last);
        }
        return parts;
    }

    /// <summary>Простое имя из текста узла-базы: срезать аргументы, дженерики, квалификацию.</summary>
    static string SimpleName(string t) {
        t = t.Split('(')[0].Split('<')[0].Trim();
        int dot = t.LastIndexOf('.');
        if (dot >= 0) t = t[(dot + 1)..];
        return t;
    }

    static int Row(Node n) => (int)n.StartPosition.Row + 1;
}
