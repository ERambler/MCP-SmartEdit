using System.Text;
using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Core.SymbolScan;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

// v0.2 мета-утилиты: калькулятор, атрибуты файла, конвертация кодировки.

sealed class CalcTool : ITool {
    public string Name => "calc";
    public string Description =>
        "Exact calculator (LLMs are bad at arithmetic): + - * / % ^ (power, right-assoc), unary minus, postfix ! factorial, " +
        "hex 0x…, binary 0b…, scientific 1e-3, functions sin cos tan asin acos atan atan2 sqrt abs ln log log2 log10 exp " +
        "round floor ceil min max pow, constants pi e tau. Integer results also shown as hex.";
    public string InputSchemaJson => """
        {"type":"object","properties":{"expression":{"type":"string"}},"required":["expression"]}
        """;

    public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        string expr = args.GetString("expression");
        if (expr == "") throw new ArgumentException("expression is required");
        string value = Calc.Evaluate(expr);
        return Task.FromResult(ToolOps.Json(new CalcResult { Expr = expr, Value = value }, RpcJson.Default.CalcResult));
    }
}

sealed class FileInfoTool : ITool {
    public string Name => "file_info";
    public string Description =>
        "File attributes without opening a session: size, encoding (BOM/UTF-8 validation sample), newline style, " +
        "timestamps, read-only/hidden, is-code flag. Cheap — reads only the first 64KB.";
    public string InputSchemaJson => """
        {"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}
        """;

    public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        string path = args.GetString("path");
        if (path == "" || !File.Exists(path)) throw new FileNotFoundException("file not found: " + path);
        path = System.IO.Path.GetFullPath(path);
        var fi = new FileInfo(path);

        string encoding = "unknown"; bool isUtf8 = false; string newlines = "none";
        try {
            var kind = EncodingDetect.Detect(path);
            (encoding, isUtf8) = kind switch {
                FileKind.Utf8 => ("utf-8", true),
                FileKind.Utf8Bom => ("utf-8-bom", true),
                FileKind.Utf16Le => ("utf-16le", false),
                FileKind.Utf16Be => ("utf-16be", false),
                _ => ("not-utf8 (pass encoding to open: cp1251?)", false),
            };
            using var fs = File.OpenRead(path);
            var buf = new byte[Math.Min(fs.Length, 64 * 1024)];
            int n = fs.Read(buf);
            int lf = 0, crlf = 0;
            for (int i = 0; i < n; i++) {
                if (buf[i] == 0x0A) { lf++; if (i > 0 && buf[i - 1] == 0x0D) crlf++; }
            }
            newlines = lf == 0 ? "none" : crlf * 2 >= lf ? "crlf" : "lf";
        }
        catch (IOException) { }

        return Task.FromResult(ToolOps.Json(new FileInfoResult {
            Path = path,
            Bytes = fi.Length,
            Encoding = encoding,
            Newlines = newlines,
            ModifiedUtc = fi.LastWriteTimeUtc,
            CreatedUtc = fi.CreationTimeUtc,
            ReadOnly = fi.IsReadOnly,
            Hidden = (fi.Attributes & FileAttributes.Hidden) != 0,
            IsCode = SymbolDispatcher.IsCode(path),
            IsUtf8 = isUtf8,
        }, RpcJson.Default.FileInfoResult));
    }
}

sealed class ConvertTool : ITool {
    public string Name => "convert";
    public string Description =>
        "Convert file encoding in place (atomic, .smartedit-bak backup kept). " +
        "from: autodetect (BOM/UTF-8; non-UTF-8 needs explicit 'cp1251' etc). to: utf-8 | utf-8-bom | utf-16le | utf-16be | cp1251. " +
        "utf-8 output has no BOM unless utf-8-bom.";
    public string InputSchemaJson => """
        {"type":"object","properties":{
          "path":{"type":"string"},
          "to":{"type":"string","enum":["utf-8","utf-8-bom","utf-16le","utf-16be","cp1251"]},
          "from":{"type":"string","description":"source encoding when autodetect can't (cp1251|utf-16le|utf-16be)"}
        },"required":["path","to"]}
        """;

    public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        string path = args.GetString("path");
        if (path == "" || !File.Exists(path)) throw new FileNotFoundException("file not found: " + path);
        path = System.IO.Path.GetFullPath(path);
        string to = EncodingDetect.CanonicalName(args.GetString("to"));
        string? fromArg = args.GetNullableString("from");

