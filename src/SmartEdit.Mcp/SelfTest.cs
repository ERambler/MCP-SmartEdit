using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Rpc;
using SmartEdit.Mcp.Tools;

namespace SmartEdit.Mcp;

/// <summary>
/// SmartEdit.Mcp.exe selftest: вся логика без MCP-клиента. Плюс протоколный прогон
/// через McpProtocolHandler. Печатает PASS/FAIL, код выхода 0/1.
/// </summary>
public static class SelfTest {
    static int _failed;

    public static async Task<int> RunAsync() {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* redirected — не важно */ }
        Console.WriteLine("SmartEdit selftest");
        var dir = Path.Combine(Path.GetTempPath(), "smartedit-selftest");
        try { Directory.Delete(dir, true); } catch { }
        Directory.CreateDirectory(dir);

        MainFlow(dir);
        TrailingNewlineSemantics(dir);
        EmptyFile(dir);
        CrlfFile(dir);
        BomUtf8(dir);
        Cp1251File(dir);
        Utf16File(dir);
        MtimeConflict(dir);
        V02Features.CodeSymbols(dir);
        V02Features.PythonSymbols(dir);
        V02Features.MarkupTree(dir);
        V02Features.Brackets(dir);
        V02Features.CalcTests();
        V02Features.FileInfoAndConvert(dir);
        await V02Features.TreeMapAsync();
        V04Features.Collaboration(dir);
        V05Features.LogHandle();
        V05Nav.UndoAndGoto(dir);
        V051Detect.Utf8BoundaryCyrillic(dir);
        V053Security.DebugBrief();
        V054Update.Run(dir);
        V062Outline.Run(dir);
        V070Stale.Run(dir);
        V072SaveTorture.Run(dir);
        V073Harden.Run(dir);
        V080Literals.Run(dir);
        V081Help.Run(dir);
        V082Polish.Run(dir);
        V082Lock.Run(dir);
        V090PathTxn.Run(dir);
        V091Content.Run(dir);
        V092CrossProcess.Run(dir);
        V093Nag.Run(dir);
        V100Gate.Run(dir);
        V03Features.BatchAndAnchors(dir);
        await V03Features.StructuredErrorsAsync(dir);
        V03Features.ReplaceAllTests(dir);
        V03Features.TreeRecent();
        await BigFile(dir);
        await ProtocolFlow(dir);

        Console.WriteLine(_failed == 0 ? "ALL PASS" : $"{_failed} FAILED");
        try { Directory.Delete(dir, true); } catch { }
        return _failed == 0 ? 0 : 1;
    }

    // ── 1. основной сценарий: 1М строк, outline, find, replace, rev-гард, save-сверка ──

    static void MainFlow(string dir) {
        string p = Path.Combine(dir, "main.txt");
        var mirror = new List<string>();
        var sb = new StringBuilder();
        for (int i = 1; i <= 1_000_000; i++) {
            string l = i % 100_000 == 0 ? $"## Часть {i / 100_000}"
                : i == 5 ? "<!-- sec: intro -->"
                : i % 77_777 == 0 ? $"L{i} TODO: fix me"
                : $"L{i}";
            mirror.Add(l);
            sb.Append(l).Append('\n');
        }
        File.WriteAllText(p, sb.ToString(), new UTF8Encoding(false));

        var s = EditSession.Open(p, null);
        SelfTest.Check(s.TotalLines == 1_000_000, $"open: totalLines={s.TotalLines}");
        SelfTest.Check(!s.Crlf && s.DiskEncoding == "utf-8", "open: encoding/crlf");
        SelfTest.Check(s.Outline.Length == 11, $"open: outline entries={s.Outline.Length} (10 заголовков + 1 sec-маркер)");
        SelfTest.Check(s.Outline[0].Line == 5 && s.Outline[0].Title == "intro", "open: sec-маркер распознан");
        SelfTest.Check(s.TodoCount == 12, $"open: todoCount={s.TodoCount}");

        var (text, trunc) = s.Show(999_998, 1_000_000, 20_000);
        SelfTest.Check(!trunc && text.Contains("L999998") && text.Contains("## Часть 10"), "show: хвост файла");
        (_, trunc) = s.Show(1, 1_000_000, 1_000);
        SelfTest.Check(trunc, "show: max_chars триггерит урезку");

        var (hits, htr) = s.Find(new Regex("^L99999[0-9]$"), 0, 20);
        SelfTest.Check(!htr && hits.Count == 10 && hits[0].Line == 999_990, $"find: точный анкор, hits={hits.Count}");
        var (miss, _) = s.Find(new Regex("NEEDLE"), 0, 5);
        SelfTest.Check(miss.Count == 0, "find: пустой результат");

        var m = s.ReplaceLines(500_010, 500_012, "REPLACED\nкириллица ✓", 0, dryRun: false);
        SelfTest.Check(m.Rev == 1 && m.TotalLines == 999_999, $"replace: rev/total ({m.Rev}/{m.TotalLines})");
        SelfTest.Check(m.Diff.Contains("-L500010") && m.Diff.Contains("+REPLACED"), "replace: diff содержателен");
        mirror.RemoveRange(500_009, 3);
        mirror.InsertRange(500_009, ["REPLACED", "кириллица ✓"]);

        CheckThrows<ToolException>(() => s.ReplaceLines(1, 1, "x", 0, false), "rev-гард: устаревший rev отклонён", "stale");

        m = s.InsertAfter(0, "TOP", 1);
        SelfTest.Check(m.Rev == 2 && s.GetLineText(1) == "TOP", "insert_after 0: строка сверху");
        mirror.Insert(0, "TOP");
        m = s.InsertAfter(s.TotalLines, "BOTTOM", 2);
        SelfTest.Check(m.Rev == 3 && s.GetLineText(s.TotalLines) == "BOTTOM", "insert_after N: строка снизу");
        mirror.Add("BOTTOM");

        m = s.ReplaceLines(2, 2, "NEW", 3, dryRun: true);
        SelfTest.Check(m.DryRun && m.Rev == 3 && s.TotalLines == m.TotalLines, "dry_run: состояние не меняется");
        SelfTest.Check(File.Exists(p + ".smartedit-shadow"), "shadow существует после мутаций");

        var save = s.Save(backup: true, "utf8", force: false);
        SelfTest.Check(save.Hash.Length == 16 && save.BackupPath is not null, "save: hash + backup");
        string expected = string.Join('\n', mirror) + "\n";
        SelfTest.Check(File.ReadAllText(p) == expected, "save: диск == зеркальная модель");
        SelfTest.Check(!File.Exists(p + ".smartedit-shadow"), "shadow удалён после save");
        SelfTest.Check(s.Rev == 3 && !s.Dirty && s.GetLineText(1) == "TOP", "save: сессия жива, rev НЕ растёт (save не перенумеровывает)");
        s.Close(discard: false);
    }

    // ── 2. файл без завершающего перевода: правка нормализует хвост (документировано) ──

    static void TrailingNewlineSemantics(string dir) {
        string p = Path.Combine(dir, "nonl.txt");
        File.WriteAllText(p, "a\nb\nc");
        var s = EditSession.Open(p, null);
        SelfTest.Check(s.TotalLines == 3, "no-trailing: 3 строки");
        s.ReplaceLines(3, 3, "C", 0, false);
        s.Save(false, "utf8", false);
        SelfTest.Check(File.ReadAllText(p) == "a\nb\nC\n", "no-trailing: правка добавляет завершающий \\n");
        s.Dispose();

        File.WriteAllText(p, "a\nb\nc");
        s = EditSession.Open(p, null);
        s.InsertAfter(3, "d", 0, false);
        s.Save(false, "utf8", false);
        SelfTest.Check(File.ReadAllText(p) == "a\nb\nc\nd\n", "no-trailing: вставка в конец добавляет \\n");
        s.Dispose();
    }

    // ── 3. пустой файл ──

    static void EmptyFile(string dir) {
        string p = Path.Combine(dir, "empty.txt");
        File.WriteAllText(p, "");
        var s = EditSession.Open(p, null);
        SelfTest.Check(s.TotalLines == 0, "empty: 0 строк");
        CheckThrows<ArgumentException>(() => s.Show(1, 1, 100), "empty: show вне диапазона");
        s.InsertAfter(0, "x", 0, false);
        s.Save(false, "utf8", false);
        SelfTest.Check(File.ReadAllText(p) == "x\n", "empty: вставка в пустой файл");
        s.Dispose();
    }

    // ── 4. CRLF ──

    static void CrlfFile(string dir) {
        string p = Path.Combine(dir, "crlf.txt");
        File.WriteAllText(p, "one\r\ntwo\r\nthree\r\n");
        var s = EditSession.Open(p, null);
        SelfTest.Check(s.Crlf && s.GetLineText(2) == "two", "crlf: детект и чтение без \\r");
        s.ReplaceLines(2, 2, "TWO", 0, false);
        s.Save(false, "utf8", false);
        SelfTest.Check(File.ReadAllText(p) == "one\r\nTWO\r\nthree\r\n", "crlf: вставленный текст получил CRLF");
        s.Dispose();
    }

    // ── 5. UTF-8 BOM ──

    static void BomUtf8(string dir) {
        string p = Path.Combine(dir, "bom.txt");
        File.WriteAllText(p, "x\ny\n", new UTF8Encoding(true));
        var s = EditSession.Open(p, null);
        SelfTest.Check(s.DiskEncoding == "utf-8-bom" && s.GetLineText(1) == "x", "bom: детект и пропуск BOM");
        s.Dispose();
    }

    // ── 6. CP1251: транскод, save keep / utf8 ──

    static void Cp1251File(string dir) {
        var cp1251 = Encoding.GetEncoding(1251);
        string p = Path.Combine(dir, "ansi.txt");
        File.WriteAllBytes(p, cp1251.GetBytes("привет\nмир"));

        CheckThrows<IOException>(() => EditSession.Open(p, null), "cp1251: без параметра — подсказка", "cp1251");
        var s = EditSession.Open(p, "cp1251");
        SelfTest.Check(s.TotalLines == 2 && s.GetLineText(1) == "привет", "cp1251: открытие с параметром");
        s.ReplaceLines(2, 2, "мир! и ё", 0, false);
        var save = s.Save(backup: false, encoding: "keep", force: false);
        SelfTest.Check(save.Encoding == "cp1251", "cp1251: save keep");
        string expected = "привет\nмир! и ё\n"; // вставленная строка завершается LF-стилем файла
        SelfTest.Check(File.ReadAllBytes(p).SequenceEqual(cp1251.GetBytes(expected)), "cp1251: байты на диске = cp1251");

        save = s.Save(false, "utf8", false);
        SelfTest.Check(save.Encoding == "utf-8" && File.ReadAllText(p) == expected, "cp1251→utf8: конвертация при save");
        s.Dispose();
    }

    // ── 7. UTF-16LE: BOM, транскод, save keep round-trip ──

    static void Utf16File(string dir) {
        var utf16 = new UnicodeEncoding(false, true);
        string p = Path.Combine(dir, "u16.txt");
        File.WriteAllBytes(p, utf16.GetBytes("alpha\nbeta"));

        var s = EditSession.Open(p, "utf-16le");
        SelfTest.Check(s.TotalLines == 2 && s.GetLineText(2) == "beta", "utf16: открытие");
        s.ReplaceLines(1, 1, "Альфа", 0, false);
        s.Save(false, "keep", false);
        // save-путь UTF-16 всегда пишет BOM (GetBytes его не включает — добавляем руками)
        byte[] expected = utf16.GetPreamble().Concat(utf16.GetBytes("Альфа\nbeta")).ToArray();
        SelfTest.Check(File.ReadAllBytes(p).SequenceEqual(expected), "utf16: round-trip байты равны (BOM + LE)");
        s.Dispose();
    }

    // ── 8. внешний конфликт mtime/size ──

    static void MtimeConflict(string dir) {
        string p = Path.Combine(dir, "conflict.txt");
        // транскод-путь (MemorySource) не держит файл: внешняя правка возможна и видна по mtime/size.
        // mmap-путь файл держит — внешний писатель блокирован до close (это осознанное поведение).
        File.WriteAllBytes(p, Encoding.GetEncoding(1251).GetBytes("x\ny\n"));
        var s = EditSession.Open(p, "cp1251");
        s.ReplaceLines(1, 1, "z", 0, false);
        File.WriteAllText(p, "external\n");
        CheckThrows<ToolException>(() => s.Save(false, "utf8", false), "mtime: конфликт отклонён", "changed on disk");
        s.Save(false, "utf8", force: true);
        SelfTest.Check(File.ReadAllText(p) == "z\ny\n", "mtime: force=true перезаписывает");
        s.Dispose();
    }

    // ── 9. гигант: 300 МБ, 4 915 200 строк по 64 байта, тайминги ──

    static async Task BigFile(string dir) {
        string p = Path.Combine(dir, "big.txt");
        int lines = 300 * 1024 * 1024 / 64; // 4_915_200
        var sw = Stopwatch.StartNew();
        using (var fs = new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20)) {
            var chunk = new StringBuilder();
            for (int i = 0; i < lines; i++) {
                string head = i % 1_000_000 == 0 ? "NEEDLE " + i.ToString("D8") : i.ToString("D8");
                chunk.Append(head).Append('x', 63 - head.Length).Append('\n');
                if (chunk.Length >= 600_000) { fs.Write(Encoding.ASCII.GetBytes(chunk.ToString())); chunk.Clear(); }
            }
            if (chunk.Length > 0) fs.Write(Encoding.ASCII.GetBytes(chunk.ToString()));
        }
        Console.WriteLine($"  big: генерация 300МБ за {sw.Elapsed.TotalSeconds:0.0}с");

        sw.Restart();
        var s = EditSession.Open(p, null);
        Console.WriteLine($"  big: open {sw.Elapsed.TotalSeconds:0.00}с, lines={s.TotalLines}");
        SelfTest.Check(s.TotalLines == lines, $"big: totalLines={s.TotalLines} (ожидалось {lines})");

        sw.Restart();
        var (hits, _) = s.Find(new Regex("^NEEDLE"), 0, 20);
        Console.WriteLine($"  big: find {sw.Elapsed.TotalSeconds:0.00}с, hits={hits.Count}");
        SelfTest.Check(hits.Count == 5 && hits[^1].Line > 4_000_000, "big: 5 игл найдено");

        s.ReplaceLines(lines, lines, "REPLACED big", 0, false);
        sw.Restart();
        s.Save(backup: false, "utf8", force: false);
        Console.WriteLine($"  big: save {sw.Elapsed.TotalSeconds:0.00}с");
        SelfTest.Check(new FileInfo(p).Length == 300L * 1024 * 1024 - 64 + 13, "big: размер после правки");
        SelfTest.Check(s.GetLineText(s.TotalLines) == "REPLACED big", "big: последняя строка после reload");
        s.Dispose();
        await Task.CompletedTask;
    }

    // ── 10. протокол: initialize / tools/list / tools/call полный круг ──

    static async Task ProtocolFlow(string dir) {
        var tools = new ToolRegistry();
        tools.RegisterEditTools();
        var h = new McpProtocolHandler(tools);
        static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

        var (res, err) = await h.HandleAsync(new RpcRequest {
            Method = "initialize", Id = J("1"),
            Params = J("""{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"0"}}"""),
        });
        SelfTest.Check(err is null && res!.Value.GetProperty("serverInfo").GetProperty("name").GetString() == "smartedit", "proto: initialize");

        (res, err) = await h.HandleAsync(new RpcRequest { Method = "tools/list", Id = J("2") });
        SelfTest.Check(err is null && res!.Value.GetProperty("tools").GetArrayLength() == 23, "proto: 23 инструмента (v0.9.1 + replace_content)");

        string p = Path.Combine(dir, "proto.txt");
        File.WriteAllText(p, "1\n2\n3\n");
        (res, err) = await h.HandleAsync(new RpcRequest {
            Method = "tools/call", Id = J("3"),
            Params = J($"{{\"name\":\"open\",\"arguments\":{{\"path\":{JsonSerializer.Serialize(p, RpcJson.Default.String)}}}}}"),
        });
        var open = OpenPayload(res!, err);
        SelfTest.Check(open.TotalLines == 3 && open.Rev == 0, "proto: open через tools/call");

        (res, err) = await h.HandleAsync(new RpcRequest {
            Method = "tools/call", Id = J("4"),
            Params = J($"{{\"name\":\"replace_lines\",\"arguments\":{{\"session_id\":\"{open.SessionId}\",\"start\":2,\"end\":2,\"new_text\":\"два\",\"expected_rev\":0}}}}"),
        });
        var mut = MutPayload(res!, err);
        SelfTest.Check(mut.Rev == 1 && mut.Diff.Contains("+два"), "proto: replace_lines");

        (res, err) = await h.HandleAsync(new RpcRequest {
            Method = "tools/call", Id = J("5"),
            Params = J($"{{\"name\":\"save\",\"arguments\":{{\"session_id\":\"{open.SessionId}\"}}}}"),
        });
        var save = SavePayload(res!, err);
        SelfTest.Check(save.Encoding == "utf-8" && File.ReadAllText(p) == "1\nдва\n3\n", "proto: save на диск");

        (res, err) = await h.HandleAsync(new RpcRequest {
            Method = "tools/call", Id = J("6"),
            Params = J($"{{\"name\":\"close\",\"arguments\":{{\"session_id\":\"{open.SessionId}\"}}}}"),
        });
        SelfTest.Check(err is null && !res!.Value.GetProperty("isError").GetBoolean()
            && ClosePayload(res, err).Closed, "proto: close без isError");
    }

    internal static string PayloadText(JsonElement? result, RpcError? err) {
        SelfTest.Check(err is null, "proto: без JSON-RPC ошибки");
        return result!.Value.GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    static OpenResult OpenPayload(JsonElement? result, RpcError? err)
        => JsonSerializer.Deserialize<OpenResult>(PayloadText(result, err), RpcJson.Default.OpenResult)!;

    static CloseResult ClosePayload(JsonElement? result, RpcError? err)
        => JsonSerializer.Deserialize<CloseResult>(PayloadText(result, err), RpcJson.Default.CloseResult)!;

    static MutationResult MutPayload(JsonElement? result, RpcError? err)
        => JsonSerializer.Deserialize<MutationResult>(PayloadText(result, err), RpcJson.Default.MutationResult)!;

    static SaveResult SavePayload(JsonElement? result, RpcError? err)
        => JsonSerializer.Deserialize<SaveResult>(PayloadText(result, err), RpcJson.Default.SaveResult)!;

    // ── helpers ──

    public static void CheckPublic(bool cond, string name) => Check(cond, name);

    internal static void Check(bool cond, string name) {
        Console.WriteLine((cond ? "  PASS: " : "  FAIL: ") + name);
        if (!cond) _failed++;
    }

    internal static void CheckThrows<TEx>(Action a, string name, string? contains = null) where TEx : Exception {
        try {
            a();
            SelfTest.Check(false, name + " (не бросил исключение)");
        }
        catch (TEx ex) when (contains is null || ex.Message.Contains(contains)
            || (ex is ToolException te && te.Kind == contains)) { // contains может быть kind-ом ToolException
            SelfTest.Check(true, name);
        }
        catch (Exception other) {
            SelfTest.Check(false, $"{name} (не тот тип: {other.GetType().Name}: {other.Message})");
        }
    }
}

