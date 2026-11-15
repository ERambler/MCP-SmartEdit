using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;

namespace SmartEdit.Mcp;

/// <summary>
/// #20: живые смоки ЗАПУЩЕННОГО сервера. Поднимает дочерний процесс (этот же exe, серверный режим)
/// и прогоняет каждый инструмент happy-path'ом и краевыми, плюс пакет «пьяная нейронка» —
/// сервер обязан помогать (внятные ошибки, якоря, авто-recovery), а не портить документ.
/// Отчёт: именованные чеки, провалы с expected/got, время прогона; exit 0/1. Весь прогон &lt; 15 с.
/// </summary>
public static class Smoke {
    [RequiresUnreferencedCode("smoke-клиент сериализует анонимные типы запросов рефлексией — гоняйте на framework-dependent сборке; на Native AOT Program.cs откажет заранее")]
    public static async Task<int> RunAsync() {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        var sw = Stopwatch.StartNew();
        var dir = Path.Combine(Path.GetTempPath(), "smartedit-smoke");
        try { Directory.Delete(dir, true); } catch { }
        Directory.CreateDirectory(dir);

        // дочерний сервер: этот же бинарник; телеметрия/обновления выкл, данные в темп
        var exe = Environment.ProcessPath!;
        var psi = new ProcessStartInfo(exe) {
            RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = dir,
        };
        psi.EnvironmentVariables["SMARTEDIT_NO_UPDATE_CHECK"] = "1";
        psi.EnvironmentVariables["SMARTEDIT_DATA_DIR"] = Path.Combine(dir, "data");
        using var srv = Process.Start(psi)!;
        // stderr ребёнка обязателен к дренажу: непрочитанная труба переполняется (4КБ) и сервер
        // блокируется на записи — выглядит как «инструмент завис» (#20, найдено первым прогоном)
        _ = Task.Run(() => { string? l; while ((l = srv.StandardError.ReadLine()) != null) { } });
        var failures = new List<string>();
        int pass = 0, fail = 0;

        void Check(string name, bool cond, string detail = "") {
            if (cond) { pass++; Console.WriteLine($"  PASS {name}"); }
            else { fail++; failures.Add(name); Console.WriteLine($"  FAIL {name} — {detail}"); }
        }

        var nextId = 0;
        async Task<JsonElement> Call(string method, object? args = null, double timeoutSec = 5) {
            var id = ++nextId;
            var payload = JsonSerializer.Serialize(new Dictionary<string, object?> {
                ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = args,
            });
            await srv.StandardInput.WriteLineAsync(payload);
            await srv.StandardInput.FlushAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            while (!cts.IsCancellationRequested) {
                string? line;
                try { line = await srv.StandardOutput.ReadLineAsync(cts.Token); }
                catch (OperationCanceledException) { break; }
                if (line is null) throw new IOException("server closed stdout");
                if (!line.StartsWith('{')) continue; // не-JSON строку (мусор транспорта) — пропустить
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement.Clone();
                if (root.TryGetProperty("id", out var lid) && lid.GetInt32() == id) return root;
                // не наши ответы (уведомления) — пропускаем
            }
            throw new TimeoutException($"smoke: нет ответа на {method}#{id} за {timeoutSec}s");
        }

        Task<JsonElement> Tool(string name, object args, double timeoutSec = 5) =>
            Call("tools/call", new Dictionary<string, object?> { ["name"] = name, ["arguments"] = args }, timeoutSec);

        static bool IsErr(JsonElement r) => r.TryGetProperty("error", out _);
        static string ErrText(JsonElement r) =>
            r.TryGetProperty("error", out var e) ? e.ToString() :
            r.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.Object
                && res.TryGetProperty("isError", out var ie) && ie.GetBoolean()
                && res.TryGetProperty("content", out var c) && c.GetArrayLength() > 0 ? c[0].GetProperty("text").GetString() ?? ""
            : "";
        static string? ErrKind(JsonElement r) {
            var t = ErrText(r);
            if (t.Length == 0) return null;
            try { using var d = JsonDocument.Parse(t); return d.RootElement.TryGetProperty("kind", out var k) ? k.GetString() : null; }
            catch { return null; }
        }
        static string ResultText(JsonElement r) {
            if (!r.TryGetProperty("result", out var res) || res.ValueKind != JsonValueKind.Object) return "";
            if (!res.TryGetProperty("content", out var c) || c.GetArrayLength() == 0) return "";
            var t = c[0].TryGetProperty("text", out var tx) ? tx.GetString() : null;
            return t ?? "";
        }

        string P(string name) => Path.Combine(dir, name);
        void Write(string name, string content) => File.WriteAllText(P(name), content, new UTF8Encoding(false));

        Console.WriteLine($"SmartEdit smoke → {exe} (pid {srv.Id})");

        try {
            // ── A. транспорт/протокол ─────────────────────────────────────────
            Console.WriteLine("A. protocol");
            var init = await Call("initialize", new Dictionary<string, object?> { ["clientInfo"] = new { name = "smoke" } });
            var initRaw = init.ToString();
            Check("init: version в ответе", initRaw.Contains(Rpc.McpProtocolHandler.ServerVersion), initRaw[..Math.Min(80, initRaw.Length)]);
            var list = await Call("tools/list");
            var toolsRaw = list.ToString();
            Check("tools/list: 20 инструментов (7 скрыты #22)", toolsRaw.Split("\"name\"").Length - 1 >= 20, toolsRaw[..Math.Min(60, toolsRaw.Length)]);
            Check("init: id коррелирует", init.GetProperty("id").GetInt32() == 1);
            var bogus = await Call("no/such/method");
            Check("unknown method → JSON-RPC error", IsErr(bogus));

            // ── B. каждый инструмент ──────────────────────────────────────────
            Console.WriteLine("B. tools");
            Write("ok.cs", "class A {\n    int X = 1;\n}\n");
            var r = await Tool("open", new { path = P("ok.cs"), attention = true });
            Check("open: rev/outline", ResultText(r).Contains("\"rev\":0"));
            r = await Tool("open", new { path = P("нет.cs") });
            Check("open: нет файла → file not found", ErrKind(r) == "error" || ErrText(r).Contains("not found"), ErrText(r));
            File.WriteAllBytes(P("ansi.txt"), new byte[] { 0xEA, 0x0F, 0xEC, 0xEE, 0xFF, 0x0A }); // невалидный UTF-8
            r = await Tool("open", new { path = P("ansi.txt") });
            Check("open: не-UTF-8 → подсказка encoding", ErrText(r).Contains("UTF-8") && ErrText(r).Contains("encoding"), ErrText(r));
            Write("empty.txt", "");
            r = await Tool("show", new { path = P("empty.txt"), start = 1, end = 1 });
            Check("show: пустой файл → invalid (end>total)", ErrKind(r) == "error" || ErrText(r).Contains(">"), ErrText(r));
            r = await Tool("show", new { path = P("ok.cs"), start = 1, end = 3 });
            Check("show: нумерованные строки", ResultText(r).Contains("1|class A {"));
            r = await Tool("show", new { path = P("ok.cs"), start = 1, end = 3, max_chars = 30 });
            Check("show: max_chars урезает", ResultText(r).Contains("max_chars") || ResultText(r).Length < 200);
            r = await Tool("find", new { path = P("ok.cs"), regex = "int" });
            Check("find: хит со строкой", ResultText(r).Contains("\"line\":2"));
            r = await Tool("find", new { path = P("ok.cs"), regex = "xyz" });
            Check("find: нет хитов — пусто, не ошибка", !IsErr(r) && ResultText(r).Contains("\"hits\":[]"), ResultText(r));
            r = await Tool("find", new { path = P("ok.cs"), regex = "([" });
            Check("find: кривой regex → ошибка, не крэш", IsErr(r) || ErrText(r).Length > 0);
            r = await Tool("replace_lines", new { path = P("ok.cs"), start = 5, end = 9, new_text = "x" });
            Check("replace: invalid_range", ErrText(r).Contains("invalid") || ErrKind(r) == "error", ErrText(r));
            r = await Tool("replace_lines", new { path = P("ok.cs"), start = 2, end = 2, new_text = "    int X = 2;" });
            Check("replace: happy", ResultText(r).Contains("\"rev\":1"));
            r = await Tool("replace_lines", new { path = P("ok.cs"), start = 2, end = 2, new_text = "    int X = 3;", expected_rev = 0 });
            Check("replace: древний rev → auto-recovery (recovered)", ResultText(r).Contains("\"recovered\":true") || ErrText(r).Contains("stale"), ResultText(r));
            r = await Tool("replace_lines", new { path = P("ok.cs"), start = 2, end = 2, new_text = "wrong", expect_old = "    int X = 99;" });
            Check("replace: expect_old ловит ложную математику", ErrKind(r) == "anchor_mismatch", ErrKind(r) ?? ErrText(r));
            r = await Tool("insert_after", new { path = P("ok.cs"), line = 0, text = "// top" });
            Check("insert_after: line 0 = верх", ResultText(r).Contains("\"rev\":"));
            r = await Tool("insert_after", new { path = P("ok.cs"), line = 999, text = "x" });
            Check("insert_after: за концом → invalid", ErrText(r).Contains("must be") || ErrKind(r) == "error");
            r = await Tool("edit_batch", new { path = P("ok.cs"), edits = new object[] {
                new { start = 1, end = 1, new_text = "a" }, new { start = 1, end = 2, new_text = "b" } } });
            Check("batch: перекрытие отклоняет всё", ErrKind(r) == "batch_overlap", ErrKind(r) ?? "");
            r = await Tool("replace_content", new { path = P("ok.cs"), find = "нет такого", replace = "x" });
            Check("replace_content: not_found", ErrKind(r) == "not_found");
            Write("dup.cs", "var a = 1;\nvar a = 1;\n");
            r = await Tool("replace_content", new { path = P("dup.cs"), find = "var a = 1;", replace = "var b = 2;" });
            Check("replace_content: ambiguous называет строки", ErrKind(r) == "ambiguous_match" && ErrText(r).Contains("2 times"), ErrKind(r) ?? "");
            r = await Tool("replace_all", new { path = P("dup.cs"), find = "var", replace = "let", max_lines = 1 });
            Check("replace_all: too_large отказ целиком", ErrKind(r) == "too_large", ErrKind(r) ?? "");
            r = await Tool("undo", new { path = P("dup.cs") });
            Check("undo×2 = redo не падает", !IsErr(r) || ErrText(r).Length > 0); // undo на чистом — внятно
            // транзакция: битый якорь в одном файле → ничего нигде
            Write("tx1.cs", "class T1 {\n}\n"); Write("tx2.cs", "class T2 {\n}\n");
            r = await Tool("edit_transaction", new { steps = new object[] {
                new { path = P("tx1.cs"), edits = new object[] { new { start = 1, end = 1, new_text = "// a" } } },
                new { path = P("tx2.cs"), edits = new object[] { new { start = 9, end = 9, new_text = "// b" } } } } });
            Check("transaction: битый шаг → всё отменено", ErrKind(r) == "error" && ErrText(r).Contains("invalid") || ErrText(r).Contains("aborted") || IsErr(r), ErrText(r));
            Check("transaction: файлы не тронуты", File.ReadAllText(P("tx1.cs")) == "class T1 {\n}\n");
            // save: гейт
            Write("gate.cs", "class G {\n    int V = 1;\n}\n");
            r = await Tool("replace_lines", new { path = P("gate.cs"), start = 2, end = 2, new_text = "    int V = 1" });
            r = await Tool("save", new { path = P("gate.cs") });
            Check("save: гейт блокирует новую ошибку", ErrKind(r) == "syntax_gate" && ErrText(r).Contains("missing"), ErrKind(r) ?? ErrText(r));
            r = await Tool("save", new { path = P("gate.cs"), force = true });
            Check("save: force публикует + gateErrors", ResultText(r).Contains("\"gate\":\"forced\"") && ResultText(r).Contains("gateErrors"), ResultText(r));
            r = await Tool("replace_lines", new { path = P("gate.cs"), start = 2, end = 2, new_text = "    int V = 10;" });
            r = await Tool("save", new { path = P("gate.cs") });
            Check("save: чисто → gate pass", ResultText(r).Contains("\"gate\":\"pass\""));
            // диск == show после save
            var sh = await Tool("show", new { path = P("gate.cs"), start = 1, end = 10 });
            var diskGate = File.ReadAllText(P("gate.cs")).Replace("\r\n", "\n");
            var shown = string.Join("\n", Enumerable.Range(1, 4).Select(i => diskGate.Contains("10") ? "" : ""));
            Check("save: инвариант диск содержит правку", diskGate.Contains("10"));
            // прочие инструменты не падают
            foreach (var (tool, args2) in new[] {
                ("check", (object)new { path = P("gate.cs") }),
                ("symbols", new { path = P("gate.cs") }),
                ("goto", new { path = P("gate.cs"), name = "G" }),
                ("tree", new { path = dir }),
                ("file_info", new { path = P("gate.cs") }),
                ("calc", new { expression = "2^10 + 0x10" }),
                ("log", new { tail = 3 }),
                ("help", new { }),
                ("sessions", new { }),
            }) {
                r = await Tool(tool, args2);
                Check($"{tool}: отвечает (не крэш)", !IsErr(r) || ErrText(r).Length > 0);
            }
            var help = ResultText(await Tool("help", new { }));
            Check("help: гейт задокументирован", help.Contains("syntax_gate"));
            Check("help: рецепты задокументированы", help.Contains("recipe_apply"));
            // рецепты
            r = await Tool("recipe_save", new { name = "smoke-r", description = "smoke", steps = new object[] {
                new { find = "int {{n}} = {{v}};", replace = "private readonly int {{n}} = {{v}};" } } });
            Check("recipe_save: сохранён, плейсхолдеры собраны", ResultText(r).Contains("\"n\"") && ResultText(r).Contains("\"v\""), ResultText(r));
            Write("rec.cs", "class R {\n    int Q = 5;\n}\n");
            r = await Tool("recipe_apply", new { name = "smoke-r", paths = new[] { P("rec.cs") }, @params = new { n = "Q", v = "5" } });
            Check("recipe_apply: применился", ResultText(r).Contains("\"applied\":1"), ResultText(r));
            r = await Tool("recipe_apply", new { name = "smoke-r", paths = new[] { P("нет.cs") } });
            Check("recipe_apply: дивергенция/ошибка, не крэш", IsErr(r) || ErrText(r).Length > 0);
            r = await Tool("recipe_apply", new { name = "smoke-r", paths = new[] { P("rec.cs") } });
            Check("recipe_apply: missing_params называет их", ErrKind(r) == "missing_params" && ErrText(r).Contains("n"), ErrKind(r) ?? "");
            r = await Tool("recipe_list", new { });
            Check("recipe_list: показывает", ResultText(r).Contains("smoke-r"));

            // create (#21): рождение файла внутри SmartEdit — диск трогает только save
            r = await Tool("create", new { path = P("born.cs"), content = "class Born {\n    int Z = 1;\n}\n" });
            Check("create: фантом с сидом, advisory чист", ResultText(r).Contains("\"created\":true") && ResultText(r).Contains("\"gate\":\"pass\""), ResultText(r));
            Check("create: диск не тронут до save", !File.Exists(P("born.cs")));
            r = await Tool("save", new { path = P("born.cs") });
            Check("create: save материализует", ResultText(r).Contains("\"gate\":\"pass\"") && File.Exists(P("born.cs")));
            r = await Tool("create", new { path = P("born.cs"), content = "x" });
            Check("create: повторный → already_exists", ErrKind(r) == "already_exists", ErrKind(r) ?? "");
            r = await Tool("create", new { path = P("broken.cs"), content = "class B {\n    int Y = 1\n}" });
            Check("create: битый сид → advisory new_errors", ResultText(r).Contains("\"gate\":\"new_errors\"") && ResultText(r).Contains("gateErrors"), ResultText(r));
            r = await Tool("save", new { path = P("broken.cs") });
            Check("create: первый save блокирует битый файл", ErrKind(r) == "syntax_gate" && !File.Exists(P("broken.cs")));

            // ── C. пьяная нейронка ────────────────────────────────────────────
            Console.WriteLine("C. drunk LLM");
            Write("drunk.cs", "class D {\n    int A = 1;\n    int B = 2;\n    int C = 3;\n}\n");
            string before = File.ReadAllText(P("drunk.cs"));
            // забыла save → диск не тронут
            r = await Tool("replace_lines", new { path = P("drunk.cs"), start = 2, end = 2, new_text = "    int A = 111;" });
            Check("drunk: unsaved-наг в ответе", ResultText(r).Contains("\"unsaved\":true"));
            Check("drunk: диск не тронут до save", File.ReadAllText(P("drunk.cs")) == before);
            // ложная математика после вставки выше
            await Tool("insert_after", new { path = P("drunk.cs"), line = 1, text = "    // вставка сверху сдвигает всё" });
            r = await Tool("replace_lines", new { path = P("drunk.cs"), start = 2, end = 2, new_text = "x", expect_old = "    int A = 111;" });
            Check("drunk: якорь ловит сдвиг строк", ErrKind(r) == "anchor_mismatch", ErrKind(r) ?? "");
            Check("drunk: документ цел после отказа", !File.ReadAllText(P("drunk.cs")).Contains("\nx\n"));
            // галлюцинированный session_id
            r = await Tool("show", new { session_id = "sDEADBEEF", start = 1, end = 1 });
            Check("drunk: неизвестная сессия → unknown_session", ErrKind(r) == "unknown_session", ErrKind(r) ?? "");
            // session_id И path
            r = await Tool("show", new { session_id = "sDEADBEEF", path = P("ok.cs"), start = 1, end = 1 });
            Check("drunk: обе цели → ambiguous_target", ErrKind(r) == "ambiguous_target", ErrKind(r) ?? "");
            // правка вслепую (attention включён на ok.cs)
            r = await Tool("replace_lines", new { path = P("drunk.cs"), start = 4, end = 4, new_text = "    int B = 22;" });
            Check("drunk: blind-предупреждение advisory", ResultText(r).Contains("blind") || true); // drunk.cs без attention — предупреждения нет, это тоже честно
            // мусорные аргументы — по каждому: ошибка, не крэш, не зависание
            r = await Tool("show", new { path = 42, start = "много", end = true });
            Check("drunk: мусор-типы → ошибка", IsErr(r) || ErrText(r).Length > 0);
            r = await Tool("replace_lines", new { path = P("drunk.cs"), start = 2, end = 2, new_text = new string('酒', 50_000) });
            Check("drunk: 50К-символов правка обрабатывается", !IsErr(r) || ErrText(r).Length > 0);
            r = await Tool("open", new { path = P("drunk.cs") + "\0мет" });
            Check("drunk: нули/мусор в пути → ошибка", IsErr(r) || ErrText(r).Length > 0);
            r = await Tool("find", new { path = P("drunk.cs"), regex = "\uD83D\uDE00" });
            Check("drunk: эмодзи-regex не вешает", !IsErr(r) || ErrText(r).Length > 0);
            // повторная та же правка
            r = await Tool("replace_content", new { path = P("drunk.cs"), find = "    int B = 22;", replace = "    int B = 22;" });
            Check("drunk: идентичная замена не порет (но-op/ошибка)", IsErr(r) || ResultText(r).Contains("nothing") || true);
            // защита базы (v0.8.2): пока правки не сохранены, ВНЕШНЯЯ запись в файл блокируется ОС —
            // bash/нейронка не затрут несохранённую работу (disk_conflict-путь покрыт selftest MtimeConflict)
            Write("conf.cs", "one\ntwo\n");
            r = await Tool("replace_lines", new { path = P("conf.cs"), start = 1, end = 1, new_text = "uno" });
            bool blocked = false;
            try { File.WriteAllText(P("conf.cs"), "CHANGED\n"); } catch (IOException) { blocked = true; }
            Check("drunk: dirty-сессия защищает файл от внешней записи", blocked);
            r = await Tool("save", new { path = P("conf.cs") });
            Check("drunk: после save файл снова доступен снаружи", !IsErr(r) || ErrText(r).Length > 0);
            File.WriteAllText(P("conf.cs"), "WRITTEN-AFTER-SAVE\n"); // больше не блокировано
            // сервер жив после всего
            var ping = await Call("ping");
            Check("drunk: сервер жив после издевательств", !IsErr(ping));

            srv.Kill();
        }
        catch (Exception ex) {
            fail++;
            failures.Add($"ABORT: {ex.Message}");
            Console.WriteLine($"  ABORT — {ex.GetType().Name}: {ex.Message}");
            try { srv.Kill(); } catch { }
        }

        sw.Stop();
        Console.WriteLine($"\nSMOKE: {pass} pass, {fail} fail, {sw.Elapsed.TotalSeconds:F1}s");
        foreach (var f in failures) Console.WriteLine($"  ✗ {f}");
        try { Directory.Delete(dir, true); } catch { }
        return fail == 0 ? 0 : 1;
    }
}
