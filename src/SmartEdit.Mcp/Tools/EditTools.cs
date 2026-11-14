using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;


public static class EditToolsRegistration {
    public static void RegisterEditTools(this ToolRegistry tools) {
        tools.Register(new OpenTool());
        tools.Register(new ShowTool());
        tools.Register(new FindTool());
        tools.Register(new ReplaceLinesTool());
        tools.Register(new InsertAfterTool());
        tools.Register(new SaveTool());
        tools.Register(new CloseTool());
        tools.Register(new CreateTool());       // #21 (v0.12)
        tools.Register(new RecipeSaveTool());   // #8 (v0.11)
        tools.Register(new RecipeListTool());
        tools.Register(new RecipeApplyTool());
        // v0.2: структурное зрение и мета-утилиты
        tools.Register(new SymbolsTool());
        tools.Register(new CheckTool());
        tools.Register(new TreeTool());
        tools.Register(new CalcTool());
        tools.Register(new FileInfoTool());
        tools.Register(new ConvertTool());
        // v0.3: серия правок одним вызовом + литеральный rename по файлу
        tools.Register(new BatchTool());
        tools.Register(new ReplaceAllTool());
        tools.Register(new SessionsTool());
        tools.Register(new SyncTool()); // v0.4
        tools.Register(new LogTool());  // v0.5: экстренная ручка
        tools.Register(new UndoTool());
        tools.Register(new GotoTool()); // v0.5
        tools.Register(new HelpTool());
        tools.Register(new TransactionTool()); // v0.9 (#3)
        tools.Register(new ReplaceContentTool()); // v0.9.1: 23 инструмента — замена точного текста без номеров строк
    }
}

/// <summary>Помощники инструментов: сериализация, лок сессии, компиляция regex.</summary>
static class ToolOps {
    internal static string Json<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Serialize(value, typeInfo);

    /// <summary>Все операции сессии — под её семафором.</summary>
    internal static async Task<T> LockedAsync<T>(EditSession s, Func<T> work) {
        await s.Gate.WaitAsync();
        try {
            s.EnsureLive();   // #14: хэндл отпущен между вызовами — восстановить (или прозрачно сбросить при внешней замене)
            return work();
        }
        finally {
            s.ReleaseIfClean(); // #14: чистый документ не держит файл между вызовами (dirty держит — защита базы)
            s.Gate.Release();
        }
    }

    internal static async Task Locked(EditSession s, Action work) {
        await s.Gate.WaitAsync();
        try {
            s.EnsureLive();   // #14
            work();
        }
        finally {
            s.ReleaseIfClean();
            s.Gate.Release();
        }
    }

    internal static Regex CompileRegex(string pattern, bool ignoreCase) {
        try {
            return new Regex(pattern,
                RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : 0),
                TimeSpan.FromSeconds(15));
        }
        catch (ArgumentException ex) {
            throw new ArgumentException($"invalid regex: {ex.Message}");
        }
    }
}