/// <summary>v0.2: структурное зрение, скобки, calc, мета-утилиты.</summary>
static class V02Features {
    public static void CodeSymbols(string dir) {
        string p = Path.Combine(dir, "sample.cs");
        File.WriteAllText(p, """
            using System;
            using System.Text;

            namespace Demo.App;

            /// <summary>
            /// Сервис примера.
            /// </summary>
            public sealed class Service<T> where T : class {
                private readonly ILogger _log;

                /// <summary>Зависимость логгера.</summary>
                public ILogger Log { get; }

                public int Count { get; set; }

                /// <summary>Выполняет работу.</summary>
                public async Task<int> RunAsync(
                    string name,
                    int retries = 3) {
                    var x = 1;
                    return x;
                }

                public void Dispose() { }
            }

            public interface ILogger { void Info(string m); }
            """, new UTF8Encoding(false));
        var s = EditSession.Open(p, null);
        var r = s.Symbols(docsOnly: false);
        SelfTest.Check(r.Language == "csharp" && r.Usings.Count == 2, "cs-symbols: язык + usings");
        var ns = r.Roots.FirstOrDefault(x => x.Kind == "namespace");
        SelfTest.Check(ns?.Name == "Demo.App", "cs-symbols: file-scoped namespace");
        var svc = ns?.Children.FirstOrDefault(c => c.Name.StartsWith("Service"));
        SelfTest.Check(svc is not null && svc.Kind == "class" && svc.Doc == "Сервис примера.", "cs-symbols: класс + XML-doc");
        SelfTest.Check(s.GetLineText(svc!.Line).Contains("class Service"), $"cs-symbols: строка класса точна (line {svc.Line})");
        SelfTest.Check(svc.Children.Any(m => m.Kind == "method" && m.Name == "RunAsync" && m.Doc == "Выполняет работу."),
            "cs-symbols: метод с многострочной сигнатурой + doc");
        SelfTest.Check(svc.Children.Any(m => m.Kind == "property" && m.Name == "Log" && m.Doc == "Зависимость логгера."),
            "cs-symbols: property с doc");
        SelfTest.Check(svc.Children.Any(m => m.Kind == "field" && m.Name == "_log"), "cs-symbols: поле");
        SelfTest.Check(svc.Children.All(m => m.Name != "var"), "cs-symbols: тела методов не парсятся как члены");
        var docs = s.Symbols(docsOnly: true);
        var docd = docs.Roots.SelectMany(x => x.Children).SelectMany(x => x.Children);
        SelfTest.Check(docd.Count() >= 2 && docd.All(d => d.Doc is not null), "cs-symbols: docs_only фильтрует");
        var (hits, _) = s.Find(new Regex("readonly ILogger"), 0, 5);
        var where = hits.FirstOrDefault(h => h.Where != null)?.Where;
        SelfTest.Check(where == "Demo.App.Service<T>._log", $"cs-symbols: find breadcrumb = {where}");
        s.Dispose();
    }

