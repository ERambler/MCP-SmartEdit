namespace SmartEdit.Mcp.Core;

/// <summary>
/// Ошибка инструмента с машинным kind: клиент (агент) ветвится по kind, а не парсит прозу.
/// Сериализуется в ответе как JSON {kind, message, current_rev?, total_lines?}.
/// </summary>
public sealed class ToolException(
    string kind, string message, int? currentRev = null, int? totalLines = null,
    string? retryHint = null, string? currentText = null) : Exception(message) {
    public string Kind { get; } = kind;
    public int? CurrentRev { get; } = currentRev;
    public int? TotalLines { get; } = totalLines;
    /// <summary>#1: подсказка агенту, как выйти из ошибки (stale_conflict → показать диапазон и перезапустить).</summary>
    public string? RetryHint { get; } = retryHint;
    /// <summary>#1: текущий текст спорного диапазона (stale_conflict) — «обе версии» без отдельного show.</summary>
    public string? CurrentText { get; } = currentText;

    public const string StaleRev = "stale_rev";
    public const string AnchorMismatch = "anchor_mismatch";
    public const string InvalidRange = "invalid_range";
    public const string InvalidArgs = "invalid_args";
    public const string UnknownSession = "unknown_session";
    public const string DiskConflict = "disk_conflict";
    public const string DirtyClose = "dirty_close";
    public const string TooLarge = "too_large";
    public const string BadRegex = "bad_regex";
    public const string BatchOverlap = "batch_overlap";
}
