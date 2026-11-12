namespace SmartEdit.Mcp.Tools;

// Результаты инструментов (сериализуются source-gen контекстом RpcJson).

public sealed class OpenResult {
    public string SessionId { get; set; } = "";
    public string Path { get; set; } = "";
    public int Rev { get; set; }
    public int TotalLines { get; set; }
    public long Bytes { get; set; }
    public string Encoding { get; set; } = "";
    public bool Crlf { get; set; }
    public int TodoCount { get; set; }
    public string[] Usings { get; set; } = [];
    public OutlineDto[] Outline { get; set; } = [];
    public SymbolBriefDto[]? CodeOutline { get; set; } // v0.6.2 (#2): топ-символы кода для файлов ≤5k строк (null = не код/слишком большой)
    public int CodeOutlineTotal { get; set; }      // v0.8.1 (#2): сколько символов ВСЕГО
    public bool CodeOutlineTruncated { get; set; } // v0.8.1 (#2): true = топ-50, НЕ весь файл
    public string Preview { get; set; } = "";
    public bool Reused { get; set; }            // v0.4: путь уже был открыт — подключение к общему документу
    public PeerDto[] Peers { get; set; } = [];  // кто сейчас в файле (id/note/несохранённые/stale)
    public string[] Recent { get; set; } = [];  // последние мутации общего документа
}