/// <summary>#21 (консилиум 2026-11-14): рождение файла внутри SmartEdit — RAM до save, гейт участвует.</summary>
sealed class CreateTool : ITool {
    public string Name => "create";
    public string Description =>
        "Create a NEW file as a RAM session (nothing touches disk until save). content seeds it (multi-line, ≤2MB, optional). " +
        "Existing file or live session on the path → already_exists (create never truncates). Advisory syntax check of the seed is in the response; " +
        "the FIRST save gates hard (empty baseline = every syntax error is NEW; fix in RAM, save once — force=true is the conscious override). " +
        "create_parents:true creates missing directories at save. New files: UTF-8 no BOM, LF.";
    public string InputSchemaJson => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"absolute path of the NEW file"},
          "content":{"type":"string","description":"seed content (multi-line, default empty)"},
          "create_parents":{"type":"boolean","description":"default false; true = create missing parent directories at save"},
          "note":{"type":"string","description":"presence annotation for peers"},
          "attention":{"type":"boolean","description":"#5 opt-in seen-tracking for the new document"}
        },"required":["path"]}
        """;

    public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        string path = args.GetString("path", "");
        if (path.Length == 0) throw new ToolException(ToolException.InvalidArgs, "path is required");
        string content = args.GetString("content", "");
        if (content.Length > (2 << 20))
            throw new ToolException(ToolException.InvalidArgs, $"content is {content.Length / 1024}KB — cap 2MB per create; seed empty and build via edit_batch");
        bool createParents = args.GetBool("create_parents");
        var (att, _) = Sessions.Create(path, content, createParents, args.GetNullableString("note"));
        var s = att.Doc;
        if (args.GetBool("attention")) s.Attention = true; // #5

        // advisory-парс сеяного контента (#21): мгновенная обратная связь, НЕ блок
        string advisory = "off";
        GateErrorDto[] advisoryErrors = [];
        if (content.Length > 0 && SyntaxGate.Applies(s.Path)) {
            var res = SyntaxGate.Analyze(s.Path, content);
            if (res.Status == "pass" && res.Errors.Count > 0) {
                advisory = "new_errors";
                advisoryErrors = [.. res.Errors.Take(10).Select(d => new GateErrorDto {
                    Kind = d.Kind, Token = d.Token, Row = d.Row, Col = d.Col, Fragment = d.Fragment })];
            }
            else advisory = res.Status;
        }
        return Task.FromResult(ToolOps.Json(new CreateResult {
            SessionId = att.Id, Path = s.Path, Rev = s.Rev, TotalLines = s.TotalLines,
            SeededLines = s.TotalLines, Created = true,
            Gate = advisory, GateErrors = advisoryErrors,
            Unsaved = true, // диск не тронут: save(path) материализует файл
        }, RpcJson.Default.CreateResult));
    }
}

sealed class OpenTool : ITool {
    public string Name => "open";
    public string Description =>
        "Open a text file as an edit session. Any size for UTF-8 (memory-mapped); non-UTF-8 needs encoding param (cp1251|utf-16le|utf-16be, up to 256MB). " +
        "note = why you're here (shown to other editors). Re-opening the SAME path reuses the shared document: one rev space for everyone — " +
        "the response lists peers (who's here, notes) and recent edits. " +
        "Returns session_id, rev, total_lines, encoding, crlf, outline (sections), code_outline (top-50 symbols for code files ≤5k lines), todo_count, folded preview.";
    public string InputSchemaJson => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"absolute file path"},
          "encoding":{"type":"string","description":"force: cp1251 | utf-16le | utf-16be (non-UTF-8 files); default: autodetect"},
          "note":{"type":"string","description":"why you're editing (presence annotation for peers)"},
          "attention":{"type":"boolean","description":"#5 opt-in: track seen ranges (show/find/goto) — edits to unseen lines get a blind warning"}
        },"required":["path"]}
        """;

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        string path = args.GetString("path");
        if (path == "") throw new ArgumentException("path is required");
        string? note = args.GetNullableString("note");
        var (att, reused) = Sessions.Attach(path, args.GetNullableString("encoding"), note);
        var s = att.Doc;
        if (args.GetBool("attention")) s.Attention = true; // #5: свойство документа; off по умолчанию
        var peers = Sessions.Peers(s);
        // v0.6.2 (#2): outline в open-ответе; preview читает строки — потому ВСЁ под одним замком (#14)
        var (outlineSyms, outlineTotal, outlineTruncated, preview) = await ToolOps.LockedAsync(s, () => {
            var o = BuildCodeOutline(s);
            return (o.Outline, o.Total, o.Truncated, BuildPreview(s));
        });
        return ToolOps.Json(new OpenResult {
            SessionId = att.Id,
            Path = s.Path,
            Rev = s.Rev,
            TotalLines = s.TotalLines,
            Bytes = s.Bytes,
            Encoding = s.DiskEncoding,
            Crlf = s.Crlf,
            TodoCount = s.TodoCount,
            Usings = [.. s.Usings],
            Outline = s.Outline.Select(e => new OutlineDto { Line = e.Line, Level = e.Level, Title = e.Title }).ToArray(),
            CodeOutline = outlineSyms,
            CodeOutlineTotal = outlineTotal,
            CodeOutlineTruncated = outlineTruncated,
            Preview = preview,
            Reused = reused,
            Peers = [.. peers.Select(p => new PeerDto {
                SessionId = p.Id, Note = p.Note, Stale = p.Stale,
                PendingEdits = s.PendingEdits.GetValueOrDefault(p.Id),
            })],
            Recent = [.. s.History.TakeLast(10).Select(h => $"r{h.Rev} {h.Actor}: {h.Desc}")],
        }, RpcJson.Default.OpenResult);
    }

    /// <summary>
    /// Компактный code outline (issue #2): для кода ≤5k строк греем кэш symbols (пересчёт только при смене rev)
    /// и отдаём топ-50 символов kind/name/line — агенту больше не нужен отдельный вызов symbols для ориентирования.
    /// </summary>
    internal static (SymbolBriefDto[]? Outline, int Total, bool Truncated) BuildCodeOutline(EditSession s) {
        const int MaxLines = 5000, Top = 50;
        if (s.TotalLines > MaxLines || !Core.SymbolScan.SymbolDispatcher.IsCode(s.Path)) return (null, 0, false);
        var scan = s.Symbols(docsOnly: false);
        var list = new List<SymbolBriefDto>(64);
        int total = 0;
        void Walk(IEnumerable<Core.SymbolScan.Symbol> symbols) {
            foreach (var sym in symbols) {
                total++;
                if (list.Count < Top) list.Add(new SymbolBriefDto { Line = sym.Line, Kind = sym.Kind, Name = sym.Name });
                Walk(sym.Children);
            }
        }
        Walk(scan.Roots);
        return (list.Count > 0 ? [.. list] : null, total, total > list.Count);
    }

    static string BuildPreview(EditSession s) {
        var sb = new System.Text.StringBuilder();
        int n = s.TotalLines;
        int head = Math.Min(n, 12);
        for (int i = 1; i <= head; i++)
            sb.Append(i).Append('|').Append(TrimLine(s.GetLineText(i))).Append('\n');
        if (n > head + 3) sb.Append($"… [{n - head - 3} lines] …\n");
        for (int i = Math.Max(head + 1, n - 2); i <= n; i++)
            sb.Append(i).Append('|').Append(TrimLine(s.GetLineText(i))).Append('\n');
        return sb.ToString();
    }

    static string TrimLine(string line) => line.Length <= 200 ? line : line[..197] + "…";
}

