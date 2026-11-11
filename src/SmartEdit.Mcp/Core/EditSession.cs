using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;
using SmartEdit.Mcp.Core.SymbolScan;

namespace SmartEdit.Mcp.Core;

public sealed record FindHit(int Line, string Preview, string? Snippet, string? Where);
public sealed record MutationOutcome(int Rev, int TotalLines, string Diff, bool DryRun, List<string>? Brackets);
public sealed record SaveOutcome(long BytesWritten, string? BackupPath, string Hash, string Encoding);
public sealed record BatchOp(int Start, int End, string NewText, string? ExpectOld);
public sealed record BatchOutcome(int Rev, int TotalLines, int Applied, List<string> Diffs, bool DryRun, List<string>? Brackets);
public sealed record ReplaceAllOutcome(int Rev, int TotalLines, int Occurrences, List<int> Lines, bool DryRun, List<string>? Brackets);

/// <summary>Компенсирующая операция для undo: строки [Old ↔ New] на позиции Start (позиция — hint: undo ищет по содержимому).</summary>
public sealed record InverseOp(int Start, List<string> Old, List<string> New);

/// <summary>Запись истории мутаций общего документа (кто/что/когда; Ops — инверсии для undo, null = правка не отменяема).</summary>
public sealed record HistoryEntry(int Rev, string Actor, string Desc, DateTime Ts, List<InverseOp>? Ops = null);

/// <summary>
/// Сессия редактирования одного файла: неизменяемая база (mmap или RAM) + piece table правок.
/// Все методы вызываются под Gate (SemaphoreSlim) со стороны инструментов — сессии между
/// собой полностью параллельны. Правки не требуют воспроизводить старый текст: только
/// номера строк и expected_rev (защита от дрейфа после перенумерации).
/// </summary>
public sealed class EditSession : IDisposable {
    public const long ShadowLimit = 32L << 20; // shadow-снапшот после каждой мутации — до 32 МБ

    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public string SessionId { get; } = "s" + Guid.NewGuid().ToString("N")[..8];
    public string Path { get; }
    public bool Crlf { get; private set; }
    public string DiskEncoding { get; private set; } = "utf-8"; // фактическая кодировка на диске
    public int Rev { get; private set; }
    public bool Dirty { get; private set; }
    public int TotalLines => _pieces.LineCount;
    public long BaseLength { get; private set; }
    public long Bytes { get; private set; }
    public OutlineEntry[] Outline { get; private set; } = [];
    public int TodoCount { get; private set; }
    public List<string> Usings { get; private set; } = [];
    // ── v0.4: коллаборация — один документ на путь, N подключений ───────────
    public List<HistoryEntry> History { get; } = new(capacity: 64); // последние ≤50 мутаций
    public HistoryEntry? LastMutation { get; private set; }
    /// <summary>actor → число правок с прошлого save (для peers_dirty-гарда и «своих» несохранённых).</summary>
    public Dictionary<string, int> PendingEdits { get; } = new();
    public void RecordMutation(string? actor, string desc, List<InverseOp>? ops = null) {
        var e = new HistoryEntry(Rev, actor ?? "?", desc, DateTime.Now, ops);
        LastMutation = e;
        History.Add(e);
        if (History.Count > 50) History.RemoveRange(0, History.Count - 50);
        if (actor is not null) PendingEdits[actor] = PendingEdits.GetValueOrDefault(actor) + 1;
    }

    public void ClearPendingEdits() => PendingEdits.Clear();
    internal SemaphoreSlim Gate { get; } = new(1, 1);

    // symbols-кэш: пересчёт только если rev изменился (правки двигают строки)
    SymbolScanResult? _symbols;
    int _symbolsRev = -1;
    List<Symbol>? _symbolsFlat;
    Dictionary<Symbol, Symbol?>? _symbolsParents;

    ByteSource _src = null!;
    long[] _baseStarts = [];
    bool _endsWithNewline;
    PieceTable _pieces = null!;
    List<InsertBuffer> _inserts = [];
    DateTime _diskMtimeUtc;
    long _diskSize;
    bool _shadowKept; // discard-close грязной сессии: shadow не удалять (страховка несохранённых правок)

    EditSession(string path) { Path = path; }

    // ── открытие / перезагрузка ──────────────────────────────────────────────

    public static EditSession Open(string path, string? encodingOverride) {
        if (!File.Exists(path)) throw new FileNotFoundException("file not found: " + path);
        var s = new EditSession(System.IO.Path.GetFullPath(path)); // Path — ещё и свойство класса
        s.Init(encodingOverride);
        return s;
    }