/// <summary>Компактная строка code-outline в open-ответе: kind/name/line без сигнатур и детей.</summary>
public sealed class SymbolBriefDto {
    public int Line { get; set; }
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class PeerDto {
    public string SessionId { get; set; } = "";
    public string? Note { get; set; }
    public int PendingEdits { get; set; }
    public bool Stale { get; set; } // last_seen > 30 минут — возможно, мёртвый агент
}

public sealed class OutlineDto {
    public int Line { get; set; }
    public int Level { get; set; }
    public string Title { get; set; } = "";
}

public sealed class ShowResult {
    public int Rev { get; set; }
    public int TotalLines { get; set; }
    public int Start { get; set; }
    public int End { get; set; }
    public string Text { get; set; } = "";
    public bool Truncated { get; set; }
    public string[]? Events { get; set; } // v0.4: чужие правки с твоего last_seen (null = не отставал)
}

public sealed class FindResult {
    public int Rev { get; set; }
    public HitDto[] Hits { get; set; } = [];
    public bool Truncated { get; set; }
    public string[]? Events { get; set; } // v0.4: чужие правки с твоего last_seen
}

public sealed class HitDto {
    public int Line { get; set; }
    public string Preview { get; set; } = "";
    public string? Snippet { get; set; }
    public string? Where { get; set; } // breadcrumb Ns.Class.Method (если symbols-кэш прогрет)
    public SpanDto? Span { get; set; }  // v0.8.1 (#3): строки, которые покрывает snippet — редактируй без follow-up show
}

/// <summary>Диапазон строк (1-based, включительно).</summary>
public sealed class SpanDto {
    public int Start { get; set; }
    public int End { get; set; }
}

public sealed class MutationResult {
    public int Rev { get; set; }
    public int TotalLines { get; set; }
    public string Diff { get; set; } = "";
    public bool DryRun { get; set; }
    public string[]? Brackets { get; set; } // проблемы баланса {}[]() после правки (null — не код/сухой прогон)
    public bool Recovered { get; set; }              // v0.7 (#1): применено после auto-recovery из stale_rev
    public string[]? Intervening { get; set; }       // что меняли чужие правки, пока твой rev устарел
    public DiagDto[]? Diagnostics { get; set; }      // v0.8: структурные якоря (line/code/message), null = чисто
}

/// <summary>Per-file итог edit_transaction (#3).</summary>
public sealed class TxFileDto {
    public string Path { get; set; } = "";
    public string SessionId { get; set; } = "";
    public int Rev { get; set; }
    public int TotalLines { get; set; }
    public int Applied { get; set; }
    public List<string> Diffs { get; set; } = [];
    public DiagDto[]? Diagnostics { get; set; }
    public bool? SavedOk { get; set; }     // save:true → статус файла (null = save не запрашивался)
    public string? SaveError { get; set; }
}

public sealed class TransactionResult {
    public bool DryRun { get; set; }
    public bool SaveRequested { get; set; }
    public int Applied { get; set; }
    public List<TxFileDto> Files { get; set; } = [];
}

/// <summary>Структурная диагностика в ответе правки: якорная строка + машинный код + текст.</summary>
public sealed class DiagDto {
    public int Line { get; set; }
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class SaveResult {
    public long BytesWritten { get; set; }
    public string? BackupPath { get; set; }
    public string Hash { get; set; } = "";
    public string Encoding { get; set; } = "";
}

public sealed class CloseResult {
    public bool Closed { get; set; }
    public bool Dirty { get; set; }
    public string? Shadow { get; set; } // путь к shadow, если discard грязной сессии оставил страховку на диске
}

// ── v0.2: структура кода, дерево проекта, мета-утилиты ──

public sealed class SymbolsResult {
    public string Language { get; set; } = "";
    public string[] Usings { get; set; } = [];
    public string[] Warnings { get; set; } = [];
    public SymbolDto[] Symbols { get; set; } = [];
}

public sealed class SymbolDto {
    public int Line { get; set; }
    public int EndLine { get; set; }
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string Sig { get; set; } = "";
    public string? Doc { get; set; }
    public SymbolDto[] Children { get; set; } = [];
}

public sealed class TreeResult {
    public string Root { get; set; } = "";
    public int TotalFiles { get; set; }
    public int ListedFiles { get; set; }
    public int SkippedDirs { get; set; }
    public long TotalBytes { get; set; }
    public TreeGroupDto[] Groups { get; set; } = [];
}

public sealed class TreeGroupDto {
    public string Namespace { get; set; } = ""; // "" = без namespace / не C#
    public TreeFileDto[] Files { get; set; } = [];
}
public sealed class TreeFileDto {
    public string Path { get; set; } = "";
    public int Lines { get; set; }
    public long Bytes { get; set; }
    public DateTime? ModifiedUtc { get; set; } // заполняется только в recent-режиме tree
}

public sealed class FileInfoResult {
    public string Path { get; set; } = "";
    public long Bytes { get; set; }
    public string Encoding { get; set; } = "";
    public string Newlines { get; set; } = "";
    public DateTime ModifiedUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public bool ReadOnly { get; set; }
    public bool Hidden { get; set; }
    public bool IsCode { get; set; }
    public bool IsUtf8 { get; set; }
}

public sealed class ConvertResult {
    public string Path { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public long Bytes { get; set; }
}

public sealed class CalcResult {
    public string Expr { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class CheckResult {
    public string[] Problems { get; set; } = [];
}

/// <summary>v0.5: хвост общего журнала сервера (все процессы/проекты).</summary>
public sealed class LogResult {
    public string Path { get; set; } = "";
    public long LinesScanned { get; set; }
    public string[] Lines { get; set; } = [];
}

/// <summary>v0.5: символы по имени (goto) — строки совпадают с show/replace_lines.</summary>
public sealed class GotoResult {
    public string Language { get; set; } = "";
    public SymbolDto[] Symbols { get; set; } = [];
    public bool Truncated { get; set; }
}

// ── v0.3: серия правок, литеральная замена ──

public sealed class BatchResult {
    public int Rev { get; set; }
    public int TotalLines { get; set; }
    public int Applied { get; set; }
    public bool DryRun { get; set; }
    public string[] Diffs { get; set; } = [];
    public string[]? Brackets { get; set; }
    public bool Recovered { get; set; }          // v0.7 (#1)
    public string[]? Intervening { get; set; }   // v0.7 (#1)
    public DiagDto[]? Diagnostics { get; set; }  // v0.8
}

public sealed class ReplaceAllResult {
    public int Rev { get; set; }
    public int TotalLines { get; set; }
    public int Occurrences { get; set; }
    public int[] Lines { get; set; } = [];
    public bool DryRun { get; set; }
    public string[]? Brackets { get; set; }
}

// ── v0.3.1: диагностика сессий ──

public sealed class SessionsResult {
    public SessionDto[] Sessions { get; set; } = [];
}

public sealed class SessionDto {
    public string SessionId { get; set; } = "";
    public string Path { get; set; } = "";
    public int Rev { get; set; }
    public bool Dirty { get; set; }
    public int TotalLines { get; set; }
    public long Bytes { get; set; }
    public string? Note { get; set; }        // v0.4
    public int PendingEdits { get; set; }    // несохранённые правки этого подключения
    public bool Stale { get; set; }
}

public sealed class SyncResult {
    public int Rev { get; set; }
    public int TotalLines { get; set; }
    public string[] Events { get; set; } = [];
    public PeerDto[] Peers { get; set; } = [];
}
