using System.Text;

namespace SmartEdit.Mcp.Core;

public enum FileKind { Utf8, Utf8Bom, Utf16Le, Utf16Be, NotUtf8 }

/// <summary>
/// Детект кодировки: BOM → UTF-8/UTF-16; иначе строгая валидация выборки UTF-8.
/// Не-UTF-8 файлы открываются только с явным encoding-параметром (или UTF-16 по BOM).
/// </summary>
public static class EncodingDetect {
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    static readonly UTF8Encoding LenientUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    public const long TranscodeLimit = 256L << 20; // RAM-путь (не-UTF-8) — до 256 МБ исходника

    public static FileKind Detect(string path) {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        Span<byte> head = stackalloc byte[4096];
        int n = fs.Read(head);
        var buf = head.Slice(0, n);

        if (buf.Length >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) return FileKind.Utf8Bom;
        if (buf.Length >= 2 && buf[0] == 0xFF && buf[1] == 0xFE) return FileKind.Utf16Le;
        if (buf.Length >= 2 && buf[0] == 0xFE && buf[1] == 0xFF) return FileKind.Utf16Be;

        // строгая валидация выборки сканером (без исключений). Хвост мог оборвать мультибайт на границе
        // среза (баг до v0.5.1: кириллица, начавшаяся ровно на validLen-1, роняла детект) — обрыв
        // на границе допустим; битый байт/continuation внутри — честный NotUtf8.
        int validLen = buf.Length >= 4096 ? buf.Length - 3 : buf.Length;
        return Utf8PrefixOk(buf.Slice(0, validLen)) ? FileKind.Utf8 : FileKind.NotUtf8;
    }

    /// <summary>Весь ли срез — корректный UTF-8 (обрыв последовательности на самом конце допустим).</summary>
    static bool Utf8PrefixOk(ReadOnlySpan<byte> b) {
        int i = 0;
        while (i < b.Length) {
            byte c = b[i];
            int len = c < 0x80 ? 1 : c >= 0xF0 ? 4 : c >= 0xE0 ? 3 : c >= 0xC2 ? 2 : 0;
            if (len == 0) return false;                       // одиночный continuation / 0xC0-0xC1 / 0xF5+
            if (i + len > b.Length) return true;              // обрыв ровно на границе среза — не судим
            for (int k = 1; k < len; k++)
                if ((b[i + k] & 0xC0) != 0x80) return false;  // битый continuation внутри среза
            i += len;
        }
        return true;
    }

    /// <summary>Каноническое имя по пользовательскому алиасу.</summary>
    public static string CanonicalName(string? name) => (name ?? "").Trim().ToLowerInvariant() switch {
        "" or "utf8" or "utf-8" => "utf-8",
        "utf8-bom" or "utf-8-bom" or "utf8bom" => "utf-8-bom",
        "utf16" or "utf-16" or "utf16le" or "utf-16le" => "utf-16le",
        "utf16be" or "utf-16be" => "utf-16be",
        "cp1251" or "windows-1251" or "1251" or "ansi" => "cp1251",
        _ => throw new ArgumentException($"unknown encoding '{name}' (use utf-8 | utf-8-bom | cp1251 | utf-16le | utf-16be)"),
    };

    /// <summary>
    /// Транскод не-UTF-8 файла в UTF-8 в RAM (MemorySource). Лимит — TranscodeLimit.
    /// Возвращает (utf8-байты, каноническое имя исходной кодировки).
    /// </summary>
    public static (byte[] Utf8, string Canonical) LoadAsUtf8(string path, string canonical) {
        var fi = new FileInfo(path);
        if (fi.Length > TranscodeLimit)
            throw new IOException($"non-UTF-8 files above {TranscodeLimit >> 20} MB are not supported (convert to UTF-8 first)");

        byte[] raw = File.ReadAllBytes(path);
        string text = canonical switch {
            "utf-16le" => DecodeUtf16(raw, bigEndian: false),
            "utf-16be" => DecodeUtf16(raw, bigEndian: true),
            "cp1251" => Encoding.GetEncoding(1251,
                EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(raw),
            "utf-8" => StrictUtf8.GetString(raw),
            "utf-8-bom" => StrictUtf8.GetString(raw.AsSpan(3)),
            _ => throw new ArgumentException($"'{canonical}' does not need transcoding"),
        };
        return (LenientUtf8.GetBytes(text), canonical);
    }

    static string DecodeUtf16(byte[] raw, bool bigEndian) {
        int skip = raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE && !bigEndian ? 2
                 : raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF && bigEndian ? 2
                 : 0;
        var enc = new UnicodeEncoding(bigEndian, byteOrderMark: false, throwOnInvalidBytes: true);
        return enc.GetString(raw.AsSpan(skip));
    }

    /// <summary>Кодировка для записи на диск при save.</summary>
    public static Encoding OutputEncoding(string canonical) => canonical switch {
        "utf-8" => new UTF8Encoding(false),
        "utf-8-bom" => new UTF8Encoding(true),
        "utf-16le" => new UnicodeEncoding(false, true),
        "utf-16be" => new UnicodeEncoding(true, true),
        "cp1251" => Encoding.GetEncoding(1251, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        _ => new UTF8Encoding(false),
    };
}