        var kind = EncodingDetect.Detect(path);
        string from = fromArg is null
            ? kind switch {
                FileKind.Utf8 => "utf-8",
                FileKind.Utf8Bom => "utf-8-bom",
                FileKind.Utf16Le => "utf-16le",
                FileKind.Utf16Be => "utf-16be",
                _ => throw new IOException("file is not valid UTF-8 and has no BOM — pass from:'cp1251' (or other) explicitly"),
            }
            : EncodingDetect.CanonicalName(fromArg);
        if (from == to) return Task.FromResult(ToolOps.Json(
            new ConvertResult { Path = path, From = from, To = to, Bytes = new FileInfo(path).Length }, RpcJson.Default.ConvertResult));

        var (utf8, canonicalFrom) = EncodingDetect.LoadAsUtf8(path, from);
        var enc = EncodingDetect.OutputEncoding(to);
        byte[] outBytes = enc.GetPreamble().Concat(enc.GetBytes(Encoding.UTF8.GetString(utf8))).ToArray();

        string temp = path + ".smartedit-tmp";
        File.WriteAllBytes(temp, outBytes);
        try { File.Copy(path, path + ".smartedit-bak", overwrite: true); } catch (IOException) { }
        File.Move(temp, path, overwrite: true);
        return Task.FromResult(ToolOps.Json(
            new ConvertResult { Path = path, From = canonicalFrom, To = to, Bytes = outBytes.LongLength }, RpcJson.Default.ConvertResult));
    }
}

/// <summary>
/// v0.5: экстренная ручка — общий журнал сервера (все чаты/проекты/процессы): хвост, фильтры, grep.
/// Агент сам себе «прокручивает» прошлое: что делал, что упало и с каким kind.
/// </summary>
sealed class LogTool : ITool {
    public string Name => "log";
    public string Description =>
        "Emergency handle: the SHARED server log across all chats/projects/processes (every tool call, error+kind, per-pid). " +
        "tail=N (default 50, max 500) returns the LAST matching lines; level=INFO|WARN|ERROR filters; grep=substring filter; " +
        "errors_only=true → [ERROR] only. Rotates at 5MB (mcp.log.1/.2 kept). Use to retrace your own past actions or diagnose a weird session.";
    public string InputSchemaJson => """
        {"type":"object","properties":{
          "tail":{"type":"integer","description":"last N matching lines, default 50, max 500"},
          "level":{"type":"string","enum":["INFO","WARN","ERROR"],"description":"filter by level"},
          "grep":{"type":"string","description":"substring filter"},
          "errors_only":{"type":"boolean","description":"shortcut for level=ERROR"}
        },"required":[]}
        """;

    public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        int tail = Math.Clamp(args.GetInt("tail", 50), 1, 500);
        string? level = args.GetNullableString("level")?.ToUpperInvariant();
        if (level is not ("INFO" or "WARN" or "ERROR" or null)) level = null;
        string? grep = args.GetNullableString("grep");
        bool errorsOnly = args.GetBool("errors_only");
        if (errorsOnly) level = "ERROR";

        bool Match(string line) =>
            (level is null || line.Contains($"[{level}]"))
            && (grep is null || line.Contains(grep, StringComparison.OrdinalIgnoreCase));

        // хронология: архивы старее живого журнала; держим только хвост подходящих (RAM O(tail))
        var keep = new Queue<string>();
        long scanned = 0;
        for (int a = SmartEdit.Mcp.Infra.Log.KeepArchives; a >= 0; a--) {
            string path = SmartEdit.Mcp.Infra.Log.ArchivePath(a);
            if (!File.Exists(path)) continue;
            try {
                foreach (var line in File.ReadLines(path)) {
                    scanned++;
                    if (!Match(line)) continue;
                    keep.Enqueue(line);
                    if (keep.Count > tail) keep.Dequeue();
                }
            }
            catch (IOException) { }
        }
        return Task.FromResult(ToolOps.Json(new LogResult {
            Path = SmartEdit.Mcp.Infra.Log.ArchivePath(0),
            LinesScanned = scanned,
            Lines = [.. keep],
        }, RpcJson.Default.LogResult));
    }
}

