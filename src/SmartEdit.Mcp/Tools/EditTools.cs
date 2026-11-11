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
        tools.Register(new GotoTool()); // v0.5: 20 инструментов
    }
}

/// <summary>Помощники инструментов: сериализация, лок сессии, компиляция regex.</summary>
static class ToolOps {
    internal static string Json<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Serialize(value, typeInfo);

    /// <summary>Все операции сессии — под её семафором.</summary>
    internal static async Task<T> LockedAsync<T>(EditSession s, Func<T> work) {
        await s.Gate.WaitAsync();
        try { return work(); }
        finally { s.Gate.Release(); }
    }

    internal static async Task Locked(EditSession s, Action work) {
        await s.Gate.WaitAsync();
        try { work(); }
        finally { s.Gate.Release(); }
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

sealed class OpenTool : ITool {
    public string Name => "open";
    public string Description =>
        "Open a text file as an edit session. Any size for UTF-8 (memory-mapped); non-UTF-8 needs encoding param (cp1251|utf-16le|utf-16be, up to 256MB). " +
        "note = why you're here (shown to other editors). Re-opening the SAME path reuses the shared document: one rev space for everyone — " +
        "the response lists peers (who's here, notes) and recent edits. " +
        "Returns session_id, rev, total_lines, encoding, crlf, outline, todo_count, folded preview.";
    public string InputSchemaJson => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"absolute file path"},
          "encoding":{"type":"string","description":"force: cp1251 | utf-16le | utf-16be (non-UTF-8 files); default: autodetect"},
          "note":{"type":"string","description":"why you're editing (presence annotation for peers)"}
        },"required":["path"]}
        """;

    public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        string path = args.GetString("path");
        if (path == "") throw new ArgumentException("path is required");
        string? note = args.GetNullableString("note");
        var (att, reused) = Sessions.Attach(path, args.GetNullableString("encoding"), note);
        var s = att.Doc;
        var peers = Sessions.Peers(s);
        return Task.FromResult(ToolOps.Json(new OpenResult {
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
            Preview = BuildPreview(s),
            Reused = reused,
            Peers = [.. peers.Select(p => new PeerDto {
                SessionId = p.Id, Note = p.Note, Stale = p.Stale,
                PendingEdits = s.PendingEdits.GetValueOrDefault(p.Id),
            })],
            Recent = [.. s.History.TakeLast(10).Select(h => $"r{h.Rev} {h.Actor}: {h.Desc}")],
        }, RpcJson.Default.OpenResult));
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
          "session_id":{"type":"string"},
          "start":{"type":"integer","description":"first line, 1-based"},
          "end":{"type":"integer","description":"last line, inclusive"},
          "max_chars":{"type":"integer","description":"output budget, default 20000"}
        },"required":["session_id","start","end"]}
        """;

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var att = Sessions.Get(args.GetString("session_id"));
        var s = att.Doc;
        Sessions.Touch(att);
        int start = args.GetRequiredInt("start");
        int end = args.GetRequiredInt("end");
        int maxChars = Math.Clamp(args.GetInt("max_chars", 20_000), 1_000, 200_000);
        var (text, truncated) = await ToolOps.LockedAsync(s, () => s.Show(start, end, maxChars));
        string[]? events = Presence.EventsSince(s, att); // компактные события, если отстал по rev
        return ToolOps.Json(new ShowResult {
            Rev = s.Rev, TotalLines = s.TotalLines, Start = start, End = end, Text = text, Truncated = truncated,
            Events = events,
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
          "session_id":{"type":"string"},
          "regex":{"type":"string","description":".NET regex"},
          "after_line":{"type":"integer","description":"skip lines up to this one (default 0)"},
          "max_hits":{"type":"integer","description":"default 20, max 200"},
          "context":{"type":"integer","description":"lines around each hit for snippets, default 0, max 10"},
          "ignore_case":{"type":"boolean","description":"default false"}
        },"required":["session_id","regex"]}
        """;

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var att = Sessions.Get(args.GetString("session_id"));
        var s = att.Doc;
        Sessions.Touch(att);
        var rx = ToolOps.CompileRegex(args.GetString("regex"), args.GetBool("ignore_case"));
        int afterLine = args.GetInt("after_line", 0);
        int maxHits = Math.Clamp(args.GetInt("max_hits", 20), 1, 200);
        int context = Math.Clamp(args.GetInt("context", 0), 0, 10);
        var (hits, truncated) = await ToolOps.LockedAsync(s, () => s.Find(rx, afterLine, maxHits, context));
        string[]? events = Presence.EventsSince(s, att);
        return ToolOps.Json(new FindResult {
            Rev = s.Rev,
            Hits = hits.Select(h => new HitDto { Line = h.Line, Preview = h.Preview, Snippet = h.Snippet, Where = h.Where }).ToArray(),
            Truncated = truncated,
            Events = events,
        }, RpcJson.Default.FindResult);
    }
}

sealed class ReplaceLinesTool : ITool {
    public string Name => "replace_lines";
    public string Description =>
        "Replace lines start..end (1-based, inclusive) with new_text lines. new_text='' deletes the range; a lone trailing newline is ignored. " +
        "expected_rev must equal the current rev; on mismatch the error carries current_rev. " +
        "expect_old (optional anchor): exact current text of the range — guards against wrong-line math (kind=anchor_mismatch names the line). " +
        "dry_run=true returns the diff without applying. For 2+ edits use edit_batch.";
    public string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string"},
          "start":{"type":"integer"},
          "end":{"type":"integer"},
          "new_text":{"type":"string","description":"replacement lines ('' = delete)"},
          "expect_old":{"type":"string","description":"optional anchor: exact current text of start..end"},
          "expected_rev":{"type":"integer"},
          "dry_run":{"type":"boolean","description":"default false"}
        },"required":["session_id","start","end","new_text","expected_rev"]}
        """;

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var att = Sessions.Get(args.GetString("session_id"));
        var s = att.Doc;
        int start = args.GetRequiredInt("start");
        int end = args.GetRequiredInt("end");
        string newText = args.GetString("new_text");
        int expectedRev = args.GetRequiredInt("expected_rev");
        bool dryRun = args.GetBool("dry_run");
        var outcome = await ToolOps.LockedAsync(s, () => s.ReplaceLines(start, end, newText, expectedRev, dryRun, args.GetNullableString("expect_old"), att.Id));
        return ToolOps.Json(new MutationResult {
            Rev = outcome.Rev, TotalLines = outcome.TotalLines, Diff = outcome.Diff, DryRun = outcome.DryRun,
            Brackets = outcome.Brackets?.ToArray(),
        }, RpcJson.Default.MutationResult);
    }
}

