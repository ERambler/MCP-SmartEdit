using System.Text.Json;
using SmartEdit.Mcp.Core;
using SmartEdit.Mcp.Core.SymbolScan;
using SmartEdit.Mcp.Rpc;

namespace SmartEdit.Mcp.Tools;

sealed class FileInfoTool : ToolBase {
    public override string Name => "file_info";
    public override string Description =>
        "File attributes without opening a session: size, encoding (BOM/UTF-8 validation sample), newline style, " +
        "timestamps, read-only/hidden, is-code flag. Cheap — reads only the first 64KB.";
    public override string InputSchemaJson =>
        """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""";

    public override Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) {
        string path = args.GetString("path");
        if (path == "" || !File.Exists(path)) throw new FileNotFoundException("file not found: " + path);
        path = System.IO.Path.GetFullPath(path);
        var fi = new FileInfo(path);

        string encoding = "unknown"; bool isUtf8 = false; string newlines = "none";
        try {
            var kind = EncodingDetect.Detect(path);
            (encoding, isUtf8) = kind switch {
                FileKind.Utf8 => ("utf-8", true),
                FileKind.Utf8Bom => ("utf-8-bom", true),
                FileKind.Utf16Le => ("utf-16le", false),
                FileKind.Utf16Be => ("utf-16be", false),
                _ => ("not-utf8 (pass encoding to open: cp1251?)", false),
            };
            using var fs = File.OpenRead(path);
            var buf = new byte[Math.Min(fs.Length, 64 * 1024)];
            int n = fs.Read(buf);
            int lf = 0, crlf = 0;
            for (int i = 0; i < n; i++) {
                if (buf[i] == 0x0A) { lf++; if (i > 0 && buf[i - 1] == 0x0D) crlf++; }
            }
            newlines = lf == 0 ? "none" : crlf * 2 >= lf ? "crlf" : "lf";
        }
        catch (IOException) { }

        return Task.FromResult(Json(new FileInfoResult {
            Path = path,
            Bytes = fi.Length,
            Encoding = encoding,
            Newlines = newlines,
            ModifiedUtc = fi.LastWriteTimeUtc,
            CreatedUtc = fi.CreationTimeUtc,
            ReadOnly = fi.IsReadOnly,
            Hidden = (fi.Attributes & FileAttributes.Hidden) != 0,
            IsCode = SymbolDispatcher.IsCode(path),
            IsUtf8 = isUtf8,
        }, RpcJson.Default.FileInfoResult));
    }
}

public sealed class FileInfoResult {
    public string Path { get; init; } = "";
    public long Bytes { get; init; }
    public string Encoding { get; init; } = "";
    public string Newlines { get; init; } = "";
    public DateTime ModifiedUtc { get; init; }
    public DateTime CreatedUtc { get; init; }
    public bool ReadOnly { get; init; }
    public bool Hidden { get; init; }
    public bool IsCode { get; init; }
    public bool IsUtf8 { get; init; }
}
