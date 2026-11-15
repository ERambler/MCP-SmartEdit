using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

sealed class OpenTool : ToolBase {
    public override string Name => "open";
    public override string Description =>
        "Open a text file as an edit session. Any size for UTF-8 (memory-mapped); non-UTF-8 needs encoding param (cp1251|utf-16le|utf-16be, up to 256MB). " +
        "note = why you're here (shown to other editors). Re-opening the SAME path reuses the shared document: one rev space for everyone — " +
        "the response lists peers (who's here, notes) and recent edits. " +
        "Returns session_id, rev, total_lines, encoding, crlf, outline (sections), code_outline (top-50 symbols for code files ≤5k lines), todo_count, folded preview.";
    public override string InputSchemaJson => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"absolute file path"},
          "encoding":{"type":"string","description":"force: cp1251 | utf-16le | utf-16be (non-UTF-8 files); default: autodetect"},
          "note":{"type":"string","description":"why you're editing (presence annotation for peers)"},
          "attention":{"type":"boolean","description":"#5 opt-in: track seen ranges (show/find/goto) — edits to unseen lines get a blind warning"}
        },"required":["path"]}
        """;

    public override async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        string path = args.GetString("path");
        if (path == "") throw new ArgumentException("path is required");
        string? note = args.GetNullableString("note");
        var (att, reused) = Sessions.Attach(path, args.GetNullableString("encoding"), note);
        var s = att.Doc;
        if (args.GetBool("attention")) s.Attention = true; // #5: свойство документа; off по умолчанию
        var peers = Sessions.Peers(s);
        // v0.6.2 (#2): outline в open-ответе; preview читает строки — потому ВСЁ под одним замком (#14)
        var (outlineSyms, outlineTotal, outlineTruncated, preview) = await LockedAsync(s, () => {
            var o = BuildCodeOutline(s);
            return (o.Outline, o.Total, o.Truncated, BuildPreview(s));
        });
        return Json(new OpenResult {
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

public sealed class OpenResult {
    public string SessionId { get; init; } = "";
    public string Path { get; init; } = "";
    public int Rev { get; init; }
    public int TotalLines { get; init; }
    public long Bytes { get; init; }
    public string Encoding { get; init; } = "";
    public bool Crlf { get; init; }
    public int TodoCount { get; init; }
    public string[] Usings { get; init; } = [];
    public OutlineDto[] Outline { get; init; } = [];
    public SymbolBriefDto[]? CodeOutline { get; init; } // v0.6.2 (#2): топ-символы кода для файлов ≤5k строк (null = не код/слишком большой)
    public int CodeOutlineTotal { get; init; }      // v0.8.1 (#2): сколько символов ВСЕГО
    public bool CodeOutlineTruncated { get; init; } // v0.8.1 (#2): true = топ-50, НЕ весь файл
    public string Preview { get; init; } = "";
    public bool Reused { get; init; }            // v0.4: путь уже был открыт — подключение к общему документу
    public PeerDto[] Peers { get; init; } = [];  // кто сейчас в файле (id/note/несохранённые/stale)
    public string[] Recent { get; init; } = [];  // последние мутации общего документа
}

/// <summary>Компактная строка code-outline в open-ответе: kind/name/line без сигнатур и детей.</summary>
public sealed class SymbolBriefDto {
    public int Line { get; init; }
    public string Kind { get; init; } = "";
    public string Name { get; init; } = "";
}

public sealed class OutlineDto {
    public int Line { get; init; }
    public int Level { get; init; }
    public string Title { get; init; } = "";
}