    void Init(string? encodingOverride) {
        string canonical = encodingOverride is null ? "" : EncodingDetect.CanonicalName(encodingOverride);
        var kind = EncodingDetect.Detect(Path);

        long bomLen;
        if (canonical is "" or "utf-8" or "utf-8-bom") {
            switch (kind) {
                case FileKind.Utf8:
                case FileKind.Utf8Bom:
                    _src = new MmapSource(Path);
                    bomLen = kind == FileKind.Utf8Bom ? 3 : 0;
                    DiskEncoding = kind == FileKind.Utf8Bom ? "utf-8-bom" : "utf-8";
                    break;
                case FileKind.Utf16Le:
                case FileKind.Utf16Be:
                    throw new IOException("file has a UTF-16 BOM — reopen with encoding:'utf-16le' (or 'utf-16be')");
                default:
                    throw new IOException("file is not valid UTF-8 — reopen with encoding:'cp1251' if this is an ANSI file");
            }
        }
        else {
            var (utf8, canon) = EncodingDetect.LoadAsUtf8(Path, canonical);
            _src = new MemorySource(utf8);
            bomLen = 0;
            DiskEncoding = canon;
        }

        BaseLength = _src.Length;
        Bytes = BaseLength;
        (_baseStarts, Crlf) = LineScanner.Scan(_src, bomLen);
        _endsWithNewline = BaseLength > bomLen && _src.ByteAt(BaseLength - 1) == 0x0A;
        _pieces = new PieceTable(_baseStarts.Length);
        _inserts = [];
        (Outline, TodoCount) = OutlineScanner.Scan(_src, _baseStarts, BaseLength);
        Usings = ScanUsings();
        _symbols = null;
        _symbolsRev = -1;
        var fi = new FileInfo(Path);
        _diskMtimeUtc = fi.LastWriteTimeUtc;
        _diskSize = fi.Length;
    }

    /// <summary>Лёгкий using/import/#include-скан первых 300 строк (полные symbols — по требованию).</summary>
    List<string> ScanUsings() {
        if (!SymbolDispatcher.IsCode(Path) || TotalLines == 0) return [];
        var usings = new List<string>();
        int n = Math.Min(TotalLines, 300);
        for (int i = 1; i <= n; i++) {
            string t = GetLineText(i).TrimStart();
            if (t.StartsWith("using ") && t.Contains(';')) usings.Add(t.TrimEnd(';').Trim()[6..].Trim());
            else if (t.StartsWith("import ") || t.StartsWith("from ")) usings.Add(t);
            else if (t.StartsWith("#include")) usings.Add(t);
            else if (usings.Count > 0 && !t.StartsWith("//") && t.Length > 0) break; // шапка импортов кончилась
        }
        return usings.Take(100).ToList();
    }

    /// <summary>Структура кода (по расширению). docs_only — только задокументированные узлы.</summary>
    public SymbolScanResult Symbols(bool docsOnly) {
        if (TotalLines > 500_000)
            throw new ToolException(ToolException.TooLarge,
                $"symbols: {TotalLines} lines is above the 500k limit (use find/show for files this big)");
        if (_symbols is null || _symbolsRev != Rev) {
            var lines = new List<string>(TotalLines);
            for (int i = 1; i <= TotalLines; i++) lines.Add(GetLineText(i));
            _symbols = SymbolDispatcher.Scan(Path, lines);
            var flat = new List<Symbol>();
            var parents = new Dictionary<Symbol, Symbol?>();
            Symbol.Flatten(_symbols.Roots, flat, parents);
            _symbolsFlat = flat;
            _symbolsParents = parents;
            _symbolsRev = Rev;
        }
        if (!docsOnly) return _symbols;
        return new SymbolScanResult(FilterDocumented(_symbols.Roots), _symbols.Language, _symbols.Usings, _symbols.Warnings);
    }

    /// <summary>Enclosing-путь для строки (breadcrumb). Требует прогретого symbols-кэша.</summary>
    public string? WhereAmI(int line) {
        if (_symbolsFlat is null || _symbolsParents is null) return null;
        return Symbol.EnclosingPath(_symbolsFlat, _symbolsParents, line);
    }

    static List<Symbol> FilterDocumented(List<Symbol> roots) {
        var result = new List<Symbol>();
        foreach (var s in roots) {
            var kids = FilterDocumented(s.Children);
            if (s.Doc is not null || kids.Count > 0)
                result.Add(s with { Children = kids });
        }
        return result;
    }

