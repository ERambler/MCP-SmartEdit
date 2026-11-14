using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

/// <summary>Шаг транзакции после разбора аргументов.</summary>
sealed class TxStep {
    public required Attachment Att { get; init; }
    public required List<BatchOp> Ops { get; init; }
    public int? ExpectedRev { get; init; }
}

/// <summary>
/// v0.9 (#3): атомарные правки по N файлам одним вызовом. Всё-или-ничего В ПАМЯТИ:
/// глобальный txn-лок → док-локи в каноническом порядке путей → сухой проход (валидация всех
/// диапазонов/якорей/ревизий; любая ошибка = transaction_aborted, НИЧЕГО не применено) → применение.
/// Дисковая философия сохранена: save по умолчанию false (RAM-staging) — посмотрел diffs/диагностики,
/// доволен → сохранил (save:true = per-file best-effort с честным статусом; кросс-файловая дисковая
/// атомарность в POSIX невозможна и не обещается). Путь ИЛИ session_id на шаг (смерть ритуала open).
/// </summary>
sealed class TransactionTool : HiddenTool {
    static readonly SemaphoreSlim TxnGate = new(1, 1);

    public override string Name => "edit_transaction";
    public override string Description =>
        "Atomic edits across MULTIPLE files in ONE call — the all-or-nothing refactor. " +
        "steps=[{path|session_id, edits:[{start,end,new_text,expect_old?}], expected_rev?}] (expected_rev omitted = accept current; anchors still validated). " +
        "Every range/anchor/rev is validated on EVERY file BEFORE anything applies — one bad step rejects the whole transaction (kind=transaction_aborted names the file). " +
        "Disk philosophy: save defaults to FALSE (RAM-staging; save each file when happy); save=true saves per-file best-effort with honest per-file status — cross-file DISK atomicity is not promised (RAM atomicity is). " +
        "Limits: 1..16 files, <=200 edits total, >8 files requires reason. Gates: global txn lock, then per-file locks in canonical path order. dry_run=true → planned diffs only.";
    public override string InputSchemaJson => """
        {"type":"object","properties":{
          "steps":{"type":"array","minItems":1,"maxItems":16,"items":{"type":"object","properties":{
            "path":{"type":"string","description":"file path (open-by-path: skip open entirely)"},
            "session_id":{"type":"string","description":"or session_id from a previous response"},
            "expected_rev":{"type":"integer","description":"omit = the last rev you observed for this file"},
            "edits":{"type":"array","maxItems":200,"items":{"type":"object","properties":{
              "start":{"type":"integer"},"end":{"type":"integer"},"new_text":{"type":"string"},
              "expect_old":{"type":"string","description":"optional anchor: exact current text of start..end"}
            },"required":["start","end","new_text"]}}
          },"required":["edits"]}},
          "save":{"type":"boolean","description":"default false: RAM-staging; true = save each file after apply (best-effort, per-file status)"},
          "dry_run":{"type":"boolean","description":"validate + planned diffs, nothing applied"},
          "reason":{"type":"string","description":"required when >8 files (runaway guard)"}
        },"required":["steps"]}
        """;

    public async override Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        bool dryRun = args.GetBool("dry_run");
        bool save = args.GetBool("save");

