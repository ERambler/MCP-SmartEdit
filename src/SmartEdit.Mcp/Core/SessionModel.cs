using SmartEdit.Mcp.Core.SymbolScan;

namespace SmartEdit.Mcp.Core;

// Модели результатов мутаций документа (v0.13.1: вынесены из EditSession.cs — один класс на файл).

public sealed record FindHit(int Line, string Preview, string? Snippet, string? Where, int SpanStart = 0, int SpanEnd = 0);
public sealed record MutationOutcome(int Rev, int TotalLines, string Diff, bool DryRun, List<string>? Brackets, bool Recovered = false, List<string>? Intervening = null, List<BracketCheck.Diag>? Diags = null, string? Blind = null);
public sealed record SaveOutcome(long BytesWritten, string? BackupPath, string Hash, string Encoding,
    string GateStatus = "off", List<SyntaxGate.Diag>? GateNew = null);
public sealed record BatchOp(int Start, int End, string NewText, string? ExpectOld);
public sealed record BatchOutcome(int Rev, int TotalLines, int Applied, List<string> Diffs, bool DryRun, List<string>? Brackets, bool Recovered = false, List<string>? Intervening = null, List<BracketCheck.Diag>? Diags = null, string? Blind = null);
public sealed record ReplaceAllOutcome(int Rev, int TotalLines, int Occurrences, List<int> Lines, bool DryRun, List<string>? Brackets);

/// <summary>Компенсирующая операция для undo: строки [Old ↔ New] на позиции Start (позиция — hint: undo ищет по содержимому).</summary>
public sealed record InverseOp(int Start, List<string> Old, List<string> New);

/// <summary>Запись истории мутаций общего документа (кто/что/когда; Ops — инверсии для undo, null = правка не отменяемая).</summary>
/// <summary>Span мутации в её же координатах ДО применения: [SpanStart..SpanEnd] — задетые строки
/// (SpanStart = SpanEnd+1 — точка вставки), SpanDelta — изменение числа строк. Null = неизвестно/undo → консервативно небезопасно.</summary>
public sealed record HistoryEntry(int Rev, string Actor, string Desc, DateTime Ts, List<InverseOp>? Ops = null, int? SpanStart = null, int? SpanEnd = null, int? SpanDelta = null);