sealed class ShowTool : ITool {
    public string Name => "show";
    public string Description =>
        "Return numbered lines start..end (1-based, inclusive). Show a range right before editing it. max_chars caps the output (default 20000, max 200000).";
    public string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string","description":"from a previous response; optional if path given"},"path":{"type":"string","description":"open-by-path: skip open entirely"},
          "start":{"type":"integer","description":"first line, 1-based"},
          "end":{"type":"integer","description":"last line, inclusive"},
          "max_chars":{"type":"integer","description":"output budget, default 20000"}
        },"required":["start","end"]}
        """;

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var att = Sessions.Resolve(args.GetNullableString("session_id"), args.GetNullableString("path"));
        var s = att.Doc;
        Sessions.Touch(att);
        int start = args.GetRequiredInt("start");
        int end = args.GetRequiredInt("end");
        int maxChars = Math.Clamp(args.GetInt("max_chars", 20_000), 1_000, 200_000);
        var (text, truncated) = await ToolOps.LockedAsync(s, () => {
            var r = s.Show(start, end, maxChars);
            s.MarkSeen(att.Id, start, Math.Min(end, s.TotalLines), "show"); // #5: показанное = увиденное
            return r;
        });
        string[]? events = Presence.EventsSince(s, att); // компактные события, если отстал по rev
        return ToolOps.Json(new ShowResult {
            Rev = s.Rev, TotalLines = s.TotalLines, Start = start, End = end, Text = text, Truncated = truncated,
            Events = events,
            Unsaved = s.Dirty, // v0.9.3: есть несохранённые правки — save(path) перед git/тестами/bash
        }, RpcJson.Default.ShowResult);
    }
}

sealed class FindTool : ITool {
    public string Name => "find";
    public string Description =>
        "Regex search across session lines after after_line. Returns line numbers + short previews + where (breadcrumb Ns.Class.Member " +
        "if symbols were requested). context=N adds a numbered snippet around each hit (all occurrences view). Feed line numbers into replace_lines/insert_after.";
    public string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string","description":"from a previous response; optional if path given"},"path":{"type":"string","description":"open-by-path: skip open entirely"},
          "regex":{"type":"string","description":".NET regex"},
          "after_line":{"type":"integer","description":"skip lines up to this one (default 0)"},
          "max_hits":{"type":"integer","description":"default 20, max 200"},
          "context":{"type":"integer","description":"lines around each hit for snippets, default 0, max 10"},
          "ignore_case":{"type":"boolean","description":"default false"}
        },"required":["regex"]}
        """;

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var att = Sessions.Resolve(args.GetNullableString("session_id"), args.GetNullableString("path"));
        var s = att.Doc;
        Sessions.Touch(att);
        var rx = ToolOps.CompileRegex(args.GetString("regex"), args.GetBool("ignore_case"));
        int afterLine = args.GetInt("after_line", 0);
        int maxHits = Math.Clamp(args.GetInt("max_hits", 20), 1, 200);
        int context = Math.Clamp(args.GetInt("context", 0), 0, 10);
        var (hits, truncated) = await ToolOps.LockedAsync(s, () => {
            var r = s.Find(rx, afterLine, maxHits, context);
            foreach (var h in r.Hits) // #5: сниппет find = увиденное; без context — сама строка хита
                s.MarkSeen(att.Id, h.Snippet is null ? h.Line : h.SpanStart, h.Snippet is null ? h.Line : h.SpanEnd, "find");
            return r;
        });
        string[]? events = Presence.EventsSince(s, att);
        return ToolOps.Json(new FindResult {
            Rev = s.Rev,
            Hits = hits.Select(h => new HitDto {
                Line = h.Line, Preview = h.Preview, Snippet = h.Snippet, Where = h.Where,
                Span = h.Snippet is null ? null : new SpanDto { Start = h.SpanStart, End = h.SpanEnd },
            }).ToArray(),
            Truncated = truncated,
            Events = events,
        }, RpcJson.Default.FindResult);
    }
}