    public static void PythonSymbols(string dir) {
        string p = Path.Combine(dir, "sample.py");
        File.WriteAllText(p, """"
            import os
            import sys

            class Greeter:
                """Приветствует мир."""
                def greet(self, name):
                    """Возвращает приветствие."""
                    return f"hi {name}"

            def main():
                return 0
            """", new UTF8Encoding(false));
        var s = EditSession.Open(p, null);
        var r = s.Symbols(false);
        var cls = r.Roots.FirstOrDefault(x => x.Kind == "class" && x.Name == "Greeter");
        SelfTest.Check(cls is not null && cls.Doc == "Приветствует мир.", "py-symbols: класс + docstring");
        SelfTest.Check(cls?.Children.Any(m => m.Kind == "def" && m.Name.StartsWith("greet") && m.Doc == "Возвращает приветствие.") == true,
            "py-symbols: метод + docstring");
        SelfTest.Check(r.Roots.Any(x => x.Kind == "def" && x.Name.StartsWith("main")), "py-symbols: функция верхнего уровня");
        SelfTest.Check(r.Usings.Count == 2, "py-symbols: imports");
        s.Dispose();
    }

    public static void MarkupTree(string dir) {
        string p = Path.Combine(dir, "sample.html");
        File.WriteAllText(p, "<html>\n<head><title>T</title></head>\n<body>\n<br>\n<div id=\"a\"><p>hi</p></div>\n</body>\n</html>\n", new UTF8Encoding(false));
        var s = EditSession.Open(p, null);
        var r = s.Symbols(false);
        var html = r.Roots.FirstOrDefault(x => x.Name == "html");
        SelfTest.Check(html is not null, "markup: корень html");
        SelfTest.Check(html?.Children.Any(c => c.Name == "head") == true && html?.Children.Any(c => c.Name == "body") == true, "markup: head+body");
        var body = html?.Children.FirstOrDefault(c => c.Name == "body");
        SelfTest.Check(body?.Children.Any(d => d.Name == "div" && d.Children.Any(pp => pp.Name == "p")) == true, "markup: div>p вложенность");
        SelfTest.Check(r.Warnings.Count == 0, $"markup: без предупреждений ({string.Join("; ", r.Warnings.Take(3))})");
        s.Dispose();

        string bad = Path.Combine(dir, "bad.xml");
        File.WriteAllText(bad, "<a>\n<b>\n</a>\n");
        var s2 = EditSession.Open(bad, null);
        SelfTest.Check(s2.Symbols(false).Warnings.Count > 0, "markup: незакрытый <b> пойман");
        s2.Dispose();
    }

    public static void Brackets(string dir) {
        string p = Path.Combine(dir, "brackets.cs");
        File.WriteAllText(p, "class A {\n    void M() {\n        int x = 1;\n    }\n}\n", new UTF8Encoding(false));
        var s = EditSession.Open(p, null);
        SelfTest.Check(s.CheckBrackets(0, 0).Count == 0, "brackets: чистый файл проходит");
        var m = s.ReplaceLines(3, 3, "        if (x > 0 {", 0, false); // потеряли ')'
        SelfTest.Check(m.Brackets is { Count: > 0 }, $"brackets: кривая правка семафорит ({string.Join("; ", m.Brackets ?? [])})");
        s.ReplaceLines(3, 3, "        if (x > 0) {", 1, false);
        var m2 = s.InsertAfter(4, "        }", 2, false);
        SelfTest.Check(m2.Brackets is { Count: 0 }, "brackets: восстановленный код чист");
        SelfTest.Check(s.CheckBrackets(0, 0).Count == 0, "brackets: полный чек файла чист");
        s.Dispose();

        // интерполяция: многострочный вызов с $"…", вложенные кавычки в дырке, {{ }} — экранированные
        string interp = Path.Combine(dir, "interp.cs");
        File.WriteAllText(interp,
            "class B {\n" +
            "    void M(int x) {\n" +
            "        throw new InvalidOperationException(\n" +
            "            $\"stale {x} — retry\");\n" +
            "        var d = new Dictionary<string, int>();\n" +
            "        var s = $\"k={d[\"k\"]} {{lit}} {x + 1}\";\n" +
            "    }\n" +
            "}\n", new UTF8Encoding(false));
        var s3 = EditSession.Open(interp, null);
        SelfTest.Check(s3.CheckBrackets(0, 0).Count == 0,
            "brackets: интерполяция (многострочный вызов + {{ }} + кавычки в дырке) чиста");
        s3.Dispose();

        // v0.7.1: якоря — каждый отчёт указывает на строку ОТКРЫВАЮЩЕЙ скобки
        string anchors = Path.Combine(dir, "anchors.cs");
        File.WriteAllText(anchors,
            "class C {\n" +           // 1: '{' A — не закрыта (внешняя, в отчёте ПЕРВОЙ)
            "    void M() {\n" +      // 2: '{' B — не закрыта
            "        if (x > 0\n" +   // 3: '(' C — не закрыта
            "            Do(1;\n" +   // 4: '(' D — не закрыта
            "    }\n" +               // 5: '}' при открытой D → mismatch: якорь на line 4, ресинк
            "}\n", new UTF8Encoding(false)); // 6: '}' при открытой C → mismatch: якорь на line 3
        var s4 = EditSession.Open(anchors, null);
        var probs = s4.CheckBrackets(0, 0);
        SelfTest.CheckPublic(probs.Count > 0 && probs[0].StartsWith("line 5:") && probs[0].Contains("from line 4 ")
            && probs[0].Contains("missing ')'"),
            $"brackets: mismatch даёт якорь на открывающую ({probs.FirstOrDefault() ?? "ничего"})");
        SelfTest.CheckPublic(probs.Any(p => p.StartsWith("line 1:") && p.Contains("never closed")),
            "brackets: незакрытая внешняя '{' репортится на СВОЕЙ строке открытия");
        SelfTest.CheckPublic(probs.Where(p => p.Contains("never closed")).Select(p => p.Split(':')[0]).SequenceEqual(new[] { "line 1", "line 2" }),
            "brackets: незакрытые — в порядке исходника, внешняя первой (без каскада от ресинка)");
        s4.Dispose();

        string stray = Path.Combine(dir, "stray.cs");
        File.WriteAllText(stray, "int a = 1;\n}\n", new UTF8Encoding(false));
        var s5 = EditSession.Open(stray, null);
        var strayProblems = s5.CheckBrackets(0, 0);
        SelfTest.CheckPublic(strayProblems.Count == 1 && strayProblems[0].StartsWith("line 2:") && strayProblems[0].Contains("closes nothing"),
            $"brackets: лишняя закрывающая — строка закрытия + «closes nothing» ({string.Join("; ", strayProblems)})");
        s5.Dispose();
    }

    public static void CalcTests() {
        SelfTest.Check(Calc0("2+2*2") == "6 (0x6)", $"calc: приоритет ({Calc0("2+2*2")})");
        SelfTest.Check(Calc0("0x10 * 2").StartsWith("32"), $"calc: hex ({Calc0("0x10 * 2")})");
        SelfTest.Check(Calc0("10!").StartsWith("3628800"), $"calc: факториал ({Calc0("10!")})");
        SelfTest.Check(Calc0("2^10").StartsWith("1024"), $"calc: степень ({Calc0("2^10")})");
        SelfTest.Check(Calc0("min(3, sqrt(16))").StartsWith("3"), $"calc: функции ({Calc0("min(3, sqrt(16))")})");
        SelfTest.Check(Calc0("1/3").Contains("0.333"), $"calc: float ({Calc0("1/3")})");
        SelfTest.Check(Calc0("0b101").StartsWith("5"), $"calc: binary ({Calc0("0b101")})");
        try { Calc.Evaluate("2+"); SelfTest.CheckPublic(false, "calc: синтакс-ошибка отклонена"); }
        catch (ArgumentException) { SelfTest.CheckPublic(true, "calc: синтакс-ошибка отклонена"); }
    }

    static string Calc0(string e) => Calc.Evaluate(e);

    public static void FileInfoAndConvert(string dir) {
        var cp1251 = Encoding.GetEncoding(1251);
        string p = Path.Combine(dir, "conv.txt");
        File.WriteAllBytes(p, cp1251.GetBytes("текст\nперекодировка\n"));

        var tools = new ToolRegistry();
        tools.RegisterEditTools();
        var fi = (FileInfoResult)Json(txt(tools, "file_info", $$"""{"path":{{JsonSerializer.Serialize(p, RpcJson.Default.String)}}}"""), RpcJson.Default.FileInfoResult)!;
        SelfTest.CheckPublic(!fi.IsUtf8 && fi.Encoding.StartsWith("not-utf8"), "file_info: не-utf8 распознан");
        SelfTest.CheckPublic(fi.Newlines == "lf" && fi.Bytes > 0, "file_info: newlines+bytes");

        var cv = (ConvertResult)Json(txt(tools, "convert", $$"""{"path":{{JsonSerializer.Serialize(p, RpcJson.Default.String)}},"to":"utf-8","from":"cp1251"}"""), RpcJson.Default.ConvertResult)!;
        SelfTest.CheckPublic(cv.From == "cp1251" && cv.To == "utf-8", "convert: cp1251→utf-8");
        SelfTest.CheckPublic(File.ReadAllText(p) == "текст\nперекодировка\n" && File.Exists(p + ".smartedit-bak"), "convert: содержимое сохранено + bak");

        var fi2 = (FileInfoResult)Json(txt(tools, "file_info", $$"""{"path":{{JsonSerializer.Serialize(p, RpcJson.Default.String)}}}"""), RpcJson.Default.FileInfoResult)!;
        SelfTest.CheckPublic(fi2.IsUtf8, "file_info: после конверта utf-8");
    }

    public static async Task TreeMapAsync() {
        string root = Path.Combine(Path.GetTempPath(), "smartedit-selftest", "proj");
        Directory.CreateDirectory(Path.Combine(root, "Sub"));
        Directory.CreateDirectory(Path.Combine(root, "obj"));
        File.WriteAllText(Path.Combine(root, "A.cs"), "namespace Ns.A;\nclass X {}\n");
        File.WriteAllText(Path.Combine(root, "Sub", "B.cs"), "namespace Ns.B;\nclass Y {}\n");
        File.WriteAllText(Path.Combine(root, "obj", "Skipped.cs"), "namespace Should.Not.Appear;\n");
        File.WriteAllText(Path.Combine(root, "readme.md"), "hi\n");

        var tools = new ToolRegistry();
        tools.RegisterEditTools();
        string args = $$"""{"path":{{JsonSerializer.Serialize(root, RpcJson.Default.String)}},"pattern":"*.*"}""";
        string json = await tools.CallAsync("tree", JsonDocument.Parse(args).RootElement.Clone(), default);
        var t = JsonSerializer.Deserialize<TreeResult>(json, RpcJson.Default.TreeResult)!;
        SelfTest.CheckPublic(t.Groups.Any(g => g.Namespace == "Ns.A") && t.Groups.Any(g => g.Namespace == "Ns.B"),
            $"tree: namespace-группы ({t.Groups.Length})");
        SelfTest.CheckPublic(t.Groups.All(g => g.Namespace != "Should.Not.Appear"), "tree: obj/ пропущен");
        SelfTest.CheckPublic(t.Groups.SelectMany(g => g.Files).Any(f => f.Path.Replace('\\', '/') == "Sub/B.cs" && f.Lines == 2),
            "tree: относительные пути + строки");
    }

    static string txt(ToolRegistry tools, string name, string argsJson) {
        var el = JsonDocument.Parse(argsJson).RootElement.Clone();
        return tools.CallAsync(name, el, default).GetAwaiter().GetResult();
    }

    static T Json<T>(string payload, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> ti)
        => JsonSerializer.Deserialize<T>(payload, ti)!;
}

/// <summary>v0.3: батч-правки, replace_all, якоря expect_old, структурированные ошибки, tree recent.</summary>
static class V03Features {
    public static void BatchAndAnchors(string dir) {
        string p = Path.Combine(dir, "batch.txt");
        File.WriteAllText(p, "one\ntwo\nthree\nfour\nfive\n", new UTF8Encoding(false));
        var s = EditSession.Open(p, null);

        // батч: замена с якорем + удаление + вставка в конец (end=start-1); применяется снизу вверх
        var ops = new List<BatchOp> {
            new(2, 2, "TWO", "two"),
            new(4, 4, "", null),
            new(6, 5, "SIX", null),
        };
        var b = s.EditBatch(ops, 0, dryRun: false);
        SelfTest.Check(b.Applied == 3 && b.Rev == 1 && s.TotalLines == 5, $"batch: applied/rev/total ({b.Applied}/{b.Rev}/{s.TotalLines})");
        SelfTest.Check(s.GetLineText(2) == "TWO" && s.GetLineText(4) == "five" && s.GetLineText(5) == "SIX",
            "batch: замена+удаление+вставка по исходным номерам (без пересчёта сдвигов)");
        SelfTest.Check(b.Diffs.Count == 3 && b.Diffs[0].Contains("-two") && b.Diffs[2].Contains("+SIX"), "batch: диффы по каждой правке в порядке агента");

        // кривой якорь → ВЕСЬ батч отклонён, состояние нетронуто
        var bad = new List<BatchOp> { new(1, 1, "X", null), new(3, 3, "Y", "WRONG") };
        SelfTest.CheckThrows<ToolException>(() => s.EditBatch(bad, 1, false), "batch: кривой якорь отклоняет всё", "anchor_mismatch");
        SelfTest.Check(s.Rev == 1 && s.GetLineText(1) == "one" && s.TotalLines == 5, "batch: после отказа состояние не тронуто");

        // перекрытие диапазонов → отказ
        var overlap = new List<BatchOp> { new(1, 3, "A", null), new(2, 2, "B", null) };
        SelfTest.CheckThrows<ToolException>(() => s.EditBatch(overlap, 1, false), "batch: перекрытие отклонено", "batch_overlap");

        // dry_run: диффы есть, rev не растёт
        var dr = s.EditBatch([new(1, 1, "ONE", "one")], 1, dryRun: true);
        SelfTest.Check(dr.DryRun && s.Rev == 1 && s.GetLineText(1) == "one", "batch: dry_run не меняет состояние");

        // expect_old в одиночной replace_lines
        SelfTest.CheckThrows<ToolException>(() => s.ReplaceLines(3, 3, "x", 1, false, "THREE"), "replace_lines: якорь-мисматч отклонён", "anchor_mismatch");
        var m = s.ReplaceLines(3, 3, "THREE!", 1, false, "three");
        SelfTest.Check(m.Rev == 2 && s.GetLineText(3) == "THREE!", "replace_lines: верный якорь применён");
        s.Dispose();

        // close(discard=true) грязной сессии: shadow ОСТАЁТСЯ на диске как страховка
        string q2 = Path.Combine(dir, "shadow.txt");
        File.WriteAllText(q2, "a\n");
        var s4 = EditSession.Open(q2, null);
        s4.ReplaceLines(1, 1, "b", 0, false);
        SelfTest.Check(File.Exists(q2 + ".smartedit-shadow"), "close-discard: shadow жив при открытой сессии");
        s4.Close(discard: true);
        SelfTest.Check(File.Exists(q2 + ".smartedit-shadow"), "close-discard: shadow СОХРАНЁН (восстановление = копирование поверх)");
        File.Delete(q2 + ".smartedit-shadow");
    }

    public static void ReplaceAllTests(string dir) {
        string p = Path.Combine(dir, "ren.txt");
        File.WriteAllText(p, "foo bar\nbaz foo foo\nnone\nfoo\n", new UTF8Encoding(false));
        var s = EditSession.Open(p, null);
        var r = s.ReplaceAll("foo", "QUX", 200, 0, false);
        SelfTest.Check(r.Occurrences == 4 && r.Lines.Count == 3 && r.Lines[0] == 1, $"replace_all: счётчики ({r.Occurrences}/{r.Lines.Count})");
        SelfTest.Check(s.GetLineText(2) == "baz QUX QUX" && s.Rev == 1, "replace_all: все вхождения в строке, один rev");
        SelfTest.CheckThrows<ToolException>(() => s.ReplaceAll("QUX", "x", 2, 1, false), "replace_all: кап-отказ целиком", "too_large");
        SelfTest.Check(s.GetLineText(1) == "QUX bar", "replace_all: после кап-отказа файл не тронут");
        SelfTest.Check(s.ReplaceAll("NO_SUCH_NEEDLE", "x", 200, 1, false).Occurrences == 0, "replace_all: ноль вхождений — no-op");
        s.Dispose();
    }

    /// <summary>Протокольный уровень: ошибка инструмента = isError + текст-JSON {kind, current_rev}.</summary>
    public static async Task StructuredErrorsAsync(string dir) {
        var tools = new ToolRegistry();
        tools.RegisterEditTools();
        var h = new McpProtocolHandler(tools);
        static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

        string p = Path.Combine(dir, "err.txt");
        File.WriteAllText(p, "a\nb\n");
        (var res, var err) = await h.HandleAsync(new RpcRequest {
            Method = "tools/call", Id = J("1"),
            Params = J($"{{\"name\":\"open\",\"arguments\":{{\"path\":{JsonSerializer.Serialize(p, RpcJson.Default.String)}}}}}"),
        });
        string sid = JsonSerializer.Deserialize<OpenResult>(SelfTest.PayloadText(res, err), RpcJson.Default.OpenResult)!.SessionId;

        (res, err) = await h.HandleAsync(new RpcRequest {
            Method = "tools/call", Id = J("2"),
            Params = J($"{{\"name\":\"replace_lines\",\"arguments\":{{\"session_id\":\"{sid}\",\"start\":1,\"end\":1,\"new_text\":\"z\",\"expected_rev\":42,\"retry_on_stale\":false}}}}"),
        });
        SelfTest.Check(err is null && res!.Value.GetProperty("isError").GetBoolean(), "errors: isError=true");
        var payload = JsonSerializer.Deserialize<ToolErrorPayload>(
            res!.Value.GetProperty("content")[0].GetProperty("text").GetString()!, RpcJson.Default.ToolErrorPayload)!;
        SelfTest.Check(payload.Kind == "stale_rev" && payload.CurrentRev == 0, $"errors: JSON kind/current_rev ({payload.Kind}/{payload.CurrentRev})");
        SelfTest.Check(payload.ServerVersion == Rpc.McpProtocolHandler.ServerVersion,
            $"errors: server_version в payload ({payload.ServerVersion ?? "null"}) — легаси-бинарник виден сразу (v0.9.3)");
    }

    public static void TreeRecent() {
        string root = Path.Combine(Path.GetTempPath(), "smartedit-selftest", "recent");
        try { Directory.Delete(root, true); } catch { }
        Directory.CreateDirectory(root);
        string a = Path.Combine(root, "a.txt"), b = Path.Combine(root, "b.txt");
        File.WriteAllText(a, "1\n"); File.WriteAllText(b, "2\n");
        File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddDays(-2));

