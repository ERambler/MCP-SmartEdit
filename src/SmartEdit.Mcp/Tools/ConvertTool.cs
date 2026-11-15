using System.Text;
using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

sealed class ConvertTool : HiddenTool {
    public override string Name => "convert";
    public override string Description =>
        "Convert file encoding in place (atomic, .smartedit-bak backup kept). " +
        "from: autodetect (BOM/UTF-8; non-UTF-8 needs explicit 'cp1251' etc). to: utf-8 | utf-8-bom | utf-16le | utf-16be | cp1251. " +
        "utf-8 output has no BOM unless utf-8-bom.";
    public override string InputSchemaJson => """
        {"type":"object","properties":{
          "path":{"type":"string"},
          "to":{"type":"string","enum":["utf-8","utf-8-bom","utf-16le","utf-16be","cp1251"]},
          "from":{"type":"string","description":"source encoding when autodetect can't (cp1251|utf-16le|utf-16be)"}
        },"required":["path","to"]}
        """;

    public override Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        string path = args.GetString("path");
        if (path == "" || !File.Exists(path)) throw new FileNotFoundException("file not found: " + path);
        path = System.IO.Path.GetFullPath(path);
        string to = EncodingDetect.CanonicalName(args.GetString("to"));
        string? fromArg = args.GetNullableString("from");

        var kind = EncodingDetect.Detect(path);
        string from = fromArg is null
            ? kind switch {
                FileKind.Utf8 => "utf-8",
                FileKind.Utf8Bom => "utf-8-bom",
                FileKind.Utf16Le => "utf-16le",
                FileKind.Utf16Be => "utf-16be",
                _ => throw new IOException("file is not valid UTF-8 and has no BOM — pass from:'cp1251' (or other) explicitly"),
            }
            : EncodingDetect.CanonicalName(fromArg);
        if (from == to) return Task.FromResult(Json(
            new ConvertResult { Path = path, From = from, To = to, Bytes = new FileInfo(path).Length }, RpcJson.Default.ConvertResult));

        var (utf8, canonicalFrom) = EncodingDetect.LoadAsUtf8(path, from);
        var enc = EncodingDetect.OutputEncoding(to);
        byte[] outBytes = enc.GetPreamble().Concat(enc.GetBytes(Encoding.UTF8.GetString(utf8))).ToArray();

        string temp = path + ".smartedit-tmp";
        File.WriteAllBytes(temp, outBytes);
        try { File.Copy(path, path + ".smartedit-bak", overwrite: true); } catch (IOException) { }
        File.Move(temp, path, overwrite: true);
        return Task.FromResult(Json(
            new ConvertResult { Path = path, From = canonicalFrom, To = to, Bytes = outBytes.LongLength }, RpcJson.Default.ConvertResult));
    }
}

public sealed class ConvertResult {
    public string Path { get; init; } = "";
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public long Bytes { get; init; }
}