sealed class ReplaceLinesTool : ITool {
    public string Name => "replace_lines";
    public string Description =>
        "Replace lines start..end (1-based, inclusive) with new_text lines. new_text='' deletes the range; a lone trailing newline is ignored. " +
        "expected_rev (optional: omit = the last rev you observed) must equal the current rev; on mismatch the error carries current_rev. " +
        "retry_on_stale=true (#1): on stale_rev auto-retry ONCE at the fresh rev when it is provably safe — expect_old anchor still matches, or the range was untouched by the intervening edits (line shifts tracked); response gains recovered=true + intervening=[what others did]. Never blind: changed range → structured stale_conflict with the current text of the range. " +
        "expect_old (optional anchor): exact current text of the range — guards against wrong-line math (kind=anchor_mismatch names the line). " +
        "dry_run=true returns the diff without applying. For 2+ edits use edit_batch.";
    public string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string","description":"from a previous response; optional if path given"},"path":{"type":"string","description":"open-by-path: skip open entirely"},
          "start":{"type":"integer"},
          "end":{"type":"integer"},
          "new_text":{"type":"string","description":"replacement lines ('' = delete)"},
          "expect_old":{"type":"string","description":"optional anchor: exact current text of start..end"},
          "expected_rev":{"type":"integer","description":"optional: omit = the last rev you observed (server tracks it)"},
          "retry_on_stale":{"type":"boolean","description":"auto-retry once on stale_rev when provably safe (anchor intact or range untouched); default false"},
          "dry_run":{"type":"boolean","description":"default false"}
        },"required":["start","end","new_text"]}
        """;

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var att = Sessions.Resolve(args.GetNullableString("session_id"), args.GetNullableString("path"));
        var s = att.Doc;
        int start = args.GetRequiredInt("start");
        int end = args.GetRequiredInt("end");
        string newText = args.GetString("new_text");
        int expectedRev = args.GetInt("expected_rev", att.LastSeenRev); // v0.9: omit = последний увиденный rev
        bool dryRun = args.GetBool("dry_run");
        string? expectOld = args.GetNullableString("expect_old");
        bool retryOnStale = args.GetBool("retry_on_stale", true); // v0.8: recovery по умолчанию (opt-out: false)
        // retry живёт ВНУТРИ блокировки: между проверкой и повторным применением rev не двинется
        var outcome = await ToolOps.LockedAsync(s, () => {
            try { return s.ReplaceLines(start, end, newText, expectedRev, dryRun, expectOld, att.Id); }
            catch (ToolException tex) when (tex.Kind == ToolException.StaleRev && retryOnStale) {
                return StaleRecovery.RecoverReplace(s, att.Id, start, end, newText, expectedRev, dryRun, expectOld);
            }
        });
        att.LastSeenRev = outcome.Rev; // v0.9: ответ доставил состояние
        return ToolOps.Json(new MutationResult {
            Rev = outcome.Rev, TotalLines = outcome.TotalLines, Diff = outcome.Diff, DryRun = outcome.DryRun,
            Brackets = outcome.Brackets?.ToArray(),
            Recovered = outcome.Recovered, Intervening = outcome.Intervening?.ToArray(),
            Diagnostics = outcome.Diags?.Select(d => new DiagDto { Line = d.Line, Code = d.Code, Message = d.Message }).ToArray(),
            Unsaved = s.Dirty, // v0.9.3: применено в RAM, НЕ сохранено — не забудь save(path)
            Blind = outcome.Blind, // #5: правка вслепую (только при open(attention:true))
        }, RpcJson.Default.MutationResult);
    }
}

/// <summary>
/// v0.7 (#1) stale-rev auto-recovery: повтор ровно один раз, и только когда это ДОКАЗУЕМО безопасно —
/// якорь expect_old совпадает на свежем реве, либо диапазон не тронут чужими мутациями (сдвиги строк
/// отслежены через спаны истории). Слепых повторов нет: изменившийся диапазон → stale_conflict с текущим
/// текстом («обе версии» без отдельного show) и retry_hint.
/// </summary>
static class StaleRecovery {
    public static MutationOutcome RecoverReplace(EditSession s, string actor, int start, int end, string newText, int expectedRev, bool dryRun, string? expectOld) {
        var intervening = s.MutationsSince(expectedRev); // снимок ДО re-apply: только чужие правки
        if (expectOld is not null && s.AnchorMatches(start, end - start + 1, expectOld)) {
            var oc = s.ReplaceLines(start, end, newText, s.Rev, dryRun, expectOld, actor);
            return oc with { Recovered = true, Intervening = intervening };
        }
        if (expectOld is null && s.RangeSafeSince(start, end, expectedRev, out var rs, out var re)) {
            var oc = s.ReplaceLines(rs, re, newText, s.Rev, dryRun, null, actor);
            return oc with { Recovered = true, Intervening = intervening };
        }
        throw Conflict(s, start, end, expectedRev, expectOld is not null
            ? "expect_old anchor no longer matches the current content"
            : "range was touched by the intervening edits");
    }

    public static BatchOutcome RecoverBatch(EditSession s, string actor, List<BatchOp> ops, int expectedRev, bool dryRun) {
        var intervening = s.MutationsSince(expectedRev); // снимок ДО re-apply
        var shifted = new List<BatchOp>(ops.Count);
        foreach (var op in ops) {
            if (op.ExpectOld is not null && s.AnchorMatches(op.Start, op.End - op.Start + 1, op.ExpectOld)) { shifted.Add(op); continue; }
            if (op.ExpectOld is null && s.RangeSafeSince(op.Start, op.End, expectedRev, out var rs, out var re)) { shifted.Add(op with { Start = rs, End = re }); continue; }
            throw Conflict(s, op.Start, op.End, expectedRev, $"edit [{op.Start}..{op.End}] is no longer provably safe");
        }
        var oc = s.EditBatch(shifted, s.Rev, dryRun, actor);
        return oc with { Recovered = true, Intervening = intervening };
    }

    static ToolException Conflict(EditSession s, int start, int end, int expectedRev, string why) =>
        new("stale_conflict",
            $"range [{start}..{end}] changed since rev {expectedRev} ({why}) — refusing blind apply; current text of the range follows",
            currentRev: s.Rev, totalLines: s.TotalLines,
            currentText: RangePreview(s, start, end),
            retryHint: $"show {Math.Max(1, start - 2)}..{Math.Min(s.TotalLines, end + 2)} at rev {s.Rev}, then re-apply with expected_rev={s.Rev}") {
            RetryStrategy = "reread_range",
            RetryStart = Math.Max(1, start - 2),
            RetryEnd = Math.Min(s.TotalLines, end + 2),
        };

    /// <summary>Нумерованный текущий текст диапазона (≤40 строк) — «обе версии» прямо в ошибке.</summary>
    static string RangePreview(EditSession s, int start, int end) {
        int lo = Math.Max(1, start), hi = Math.Min(s.TotalLines, end);
        if (hi < lo) return "(range is beyond the file)";
        bool cut = hi - lo + 1 > 40;
        if (cut) hi = lo + 39;
        var sb = new System.Text.StringBuilder();
        for (int i = lo; i <= hi; i++) sb.Append(i).Append('|').Append(s.GetLineText(i)).Append('\n');
        if (cut) sb.Append("…\n");
        return sb.ToString();
    }
}

sealed class InsertAfterTool : ITool {
    public string Name => "insert_after";
    public string Description =>
        "Insert text after line N (line=0 inserts at top). Same rev contract and dry_run as replace_lines.";
    public string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string","description":"from a previous response; optional if path given"},"path":{"type":"string","description":"open-by-path: skip open entirely"},
          "line":{"type":"integer","description":"0 = at top"},
          "text":{"type":"string"},
          "expected_rev":{"type":"integer","description":"optional: omit = the last rev you observed (server tracks it)"},
          "dry_run":{"type":"boolean","description":"default false"}
        },"required":["line","text"]}
        """;

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var att = Sessions.Resolve(args.GetNullableString("session_id"), args.GetNullableString("path"));
        var s = att.Doc;
        int line = args.GetRequiredInt("line");
        string text = args.GetString("text");
        int expectedRev = args.GetInt("expected_rev", att.LastSeenRev); // v0.9: omit = последний увиденный rev
        bool dryRun = args.GetBool("dry_run");
        var outcome = await ToolOps.LockedAsync(s, () => s.InsertAfter(line, text, expectedRev, dryRun, att.Id));
        att.LastSeenRev = outcome.Rev; // v0.9: ответ доставил состояние
        return ToolOps.Json(new MutationResult {
            Rev = outcome.Rev, TotalLines = outcome.TotalLines, Diff = outcome.Diff, DryRun = outcome.DryRun,
            Brackets = outcome.Brackets?.ToArray(),
            Unsaved = s.Dirty, // v0.9.3: применено в RAM, НЕ сохранено — не забудь save(path)
            Blind = outcome.Blind, // #5
        }, RpcJson.Default.MutationResult);
    }
}