        var tools = new ToolRegistry();
        tools.RegisterEditTools();
        string argsJson = $$"""{"path":{{JsonSerializer.Serialize(root, RpcJson.Default.String)}},"recent_hours":24}""";
        string json = tools.CallAsync("tree", JsonDocument.Parse(argsJson).RootElement.Clone(), default).GetAwaiter().GetResult();
        var t = JsonSerializer.Deserialize<TreeResult>(json, RpcJson.Default.TreeResult)!;
        var files = t.Groups.SelectMany(g => g.Files).ToList();
        SelfTest.CheckPublic(files.Count == 1 && files[0].Path == "a.txt", "tree-recent: только свежий файл");
        SelfTest.CheckPublic(files[0].ModifiedUtc != default, "tree-recent: modified_utc заполнен");
    }
}

/// <summary>v0.4: коллаборация — общий документ на путь, presence, peers_dirty, offender в stale_rev.</summary>
static class V04Features {
    public static void Collaboration(string dir) {
        string p = Path.Combine(dir, "collab.txt");
        File.WriteAllText(p, "a\nb\nc\n", new UTF8Encoding(false));

        var tools = new ToolRegistry();
        tools.RegisterEditTools();

        var o1 = Open(tools, p, "чиню конфиг");
        SelfTest.CheckPublic(!o1.Reused && o1.Peers.Length == 1 && o1.Peers[0].Note == "чиню конфиг", "collab: первый open");
        var o2 = Open(tools, p, "рефакторю");
        SelfTest.CheckPublic(o2.Reused && o2.Peers.Length == 2 && o2.Rev == 0, "collab: повторный open переиспользует документ (peers=2)");

        // правка от первого → у второго stale_rev называет виновника; show доносит события
        Mut(tools, o1.SessionId, 2, 2, "B!");
        var mutErr = CallErr(tools, "replace_lines", $$"""{"session_id":"{{o2.SessionId}}","start":1,"end":1,"new_text":"x","expected_rev":0,"retry_on_stale":false}""");
        SelfTest.CheckPublic(mutErr?.Kind == "stale_rev" && (mutErr.CurrentRev ?? -1) == 1 && mutErr.Message.Contains(o1.SessionId),
            $"collab: stale_rev называет виновника ({mutErr?.Kind})");
        var show = Call(tools, "show", $$"""{"session_id":"{{o2.SessionId}}","start":1,"end":3}""", RpcJson.Default.ShowResult);
        SelfTest.CheckPublic(show.Events is { Length: > 0 } && show.Events[0].Contains(o1.SessionId), "collab: show доносит события с last_seen");
        var sync = Call(tools, "sync", $$"""{"session_id":"{{o2.SessionId}}"}""", RpcJson.Default.SyncResult);
        SelfTest.CheckPublic(sync.Peers.Length == 2 && sync.Events.Length == 0, "collab: sync — peers=2, события уже доставлены");

        // save у второго при несохранённых правках первого → peers_dirty; force публикует всё
        var errSave = CallErr(tools, "save", $$"""{"session_id":"{{o2.SessionId}}"}""");
        SelfTest.CheckPublic(errSave?.Kind == "peers_dirty" && errSave.Message.Contains(o1.SessionId), "collab: peers_dirty отклоняет чужой save");
        _ = Call(tools, "save", $$"""{"session_id":"{{o2.SessionId}}","force":true}""", RpcJson.Default.SaveResult);
        SelfTest.CheckPublic(File.ReadAllText(p).Contains("B!"), "collab: force публикует общие правки");

        // detach не последним: документ жив у peers; последним — гасится
        _ = Call(tools, "close", $$"""{"session_id":"{{o2.SessionId}}"}""", RpcJson.Default.CloseResult);
        var o3 = Open(tools, p, null);
        SelfTest.CheckPublic(o3.Reused && o3.Peers.Length == 2, "collab: после detach одного документ жив у peers (o1+o3)");
        var c = Call(tools, "close", $$"""{"session_id":"{{o3.SessionId}}"}""", RpcJson.Default.CloseResult);
        SelfTest.CheckPublic(c.Closed, "collab: последний close гасит документ");
    }

    static OpenResult Open(ToolRegistry tools, string path, string? note) {
        string noteJson = note is null ? "" : ",\"note\":" + JsonSerializer.Serialize(note, RpcJson.Default.String);
        string args = "{\"path\":" + JsonSerializer.Serialize(path, RpcJson.Default.String) + noteJson + "}";
        return Call(tools, "open", args, RpcJson.Default.OpenResult);
    }

    static void Mut(ToolRegistry tools, string sid, int start, int end, string newText) {
        string args = $"{{\"session_id\":\"{sid}\",\"start\":{start},\"end\":{end},\"new_text\":{JsonSerializer.Serialize(newText, RpcJson.Default.String)},\"expected_rev\":0}}";
        _ = Call(tools, "replace_lines", args, RpcJson.Default.MutationResult);
    }

    static T Call<T>(ToolRegistry tools, string name, string argsJson, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> ti)
        => JsonSerializer.Deserialize<T>(tools.CallAsync(name, JsonDocument.Parse(argsJson).RootElement.Clone(), default).GetAwaiter().GetResult(), ti)!;

    static ToolErrorPayload? CallErr(ToolRegistry tools, string name, string argsJson) {
        try {
            _ = tools.CallAsync(name, JsonDocument.Parse(argsJson).RootElement.Clone(), default).GetAwaiter().GetResult();
            return null;
        }
        catch (ToolException tex) {
            return new ToolErrorPayload { Kind = tex.Kind, Message = tex.Message, CurrentRev = tex.CurrentRev, TotalLines = tex.TotalLines };
        }
    }
}

/// <summary>v0.5: общий журнал — экстренная ручка (хвост/grep) читаема самим агентом.</summary>
static class V05Features {
    public static void LogHandle() {
        var tools = new ToolRegistry();
        tools.RegisterEditTools();
        const string marker = "smartedit-selftest-v05-marker";
        SmartEdit.Mcp.Infra.Log.Info(marker + " проверка ручки");

        var r = CallLog(tools, $$"""{"tail":25,"grep":"{{marker}}"}""");
        SelfTest.CheckPublic(r.Lines.Length > 0 && r.Lines[^1].Contains(marker), "log: ручка находит собственную запись (общий журнал)");
        var err = CallLog(tools, """{"errors_only":true,"tail":10}""");
        SelfTest.CheckPublic(err.Lines.All(l => l.Contains("[ERROR]")), "log: errors_only фильтрует");

        // log forging: перенос строки и ANSI ESC в сообщении не должны рождать новые строки журнала
        const string forged = "smartedit-forged\n2026-01-01 00:00:00.000 [ERROR] [pid 1] fake\u001b[31m";
        SmartEdit.Mcp.Infra.Log.Error(forged);
        var fr = CallLog(tools, """{"tail":25,"grep":"smartedit-forged"}""");
        SelfTest.CheckPublic(
            fr.Lines.Length > 0 && fr.Lines[^1].Contains("smartedit-forged\\n2026-01-01") && fr.Lines[^1].Contains("\\u001b[31m"),
            "log: sanitize — \\n и ESC экранируются, подделка строк журнала невозможна");
    }

    static LogResult CallLog(ToolRegistry tools, string argsJson) {
        var json = tools.CallAsync("log", JsonDocument.Parse(argsJson).RootElement.Clone(), default).GetAwaiter().GetResult();
        return JsonSerializer.Deserialize<LogResult>(json, RpcJson.Default.LogResult)!;
    }
}