    /// <summary>Скобочный баланс диапазона (1-based, включительно; 0 0 = весь файл).</summary>
    public List<string> CheckBrackets(int fromLine, int toLine) {
        if (!SymbolDispatcher.IsCode(Path) && SymbolDispatcher.KindOf(Path) != SymbolLang.Markup)
            return [];
        if (fromLine <= 0 || toLine <= 0) { fromLine = 1; toLine = TotalLines; }
        int a = Math.Max(1, fromLine), b = Math.Min(toLine, TotalLines);
        return BracketCheck.Check(i => GetLineText(i), Path, a, b, wholeFile: a == 1 && b == TotalLines); // потоково: без материализации строк
    }

    List<string>? BracketReport(int fromLine, int toLine) {
        if (!SymbolDispatcher.IsCode(Path)) return null; // markdown/текст не семафорим
        // незакрытая скобка может закрываться строками ниже диапазона: для типичных
        if (TotalLines <= 200_000) return CheckBrackets(0, 0); // полный чек: стриминг, RAM не растёт
        return CheckBrackets(fromLine, toLine);
    }

    /// <summary>Перезагрузка с диска после save: база = только что записанный файл.</summary>
    void Reload(string canonical) {
        _src.Dispose();
        Init(canonical == "utf-8" ? null : canonical);
    }

    // ── чтение ───────────────────────────────────────────────────────────────

    (long Start, int Len) BaseSpan(int i) {
        long start = _baseStarts[i];
        long next = i + 1 < _baseStarts.Length ? _baseStarts[i + 1] : BaseLength;
        long end = next;
        if (next > start && _src.ByteAt(next - 1) == 0x0A) {
            end = next - 1;
            if (end > start && _src.ByteAt(end - 1) == 0x0D) end--;
        }
        return (start, (int)(end - start));
    }

