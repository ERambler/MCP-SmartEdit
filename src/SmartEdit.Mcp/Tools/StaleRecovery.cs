using SmartEdit.Mcp.Core;

namespace SmartEdit.Mcp.Tools;

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
