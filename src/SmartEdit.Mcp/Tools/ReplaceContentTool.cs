using System.Text;
using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

/// <summary>
/// v0.9.1 (внешний dogfooding-отчёт): замена ТОЧНОГО текста без номеров строк — сила SmartEdit
/// (сессии, контекст-экономия, диагностики) + безопасность старого byte-exact Edit. Текст обязан
/// встречаться РОВНО один раз: 0 → not_found, >1 → ambiguous_match со списком строк (расширь
/// сниппет окружающими строками). Многострочный, частичные строки (совпадение может начинаться/
/// заканчиваться посреди строки — окружение сохраняется). Скан и применение — под одним док-локом;
/// ожидаемые строки передаются как expect_old-якорь.
/// </summary>
sealed class ReplaceContentTool : ITool {
    public string Name => "replace_content";
    public string Description =>
        "Replace an EXACT text snippet — no line numbers needed (the find→show→replace loop, minus the line math). " +
        "The snippet must occur EXACTLY ONCE: 0 matches → kind=not_found (copy it exactly from a fresh show/find — whitespace matters); " +
        ">1 → kind=ambiguous_match listing the matching lines (extend the snippet with surrounding lines to make it unique). " +
        "Multiline snippets and partial-line matches are supported (surrounding text on the first/last line is preserved). " +
        "Optional start_line/end_line narrow the search scope — the boilerplate antidote: the snippet must be unique WITHIN the bounds. " +
        "replace='' deletes the snippet. Same rev/dry_run contract as replace_lines; diagnostics returned as usual.";
    public string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string","description":"from a previous response; optional if path given"},"path":{"type":"string","description":"file path (open-by-path: skip open entirely)"},
          "find":{"type":"string","description":"exact text to replace (multiline OK; must be unique in the file)"},
          "replace":{"type":"string","description":"replacement text ('' = delete the snippet)"},
          "expected_rev":{"type":"integer","description":"optional: omit = the last rev you observed (server tracks it)"},
          "start_line":{"type":"integer","description":"optional: scan from this line (1-based) — narrows uniqueness scope"},
          "end_line":{"type":"integer","description":"optional: scan up to this line inclusive"},
          "dry_run":{"type":"boolean","description":"default false"}
        },"required":["find","replace"]}
        """;

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var att = Sessions.Resolve(args.GetNullableString("session_id"), args.GetNullableString("path"));
        var s = att.Doc;
        string find = args.GetString("find");
        string replace = args.GetString("replace");
        if (find.Length == 0)
            throw new ToolException(ToolException.InvalidArgs, "find must not be empty — for a literal rename use replace_all");
        bool dryRun = args.GetBool("dry_run");
        int expectedRev = args.GetInt("expected_rev", att.LastSeenRev);
        string[] fl = find.Replace("\r\n", "\n").Split('\n');
        string[] rl = replace.Replace("\r\n", "\n").Split('\n');
        int scopeFrom = Math.Max(1, args.GetInt("start_line", 1));   // v0.9.3 (Gemini): сужение скопа для бойлерплейта
        int scopeTo = args.GetInt("end_line", 0);                     // 0 = до конца файла

        var outcome = await ToolOps.LockedAsync(s, () => {
            // скан под локом: список вхождений (startLine, endLine, prefixLen, lastConsumed)
            var hits = new List<(int Start, int End, int Prefix, int Consumed)>();
            int lim = scopeTo > 0 ? Math.Min(scopeTo, s.TotalLines) : s.TotalLines;
            if (scopeFrom > lim) throw new ToolException(ToolException.InvalidRange, $"empty scope: start_line {scopeFrom} > end_line {lim}");
            for (int i = scopeFrom; i <= lim && hits.Count <= 10; i++) {
                string line = s.GetLineText(i);
                if (fl.Length == 1) {
                    int pos = 0;
                    while (hits.Count <= 10 && (pos = line.IndexOf(fl[0], pos, StringComparison.Ordinal)) >= 0) {
                        hits.Add((i, i, pos, pos + fl[0].Length)); // consumed = позиция конца сниппета в строке
                        pos += fl[0].Length;
                    }
                } else {
                    // первая строка сниппета кончается концом строки i; средние — точные строки; последняя — префикс строки i+k
                    if (!line.EndsWith(fl[0], StringComparison.Ordinal)) continue;
                    int last = i + fl.Length - 1;
                    if (last > lim) continue; // вхождение должно целиком лежать в скопе
                    bool ok = true;
                    for (int m = 1; m < fl.Length - 1 && ok; m++)
                        if (s.GetLineText(i + m) != fl[m]) ok = false;
                    if (ok && !s.GetLineText(last).StartsWith(fl[^1], StringComparison.Ordinal)) ok = false;
                    if (ok) hits.Add((i, last, line.Length - fl[0].Length, fl[^1].Length));
                }
            }
            if (hits.Count == 0)
                throw new ToolException("not_found",
                    $"snippet not found{(scopeTo > 0 ? $" in lines {scopeFrom}..{lim}" : "")} — copy it exactly from a fresh show/find (whitespace/indentation matter; the file may have changed)");
            if (hits.Count > 1)
                throw new ToolException("ambiguous_match",
                    $"snippet occurs {hits.Count} times: lines {string.Join(", ", hits.Take(10).Select(h => h.Start))} — " +
                    "extend it with surrounding lines, or narrow the scope with start_line/end_line");
            var (start, end, prefix, consumed) = hits[0];
            string expectOld = string.Join("\n", Enumerable.Range(start, end - start + 1).Select(s.GetLineText));
            // новый текст диапазона: префикс + замена + хвост последней строки
            var sb = new StringBuilder().Append(s.GetLineText(start), 0, prefix).Append(rl[0]);
            for (int m = 1; m < rl.Length; m++) sb.Append('\n').Append(rl[m]);
            string lastLine = s.GetLineText(end);
            sb.Append(lastLine, consumed, lastLine.Length - consumed);
            return s.ReplaceLines(start, end, sb.ToString(), expectedRev, dryRun, expectOld, att.Id);
        });
        att.LastSeenRev = outcome.Rev; // v0.9: ответ доставил состояние
        return ToolOps.Json(new MutationResult {
            Rev = outcome.Rev, TotalLines = outcome.TotalLines, Diff = outcome.Diff, DryRun = outcome.DryRun,
            Brackets = outcome.Brackets?.ToArray(),
            Recovered = outcome.Recovered, Intervening = outcome.Intervening?.ToArray(),
            Diagnostics = outcome.Diags?.Select(d => new DiagDto { Line = d.Line, Code = d.Code, Message = d.Message }).ToArray(),
            Unsaved = s.Dirty, // v0.9.3: применено в RAM, НЕ сохранено — не забудь save(path)
        }, RpcJson.Default.MutationResult);
    }
}