    string BaseLineText(int i) {
        var (start, len) = BaseSpan(i);
        if (len == 0) return "";
        byte[] buf = ArrayPool<byte>.Shared.Rent(len);
        try {
            _src.Read(start, buf.AsSpan(0, len));
            try { return StrictUtf8.GetString(buf, 0, len); }
            catch (DecoderFallbackException) {
                throw new IOException($"line {i + 1} is not valid UTF-8 — reopen with encoding:'cp1251' if this is an ANSI file");
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    public string GetLineText(int logicalLine) {
        var (p, inPiece) = _pieces.MapLine(logicalLine - 1);
        if (p.BufferIndex < 0) return BaseLineText(p.FirstLine + inPiece);
        var buf = _inserts[p.BufferIndex];
        var (s, l) = InsertBuffers.SpanOf(buf, inPiece, Crlf ? 2 : 1);
        return Encoding.UTF8.GetString(buf.Data, s, l);
    }

    public (string Text, bool Truncated) Show(int start, int end, int maxChars) {
        if (start < 1) throw new ArgumentException($"start must be >= 1 (got {start})");
        if (end > TotalLines) throw new ArgumentException($"end {end} > total lines {TotalLines}");
        if (start > end) throw new ArgumentException($"start {start} > end {end}");
        var sb = new StringBuilder();
        int budget = maxChars;
        for (int i = start; i <= end; i++) {
            string row = $"{i,7}|{GetLineText(i)}\n";
            if (row.Length > budget) {
                sb.Append($"… [{end - i + 1} lines hidden: max_chars budget {maxChars} reached — narrow the range or raise max_chars]\n");
                return (sb.ToString(), true);
            }
            sb.Append(row);
            budget -= row.Length;
        }
        return (sb.ToString(), false);
    }

    public (List<FindHit> Hits, bool Truncated) Find(Regex rx, int afterLine, int maxHits, int contextLines = 0) {
        if (afterLine < 0) throw new ArgumentException($"after_line must be >= 0 (got {afterLine})");
        var hits = new List<FindHit>();
        for (int i = afterLine + 1; i <= TotalLines; i++) {
            string line = GetLineText(i);
            if (rx.IsMatch(line)) {
                string? snippet = null;
                if (contextLines > 0) {
                    int a = Math.Max(afterLine + 1, i - contextLines);
                    int b = Math.Min(TotalLines, i + contextLines);
                    var sb = new StringBuilder();
                    for (int j = a; j <= b; j++) sb.Append(j).Append('|').Append(TrimLong(GetLineText(j))).Append('\n');
                    snippet = sb.ToString();
                }
                hits.Add(new FindHit(i, Preview(line), snippet, WhereAmI(i)));
                if (hits.Count >= maxHits) return (hits, true);
            }
        }
        return (hits, false);
    }

    static string TrimLong(string s) => s.Length <= 300 ? s : s[..297] + "…";

    static string Preview(string line) {
        var t = line.Trim();
        return t.Length <= 120 ? t : t[..117] + "…";
    }

    List<string> Context(int fromLine, int count) { // fromLine 1-based, может быть вне файла
        var list = new List<string>(count);
        int first = Math.Max(1, fromLine);
        int last = Math.Min(TotalLines, fromLine + count - 1);
        for (int i = first; i <= last; i++) list.Add(GetLineText(i));
        return list;
    }

    // ── правки ───────────────────────────────────────────────────────────────

    void CheckRev(int expectedRev) {
        if (expectedRev != Rev)
            throw new ToolException(ToolException.StaleRev,
                $"stale rev: got expected_rev={expectedRev}, current rev is {Rev} — retry with expected_rev={Rev}"
                + (LastMutation is { } m ? $" (r{m.Rev} by {m.Actor}: {m.Desc} @ {m.Ts:HH:mm:ss})" : ""),
                currentRev: Rev, totalLines: TotalLines);
    }

    /// <summary>Якорь expect_old: точный текущий текст диапазона (нормализация — как у new_text).</summary>
    void CheckAnchor(int startLine, int count, string expectOld) {
        var want = ParseLines(expectOld);
        var have = new List<string>(count);
        for (int i = 0; i < count; i++) have.Add(GetLineText(startLine + i));
        if (have.SequenceEqual(want)) return;
        for (int i = 0; i < Math.Max(have.Count, want.Count); i++) {
            string h = i < have.Count ? have[i] : "⟨no line⟩";
            string w = i < want.Count ? want[i] : "⟨no line⟩";
            if (h != w)
                throw new ToolException(ToolException.AnchorMismatch,
                    $"expect_old mismatch at line {startLine + i}: found '{Preview(h)}', expected '{Preview(w)}'",
                    currentRev: Rev, totalLines: TotalLines);
        }
        throw new ToolException(ToolException.AnchorMismatch, "expect_old mismatch", currentRev: Rev, totalLines: TotalLines);
    }

    /// <summary>Текст → строки правки: \r\n/\r → \n; одиночный хвостовой перевод игнорируется; "" → ноль строк (удаление).</summary>
    static List<string> ParseLines(string text) {
        if (text == "") return [];
        var s = text.Replace("\r\n", "\n").Replace("\r", "\n");
        if (s.EndsWith('\n')) s = s[..^1]; // привычный завершающий \n не создаёт пустую строку
        return s.Split('\n').ToList();
    }

    void Apply(int startLineZeroBased, int count, List<string> newLines) {
        int bufIdx = -1;
        if (newLines.Count > 0) {
            _inserts.Add(InsertBuffers.Build(newLines, Crlf));
            bufIdx = _inserts.Count - 1;
        }
        _pieces.ReplaceRange(startLineZeroBased, count, bufIdx, newLines.Count);
        Bytes = ContentLength();
    }

    public MutationOutcome ReplaceLines(int start, int end, string newText, int expectedRev, bool dryRun, string? expectOld = null, string? actor = null) {
        CheckRev(expectedRev);
        if (start < 1 || end > TotalLines || start > end)
            throw new ToolException(ToolException.InvalidRange,
                $"range [{start}..{end}] invalid: file has {TotalLines} lines (1-based, inclusive)",
                currentRev: Rev, totalLines: TotalLines);
        if (expectOld is not null) CheckAnchor(start, end - start + 1, expectOld);
        var newLines = ParseLines(newText);
        var oldLines = new List<string>(end - start + 1);
        for (int i = start; i <= end; i++) oldLines.Add(GetLineText(i));
        var before = Context(start - 3, 3);
        var after = Context(end + 1, 3);
        string diff = Differ.Unified(before, oldLines, newLines, after, start);

        if (dryRun) return new MutationOutcome(Rev, TotalLines, diff, DryRun: true, Brackets: null);
        Apply(start - 1, end - start + 1, newLines);
        Rev++;
        Dirty = true;
        WriteShadow();
        RecordMutation(actor, $"replace {start}..{end} ({newLines.Count} lines)",
            Undoable(oldLines, newLines) ? [(new InverseOp(start, oldLines, newLines))] : null);
        int newEnd = start + newLines.Count; // + строка после: ловим осиротевшую закрывающую
        var brackets = BracketReport(Math.Max(1, start - 1), Math.Min(TotalLines, newEnd));
        return new MutationOutcome(Rev, TotalLines, diff, DryRun: false, Brackets: brackets);
    }

    public MutationOutcome InsertAfter(int line, string text, int expectedRev, bool dryRun = false, string? actor = null) {
        CheckRev(expectedRev);
        if (line < 0 || line > TotalLines)
            throw new ToolException(ToolException.InvalidRange,
                $"line must be 0..{TotalLines} (got {line}); 0 = insert at top",
                currentRev: Rev, totalLines: TotalLines);

        var newLines = ParseLines(text);
        string diff;
        if (newLines.Count == 0) diff = "(nothing to insert: empty text)";
        else {
            var before = Context(line - 2, 2);
            var after = Context(line + 1, 2);
            diff = Differ.Unified(before, [], newLines, after, line + 1);
        }
        if (dryRun) return new MutationOutcome(Rev, TotalLines, diff, DryRun: true, Brackets: null);
        Apply(line, 0, newLines);
        Rev++;
        Dirty = true;
        WriteShadow();
        RecordMutation(actor, $"insert_after {line} (+{newLines.Count} lines)",
            Undoable([], newLines) ? [new InverseOp(line + 1, [], newLines)] : null);
        var brackets = BracketReport(Math.Max(1, line), Math.Min(TotalLines, line + newLines.Count + 1));
        return new MutationOutcome(Rev, TotalLines, diff, DryRun: false, Brackets: brackets);
    }


    // ── батч-правки, replace_all ─────────────────────────────────────────────

    /// <summary>
    /// Атомарная серия правок одним вызовом. Все диапазоны и якоря expect_old проверяются
    /// ДО первой правки — батч либо целиком, либо ничего. Применение снизу вверх (по убыванию
    /// start): правка ниже не сдвигает строки выше, номера строк актуальны для всех edits
    /// без пересчёта. end = start-1 → вставка перед start. Один rev-инкремент на весь батч.
    /// </summary>
    public BatchOutcome EditBatch(List<BatchOp> ops, int expectedRev, bool dryRun, string? actor = null) {
        CheckRev(expectedRev);
        if (ops.Count is 0 or > 200)
            throw new ToolException(ToolException.InvalidArgs, $"edits must contain 1..200 items (got {ops.Count})");

        var plan = new (BatchOp Op, int Idx, List<string> OldLines, List<string> NewLines, string Diff)[ops.Count];
        int minStart = int.MaxValue; // минимальный start уже принятой (нижней) правки
        int first = int.MaxValue, last = 0, delta = 0;
        var anchorFails = new List<string>(); // ВСЕ мисматчи якорей разом — агент чинит одним следующим вызовом
        foreach (var (op, idx) in ops.Select((op, idx) => (op, idx)).OrderByDescending(x => x.op.Start)) {
            bool insert = op.End == op.Start - 1;
            if (op.Start < 1 || op.End < op.Start - 1 || op.End > TotalLines
                || (insert && op.Start > TotalLines + 1) || (!insert && op.Start > op.End))
                throw new ToolException(ToolException.InvalidRange,
                    $"edits[{idx}]: range [{op.Start}..{op.End}] invalid: file has {TotalLines} lines (end=start-1 → insert before start)",
                    currentRev: Rev, totalLines: TotalLines);
            if (op.End >= minStart)
                throw new ToolException(ToolException.BatchOverlap,
                    $"edits[{idx}]: range [{op.Start}..{op.End}] overlaps another edit (ranges must be disjoint)",
                    currentRev: Rev, totalLines: TotalLines);
            minStart = op.Start;

            var newLines = ParseLines(op.NewText);
            var oldLines = new List<string>();
            if (!insert) for (int i = op.Start; i <= op.End; i++) oldLines.Add(GetLineText(i));
            if (op.ExpectOld is not null) {
                var want = ParseLines(op.ExpectOld);
                if (!oldLines.SequenceEqual(want)) {
                    for (int i = 0; i < Math.Max(oldLines.Count, want.Count); i++) {
                        string h = i < oldLines.Count ? oldLines[i] : "⟨no line⟩";
                        string w = i < want.Count ? want[i] : "⟨no line⟩";
                        if (h != w) {
                            anchorFails.Add($"edits[{idx}] line {op.Start + i}: found '{Preview(h)}', expected '{Preview(w)}'");
                            break;
                        }
                    }
                }
            }
            string diff = insert
                ? Differ.Unified(Context(op.Start - 3, 3), [], newLines, Context(op.Start, 3), op.Start)
                : Differ.Unified(Context(op.Start - 3, 3), oldLines, newLines, Context(op.End + 1, 3), op.Start);
            plan[idx] = (op, idx, oldLines, newLines, diff);
            first = Math.Min(first, op.Start);
            last = Math.Max(last, op.End);
            delta += newLines.Count - (insert ? 0 : op.End - op.Start + 1);
        }

        if (anchorFails.Count > 0)
            throw new ToolException(ToolException.AnchorMismatch,
                $"{anchorFails.Count} anchor mismatch(es) — batch NOT applied:\n" + string.Join("\n", anchorFails),
                currentRev: Rev, totalLines: TotalLines);

        if (dryRun)
            return new BatchOutcome(Rev, TotalLines, plan.Length, [.. plan.Select(p => p.Diff)], DryRun: true, Brackets: null);

        foreach (var (op, _, oldLines, newLines, _) in plan.OrderByDescending(p => p.Op.Start)) // снизу вверх
            Apply(op.Start - 1, op.End == op.Start - 1 ? 0 : op.End - op.Start + 1, newLines);
        Rev++;
        Dirty = true;
        WriteShadow();
        RecordMutation(actor, $"batch ×{plan.Length} ({first}..{last}, Δ{delta:+0;-0})",
            plan.All(p => Undoable(p.OldLines, p.NewLines))
                ? [.. plan.OrderByDescending(p => p.Op.Start).Select(p => new InverseOp(p.Op.Start, p.OldLines, p.NewLines))]
                : null);
        var brackets = BracketReport(Math.Max(1, first - 1), Math.Min(TotalLines, last + Math.Max(delta, 0) + 4));
        return new BatchOutcome(Rev, TotalLines, plan.Length, [.. plan.Select(p => p.Diff)], DryRun: false, Brackets: brackets);
    }

    /// <summary>
    /// Литеральная замена подстроки во всех строках сессии (все вхождения в каждой строке,
    /// не regex). Совпавших строк больше maxLines → отказ целиком: частичный rename ломает
    /// код. Реализовано поверх EditBatch: один rev-инкремент, диффы по каждой строке.
    /// </summary>

    /// <summary>Правка отменяема, если обе стороны компактны (RAM на инверсии — bounded).</summary>
    static bool Undoable(List<string> oldLines, List<string> newLines) =>
        oldLines.Count <= 200 && newLines.Count <= 200;

    /// <summary>
    /// v0.5: undo — КОМПЕНСИРУЮЩАЯ мутация, а не откат: последняя отменяемая правка (или n подряд,
    /// от новейшей) инвертируется и применяется как обычная правка (новый rev, запись в историю
    /// с обратными инверсиями — поэтому undo(undo) естественно даёт redo). Локация ищется по
    /// содержимому New-строк (позиции могли сдвинуться чужими правками): не найдено или
    /// неоднозначно → kind=undo_conflict (все проблемы разом), ничего не применяется.
    /// </summary>
    public BatchOutcome Undo(int count, int expectedRev, bool dryRun, string? actor = null) {
        CheckRev(expectedRev);
        if (count is < 1 or > 20)
            throw new ToolException(ToolException.InvalidArgs, $"count must be 1..20 (got {count})");

        var targets = History.Where(h => h.Ops is { Count: > 0 }).TakeLast(count).ToList();
        if (targets.Count == 0)
            throw new ToolException("nothing_to_undo", "history has no undoable edits (huge edits beyond the 200-line cap are not invertible)",
                currentRev: Rev, totalLines: TotalLines);

        // валидация всех локаций по содержимому ДО применения — атомарно
        var conflicts = new List<string>();
        var located = new List<(InverseOp Op, int At)>(); // At — фактическая строка (1-based) вхождения New
        foreach (var entry in targets.AsEnumerable().Reverse()) { // от НОВОЙ к СТАРОЙ
            foreach (var op in entry.Ops!) {
                int? at = FindUnique(op.New, op.Start);
                if (at is null) conflicts.Add($"r{entry.Rev} '{op.New.FirstOrDefault() ?? "∅"}…' ({op.New.Count} lines): current text not found or ambiguous");
                else located.Add((op, at.Value));
            }
        }
        if (conflicts.Count > 0)
            throw new ToolException("undo_conflict",
                $"{conflicts.Count} undo location(s) lost — peers edited the same lines (or identical text elsewhere):\n" + string.Join("\n", conflicts),
                currentRev: Rev, totalLines: TotalLines);

        var diffs = new List<string>();
        foreach (var (op, at) in located) {
            var after = Context(at + op.New.Count, 3);
            diffs.Add(Differ.Unified(Context(at - 3, 3), op.New, op.Old, after, at));
        }
        if (dryRun)
            return new BatchOutcome(Rev, TotalLines, located.Count, diffs, DryRun: true, Brackets: null);

        foreach (var (op, at) in located.OrderByDescending(l => l.At)) // снизу вверх
            Apply(at - 1, op.New.Count, op.Old);
        Rev++;
        Dirty = true;
        WriteShadow();
        RecordMutation(actor, $"undo ×{located.Count} (back to r{targets[^1].Rev - 1})",
            [.. located.Select(l => new InverseOp(l.At, l.Op.New, l.Op.Old))]); // обратные инверсии → redo через undo
        var brackets = BracketReport(Math.Max(1, located.Min(l => l.At) - 1),
            Math.Min(TotalLines, located.Max(l => l.At) + 4));
        return new BatchOutcome(Rev, TotalLines, located.Count, diffs, DryRun: false, Brackets: brackets);
    }

    /// <summary>Единственное вхождение последовательности строк в файле (hint: с ожидаемой позиции ищем в первую очередь).</summary>
    int? FindUnique(List<string> needle, int hint) {
        bool At(int i) {
            if (i < 1 || i - 1 + needle.Count > TotalLines) return false;
            for (int k = 0; k < needle.Count; k++)
                if (GetLineText(i + k) != needle[k]) return false;
            return true;
        }
        if (At(hint)) {
            // hint совпал — убедимся, что второго вхождения нет (иначе неоднозначно)
            int? second = FindFrom(1, hint - 1) ?? FindFrom(hint + 1, TotalLines);
            return second is null ? hint : null;
        }
        int? first = FindFrom(1, TotalLines);
        if (first is null) return null;
        return FindFrom(first.Value + 1, TotalLines) is null ? first : null;

        int? FindFrom(int from, int to) {
            for (int i = from; i <= to - needle.Count + 1; i++)
                if (At(i)) return i;
            return null;
        }
    }

    public ReplaceAllOutcome ReplaceAll(string find, string replace, int maxLines, int expectedRev, bool dryRun, string? actor = null) {
        CheckRev(expectedRev);
        if (find.Length == 0)
            throw new ToolException(ToolException.InvalidArgs, "find must not be empty");
        var ops = new List<BatchOp>();
        int occurrences = 0;
        for (int i = 1; i <= TotalLines; i++) {
            string line = GetLineText(i);
            int pos = 0, k = 0;
            while ((pos = line.IndexOf(find, pos, StringComparison.Ordinal)) >= 0) { k++; pos += find.Length; }
            if (k == 0) continue;
            occurrences += k;
            ops.Add(new BatchOp(i, i, line.Replace(find, replace, StringComparison.Ordinal), null));
        }
        if (ops.Count > maxLines)
            throw new ToolException(ToolException.TooLarge,
                $"replace_all: {ops.Count} lines contain the needle (cap {maxLines}) — chunk via edit_batch or raise max_lines",
                currentRev: Rev, totalLines: TotalLines);
        if (ops.Count == 0)
            return new ReplaceAllOutcome(Rev, TotalLines, 0, [], DryRun: dryRun, Brackets: null);
        var b = EditBatch(ops, Rev, dryRun, actor); // rev уже проверен — передаём текущий
        return new ReplaceAllOutcome(b.Rev, b.TotalLines, occurrences, [.. ops.Select(o => o.Start)], b.DryRun, b.Brackets);
    }
    // ── поток содержимого, save, shadow ─────────────────────────────────────

    long ContentLength() {
        long total = 0;
        foreach (var p in _pieces.Pieces) {
            if (p.BufferIndex < 0) {
                var (a, b) = BasePieceSpan(p);
                total += b - a;
            }
            else total += _inserts[p.BufferIndex].Data.LongLength;
        }
        return total;
    }

    (long Start, long End) BasePieceSpan(Piece p) {
        long a = _baseStarts[p.FirstLine];
        int last = p.FirstLine + p.LineCount - 1;
        long b = last + 1 < _baseStarts.Length ? _baseStarts[last + 1] : BaseLength;
        return (a, b);
    }

    /// <summary>Слить логическое содержимое в sink чанками. Стык «база без завершающего \n → вставка» закрывается переводом строки.</summary>
    void StreamContent(Action<ReadOnlySpan<byte>> sink) {
        byte[] buf = new byte[1 << 20];
        var pieces = _pieces.Pieces;
        for (int pi = 0; pi < pieces.Count; pi++) {
            var p = pieces[pi];
            if (p.BufferIndex < 0) {
                var (a, b) = BasePieceSpan(p);
                for (long pos = a; pos < b; ) {
                    int n = (int)Math.Min(buf.Length, b - pos);
                    _src.Read(pos, buf.AsSpan(0, n));
                    sink(buf.AsSpan(0, n));
                    pos += n;
                }
                // последняя строка базы без завершающего перевода, но за ней есть ещё куски
                if (pi < pieces.Count - 1 && b == BaseLength && !_endsWithNewline)
                    sink(Crlf ? "\r\n"u8 : "\n"u8);
            }
            else sink(_inserts[p.BufferIndex].Data);
        }
    }

    public SaveOutcome Save(bool backup, string encoding, bool force, string? actor = null) {
        // v0.4 коллаборация: чужие несохранённые правки → осознанный force (свой save публикует ВСЁ)
        var peers = PendingEdits.Where(kv => kv.Key != actor && kv.Value > 0).ToList();
        if (peers.Count > 0 && !force)
            throw new ToolException("peers_dirty",
                $"other sessions have unsaved edits: {string.Join(", ", peers.Select(kv => $"{kv.Key}×{kv.Value}"))} — "
                + "your save publishes THEM too; force=true to flush all (or let them save/detach)",
                currentRev: Rev, totalLines: TotalLines);
        // default = keep: исходная кодировка/BOM не портятся без явного требования
        string target = encoding switch {
            "" or "keep" => DiskEncoding,
            "utf8" or "utf-8" => "utf-8",
            _ => throw new ArgumentException($"encoding must be 'keep' (default) or 'utf8' (got '{encoding}')"),
        };

        var fi = new FileInfo(Path);
        if ((fi.LastWriteTimeUtc != _diskMtimeUtc || fi.Length != _diskSize) && !force)
            throw new ToolException(ToolException.DiskConflict,
                "file changed on disk since open (mtime/size mismatch) — pass force=true to overwrite anyway");

        var enc = EncodingDetect.OutputEncoding(target);
        string temp = Path + ".smartedit-tmp";
        ulong hash;
        long bytesWritten;
        if (target == "utf-8") {
            // потоково: гиганты не материализуются в RAM, хэш считается на лету
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20)) {
                var hasher = new XxHash64.Streamer();
                long total = 0;
                StreamContent(chunk => { fs.Write(chunk); hasher.Feed(chunk); total += chunk.Length; });
                hash = hasher.Finish();
                bytesWritten = total;
            }
        }
        else {
            // транскод-путь: RAM-материализация (не-UTF-8 файлы ≤256 МБ по лимиту open)
            using var ms = new MemoryStream();
            StreamContent(ms.Write);
            string text = StrictUtf8.GetString(ms.ToArray());
            byte[] finalBytes = enc.GetPreamble().Concat(enc.GetBytes(text)).ToArray();
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20)) {
                fs.Write(finalBytes);
                bytesWritten = fs.Length;
            }
            hash = XxHash64.Hash(finalBytes);
        }