sealed class SaveTool : ITool {
    public string Name => "save";
    public string Description =>
        "Write edits to disk: atomic temp+rename, .smartedit-bak backup (backup=true), rejects if the file changed on disk since open; force=true DISCARDS the on-disk version (prefer show + re-apply). " +
        "encoding: 'keep' (default — original encoding/BOM preserved) or 'utf8' (no BOM, converts). Session stays open; rev does NOT change (save does not renumber lines). " +
        "gate/v1 (.cs/.js/.ts/.tsx/.py/.sh): BLOCKED with kind syntax_gate if the edit introduced NEW syntax errors vs baseline (old errors never block); fix them or force=true. " +
        "Result carries gate status + new errors (kind error|missing, 1-based row/col; missing names the absent token).";
    public string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string","description":"from a previous response; optional if path given"},"path":{"type":"string","description":"open-by-path: skip open entirely"},
          "backup":{"type":"boolean","description":"default true"},
          "encoding":{"type":"string","enum":["keep","utf8"],"description":"default keep (original encoding)"},
          "force":{"type":"boolean","description":"overwrite despite external changes, default false"}
        },"required":[]}
        """;

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var att = Sessions.Resolve(args.GetNullableString("session_id"), args.GetNullableString("path"));
        var s = att.Doc;
        bool backup = args.GetBool("backup", true);
        string encoding = args.GetString("encoding", "keep");
        bool force = args.GetBool("force");
        var outcome = await ToolOps.LockedAsync(s, () => s.Save(backup, encoding, force, att.Id));
        return ToolOps.Json(new SaveResult {
            BytesWritten = outcome.BytesWritten, BackupPath = outcome.BackupPath, Hash = outcome.Hash, Encoding = outcome.Encoding,
            Gate = outcome.GateStatus,
            GateErrors = (outcome.GateNew ?? []).Select(d => new GateErrorDto {
                Kind = d.Kind, Token = d.Token, Row = d.Row, Col = d.Col, Fragment = d.Fragment,
            }).ToArray(),
        }, RpcJson.Default.SaveResult);
    }
}

sealed class CloseTool : ITool {
    public string Name => "close";
    public string Description => "Close a session. discard=true drops unsaved edits (otherwise it refuses while dirty).";
    public string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string","description":"from a previous response; optional if path given"},"path":{"type":"string","description":"open-by-path: skip open entirely"},
          "discard":{"type":"boolean","description":"default false"}
        },"required":[]}
        """;

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        // v0.9: close по пути тоже (Resolve): гасит path-attachment этого документа
        var att = Sessions.Resolve(args.GetNullableString("session_id"), args.GetNullableString("path"));
        string id = att.Id;
        bool discard = args.GetBool("discard");
        var doc = att.Doc;
        // detach: не последний — просто отключение (документ жив у peers); последний — гашение документа
        var (wasDirty, disposed, shadow) = await ToolOps.LockedAsync(doc, () => Sessions.Detach(id, discard));
        if (disposed) doc.Dispose(); // семафор гасим уже после Release
        return ToolOps.Json(new CloseResult {
            Closed = true, Dirty = wasDirty,
            // dirty+discard последним закрывающим оставил shadow — несохранённое можно вернуть копированием поверх
            Shadow = shadow,
        }, RpcJson.Default.CloseResult);
    }
}