/// <summary>v0.5.3: F4 — debug-лог протокола маскирует payload (аргументы tools/call несут секреты).</summary>
static class V053Security {
    public static void DebugBrief() {
        var brief = JsonRpcServer.Brief(
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"replace_lines","arguments":{"new_text":"SECRET password=hunter2"}}}""");
        SelfTest.CheckPublic(
            brief.Contains("id=7") && brief.Contains("method=tools/call") && brief.Contains("len=")
                && !brief.Contains("SECRET") && !brief.Contains("hunter2"),
            "debug-brief: payload аргументов (секреты) не попадает в подпись строки протокола");
        var bad = JsonRpcServer.Brief("not json — raw token=abc123");
        SelfTest.CheckPublic(bad.Contains("unparsable") && !bad.Contains("abc123"),
            "debug-brief: непарсируемая строка помечена, содержимое не пишется");
    }
}

/// <summary>v0.6: проверка обновлений — версии, память отказа, текст диалога, выключатель. Без сети.</summary>
static class V054Update {
    public static void Run(string dir) {
        SelfTest.CheckPublic(Infra.UpdateCheck.IsNewer("v0.5.4", "0.5.3"), "update: v0.5.4 > v0.5.3");
        SelfTest.CheckPublic(!Infra.UpdateCheck.IsNewer("v0.5.3", "0.5.3"), "update: та же версия не новее");
        SelfTest.CheckPublic(!Infra.UpdateCheck.IsNewer("v0.5.2", "0.5.3"), "update: старая не новее");
        SelfTest.CheckPublic(Infra.UpdateCheck.IsNewer("v0.6.0", "0.5.9"), "update: minor растёт");
        SelfTest.CheckPublic(!Infra.UpdateCheck.IsNewer("latest", "0.5.3"), "update: кривой тег — не новее");

        // однократность: отказ от vN — молчим по vN, спросим снова только про vN+1
        string decl = Path.Combine(dir, "update-declined.txt");
        SelfTest.CheckPublic(!Infra.UpdateCheck.IsDeclined(decl, "v0.6.0"), "update: изначально не отклонена");
        Infra.UpdateCheck.WriteDeclined(decl, "v0.6.0");
        SelfTest.CheckPublic(Infra.UpdateCheck.IsDeclined(decl, "v0.6.0"), "update: отклонённая версия не предлагается повторно");
        SelfTest.CheckPublic(!Infra.UpdateCheck.IsDeclined(decl, "v0.6.1"), "update: следующая версия снова предлагается");

        string t = Infra.UpdateCheck.PromptText("v0.6.0", "0.5.3", new string('x', 5000));
        SelfTest.CheckPublic(t.Contains("v0.6.0") && t.Contains("v0.5.3") && t.Length < 1400,
            "update: текст диалога содержит версии, длинные notes обрезаются");

        SelfTest.CheckPublic(Infra.UpdateCheck.DisabledByArgs(new[] { "--no-update" })
            && !Infra.UpdateCheck.DisabledByArgs(Array.Empty<string>())
            && Infra.UpdateCheck.DisabledByArgs(new[] { "--NO-UPDATE", "selftest" }),
            "update: --no-update отключает проверку (регистронезависимо)");

        // самозамена бинарника (rename dance) — на временных файлах, живой exe не трогаем
        string inst = Path.Combine(dir, "install");
        Directory.CreateDirectory(inst);
        string cur = Path.Combine(inst, "SmartEdit.Mcp.exe"), neu = Path.Combine(inst, "new.exe");
        File.WriteAllText(cur, "OLD"); File.WriteAllText(neu, "NEW");
        Infra.UpdateCheck.ReplaceBinary(cur, neu);
        SelfTest.CheckPublic(File.ReadAllText(cur) == "NEW", "update: бинарник заменён без участия пользователя");
        if (OperatingSystem.IsWindows())
            SelfTest.CheckPublic(File.Exists(cur + ".old") && File.ReadAllText(cur + ".old") == "OLD",
                "update: старый бинарник сохранён как .old (почистится при следующем старте)");
    }
}

/// <summary>v0.6.2 (#2): code outline прямо в open-ответе для кода ≤5k строк.</summary>
static class V062Outline {
    public static void Run(string dir) {
        var tools = new ToolRegistry();
        tools.RegisterEditTools();

        string cs = Path.Combine(dir, "v062_outline.cs");
        File.WriteAllText(cs, """
            namespace Demo;
            class Alpha {
                void One() { }
                void Two() { }
            }
            class Beta { }
            """, new UTF8Encoding(false));
        var r = JsonSerializer.Deserialize<OpenResult>(
            tools.CallAsync("open", JsonDocument.Parse($$"""{"path":"{{cs.Replace("\\", "\\\\")}}"}""").RootElement.Clone(), default)
                .GetAwaiter().GetResult(), RpcJson.Default.OpenResult)!;
        SelfTest.CheckPublic(r.CodeOutline is not null
            && r.CodeOutline.Any(s => s.Name == "Alpha" && s.Kind == "class")
            && r.CodeOutline.Any(s => s.Name == "One" && s.Kind == "method")
            && r.CodeOutline.All(s => s.Line >= 1),
            "open: code_outline с классами и методами прямо в ответе (#2)");

        string txt = Path.Combine(dir, "v062_plain.txt");
        File.WriteAllText(txt, "просто текст без символов");
        var rt = JsonSerializer.Deserialize<OpenResult>(
            tools.CallAsync("open", JsonDocument.Parse($$"""{"path":"{{txt.Replace("\\", "\\\\")}}"}""").RootElement.Clone(), default)
                .GetAwaiter().GetResult(), RpcJson.Default.OpenResult)!;
        SelfTest.CheckPublic(rt.CodeOutline is null, "open: не-код — code_outline отсутствует (null)");

        string big = Path.Combine(dir, "v062_big.cs");
        File.WriteAllText(big, "class Big {\n" + string.Join("\n", Enumerable.Repeat("    // filler", 5000)) + "\n}\n");
        var rb = JsonSerializer.Deserialize<OpenResult>(
            tools.CallAsync("open", JsonDocument.Parse($$"""{"path":"{{big.Replace("\\", "\\\\")}}"}""").RootElement.Clone(), default)
                .GetAwaiter().GetResult(), RpcJson.Default.OpenResult)!;
        SelfTest.CheckPublic(rb.CodeOutline is null && rb.TotalLines > 5000,
            "open: файл >5k строк — гейт экономит сканирование, code_outline null");
    }
}

/// <summary>v0.7 (#1): stale-rev auto-recovery — retry-once, якорь/нетронутый диапазон/сдвиги, конфликт с «обоими версиями».</summary>
static class V070Stale {
    public static void Run(string dir) {
        var tools = new ToolRegistry();
        tools.RegisterEditTools();

        // каждый сценарий — СВОЙ файл: Sessions.Attach переиспользует общий документ по пути
        string Fresh(string tag) { // не static: захватывает dir из объёмлющего метода
            string p = Path.Combine(dir, $"stale-{tag}.txt");
            File.WriteAllText(p, "one\ntwo\nthree\nfour\nfive\nsix\nseven\neight\nnine\nten\n", new UTF8Encoding(false));
            return p;
        }

        static string Call(ToolRegistry tools, string json)
            => tools.CallAsync("replace_lines", JsonDocument.Parse(json).RootElement.Clone(), default).GetAwaiter().GetResult();

        // A) якорь цел: чужая правка вне диапазона, якорь на 3..3 валиден → авто-повтор, recovered=true
        var rA = Open(tools, Fresh("a"), out var docA);
        docA.ReplaceLines(1, 1, "PEER", docA.Rev, false, null, actor: "peer");
        var resA = JsonSerializer.Deserialize<MutationResult>(
            Call(tools, $$"""{"session_id":"{{rA.SessionId}}","start":3,"end":3,"new_text":"MINE","expected_rev":0,"expect_old":"three","retry_on_stale":true}"""),
            RpcJson.Default.MutationResult)!;
        SelfTest.CheckPublic(resA.Recovered && docA.GetLineText(3) == "MINE"
            && resA.Intervening is { Length: > 0 } && resA.Intervening[0].Contains("peer"),
            "stale-recovery: якорь цел → авто-повтор, recovered+intervening");

        // B) без якоря: чужая вставка НИЖЕ диапазона — диапазон не тронут → авто-повтор
        var rB = Open(tools, Fresh("b"), out var docB);
        var insB = docB.InsertAfter(9, "peer-tail", docB.Rev, false, actor: "peer");
        SelfTest.CheckPublic(insB.Diff.Contains("nine") && insB.Diff.Contains("peer-tail") && !insB.Diff.Contains("seven"),
            "insert diff: контекст содержит ЯКОРНУЮ строку (nine), а не строку выше (seven)");
        var resB = JsonSerializer.Deserialize<MutationResult>(
            Call(tools, $$"""{"session_id":"{{rB.SessionId}}","start":3,"end":4,"new_text":"EDITED","expected_rev":0,"retry_on_stale":true}"""),
            RpcJson.Default.MutationResult)!;
        // после замены 2→1 строки всё ниже сдвигается вверх: peer-tail с 10-й уезжает на 9-ю
        SelfTest.CheckPublic(resB.Recovered && docB.GetLineText(3) == "EDITED" && docB.GetLineText(9) == "peer-tail"
            && resB.Intervening is { Length: 1 },
            "stale-recovery: без якоря, диапазон не тронут → авто-повтор (intervening — только чужие)");

        // C) без якоря: чужая правка ВНУТРИ диапазона → stale_conflict с текущим текстом, ничего не применено
        var rC = Open(tools, Fresh("c"), out var docC);
        int revBefore = docC.Rev;
        docC.ReplaceLines(3, 3, "PEER-HIJACKED", docC.Rev, false, null, actor: "peer");
        ToolException? texC = null;
        try { Call(tools, $$"""{"session_id":"{{rC.SessionId}}","start":3,"end":4,"new_text":"X","expected_rev":0,"retry_on_stale":true}"""); }
        catch (ToolException t) { texC = t; }
        SelfTest.CheckPublic(texC is not null && texC.Kind == "stale_conflict"
            && texC.CurrentText is { } ct && ct.Contains("PEER-HIJACKED")
            && texC.RetryHint is not null && docC.Rev == revBefore + 1,
            "stale-recovery: диапазон изменился → stale_conflict (текущий текст + hint), без слепого повтора");

        // D) сдвиг: чужая вставка СВЕРХУ (+2 строки) → диапазон [3..4] применяется в сдвинутых [5..6]
        var rD = Open(tools, Fresh("d"), out var docD);
        docD.InsertAfter(0, "new top\nx2", docD.Rev, false, actor: "peer");
        var resD = JsonSerializer.Deserialize<MutationResult>(
            Call(tools, $$"""{"session_id":"{{rD.SessionId}}","start":3,"end":4,"new_text":"EDITED","expected_rev":0,"retry_on_stale":true}"""),
            RpcJson.Default.MutationResult)!;
        SelfTest.CheckPublic(resD.Recovered && docD.GetLineText(1) == "new top" && docD.GetLineText(2) == "x2"
            && docD.GetLineText(5) == "EDITED" && docD.TotalLines == 11,
            "stale-recovery: вставка сверху — диапазон применяется по сдвинутым координатам");

        // E) edit_batch: якорная правка + безопасная безъярорная → весь батч восстановлен
        var rE = Open(tools, Fresh("e"), out var docE);
        docE.ReplaceLines(9, 9, "PEER9", docE.Rev, false, null, actor: "peer");
        var resE = JsonSerializer.Deserialize<BatchResult>(
            tools.CallAsync("edit_batch", JsonDocument.Parse(
                $$"""{"session_id":"{{rE.SessionId}}","expected_rev":0,"retry_on_stale":true,"edits":[{"start":1,"end":1,"new_text":"ONE","expect_old":"one"},{"start":5,"end":6,"new_text":"FIVE-SIX"}]}""").RootElement.Clone(), default).GetAwaiter().GetResult(),
            RpcJson.Default.BatchResult)!;
        SelfTest.CheckPublic(resE.Recovered && resE.Applied == 2 && docE.GetLineText(1) == "ONE" && docE.GetLineText(5) == "FIVE-SIX",
            "stale-recovery: edit_batch целиком восстановлен (якорь + нетронутый диапазон)");

        // F) без retry_on_stale поведение прежнее: голый stale_rev
        var rF = Open(tools, Fresh("f"), out var docF);
        docF.ReplaceLines(1, 1, "PEER", docF.Rev, false, null, actor: "peer");
        ToolException? texF = null;
        try { Call(tools, $$"""{"session_id":"{{rF.SessionId}}","start":3,"end":3,"new_text":"X","expected_rev":0,"expect_old":"three","retry_on_stale":false}"""); }
        catch (ToolException t) { texF = t; }
        SelfTest.CheckPublic(texF is not null && texF.Kind == "stale_rev",
            "stale-recovery: явный false — прежний честный stale_rev (по умолчанию v0.8 — recovery)");
    }

    static OpenResult Open(ToolRegistry tools, string path, out EditSession doc) {
        var json = tools.CallAsync("open", JsonDocument.Parse(
            $$"""{"path":"{{path.Replace("\\", "\\\\")}}"}""").RootElement.Clone(), default).GetAwaiter().GetResult();
        var r = JsonSerializer.Deserialize<OpenResult>(json, RpcJson.Default.OpenResult)!;
        doc = Sessions.Get(r.SessionId).Doc;
        doc.EnsureLive(); // #14: tool-вызов отпустил хэндл — прямые правки мимо обёртки требуют живого источника
        return r;
    }
}

/// <summary>
/// v0.7.2: torture-страж «save публикует ТЕКУЩИЙ ревиз» — два реальных инцидента, когда save писал
/// контент предыдущего ревиз. Детерминированный (seed) перемешанный поток мутаций
/// (replace/batch/insert/delete/undo), после КАЖДОЙ — save и побайтовая сверка диска с документом.
/// </summary>
static class V072SaveTorture {
    public static void Run(string dir) {
        string p = Path.Combine(dir, "save-torture.txt");
        File.WriteAllText(p, string.Join("\n", Enumerable.Range(1, 40).Select(i => $"line{i}")) + "\n", new UTF8Encoding(false));
        var s = EditSession.Open(p, null);
        var rng = new Random(20261111); // фиксированный seed: воспроизводимость
        int saves = 0, muts = 0;
        string Disk() => File.ReadAllText(p);
        string Doc() => string.Join("\n", Enumerable.Range(1, s.TotalLines).Select(i => s.GetLineText(i))) + "\n";
        void Verify(string tag) {
            s.Save(false, "keep", false, "torture");
            saves++;
            SelfTest.Check(Disk() == Doc(), $"save-torture: {tag} — на диске ровно текущий ревиз (r{s.Rev})");
        }

        for (int step = 0; step < 30; step++) {
            int op = rng.Next(5);
            if (op == 0 && s.TotalLines > 4) {
                int a = 1 + rng.Next(s.TotalLines - 2), b = a + rng.Next(2);
                s.ReplaceLines(a, b, $"step{step}", s.Rev, false, null, "torture"); muts++;
                Verify($"replace {a}..{b}");
            }
            else if (op == 1) {
                int at = rng.Next(s.TotalLines);
                s.InsertAfter(at, $"ins{step}\nins{step}b", s.Rev, false, "torture"); muts++;
                Verify($"insert_after {at}");
            }
            else if (op == 2 && s.TotalLines > 8) {
                int a = 1 + rng.Next(s.TotalLines - 6);
                s.EditBatch([
                    new(a, a + 1, $"b1_{step}", null),
                    new(a + 3, a + 3 + rng.Next(2), "", null),
                ], s.Rev, false, "torture"); muts++;
                Verify("edit_batch + delete");
            }
            else if (op == 3 && muts > 0) {
                try { s.Undo(1, s.Rev, false, "torture"); Verify("undo"); } catch (ToolException) { /* nothing_to_undo/undo_conflict — валидно */ }
            }
            else {
                s.ReplaceLines(1, 1, $"head{step}", s.Rev, false, "line1" == s.GetLineText(1) ? "line1" : null, "torture"); muts++;
                Verify("replace+anchor");
            }
        }
        SelfTest.CheckPublic(saves > 20 && muts > 15, $"save-torture: плотность достигнута ({muts} мутаций / {saves} сохранений)");
        s.Dispose();
    }
}

/// <summary>v0.7.3: операционная гигиена из телеметрии — #10 изоляция selftest-журнала, #11 пустой файл, #12 session GC, #13 flavor апдейта.</summary>
static class V073Harden {
    public static void Run(string dir) {
        // #10: selftest обязан писать во временный каталог данных, не в продакшн-журнал
        SelfTest.CheckPublic(Infra.AppPaths.DataRoot.Contains("smartedit-selftest-data"),
            "telemetry: selftest пишет в изолированный каталог (#10)");

        // #11: [1..0] в пустом файле = вставка первой строки (конвенция end=start-1)
        string empty = Path.Combine(dir, "empty.txt");
        File.WriteAllText(empty, "", new UTF8Encoding(false));
        var se = EditSession.Open(empty, null);
        var me = se.ReplaceLines(1, 0, "hello\nworld", 0, false);
        SelfTest.CheckPublic(me.Rev == 1 && se.TotalLines == 2 && se.GetLineText(2) == "world",
            "empty: replace_lines [1..0] вставляет в пустой файл (#11)");
        se.Dispose();

        // #12: stale-подключение без pending отцепляется при следующем open (документ жив)
        string gc = Path.Combine(dir, "gc.txt");
        File.WriteAllText(gc, "x\n", new UTF8Encoding(false));
        var (a1, _) = Sessions.Attach(gc, null, null);
        a1.LastSeen = DateTime.UtcNow.AddMinutes(-31); // поле публичное — имитируем тишину
        var (a2, _) = Sessions.Attach(gc, null, null);
        SelfTest.CheckPublic(!Sessions.Map.ContainsKey(a1.Id) && Sessions.Map.ContainsKey(a2.Id),
            "sessions: stale-подключение без pending отцеплено GC, документ жив (#12)");
        Sessions.Detach(a2.Id, true);

        // #13: flavor-сохраняющий выбор ассета — fd/sc/aot не смешиваются
        SelfTest.CheckPublic(
            Infra.UpdateCheck.IsPlatformAsset("SmartEdit-win-x64-v1.zip", "fd", true)
            && Infra.UpdateCheck.IsPlatformAsset("SmartEdit-sc-win-x64-v1.zip", "sc", true)
            && Infra.UpdateCheck.IsPlatformAsset("SmartEdit-aot-win-x64-v1.zip", "aot", true)
            && !Infra.UpdateCheck.IsPlatformAsset("SmartEdit-win-x64-v1.zip", "sc", true)
            && !Infra.UpdateCheck.IsPlatformAsset("SmartEdit-sc-win-x64-v1.zip", "fd", true)
            && Infra.UpdateCheck.IsPlatformAsset("SmartEdit-linux-x64-v1.zip", "fd", false)
            && Infra.UpdateCheck.Flavor() is "fd" or "sc" or "aot",
            "update: ассет выбирается по flavor установки (#13)");
    }
}

/// <summary>
/// v0.8: универсальный профильный лексер (консилиум-дизайн) — raw/triple/template/f-string/R"()"
/// дают ЧЕСТНЫЙ баланс скобок, а незакрытые литералы репортятся ЯКОРЕМ на строку открытия.
/// </summary>
static class V080Literals {
    public static void Run(string dir) {
        // C# raw + $$-дырки + JSON-скобки внутри — тот самый фиксаж, на котором я страдал весь день
        string cs = Path.Combine(dir, "lit.cs");
        File.WriteAllText(cs,
            """"
            class W {
                void M(int x) {
                    var j = $$"""{"a": [1, {"b": {{x}}}]}""";
                    var s = $"v={x + 1} literal={{}}";
                }
            }
            """", new UTF8Encoding(false));
        var scs = EditSession.Open(cs, null);
        SelfTest.CheckPublic(scs.CheckBrackets(0, 0).Count == 0,
            "lexer: C# raw $$-строки с JSON-скобками и $-интерполяцией — баланс чист");
        scs.Dispose();

        // v0.9 regression: // комментарий НЕ съедает следующую строку (диагностика врала → ложные strays)
        string cm = Path.Combine(dir, "comment-eats-next.cs");
        File.WriteAllText(cm,
            """"
            class C {
                void M() {
                    att.X = outcome.Rev; // v0.9: доставил
                    return Json(new R {
                    }, R);
                }
            }
            """", new UTF8Encoding(false));
        var scm = EditSession.Open(cm, null);
        SelfTest.CheckPublic(scm.CheckBrackets(0, 0).Count == 0,
            "lexer: // комментарий не съедает следующую строку (v0.9 fix)");
        scm.Dispose();

        // Незакрытый raw: диагностика якорем на строку ОТКРЫТИЯ (v0.8 киллерфича)
        string unclosed = Path.Combine(dir, "unclosed.cs");
        File.WriteAllText(unclosed,
            """"
            class U {
                void M() {
                    var j = """{
                }
            }
            """", new UTF8Encoding(false));
        var su = EditSession.Open(unclosed, null);
        var du = su.DiagReport(1, 4)!;
        SelfTest.CheckPublic(du.Any(d => d.Code == "unterminated_literal" && d.Line == 3),
            $"lexer: незакрытый raw — строка открытия [{string.Join("; ", du.Select(d => $"{d.Code}@{d.Line}"))}]");
        su.Dispose();

        // Python: triple + f-string с вложенными скобками + словарь в дырке
        string py = Path.Combine(dir, "lit.py");
        File.WriteAllText(py,
            """"
            def f(items):
                doc = """title: {x}
                """
                m = {"k": [i for i in items if i > 0]}
                return f"res={m['k'][0]} braced={{}}"
            """", new UTF8Encoding(false));
        var spy = EditSession.Open(py, null);
        SelfTest.CheckPublic(spy.CheckBrackets(0, 0).Count == 0,
            $"lexer: Python — [{string.Join("; ", spy.CheckBrackets(0, 0))}]");
        spy.Dispose();

        // JS: template с ${} (вложенный шаблон + объект) — старый стриппер слеп на `
        string js = Path.Combine(dir, "lit.js");
        File.WriteAllText(js,
            """"
            function render(user) {
                const tpl = `Hello ${user.name} (${Object.keys({a: 1}).length}) ${`inner ${user.id}`}`;
                return tpl;
            }
            """", new UTF8Encoding(false));
        var sjs = EditSession.Open(js, null);
        SelfTest.CheckPublic(sjs.CheckBrackets(0, 0).Count == 0,
            $"lexer: JS — [{string.Join("; ", sjs.CheckBrackets(0, 0))}]");
        sjs.Dispose();

        // C++: R"delim(...)delim" — скобки в сырой строке игнорируются
        string cpp = Path.Combine(dir, "lit.cpp");
        File.WriteAllText(cpp,
            """"
            int main() {
                const char* re = R"x(([{)] balanced junk)x";
                return 0;
            }
            """", new UTF8Encoding(false));
        var scpp = EditSession.Open(cpp, null);
        SelfTest.CheckPublic(scpp.CheckBrackets(0, 0).Count == 0,
            "lexer: C++ R\"delim(...)delim\" — сырая строка не ломает баланс");
        scpp.Dispose();
    }
}

/// <summary>
/// v0.8.2 (#14) unlock-when-clean: Dirty ⇒ держим файл (защита); clean ⇒ хэндл только на время вызова.
/// Регрессия инцидента v0.8.1: чистая сессия блокировала git/скрипты (EBUSY) — коммит ушёл без доков.
/// </summary>
static class V082Lock {
    public static void Run(string dir) {
        var tools = new ToolRegistry();
        tools.RegisterEditTools();
        string nl = ((char)10).ToString();

        // (a) ИНЦИДЕНТ-РЕГРЕССИЯ: open → tool-вызов (источник отпущен) → attachment ЖИВ → внешняя запись проходит
        string a = Path.Combine(dir, "lock-a.txt");
        File.WriteAllText(a, "x" + nl);
        var ra = tools.CallAsync("open", JsonDocument.Parse($$"""{"path":"{{a.Replace("\\", "\\\\")}}"}""").RootElement.Clone(), default).GetAwaiter().GetResult();
        var sid = JsonSerializer.Deserialize<OpenResult>(ra, RpcJson.Default.OpenResult)!.SessionId;
        _ = tools.CallAsync("show", JsonDocument.Parse($$"""{"session_id":"{{sid}}","start":1,"end":1}""").RootElement.Clone(), default).GetAwaiter().GetResult();
        bool cleanUnlocked = true;
        try { File.WriteAllText(a, "external-ok" + nl); } catch (IOException) { cleanUnlocked = false; }
        SelfTest.CheckPublic(cleanUnlocked, "lock: чистая сессия с живым attachment НЕ держит файл (регрессия #14)");

        // (b) Dirty держит: правка без save → внешняя запись падает; save → отпускает
        string b = Path.Combine(dir, "lock-b.txt");
        File.WriteAllText(b, "y" + nl);
        var rb = tools.CallAsync("open", JsonDocument.Parse($$"""{"path":"{{b.Replace("\\", "\\\\")}}"}""").RootElement.Clone(), default).GetAwaiter().GetResult();
        var sidB = JsonSerializer.Deserialize<OpenResult>(rb, RpcJson.Default.OpenResult)!.SessionId;
        _ = tools.CallAsync("replace_lines", JsonDocument.Parse($$"""{"session_id":"{{sidB}}","start":1,"end":1,"new_text":"EDIT","expected_rev":0}""").RootElement.Clone(), default).GetAwaiter().GetResult();
        bool dirtyHeld = false;
        try { File.WriteAllText(b, "must-fail" + nl); } catch (IOException) { dirtyHeld = true; }
        SelfTest.CheckPublic(dirtyHeld, "lock: dirty-сессия держит файл (защита базы piece-table)");
        _ = tools.CallAsync("save", JsonDocument.Parse($$"""{"session_id":"{{sidB}}"}""").RootElement.Clone(), default).GetAwaiter().GetResult();
        bool afterSave = true;
        try { File.WriteAllText(b, "after-save-ok" + nl); } catch (IOException) { afterSave = false; }
        SelfTest.CheckPublic(afterSave, "lock: save отпускает файл");

        // (c) Прозрачный сброс: внешняя замена → следующий tool-вызов видит НОВОЕ содержимое, rev 0
        File.WriteAllText(b, "FRESH" + nl);
        var sb2 = tools.CallAsync("show", JsonDocument.Parse($$"""{"session_id":"{{sidB}}","start":1,"end":1}""").RootElement.Clone(), default).GetAwaiter().GetResult();
        var showB = JsonSerializer.Deserialize<ShowResult>(sb2, RpcJson.Default.ShowResult)!;
        SelfTest.CheckPublic(showB.Text.Contains("FRESH") && showB.Rev == 0,
            "lock: внешняя замена файла → прозрачный сброс (новое содержимое, rev=0)");

        // (d) Файл исчез пока отпущен → внятный disk_conflict, не NRE
        string d = Path.Combine(dir, "lock-d.txt");
        File.WriteAllText(d, "z" + nl);
        var rd2 = tools.CallAsync("open", JsonDocument.Parse($$"""{"path":"{{d.Replace("\\", "\\\\")}}"}""").RootElement.Clone(), default).GetAwaiter().GetResult();
        var sidD = JsonSerializer.Deserialize<OpenResult>(rd2, RpcJson.Default.OpenResult)!.SessionId;
        _ = tools.CallAsync("show", JsonDocument.Parse($$"""{"session_id":"{{sidD}}","start":1,"end":1}""").RootElement.Clone(), default).GetAwaiter().GetResult();
        File.Delete(d);
        ToolException? tex = null;
        try { _ = tools.CallAsync("show", JsonDocument.Parse($$"""{"session_id":"{{sidD}}","start":1,"end":1}""").RootElement.Clone(), default).GetAwaiter().GetResult(); }
        catch (ToolException t) { tex = t; }
        SelfTest.CheckPublic(tex is not null && tex.Kind == ToolException.DiskConflict && tex.RetryHint is not null,
            "lock: файл удалён между вызовами → disk_conflict с retry_hint (не NRE)");

        // прибраться: явно закрываем чистую сессию (b/d — по пути сценариев выше)
        _ = tools.CallAsync("close", JsonDocument.Parse($$"""{"session_id":"{{sid}}","discard":true}""").RootElement.Clone(), default).GetAwaiter().GetResult();
    }
}

/// <summary>v0.9: path-everywhere (путь вместо session_id в любом инструменте — смерть ритуала open) + edit_transaction (#3).</summary>
static class V090PathTxn {
    public static void Run(string dir) {
        var tools = new ToolRegistry();
        tools.RegisterEditTools();
        string nl = ((char)10).ToString();
        string J(string path) => path.Replace("\\", "\\\\");

        // (a) path-mode: show и replace_lines БЕЗ open — путь прикрепляется к общему документу
        string p1 = Path.Combine(dir, "p-mode.txt");
        File.WriteAllText(p1, "alpha" + nl + "beta" + nl);
        string shRaw = tools.CallAsync("show", JsonDocument.Parse($$"""{"path":"{{J(p1)}}","start":1,"end":2}""").RootElement.Clone(), default).GetAwaiter().GetResult();
        var sh = JsonSerializer.Deserialize<ShowResult>(shRaw, RpcJson.Default.ShowResult)!;
        SelfTest.CheckPublic(sh.Text.Contains("alpha") && sh.Text.Contains("beta") && sh.Rev == 0, "path-mode: show без open");

        string mutRaw = tools.CallAsync("replace_lines", JsonDocument.Parse($$"""{"path":"{{J(p1)}}","start":1,"end":1,"new_text":"ALPHA"}""").RootElement.Clone(), default).GetAwaiter().GetResult();
        var mut = JsonSerializer.Deserialize<MutationResult>(mutRaw, RpcJson.Default.MutationResult)!;
        SelfTest.CheckPublic(mut.Rev == 1 && mut.Diff.Contains("ALPHA"), "path-mode: replace_lines по пути (rev 1)");

        string mutRaw2 = tools.CallAsync("replace_lines", JsonDocument.Parse($$"""{"path":"{{J(p1)}}","start":2,"end":2,"new_text":"BETA"}""").RootElement.Clone(), default).GetAwaiter().GetResult();
        var mut2 = JsonSerializer.Deserialize<MutationResult>(mutRaw2, RpcJson.Default.MutationResult)!;
        SelfTest.CheckPublic(mut2.Rev == 2, "path-mode: повторный путь = тот же документ, rev-цепочка без явного expected_rev");

        ToolException? amb = null;
        try { _ = tools.CallAsync("show", JsonDocument.Parse($$"""{"session_id":"whatever","path":"{{J(p1)}}","start":1,"end":1}""").RootElement.Clone(), default).GetAwaiter().GetResult(); }
        catch (ToolException t) { amb = t; }
        SelfTest.CheckPublic(amb is not null && amb.Kind == "ambiguous_target", "path-mode: session_id + path → ambiguous_target");

        // (b) edit_transaction all-or-nothing: битый якорь в файле 3 → отменено ВСЁ, диск не тронут
        string t1 = Path.Combine(dir, "tx1.txt"); File.WriteAllText(t1, "one" + nl);
        string t2 = Path.Combine(dir, "tx2.txt"); File.WriteAllText(t2, "two" + nl);
        string t3 = Path.Combine(dir, "tx3.txt"); File.WriteAllText(t3, "three" + nl);
        ToolException? aborted = null;
        try {
            _ = tools.CallAsync("edit_transaction", JsonDocument.Parse($$"""{"steps":[{"path":"{{J(t1)}}","edits":[{"start":1,"end":1,"new_text":"ONE"}]},{"path":"{{J(t2)}}","edits":[{"start":1,"end":1,"new_text":"TWO"}]},{"path":"{{J(t3)}}","edits":[{"start":1,"end":1,"new_text":"THREE","expect_old":"WRONG"}]}]}""").RootElement.Clone(), default).GetAwaiter().GetResult();
        }
        catch (ToolException t) { aborted = t; }
        SelfTest.CheckPublic(aborted is not null && aborted.Kind == "transaction_aborted" && aborted.Message.Contains("tx3.txt"),
            "txn: битый якорь в файле 3 → transaction_aborted с именем файла");
        SelfTest.CheckPublic(File.ReadAllText(t1) == "one" + nl && File.ReadAllText(t2) == "two" + nl && File.ReadAllText(t3) == "three" + nl,
            "txn: all-or-nothing — ни один файл не тронут");

        // (c) happy path: 3 файла + save:true → per-file статус и диск
        string txRaw = tools.CallAsync("edit_transaction", JsonDocument.Parse($$"""{"steps":[{"path":"{{J(t1)}}","edits":[{"start":1,"end":1,"new_text":"ONE"}]},{"path":"{{J(t2)}}","edits":[{"start":1,"end":1,"new_text":"TWO"}]},{"path":"{{J(t3)}}","edits":[{"start":1,"end":1,"new_text":"THREE","expect_old":"three"}]}],"save":true}""").RootElement.Clone(), default).GetAwaiter().GetResult();
        var tx = JsonSerializer.Deserialize<TransactionResult>(txRaw, RpcJson.Default.TransactionResult)!;
        SelfTest.CheckPublic(tx.Applied == 3 && tx.Files.Count == 3 && tx.Files.All(f => f.SavedOk == true), "txn: 3 правки в 3 файлах, save ok");
        SelfTest.CheckPublic(File.ReadAllText(t1).StartsWith("ONE") && File.ReadAllText(t3).StartsWith("THREE"), "txn: диск соответствует применённому");

        // (d) >8 файлов без reason → runaway guard; с reason → применяется (RAM, диск не тронут)
        var many = new List<string>();
        for (int i = 1; i <= 9; i++) { string f = Path.Combine(dir, $"tx-many-{i}.txt"); File.WriteAllText(f, "m" + nl); many.Add(f); }
        var sb = new StringBuilder("{\"steps\":[");
        for (int i = 0; i < many.Count; i++) {
            if (i > 0) sb.Append(',');
            sb.Append("{\"path\":\"").Append(J(many[i])).Append("\",\"edits\":[{\"start\":1,\"end\":1,\"new_text\":\"M\"}]}");
        }
        sb.Append("]}");
        ToolException? guard = null;
        try { _ = tools.CallAsync("edit_transaction", JsonDocument.Parse(sb.ToString()).RootElement.Clone(), default).GetAwaiter().GetResult(); }
        catch (ToolException t) { guard = t; }
        SelfTest.CheckPublic(guard is not null && guard.Kind == ToolException.InvalidArgs && guard.Message.Contains("reason"), "txn: >8 файлов требует reason");
        sb.Insert(sb.Length - 1, ",\"reason\":\"selftest\"");
        string txManyRaw = tools.CallAsync("edit_transaction", JsonDocument.Parse(sb.ToString()).RootElement.Clone(), default).GetAwaiter().GetResult();
        var txMany = JsonSerializer.Deserialize<TransactionResult>(txManyRaw, RpcJson.Default.TransactionResult)!;
        SelfTest.CheckPublic(txMany.Applied == 9 && txMany.Files.All(f => f.SavedOk is null) && many.All(f => File.ReadAllText(f) == "m" + nl),
            "txn: с reason — 9 файлов применены в RAM, save не запрашивался (SavedOk null, диск чист)");

        // прибраться — close тоже по пути (path-everywhere): не копим сессии к лимиту 32
        foreach (var f in new[] { p1, t1, t2, t3 }.Concat(many))
            _ = tools.CallAsync("close", JsonDocument.Parse($$"""{"path":"{{J(f)}}","discard":true}""").RootElement.Clone(), default).GetAwaiter().GetResult();
    }
}

/// <summary>
/// v0.9.1 (внешний dogfooding-отчёт): replace_content — замена точного текста без номеров строк.
/// Уникальность обязательна: 0 → not_found, >1 → ambiguous_match со списком строк.
/// Многострочные сниппеты и частичные строки; пустой replace = удаление сниппета.
/// </summary>
static class V091Content {
    public static void Run(string dir) {
        var tools = new ToolRegistry();
        tools.RegisterEditTools();
        string nl = ((char)10).ToString();
        string J(string path) => path.Replace("\\", "\\\\");
        string Call(string json) => tools.CallAsync("replace_content", JsonDocument.Parse(json).RootElement.Clone(), default).GetAwaiter().GetResult();
        ToolException? Err(string json) {
            try { Call(json); return null; }
            catch (ToolException t) { return t; }
        }

        string p = Path.Combine(dir, "rc.txt");
        File.WriteAllText(p,
            "alpha start" + nl + "middle one" + nl + "middle two end" + nl + "tail" + nl);

        // (a) многострочный сниппет с частичными строками: хвосты первой/последней строки сохранены
        string raw = Call($$"""{"path":"{{J(p)}}","find":"rt\nmiddle one\nmiddle two en","replace":"RT\nMID"}""");
        var m = JsonSerializer.Deserialize<MutationResult>(raw, RpcJson.Default.MutationResult)!;
        SelfTest.CheckPublic(m.Rev == 1, "rc: применён без явного rev (LastSeenRev-цепочка)");
        var sh = JsonSerializer.Deserialize<ShowResult>(
            tools.CallAsync("show", JsonDocument.Parse($$"""{"path":"{{J(p)}}","start":1,"end":3}""").RootElement.Clone(), default).GetAwaiter().GetResult(),
            RpcJson.Default.ShowResult)!;
        SelfTest.CheckPublic(sh.Text.Contains("alpha staRT") && sh.Text.Contains("MIDd") && sh.Text.Contains("tail"),
            "rc: многострочный partial — префикс/суффикс на месте");

        // (b) неоднозначность: сниппет на двух строках → ambiguous_match с номерами
        string q = Path.Combine(dir, "rc-dup.txt");
        File.WriteAllText(q, "x dup x" + nl + "y dup y" + nl);
        var amb = Err($$"""{"path":"{{J(q)}}","find":"dup","replace":"D"}""");
        SelfTest.CheckPublic(amb is not null && amb.Kind == "ambiguous_match" && amb.Message.Contains("1") && amb.Message.Contains("2"),
            "rc: два вхождения → ambiguous_match со списком строк");

        // (c) не найдено → not_found
        var nf = Err($$"""{"path":"{{J(q)}}","find":"no such snippet","replace":"D"}""");
        SelfTest.CheckPublic(nf is not null && nf.Kind == "not_found", "rc: 0 вхождений → not_found");

        // (d) пустой replace = удаление сниппета, остатки строки сливаются
        string d = Path.Combine(dir, "rc-del.txt");
        File.WriteAllText(d, "keep [junk] keep" + nl);
        _ = Call($$"""{"path":"{{J(d)}}","find":" [junk] ","replace":""}""");
        var sd = JsonSerializer.Deserialize<ShowResult>(
            tools.CallAsync("show", JsonDocument.Parse($$"""{"path":"{{J(d)}}","start":1,"end":1}""").RootElement.Clone(), default).GetAwaiter().GetResult(),
            RpcJson.Default.ShowResult)!;
        SelfTest.CheckPublic(sd.Text.Contains("keepkeep"), "rc: пустой replace удаляет сниппет, строка сшивается");

        // (e) пустой find → invalid_args (для массового переименования есть replace_all)
        var ef = Err($$"""{"path":"{{J(d)}}","find":"","replace":"x"}""");
        SelfTest.CheckPublic(ef is not null && ef.Kind == ToolException.InvalidArgs, "rc: пустой find → invalid_args");

        // прибраться по путям
        foreach (var f in new[] { p, q, d })
            _ = tools.CallAsync("close", JsonDocument.Parse($$"""{"path":"{{J(f)}}","discard":true}""").RootElement.Clone(), default).GetAwaiter().GetResult();
    }
}

/// <summary>
/// v0.9.2 (#16): (a) межпроцессный named-mutex — второй процесс на том же файле получает явный
/// cross_process_lock (а не невнятный EBUSY в момент save); (b) корпус-фаззинг лексера — все .cs
/// собственного репозитория обязаны проходить скобочный аудит без единой ложной диагностики
/// («мальчик-который-плакал-волки» из внешнего отчёта = регрессия, не погода).
/// </summary>
static class V092CrossProcess {
    public static void Run(string dir) {
        // (a) mutex, удерживаемый «другим процессом» → cross_process_lock
        string f = Path.Combine(dir, "xproc.txt");
        File.WriteAllText(f, "x" + ((char)10));
        string name = EditSession.CrossProcessMutexName(Path.GetFullPath(f));
        // named mutex реентерабелен В РАМКАХ ПОТОКА — «другой процесс» симулируем отдельным потоком-владельцем
        using var held = new Mutex(false, name);
        var acquired = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var owner = new Thread(() => {
            held.WaitOne();
            acquired.Set();
            release.Wait();
            held.ReleaseMutex();
        });
        owner.Start();
        acquired.Wait();
        try {
            ToolException? tex = null;
            try { _ = EditSession.Open(f, null); } catch (ToolException t) { tex = t; }
            SelfTest.CheckPublic(tex is not null && tex.Kind == "cross_process_lock" && tex.RetryHint is not null,
                "xproc: файл занят другим процессом → явный cross_process_lock с retry_hint (не EBUSY)");
        }
        finally { release.Set(); owner.Join(); }
        using (var doc = EditSession.Open(f, null)) {
            SelfTest.CheckPublic(doc.TotalLines == 1, "xproc: после освобождения мьютекса файл открывается");
        }

        // (b) корпус-фаззинг: собственное репо (~70 .cs, включая фикстуры в строках) — ноль шума
        string? root = FindRepoRoot(AppContext.BaseDirectory);
        if (root is null) {
            SelfTest.CheckPublic(true, "xproc: корпус пропущен (запуск вне репо)");
            return;
        }
        int files = 0;
        var noise = new List<string>();
        foreach (var cs in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)) {
            if (cs.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || cs.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            if (new FileInfo(cs).Length > 1_000_000) continue;
            files++;
            var s = EditSession.Open(cs, null);
            var problems = s.CheckBrackets(0, 0);
            if (problems.Count > 0) noise.Add($"{Path.GetFileName(cs)}: {problems[0]}");
            s.Dispose();
        }
        SelfTest.CheckPublic(files > 20 && noise.Count == 0,
            $"xproc: корпус {files} .cs без ложных диагностик" + (noise.Count > 0 ? " — ШУМ: " + string.Join("; ", noise.Take(3)) : ""));
    }

    static string? FindRepoRoot(string start) {
        var d = new DirectoryInfo(start);
        while (d is not null) {
            if (Directory.Exists(Path.Combine(d.FullName, ".git"))) return d.FullName;
            d = d.Parent;
        }
        return null;
    }
}

/// <summary>
/// v0.9.3 (консилиум-раунд-2: Claude + Gemini + R1): (a) unsaved-наг — каждый ответ правки/чтения
/// dirty-документа кричит «НЕ сохранено» (главная боль: агент забыл save → тесты по старому коду);
/// (b) replace_content с границами — бойлерплейт лечится scope, а не удлинением сниппета;
/// (c) edit_transaction без save отчитывается unsavedFiles.
/// </summary>
static class V093Nag {
    public static void Run(string dir) {
        var tools = new ToolRegistry();
        tools.RegisterEditTools();
        string nl = ((char)10).ToString();
        string J(string path) => path.Replace("\\", "\\\\");

        // (a) unsaved-наг: правка → unsaved:true; save → show → unsaved:false
        string p = Path.Combine(dir, "nag.txt");
        File.WriteAllText(p, "hello" + nl);
        var mut = JsonSerializer.Deserialize<MutationResult>(
            tools.CallAsync("replace_lines", JsonDocument.Parse($$"""{"path":"{{J(p)}}","start":1,"end":1,"new_text":"HELLO"}""").RootElement.Clone(), default).GetAwaiter().GetResult(),
            RpcJson.Default.MutationResult)!;
        SelfTest.CheckPublic(mut.Unsaved, "nag: правка отвечает unsaved=true — не забудь save");
        _ = tools.CallAsync("save", JsonDocument.Parse($$"""{"path":"{{J(p)}}"}""").RootElement.Clone(), default).GetAwaiter().GetResult();
        var sh = JsonSerializer.Deserialize<ShowResult>(
            tools.CallAsync("show", JsonDocument.Parse($$"""{"path":"{{J(p)}}","start":1,"end":1}""").RootElement.Clone(), default).GetAwaiter().GetResult(),
            RpcJson.Default.ShowResult)!;
        SelfTest.CheckPublic(!sh.Unsaved, "nag: после save чтение отвечает unsaved=false");

        // (b) replace_content + scope: dup в двух местах — без границ ambiguous, с границей применяется
        string q = Path.Combine(dir, "nag-scope.txt");
        File.WriteAllText(q, "x dup x" + nl + "filler" + nl + "filler" + nl + "y dup y" + nl);
        ToolException? amb = null;
        try { _ = tools.CallAsync("replace_content", JsonDocument.Parse($$"""{"path":"{{J(q)}}","find":"dup","replace":"D"}""").RootElement.Clone(), default).GetAwaiter().GetResult(); }
        catch (ToolException t) { amb = t; }
        SelfTest.CheckPublic(amb is not null && amb.Kind == "ambiguous_match" && amb.Message.Contains("start_line"),
            "nag: ambiguous подсказывает границы скопа");
        var rc = JsonSerializer.Deserialize<MutationResult>(
            tools.CallAsync("replace_content", JsonDocument.Parse($$"""{"path":"{{J(q)}}","find":"dup","replace":"D","start_line":4,"end_line":4}""").RootElement.Clone(), default).GetAwaiter().GetResult(),
            RpcJson.Default.MutationResult)!;
        SelfTest.CheckPublic(rc.Rev == 1 && rc.Unsaved, "nag: scope сузил до уникального — применилось (unsaved=true)");

        // (c) edit_transaction без save → unsavedFiles
        string t1 = Path.Combine(dir, "nag-t1.txt"); File.WriteAllText(t1, "a" + nl);
        string t2 = Path.Combine(dir, "nag-t2.txt"); File.WriteAllText(t2, "b" + nl);
        var tx = JsonSerializer.Deserialize<TransactionResult>(
            tools.CallAsync("edit_transaction", JsonDocument.Parse($$"""{"steps":[{"path":"{{J(t1)}}","edits":[{"start":1,"end":1,"new_text":"A"}]},{"path":"{{J(t2)}}","edits":[{"start":1,"end":1,"new_text":"B"}]}]}""").RootElement.Clone(), default).GetAwaiter().GetResult(),
            RpcJson.Default.TransactionResult)!;
        SelfTest.CheckPublic(tx.UnsavedFiles == 2, "nag: транзакция без save отчитывается unsavedFiles=2");

        foreach (var f in new[] { p, q, t1, t2 })
            _ = tools.CallAsync("close", JsonDocument.Parse($$"""{"path":"{{J(f)}}","discard":true}""").RootElement.Clone(), default).GetAwaiter().GetResult();
    }
}

/// <summary>
static class V082Polish {
    public static void Run(string dir) {
        const string Lf = "\n";
        var tools = new ToolRegistry();
        tools.RegisterEditTools();

        // (#1) retry-структура: stale_conflict → reread_range + диапазон; stale_rev → reissue_with_rev
        string c = Path.Combine(dir, "pol-retry.cs");
        File.WriteAllText(c, string.Join(Lf, "class P {", "    void M() {", "        var s = \"old;\"", "    }", "}") + Lf);
        var (att, _) = Sessions.Attach(c, null, null);
        att.Doc.ReplaceLines(3, 3, "PEER", att.Doc.Rev, false, null, "peer");
        ToolException? tex = null;
        try { tools.CallAsync("replace_lines", JsonDocument.Parse(
            $$"""{"session_id":"{{att.Id}}","start":3,"end":4,"new_text":"X","expected_rev":0,"retry_on_stale":true}""").RootElement.Clone(), default).GetAwaiter().GetResult(); }
        catch (ToolException t) { tex = t; }
        SelfTest.CheckPublic(tex is not null && tex.RetryStrategy == "reread_range" && tex.RetryStart == 1 && tex.RetryEnd == 5,
            $"polish: stale_conflict несёт retry {{strategy,range}} ({tex?.RetryStrategy})");
        Sessions.Detach(att.Id, true);

        // (#2) outline totals/truncated: >50 символов → truncated, всего честно
        var sb2 = new StringBuilder("namespace Big {").Append(Lf);
        for (int i = 1; i <= 55; i++) sb2.Append("class C").Append(i).Append(" { }").Append(Lf);
        sb2.Append('}').Append(Lf);
        string bigCs = Path.Combine(dir, "pol-outline.cs");
        File.WriteAllText(bigCs, sb2.ToString());
        var r2 = JsonSerializer.Deserialize<OpenResult>(
            tools.CallAsync("open", JsonDocument.Parse($$"""{"path":"{{bigCs.Replace("\\", "\\\\")}}"}""").RootElement.Clone(), default).GetAwaiter().GetResult(),
            RpcJson.Default.OpenResult)!;
        SelfTest.CheckPublic(r2.CodeOutline!.Length == 50 && r2.CodeOutlineTruncated && r2.CodeOutlineTotal == 56,
            $"polish: outline truncated=({r2.CodeOutline!.Length}/{r2.CodeOutlineTotal}) — агент знает полноту");

        // (#3) find span: context=2 → span вокруг хита
        string f = Path.Combine(dir, "pol-find.txt");
        File.WriteAllText(f, string.Join(Lf, "a", "b", "NEEDLE", "d", "e") + Lf);
        var (att2, _) = Sessions.Attach(f, null, null);
        var fr = JsonSerializer.Deserialize<FindResult>(
            tools.CallAsync("find", JsonDocument.Parse(
                $$"""{"session_id":"{{att2.Id}}","regex":"NEEDLE","context":2}""").RootElement.Clone(), default).GetAwaiter().GetResult(),
            RpcJson.Default.FindResult)!;
        SelfTest.CheckPublic(fr.Hits[0].Span is { Start: 1, End: 5 },
            "polish: find hit несёт span {1..5} — редактируй без follow-up show");
        Sessions.Detach(att2.Id, true);
    }
}

/// <summary>v0.8.1: help — самодокументируемый API; приглашение на первый tools/call сессии.</summary>
static class V081Help {
    public static void Run(string dir) {
        var tools = new ToolRegistry();
        tools.RegisterEditTools();
        var page = tools.CallAsync("help", JsonDocument.Parse("{}").RootElement.Clone(), default).GetAwaiter().GetResult();
        SelfTest.CheckPublic(page.Contains("1-based") && page.Contains("end = start-1") && page.Contains("[1..0]".Replace("[1..0]", "replace_lines(1, 0")),
            "help: семантика диапазонов на странице");
        SelfTest.CheckPublic(page.Contains("stale_conflict") && page.Contains("anchor_mismatch") && page.Contains("peers_dirty"),
            "help: error kinds с действиями");
        SelfTest.CheckPublic(new[] { "edit_batch", "edit_transaction", "replace_content", "replace_all", "undo", "goto", "sessions", "calc", "path-first" }.All(t => page.Contains(t)),
            "help: все ключевые инструменты в карте");
        SelfTest.CheckPublic(page.Length < 6000, $"help: компактен ({page.Length} симв.) — один вызов, не простыня");
    }
}

/// <summary>v0.5: undo (компенсирующий) и goto.</summary>
static class V05Nav {
    public static void UndoAndGoto(string dir) {
        string p = Path.Combine(dir, "undo.txt");
        File.WriteAllText(p, "one\ntwo\nthree\n", new UTF8Encoding(false));
        var s = EditSession.Open(p, null);

        s.ReplaceLines(2, 2, "TWO", 0, false, actor: "a1");
        var u = s.Undo(1, 1, false, "a1");
        SelfTest.Check(u.Applied == 1 && s.Rev == 2 && s.GetLineText(2) == "two", $"undo: replace отменён, rev вырос ({u.Rev})");
        var u2 = s.Undo(1, 2, false, "a1"); // undo undo = redo
        SelfTest.Check(u2.Applied == 1 && s.GetLineText(2) == "TWO", "undo×2 = redo: правка вернулась");

        s.InsertAfter(3, "four", 3, false, "a1");
        s.Undo(1, 4, false, "a1");
        SelfTest.Check(s.TotalLines == 3, "undo: вставка удалена");

        // конфликт: место правки затёрто НЕотменяемой правкой (250 строк > капа) — локация потеряна
        s.ReplaceLines(1, 1, "ONE", 5, false, actor: "a1");
        s.ReplaceLines(1, 1, string.Join("\n", Enumerable.Repeat("pad", 250)) + "\n", 6, false, actor: "a1");
        SelfTest.CheckThrows<ToolException>(() => s.Undo(1, 7, false, "a1"), "undo: потерянная локация → undo_conflict", "undo_conflict");
        SelfTest.Check(s.GetLineText(1) == "pad" && s.Rev == 7, "undo: после конфликта ничего не применено");
        s.Dispose();

        // goto: символы по имени
        string cs = Path.Combine(dir, "goto.cs");
        File.WriteAllText(cs, "class Alpha {\n    void Beta() { }\n}\n", new UTF8Encoding(false));
        var sg = EditSession.Open(cs, null);
        var r = sg.Symbols(false);
        _ = r; // прогрев кэша
        // goto-инструментом (Tools-слой)
        var tools = new ToolRegistry();
        tools.RegisterEditTools();
        string openJson = "{\"path\":" + JsonSerializer.Serialize(cs, RpcJson.Default.String) + "}";
        var o = JsonSerializer.Deserialize<OpenResult>(tools.CallAsync("open", JsonDocument.Parse(openJson).RootElement.Clone(), default).GetAwaiter().GetResult(), RpcJson.Default.OpenResult)!;
        var g = JsonSerializer.Deserialize<GotoResult>(tools.CallAsync("goto",
            JsonDocument.Parse($"{{\"session_id\":\"{o.SessionId}\",\"name\":\"beta\"}}").RootElement.Clone(), default).GetAwaiter().GetResult(), RpcJson.Default.GotoResult)!;
        SelfTest.Check(g.Symbols.Length == 1 && g.Symbols[0].Line == 2 && sg.GetLineText(2).Contains("Beta"), $"goto: символ по имени → строка ({g.Symbols.FirstOrDefault()?.Line})");
        sg.Dispose();
    }
}

/// <summary>v0.5.1: кириллица ровно на границе 4096-байтной выборки детекта — раньше ложный NotUtf8.</summary>
static class V051Detect {
    public static void Utf8BoundaryCyrillic(string dir) {
        string p = Path.Combine(dir, "boundary.cs");
        // 4092 ASCII + «аб» (4 байта, позиции 4092..4095): лидер «а» на 4092 рвал срез [0..4093)
        File.WriteAllText(p, new string('A', 4092) + "аб\nещё текст\n", new UTF8Encoding(false));
        var s = EditSession.Open(p, null);
        SelfTest.Check(s.TotalLines == 2 && s.GetLineText(1).EndsWith("аб"), "detect: кириллица на границе 4096 — UTF-8 распознан");
        s.Dispose();
    }
}

/// <summary>v0.10 (#18): gate/v1 — синтаксический гейт save на встроенном tree-sitter.</summary>
static class V100Gate {
    public static void Run(string dir) {
        // 1) чистая правка проходит, статус pass
        string p = Path.Combine(dir, "gate-clean.cs");
        File.WriteAllText(p, "class A {\n    int X = 1;\n    void M() { }\n}\n", new UTF8Encoding(false));
        var s = EditSession.Open(p, null);
        s.ReplaceLines(2, 2, "    int X = 2;", s.Rev, false);
        var ok = s.Save(false, "keep", false);
        SelfTest.Check(ok.GateStatus == "pass", $"gate: чистая правка проходит ({ok.GateStatus})");
        s.Dispose();

        // 2) новая ошибка блокирует (kind syntax_gate); force публикует со статусом forced + списком
        string b = Path.Combine(dir, "gate-broken.cs");
        File.WriteAllText(b, "class B {\n    int Y = 1;\n    void M() { }\n}\n", new UTF8Encoding(false));
        var s2 = EditSession.Open(b, null);
        s2.ReplaceLines(2, 2, "    int Y = 1", s2.Rev, false); // потеряли ';'
        SelfTest.CheckThrows<ToolException>(() => s2.Save(false, "keep", false),
            "gate: новая синтаксическая ошибка блокирует save", "syntax_gate");
        var forced = s2.Save(false, "keep", true);
        SelfTest.Check(forced.GateStatus == "forced" && forced.GateNew is { Count: > 0 },
            $"gate: force публикует, ошибки в ответе ({forced.GateStatus}, n={forced.GateNew?.Count})");
        SelfTest.Check(File.ReadAllText(b).Contains("int Y = 1\n"), "gate: force реально записал текст");
        s2.Dispose();

        // 3) baseline поглощает СТАРЫЕ ошибки: файл уже битый, правка чистая → проходит
        string c = Path.Combine(dir, "gate-old.cs");
        File.WriteAllText(c, "class C {\n    int Z = 1\n    void M() { }\n}\n", new UTF8Encoding(false)); // нет ';' в исходнике
        var s3 = EditSession.Open(c, null);
        s3.InsertAfter(4, "    // просто комментарий", s3.Rev, false);
        var absorbed = s3.Save(false, "keep", false);
        SelfTest.Check(absorbed.GateStatus == "pass" && absorbed.GateNew.Count == 0,
            $"gate: старая ошибка в baseline не блокирует ({absorbed.GateStatus})");
        // 4) сдвиг строк: вставка ВЫШЕ старой ошибки — тот же отпечаток, не «новая»
        s3.InsertAfter(1, "    // сдвигаю старую ошибку вниз", s3.Rev, false);
        var shifted = s3.Save(false, "keep", false);
        SelfTest.Check(shifted.GateStatus == "pass",
            $"gate: съехавшая ошибка — тот же отпечаток, не новая ({shifted.GateStatus})");
        // 5) check не трогает baseline: после check новая ошибка всё равно блокирует
        s3.CheckBrackets(0, 0);
        s3.InsertAfter(6, "    int W = 2", s3.Rev, false); // НОВАЯ битая строка (нет ';')
        SelfTest.CheckThrows<ToolException>(() => s3.Save(false, "keep", false),
            "gate: check не съедает baseline — новая ошибка блокирует", "syntax_gate");
        s3.Dispose();

        // 6) не-код: .txt — гейт off
        string t = Path.Combine(dir, "gate.txt");
        File.WriteAllText(t, "это просто текст { без скобок;\n");
        var s4 = EditSession.Open(t, null);
        s4.InsertAfter(1, "ещё строка", s4.Rev, false);
        var off = s4.Save(false, "keep", false);
        SelfTest.Check(off.GateStatus == "off", $"gate: .txt — гейт не применяется ({off.GateStatus})");
        s4.Dispose();

        // 7) другие языки: js без ')' и bash без fi блокируют; python-отступ — документированное упущение
        string js = Path.Combine(dir, "gate.js");
        File.WriteAllText(js, "function f(a) { return a; }\n");
        var s5 = EditSession.Open(js, null);
        s5.ReplaceLines(1, 1, "function f(a { return a; }", s5.Rev, false);
        SelfTest.CheckThrows<ToolException>(() => s5.Save(false, "keep", false), "gate: js без ')' блокирует", "syntax_gate");
        s5.Dispose();

        string sh = Path.Combine(dir, "gate.sh");
        File.WriteAllText(sh, "echo hi\n");
        var s6 = EditSession.Open(sh, null);
        s6.ReplaceLines(1, 1, "if [ -f x ]; then echo hi", s6.Rev, false); // нет fi
        SelfTest.CheckThrows<ToolException>(() => s6.Save(false, "keep", false), "gate: bash без fi блокирует", "syntax_gate");
        s6.Dispose();

        string py = Path.Combine(dir, "gate.py");
        File.WriteAllText(py, "def f(a):\n    return a\n");
        var s7 = EditSession.Open(py, null);
        s7.ReplaceLines(2, 2, "return a", s7.Rev, false); // битый отступ — грамматика python терпит
        var pyLimit = s7.Save(false, "keep", false);
        SelfTest.Check(pyLimit.GateStatus == "pass",
            $"gate: python-отступы не ловим — документированное ограничение ({pyLimit.GateStatus})");
        s7.Dispose();
    }
}