        string? backupPath = null;
        // освободить базу: активная mmap-секция блокирует File.Move(overwrite) на Windows
        _src.Dispose();
        try {
            if (backup) {
                backupPath = Path + ".smartedit-bak";
                try { File.Copy(Path, backupPath, overwrite: true); }
                catch (IOException) { backupPath = null; } // бак не критичен
            }
            File.Move(temp, Path, overwrite: true);
        }
        catch {
            // сессия без src нежизнеспособна — перечитаем текущее состояние диска и пробросим ошибку
            Reload(DiskEncoding);
            throw;
        }
        TryDeleteFile(Path + ".smartedit-shadow");

        Dirty = false;
        Reload(target); // rev НЕ меняется: save не перенумеровывает строки
        ClearPendingEdits(); // все правки (включая чужие) опубликованы на диск
        return new SaveOutcome(bytesWritten, backupPath, $"{hash:x16}", target);
    }

    void WriteShadow() {
        if (ContentLength() > ShadowLimit) return;
        try {
            using var fs = new FileStream(Path + ".smartedit-shadow", FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            StreamContent(fs.Write);
        }
        catch (IOException) { /* shadow — best effort */ }
    }

    static void TryDeleteFile(string path) {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    public void Close(bool discard) {
        if (Dirty && !discard)
            throw new ToolException(ToolException.DirtyClose,
                "session has unsaved edits — save first or pass discard=true");
        _shadowKept = Dirty; // discard грязной сессии: shadow остаётся на диске как страховка (можно скопировать поверх)
        ReleaseResources();
    }
    public void Dispose() {
        ReleaseResources();
        Gate.Dispose();
    }

    // Close вызывается под Gate: семафор живёт дольше — его диспозит сам вызывающий (вне локера)
    void ReleaseResources() {
        try { _src?.Dispose(); } catch { }
        if (!_shadowKept) TryDeleteFile(Path + ".smartedit-shadow");
    }
}