sealed class SessionsTool : HiddenTool {
    public override string Name => "sessions";
    public override string Description =>
        "List open documents and every attachment (diagnostics): path, shared rev, dirty, per-editor id/note/pending edits/stale marker. " +
        "Use when a file feels locked (open sessions hold it FileShare.Read — external writes fail) or before barging into a busy file.";
    public override string InputSchemaJson => """{"type":"object","properties":{},"required":[]}""";

    public override Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
        => Task.FromResult(ToolOps.Json(new SessionsResult {
            Sessions = [.. Sessions.Map.Values.OrderBy(a => a.Doc.Path).ThenBy(a => a.Id).Select(a => new SessionDto {
                SessionId = a.Id, Path = a.Doc.Path, Rev = a.Doc.Rev, Dirty = a.Doc.Dirty,
                TotalLines = a.Doc.TotalLines, Bytes = a.Doc.Bytes,
                Note = a.Note, PendingEdits = a.Doc.PendingEdits.GetValueOrDefault(a.Id), Stale = a.Stale,
            })],
        }, RpcJson.Default.SessionsResult));
}

/// <summary>v0.4: осознанная синхронизация перед большой правкой: peers + события с since_rev.</summary>
sealed class SyncTool : HiddenTool {
    public override string Name => "sync";
    public override string Description =>
        "Explicit collaboration sync: returns current rev, peers (id, note, pending edits, stale) and compact edit events since since_rev " +
        "(default: your last seen). Call before a big edit in a shared file to see who did what. No state change besides your last_seen.";
    public override string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string","description":"from a previous response; optional if path given"},"path":{"type":"string","description":"open-by-path: skip open entirely"},
          "since_rev":{"type":"integer","description":"default: your attachment's last seen rev"}
        },"required":[]}
        """;

    public async override Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var att = Sessions.Resolve(args.GetNullableString("session_id"), args.GetNullableString("path"));
        var s = att.Doc;
        Sessions.Touch(att);
        return await ToolOps.LockedAsync(s, () => {
            if (args.TryGetProperty("since_rev", out var sr) && sr.ValueKind == JsonValueKind.Number && sr.TryGetInt32(out var r))
                att.LastSeenRev = Math.Min(r, s.Rev);
            string[] events = Presence.EventsSince(s, att) ?? [];
            return ToolOps.Json(new SyncResult {
                Rev = s.Rev, TotalLines = s.TotalLines, Events = events,
                Peers = [.. Sessions.Peers(s).Select(p => new PeerDto {
                    SessionId = p.Id, Note = p.Note, Stale = p.Stale,
                    PendingEdits = s.PendingEdits.GetValueOrDefault(p.Id),
                })],
            }, RpcJson.Default.SyncResult);
        });
    }
}
