using System.Text.RegularExpressions;
using SmartEdit.Mcp.Infra;
using TreeSitter;

namespace SmartEdit.Mcp.Core;

/// <summary>
/// gate/v1 (#18): синтаксический гейт сохранения на tree-sitter. Двигатель встроен в ядро,
/// грамматики (.cs/.js/.ts/.tsx/.py/.sh) подгружаются лениво при первом сохранении своего языка.
/// Сигнал — собранные ERROR/MISSING узлы (НЕ корневой HasError: у грамматики C# есть фантомные
/// узлы с error-state на валидных конструкциях — корпус репозитория это показал).
/// Дельта считается по отпечаткам (fingerprint), не по координатам: сдвиг строк и переезд кода
/// не рождают ложных «новых» ошибок. Baseline хранит EditSession; check() его не трогает.
/// </summary>
public static partial class SyntaxGate {
    // расширение → имя грамматики tree-sitter; нет маппинга → гейт не применяется (off)
    static readonly Dictionary<string, string> LangByExt = new(StringComparer.OrdinalIgnoreCase) {
        [".cs"] = "C-Sharp",
        [".js"] = "JavaScript", [".mjs"] = "JavaScript", [".cjs"] = "JavaScript",
        [".ts"] = "TypeScript", [".tsx"] = "TSX",
        [".py"] = "Python", [".pyw"] = "Python",
        [".sh"] = "Bash", [".bash"] = "Bash",
    };

    /// <summary>Выше — гейт не гоняется (skipped_too_large): гигантские сгенерированные файлы нестоят парсинга на каждом save.</summary>
    public const int MaxGateChars = 4 << 20;
    static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(1500);

    sealed class Engine {
        public required Language Language;   // держим ссылку: Parser валиден, пока жив Language
        public required Parser Parser;
        public required SemaphoreSlim Lock;
    }

    static readonly object LoadLock = new();
    static readonly Dictionary<string, Engine> Engines = new();
    static readonly HashSet<string> Disabled = new();       // 3 удара подряд → до рестарта сервера
    static readonly Dictionary<string, int> Strikes = new();

    /// <summary>Одна синтаксическая ошибка. Row/Col — 1-based, конвент SmartEdit.</summary>
    public sealed record Diag(string Kind, string Token, int Row, int Col, string Fragment, string Fingerprint);

    /// <summary>Status: pass | degraded | skipped_too_large | off_no_grammar | off_disabled | off.</summary>
    public sealed record Result(string Status, List<Diag> Errors) {
        public static Result Off(string status = "off") => new(status, []);
    }

    public static bool Applies(string path) => LangByExt.ContainsKey(Path.GetExtension(path));

    /// <summary>Разобрать текст. Никогда не бросает и не зависает дольше Budget (провал → degraded).</summary>
    public static Result Analyze(string path, string text) {
        if (!LangByExt.TryGetValue(Path.GetExtension(path), out var langName)) return Result.Off();
        if (text.Length > MaxGateChars) return Result.Off("skipped_too_large");

        Engine eng;
        lock (LoadLock) {
            if (Disabled.Contains(langName)) return Result.Off("off_disabled");
            if (!Engines.TryGetValue(langName, out var e)) {
                try {
                    var lang = new Language(langName); // здесь грузится нативная dll грамматики
                    e = new Engine { Language = lang, Parser = new Parser(lang), Lock = new SemaphoreSlim(1) };
                    Engines[langName] = e;
                    Log.Info($"syntax gate: грамматика {langName} загружена (лениво, при первом обращении)");
                }
                catch (Exception ex) {
                    Log.Warn($"syntax gate: грамматика {langName} не загрузилась: {ex.Message}");
                    Disabled.Add(langName);
                    return Result.Off("off_no_grammar");
                }
            }
            eng = e;
        }

        try {
            // парс одного языка сериализован (Parser не потокобезопасен); общий бюджет — на ожидание
            var task = Task.Run(() => { eng.Lock.Wait(); try { return ParseAll(eng.Parser, text); } finally { eng.Lock.Release(); } });
            if (!task.Wait(Budget)) {
                var strikes = Strikes.GetValueOrDefault(langName) + 1;
                Strikes[langName] = strikes;
                if (strikes >= 3 && Disabled.Add(langName))
                    Log.Warn($"syntax gate: {langName} отключён до рестарта (3 таймаута подряд)");
                return Result.Off("degraded"); // тихий pass = дыра; блокируем с force-путём (решение консилиума)
            }
            Strikes[langName] = 0;
            return new Result("pass", task.Result);
        }
        catch (Exception ex) {
            Log.Warn($"syntax gate: parse {langName} упал: {ex.Message}");
            return Result.Off("degraded");
        }
    }

    /// <summary>Новые ошибки: отпечатки after сверх before (мультимножество — по количеству вхождений).</summary>
    public static List<Diag> NewErrors(List<Diag> before, List<Diag> after) {
        var counts = new Dictionary<string, int>();
        foreach (var d in before) counts[d.Fingerprint] = counts.GetValueOrDefault(d.Fingerprint) + 1;
        var fresh = new List<Diag>();
        foreach (var d in after) {
            if (counts.GetValueOrDefault(d.Fingerprint) > 0)
                counts[d.Fingerprint]--;
            else
                fresh.Add(d);
        }
        return fresh;
    }

    static List<Diag> ParseAll(Parser parser, string text) {
        using var tree = parser.Parse(text);
        var list = new List<Diag>();
        if (tree is null) return list;
        var stack = new Stack<Node>();
        stack.Push(tree.RootNode);
        while (stack.Count > 0) {
            var n = stack.Pop();
            if (n.IsError || n.IsMissing)
                list.Add(new Diag(
                    Kind: n.IsMissing ? "missing" : "error",
                    Token: n.IsMissing ? n.Type : "",
                    Row: (int)n.StartPosition.Row + 1,   // tree-sitter 0-based → SmartEdit 1-based
                    Col: (int)n.StartPosition.Column + 1,
                    Fragment: n.IsMissing ? "" : Trunc(Norm(n.Text)),
                    // отпечаток: для missing — сам пропущенный токен ("M:;"), для error — нормализованный
                    // текст фрагмента (позиция НЕ входит: съехавшая ошибка — та же ошибка)
                    Fingerprint: n.IsMissing ? "M:" + n.Type : "E:" + Trunc(Norm(n.Text), 160)));
            foreach (var c in n.Children) stack.Push(c);
        }
        return list;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    static string Norm(string s) => Whitespace().Replace(s, " ").Trim();
    static string Trunc(string s, int max = 80) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
