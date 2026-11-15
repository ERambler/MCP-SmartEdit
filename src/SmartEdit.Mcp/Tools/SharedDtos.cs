namespace SmartEdit.Mcp.Tools;

// Общие DTO результатов (v0.13.1: используются 2+ инструментами; пер-туловые DTO живут
// в файлах своих инструментов). Сериализуются source-gen контекстом RpcJson.

/// <summary>Кто сейчас в документе: id/заметка/несохранённые правки/stale-маркер.</summary>
public sealed class PeerDto {
    public string SessionId { get; init; } = "";
    public string? Note { get; init; }
    public int PendingEdits { get; init; }
    public bool Stale { get; init; } // last_seen > 30 минут — возможно, мёртвый агент
}

/// <summary>Результат одиночной правки (replace_lines / insert_after / replace_content).</summary>
public sealed class MutationResult {
    public int Rev { get; init; }
    public int TotalLines { get; init; }
    public string Diff { get; init; } = "";
    public bool DryRun { get; init; }
    public string[]? Brackets { get; init; } // проблемы баланса {}[]() после правки (null — не код/сухой прогон)
    public bool Recovered { get; init; }              // v0.7 (#1): применено после auto-recovery из stale_rev
    public string[]? Intervening { get; init; }       // что меняли чужие правки, пока твой rev устарел
    public DiagDto[]? Diagnostics { get; init; }      // v0.8: структурные якоря (line/code/message), null = чисто
    public bool Unsaved { get; init; }                 // v0.9.3: правка применена в RAM, но НЕ сохранена — не забудь save(path)
    /// <summary>#5 (v0.11, opt-in open(attention:true)): правка затронула строки, которых редактор не видел — advisory, не блок.</summary>
    public string? Blind { get; init; }
}

/// <summary>Структурная диагностика в ответе правки: якорная строка + машинный код + текст.</summary>
public sealed class DiagDto {
    public int Line { get; init; }
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
}

/// <summary>Новая синтаксическая ошибка гейта: kind=error|missing, Row/Col 1-based.</summary>
public sealed class GateErrorDto {
    public string Kind { get; init; } = "";
    public string Token { get; init; } = "";      // для missing — пропущенный токен (";", ")", "fi")
    public int Row { get; init; }
    public int Col { get; init; }
    public string Fragment { get; init; } = "";   // для error — нормализованный текст фрагмента
}

/// <summary>Результат серии правок (edit_batch / undo).</summary>
public sealed class BatchResult {
    public int Rev { get; init; }
    public int TotalLines { get; init; }
    public int Applied { get; init; }
    public bool DryRun { get; init; }
    public string[] Diffs { get; init; } = [];
    public string[]? Brackets { get; init; }
    public bool Recovered { get; init; }          // v0.7 (#1)
    public string[]? Intervening { get; init; }   // v0.7 (#1)
    public DiagDto[]? Diagnostics { get; init; }  // v0.8
    public bool Unsaved { get; init; }             // v0.9.3: применяется в RAM, НЕ сохранено — save(path)
    public string? Blind { get; init; }            // #5 (v0.11): первый непросмотренный edits[i] (open(attention:true))
}

/// <summary>Символ кода (symbols / goto): строки = show/replace_lines.</summary>
public sealed class SymbolDto {
    public int Line { get; init; }
    public int EndLine { get; init; }
    public string Kind { get; init; } = "";
    public string Name { get; init; } = "";
    public string Sig { get; init; } = "";
    public string? Doc { get; init; }
    public SymbolDto[] Children { get; init; } = [];
}
