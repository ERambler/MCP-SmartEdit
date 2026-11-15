using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

sealed class SaveTool : ToolBase {
    public override string Name => "save";
    public override string Description =>
        "Write edits to disk: atomic temp+rename, .smartedit-bak backup (backup=true), rejects if the file changed on disk since open; force=true DISCARDS the on-disk version (prefer show + re-apply). " +
        "encoding: 'keep' (default — original encoding/BOM preserved) or 'utf8' (no BOM, converts). Session stays open; rev does NOT change (save does not renumber lines). " +
        "gate/v1 (.cs/.js/.ts/.tsx/.py/.sh): BLOCKED with kind syntax_gate if the edit introduced NEW syntax errors vs baseline (old errors never block); fix them or force=true. " +
        "Result carries gate status + new errors (kind error|missing, 1-based row/col; missing names the absent token).";
    public override string InputSchemaJson => """
        {"type":"object","properties":{
          "session_id":{"type":"string","description":"from a previous response; optional if path given"},"path":{"type":"string","description":"open-by-path: skip open entirely"},
          "backup":{"type":"boolean","description":"default true"},
          "encoding":{"type":"string","enum":["keep","utf8"],"description":"default keep (original encoding)"},
          "force":{"type":"boolean","description":"overwrite despite external changes, default false"}
        },"required":[]}
        """;

    public override async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        var att = Sessions.Resolve(args.GetNullableString("session_id"), args.GetNullableString("path"));
        var s = att.Doc;
        bool backup = args.GetBool("backup", true);
        string encoding = args.GetString("encoding", "keep");
        bool force = args.GetBool("force");
        var outcome = await LockedAsync(s, () => s.Save(backup, encoding, force, att.Id));
        return Json(new SaveResult {
            BytesWritten = outcome.BytesWritten, BackupPath = outcome.BackupPath, Hash = outcome.Hash, Encoding = outcome.Encoding,
            Gate = outcome.GateStatus,
            GateErrors = (outcome.GateNew ?? []).Select(d => new GateErrorDto {
                Kind = d.Kind, Token = d.Token, Row = d.Row, Col = d.Col, Fragment = d.Fragment,
            }).ToArray(),
        }, RpcJson.Default.SaveResult);
    }
}

public sealed class SaveResult {
    public long BytesWritten { get; init; }
    public string? BackupPath { get; init; }
    public string Hash { get; init; } = "";
    public string Encoding { get; init; } = "";
    /// <summary>gate/v1: off|pass|forced|degraded|skipped_too_large|off_disabled|off_no_grammar. forced = сохранено с новыми ошибками через force.</summary>
    public string Gate { get; init; } = "off";
    public GateErrorDto[] GateErrors { get; init; } = []; // новые синтаксические ошибки (инфо при pass, список при forced)
}