sealed class InsertAfterTool : ITool {
    public string Name => "insert_after";
    public string Description =>
        "Insert text after line N (line=0 inserts at top). Same rev contract and dry_run as replace_lines.";
    public string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string"},
          "line":{"type":"integer","description":"0 = at top"},
          "text":{"type":"string"},
          "expected_rev":{"type":"integer"},
          "dry_run":{"type":"boolean","description":"default false"}
        },"required":["session_id","line","text","expected_rev"]}
        """;

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var att = Sessions.Get(args.GetString("session_id"));
        var s = att.Doc;
        int line = args.GetRequiredInt("line");
        string text = args.GetString("text");
        int expectedRev = args.GetRequiredInt("expected_rev");
        bool dryRun = args.GetBool("dry_run");
        var outcome = await ToolOps.LockedAsync(s, () => s.InsertAfter(line, text, expectedRev, dryRun, att.Id));
        return ToolOps.Json(new MutationResult {
            Rev = outcome.Rev, TotalLines = outcome.TotalLines, Diff = outcome.Diff, DryRun = outcome.DryRun,
            Brackets = outcome.Brackets?.ToArray(),
        }, RpcJson.Default.MutationResult);
    }
}

sealed class SaveTool : ITool {
    public string Name => "save";
    public string Description =>
        "Write edits to disk: atomic temp+rename, .smartedit-bak backup (backup=true), rejects if the file changed on disk since open unless force=true. " +
        "encoding: 'keep' (default — original encoding/BOM preserved) or 'utf8' (no BOM, converts). Session stays open; rev does NOT change (save does not renumber lines).";
    public string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string"},
          "backup":{"type":"boolean","description":"default true"},
          "encoding":{"type":"string","enum":["keep","utf8"],"description":"default keep (original encoding)"},
          "force":{"type":"boolean","description":"overwrite despite external changes, default false"}
        },"required":["session_id"]}
        """;

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var att = Sessions.Get(args.GetString("session_id"));
        var s = att.Doc;
        bool backup = args.GetBool("backup", true);
        string encoding = args.GetString("encoding", "keep");
        bool force = args.GetBool("force");
        var outcome = await ToolOps.LockedAsync(s, () => s.Save(backup, encoding, force, att.Id));
        return ToolOps.Json(new SaveResult {
            BytesWritten = outcome.BytesWritten, BackupPath = outcome.BackupPath, Hash = outcome.Hash, Encoding = outcome.Encoding,
        }, RpcJson.Default.SaveResult);
    }
}

sealed class CloseTool : ITool {
    public string Name => "close";
    public string Description => "Close a session. discard=true drops unsaved edits (otherwise it refuses while dirty).";
    public string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string"},
          "discard":{"type":"boolean","description":"default false"}
        },"required":["session_id"]}
        """;

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        string id = args.GetString("session_id");
        bool discard = args.GetBool("discard");
        var doc = Sessions.Get(id).Doc;
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

sealed class SessionsTool : ITool {
    public string Name => "sessions";
    public string Description =>
        "List open documents and every attachment (diagnostics): path, shared rev, dirty, per-editor id/note/pending edits/stale marker. " +
        "Use when a file feels locked (open sessions hold it FileShare.Read — external writes fail) or before barging into a busy file.";
    public string InputSchemaJson => """{"type":"object","properties":{},"required":[]}""";

    public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
        => Task.FromResult(ToolOps.Json(new SessionsResult {
            Sessions = [.. Sessions.Map.Values.OrderBy(a => a.Doc.Path).ThenBy(a => a.Id).Select(a => new SessionDto {
                SessionId = a.Id, Path = a.Doc.Path, Rev = a.Doc.Rev, Dirty = a.Doc.Dirty,
                TotalLines = a.Doc.TotalLines, Bytes = a.Doc.Bytes,
                Note = a.Note, PendingEdits = a.Doc.PendingEdits.GetValueOrDefault(a.Id), Stale = a.Stale,
            })],
        }, RpcJson.Default.SessionsResult));
}

/// <summary>v0.4: осознанная синхронизация перед большой правкой: peers + события с since_rev.</summary>
sealed class SyncTool : ITool {
    public string Name => "sync";
    public string Description =>
        "Explicit collaboration sync: returns current rev, peers (id, note, pending edits, stale) and compact edit events since since_rev " +
        "(default: your last seen). Call before a big edit in a shared file to see who did what. No state change besides your last_seen.";
    public string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string"},
          "since_rev":{"type":"integer","description":"default: your attachment's last seen rev"}
        },"required":["session_id"]}
        """;

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var att = Sessions.Get(args.GetString("session_id"));
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