/// <summary>
/// v0.8.1: самодокументируемый API — одна страница: карта инструментов, семантика диапазонов,
/// rev-протокол, error kinds → действия. Консилиум: «самый дешёвый токен-выигрыш — один вызов
/// вместо 5 раундов тыка». Плюс приглашение на первый tools/call в сессии (см. McpProtocolHandler).
/// </summary>
sealed class HelpTool : ITool {
    public string Name => "help";
    public string Description =>
        "ONE page you can trust: tool map (one line each), [start..end] semantics (1-based inclusive, end=start-1 inserts, [1..0] = empty file), " +
        "the rev protocol, and every error kind with the exact next action. Call it once at session start instead of reverse-engineering the API.";
    public string InputSchemaJson => """{"type":"object","properties":{},"required":[]}""";

    public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        const string page =
"""
SMARTEDIT — GOLDEN RULES
- lines are 1-based, INCLUSIVE; end = start-1 => INSERT before start; empty file: replace_lines(1, 0, text)
- path-first (v0.9): EVERY tool accepts path instead of session_id — open is optional, no ritual; both given => ambiguous_target
- expected_rev is OPTIONAL (omit = the last rev you observed — the server tracks it); pin it to assert no peer raced you
- expect_old = exact current text of the range — your guard against your own wrong-line math
- stale rev auto-recovers by default (response: recovered:true + intervening) — usually you do nothing
- v0.9.3: every edit/show response on a DIRTY document carries unsaved:true — SAVE before running external tools (git/tests/bash see the OLD disk code)
- errors are JSON {kind,...} — branch on kind, never parse prose; machine retry: {strategy: reissue_with_rev|reanchor|reread_range, start?, end?}

TOOL MAP
navigate : open(path, note?) → sessionId, rev, code_outline(≤5k-line code, +total/truncated), peers | show(start,end,max_chars?)
          find(regex, context:N) → lines+snippets+breadcrumbs+span{start,end} (edit the span, no follow-up show) | symbols / goto / tree | file_info
edit     : replace_lines(start,end,new_text,expected_rev,expect_old?) | insert_after(line,text,expected_rev)
          edit_batch(edits≤200, atomic, bottom-up = no shift math) | replace_all(literal, refuse-if-too-many)
          edit_transaction(steps[{path|session_id, edits}], save?) — MULTI-FILE all-or-nothing: every range/anchor
          validated on all files BEFORE anything applies; one bad step aborts everything (kind=transaction_aborted)
          replace_content(find, replace) — EXACT text, no line numbers; must be unique (0 → not_found, >1 → ambiguous_match)
          undo(n, expected_rev) — undo×2 = redo, peer-safe compensating mutations
lifecycle: save(keeps encoding; force=true publishes peers' edits too) | close(discard?) | sessions | sync
ops      : check(bracket audit) | log(tail/grep/errors_only — shared journal) | calc(exact math) | convert(encoding)

ERROR KINDS → ACTION
stale_rev        → already auto-retried (see recovered/intervening); if you passed retry_on_stale:false → re-issue with current_rev
stale_conflict   → the range changed under you: show(range from retry_hint), then re-apply with the fresh rev
anchor_mismatch  → your line math is off — the error names the line, found vs expected; fix coords, don't force
invalid_range / batch_overlap → fix coordinates; ranges in one batch must be disjoint
unknown_session  → session gone (GC after 30 min idle?): open again
peers_dirty      → someone mid-flight: wait, or save(force=true) publishes THEIR edits too
cross_process_lock→ ANOTHER SmartEdit process owns this file (two servers overwrite each other's saves): close it (pids in log) and retry
disk_conflict    → file changed on disk since open: SHOW the range and RE-APPLY on top; force=true DISCARDS the on-disk version
not_found / ambiguous_match (replace_content) → copy the snippet exactly from a fresh show/find / extend it to be unique
too_large        → over a cap: split the work
diagnostics (in every edit response): [{line, code, message}] — bracket_mismatch, never_closed, stray_closer,
unterminated_literal/comment/hole; unterminated_literal anchors the OPENING line. [] = clean.

QUICKSTART
open(path) → find(regex, context:2) → edit_batch(edits, expected_rev, expect_old anchors) → save → close
single file, zero ceremony (v0.9): show(path, 1..N) → replace_lines(path, ...) → save(path) — open never needed
(Disk philosophy: NOTHING touches the disk except save — edit freely, undo if wrong, save when happy.
 Locks: a DIRTY session holds its file (protection); a CLEAN session never does — between tool calls the handle is released, so git/scripts/formatters can write freely. External change while released → next call transparently starts fresh (rev=0); file deleted → disk_conflict with retry_hint.)
""";
        return Task.FromResult(page);
    }
}