        var steps = new List<TxStep>();
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("steps", out var arr) && arr.ValueKind == JsonValueKind.Array) {
            foreach (var st in arr.EnumerateArray()) {
                if (st.ValueKind != JsonValueKind.Object)
                    throw new ToolException(ToolException.InvalidArgs, "each step must be an object {path|session_id, edits[]}");
                var ops = new List<BatchOp>();
                if (st.TryGetProperty("edits", out var ea) && ea.ValueKind == JsonValueKind.Array)
                    foreach (var e in ea.EnumerateArray()) {
                        if (e.ValueKind != JsonValueKind.Object)
                            throw new ToolException(ToolException.InvalidArgs, "each edit must be an object {start,end,new_text,expect_old?}");
                        ops.Add(new BatchOp(e.GetRequiredInt("start"), e.GetRequiredInt("end"), e.GetString("new_text"), e.GetNullableString("expect_old")));
                    }
                var att = Sessions.Resolve(st.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String ? sid.GetString() : null,
                                            st.TryGetProperty("path", out var pth) && pth.ValueKind == JsonValueKind.String ? pth.GetString() : null);
                int? expectedRev = st.TryGetProperty("expected_rev", out var rv) && rv.ValueKind == JsonValueKind.Number ? rv.GetInt32() : null;
                steps.Add(new TxStep { Att = att, Ops = ops, ExpectedRev = expectedRev });
            }
        }
        if (steps.Count is 0 or > 16)
            throw new ToolException(ToolException.InvalidArgs, $"steps must contain 1..16 files (got {steps.Count})");
        int totalEdits = steps.Sum(s => s.Ops.Count);
        if (totalEdits is 0 or > 200)
            throw new ToolException(ToolException.InvalidArgs, $"total edits must be 1..200 across the transaction (got {totalEdits})");
        int fileCount = steps.Select(s => s.Att.Doc.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (fileCount > 8 && string.IsNullOrWhiteSpace(args.GetNullableString("reason")))
            throw new ToolException(ToolException.InvalidArgs, $">8 files ({fileCount}) requires reason (runaway guard)");
        var dup = steps.GroupBy(s => s.Att.Doc.Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null)
            throw new ToolException(ToolException.InvalidArgs,
                $"duplicate file in steps: {System.IO.Path.GetFileName(dup.Key)} — merge its edits into one step");

        await TxnGate.WaitAsync(ct);
        try {
            // док-локи в каноническом порядке путей (одинаковый порядок у всех транзакций — дедлок невозможен)
            var ordered = steps.Select(s => s.Att.Doc).DistinctBy(d => d.Path, StringComparer.OrdinalIgnoreCase)
                               .OrderBy(d => d.Path, StringComparer.Ordinal).ToList();
            foreach (var d in ordered) await d.Gate.WaitAsync(ct);
            try {
                var files = new List<TxFileDto>();
                // фаза 1: сухая валидация ВСЕГО (любой бросок = transaction_aborted, ничего не применено)
                foreach (var st in steps) {
                    try {
                        st.Att.Doc.EnsureLive();
                        st.Att.Doc.EditBatch(st.Ops, st.ExpectedRev ?? st.Att.LastSeenRev, dryRun: true, st.Att.Id);
                    }
                    catch (Exception ex) {
                        var kind = ex is ToolException te ? te.Kind : "error";
                        throw new ToolException("transaction_aborted",
                            $"[{System.IO.Path.GetFileName(st.Att.Doc.Path)}] {kind}: {ex.Message} — NOTHING applied (all-or-nothing)",
                            retryHint: "fix the offending step and re-issue the whole transaction");
                    }
                }
                // фаза 2: применение (rev не менялся — сухой прогон не мутировал)
                foreach (var st in steps) {
                    var oc = st.Att.Doc.EditBatch(st.Ops, st.ExpectedRev ?? st.Att.LastSeenRev, dryRun, st.Att.Id);
                    st.Att.LastSeenRev = oc.Rev; // v0.9: ответ доставил состояние
                    files.Add(new TxFileDto {
                        Path = st.Att.Doc.Path, SessionId = st.Att.Id, Rev = oc.Rev, TotalLines = oc.TotalLines,
                        Applied = st.Ops.Count, Diffs = [.. oc.Diffs],
                        Diagnostics = oc.Diags?.Select(d => new DiagDto { Line = d.Line, Code = d.Code, Message = d.Message }).ToArray(),
                    });
                }
                // фаза 3: явный save (best-effort, per-file статус; peers_dirty/disk_conflict не роняют остальные)
                if (save && !dryRun)
                    foreach (var f in files) {
                        var doc = steps.First(s => s.Att.Id == f.SessionId).Att.Doc;
                        try { doc.Save(false, "keep", false, f.SessionId); f.SavedOk = true; }
                        catch (ToolException ex) { f.SavedOk = false; f.SaveError = $"{ex.Kind}: {ex.Message}"; }
                    }
                return ToolOps.Json(new TransactionResult { DryRun = dryRun, SaveRequested = save, Applied = files.Sum(f => f.Applied), Files = files,
                    UnsavedFiles = dryRun ? 0 : files.Count(f => f.SavedOk != true) // v0.9.3: сохрани каждый
                }, RpcJson.Default.TransactionResult);
            }
            finally {
                for (int i = ordered.Count - 1; i >= 0; i--) ordered[i].Gate.Release();
            }
        }
        finally { TxnGate.Release(); }
    }
}
