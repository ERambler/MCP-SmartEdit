using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

/// <summary>#21 (консилиум 2026-11-14): рождение файла внутри SmartEdit — RAM до save, гейт участвует.</summary>
sealed class CreateTool : ToolBase {
    public override string Name => "create";
    public override string Description =>
        "Create a NEW file as a RAM session (nothing touches disk until save). content seeds it (multi-line, ≤2MB, optional). " +
        "Existing file or live session on the path → already_exists (create never truncates). Advisory syntax check of the seed is in the response; " +
        "the FIRST save gates hard (empty baseline = every syntax error is NEW; fix in RAM, save once — force=true is the conscious override). " +
        "create_parents:true creates missing directories at save. New files: UTF-8 no BOM, LF.";
    public override string InputSchemaJson => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"absolute path of the NEW file"},
          "content":{"type":"string","description":"seed content (multi-line, default empty)"},
          "create_parents":{"type":"boolean","description":"default false; true = create missing parent directories at save"},
          "note":{"type":"string","description":"presence annotation for peers"},
          "attention":{"type":"boolean","description":"#5 opt-in seen-tracking for the new document"}
        },"required":["path"]}
        """;

    public override Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        string path = args.GetString("path", "");
        if (path.Length == 0) throw new ToolException(ToolException.InvalidArgs, "path is required");
        string content = args.GetString("content", "");
        if (content.Length > (2 << 20))
            throw new ToolException(ToolException.InvalidArgs, $"content is {content.Length / 1024}KB — cap 2MB per create; seed empty and build via edit_batch");
        bool createParents = args.GetBool("create_parents");
        var (att, _) = Sessions.Create(path, content, createParents, args.GetNullableString("note"));
        var s = att.Doc;
        if (args.GetBool("attention")) s.Attention = true; // #5

        // advisory-парс сеяного контента (#21): мгновенная обратная связь, НЕ блок
        string advisory = "off";
        GateErrorDto[] advisoryErrors = [];
        if (content.Length > 0 && SyntaxGate.Applies(s.Path)) {
            var res = SyntaxGate.Analyze(s.Path, content);
            if (res.Status == "pass" && res.Errors.Count > 0) {
                advisory = "new_errors";
                advisoryErrors = [.. res.Errors.Take(10).Select(d => new GateErrorDto {
                    Kind = d.Kind, Token = d.Token, Row = d.Row, Col = d.Col, Fragment = d.Fragment })];
            }
            else advisory = res.Status;
        }
        return Task.FromResult(Json(new CreateResult {
            SessionId = att.Id, Path = s.Path, Rev = s.Rev, TotalLines = s.TotalLines,
            SeededLines = s.TotalLines, Created = true,
            Gate = advisory, GateErrors = advisoryErrors,
            Unsaved = true, // диск не тронут: save(path) материализует файл
        }, RpcJson.Default.CreateResult));
    }
}

/// <summary>#21 (v0.12): рождение файла — RAM-сессия, диск трогает только save.</summary>
public sealed class CreateResult {
    public string SessionId { get; init; } = "";
    public string Path { get; init; } = "";
    public int Rev { get; init; }
    public int TotalLines { get; init; }
    public int SeededLines { get; init; }
    public bool Created { get; init; }
    /// <summary>Advisory-парс сеяного content: off | pass | new_errors (+gateErrors[]) | degraded…</summary>
    public string Gate { get; init; } = "off";
    public GateErrorDto[] GateErrors { get; init; } = [];
    public bool Unsaved { get; init; } = true;   // save(path) материализует файл (первый save — жёсткий гейт)
}
