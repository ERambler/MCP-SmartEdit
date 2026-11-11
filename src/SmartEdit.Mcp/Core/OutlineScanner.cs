using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace SmartEdit.Mcp.Core;

public sealed record OutlineEntry(int Line, int Level, string Title);

/// <summary>
/// Outline: markdown-заголовки, явные маркеры секций (<!-- sec: name --> / // @sec: name).
/// TODO/FIXME считаем тут же — один проход по базе при open. Для файлов >512 МБ отключён.
/// </summary>
public static partial class OutlineScanner {
    public const int MaxEntries = 500;
    public const long DisabledAbove = 512L << 20;

    [GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*$")]
    private static partial Regex MdHeading();
    [GeneratedRegex(@"<!--\s*sec:?\s*([^>]+?)\s*-->")]
    private static partial Regex HtmlMarker();
    [GeneratedRegex(@"^[/#;]{0,3}\s*@sec:?\s+(.+?)\s*$")]
    private static partial Regex LineMarker();

    static readonly UTF8Encoding LenientUtf8 = new(false, false);

    public static (OutlineEntry[] Outline, int TodoCount) Scan(ByteSource src, long[] starts, long baseLength) {
        if (src.Length > DisabledAbove) return (Array.Empty<OutlineEntry>(), 0);
        int n = starts.Length;
        if (n == 0) return (Array.Empty<OutlineEntry>(), 0);

        int chunks = (int)Math.Clamp(n / 20_000L, 1, Environment.ProcessorCount);
        int step = (n + chunks - 1) / chunks;
        var entriesPer = new List<OutlineEntry>[chunks];
        var todosPer = new int[chunks];

        Parallel.For(0, chunks, i => {
            int a = i * step;
            int b = Math.Min(a + step, n);
            var entries = new List<OutlineEntry>();
            int todo = 0;
            byte[] buf = ArrayPool<byte>.Shared.Rent(300);
            try {
                for (int line = a; line < b; line++) {
                    long start = starts[line];
                    long next = line + 1 < n ? starts[line + 1] : baseLength;
                    int take = (int)Math.Min(Math.Max(next - start, 0), 300);
                    if (take == 0) continue;
                    src.Read(start, buf.AsSpan(0, take));
                    // lenient-декод префикса: маркеры/заголовки ASCII, битые хвосты не мешают
                    string s = LenientUtf8.GetString(buf.AsSpan(0, take)).TrimEnd('\r', '\n');

                    if (s.Contains("TODO") || s.Contains("FIXME")) todo++;
                    if (entries.Count < 4096) {
                        var md = MdHeading().Match(s);
                        if (md.Success) {
                            entries.Add(new OutlineEntry(line + 1, md.Groups[1].Value.Length, md.Groups[2].Value.Trim()));
                            continue;
                        }
                        var html = HtmlMarker().Match(s);
                        if (html.Success) { entries.Add(new OutlineEntry(line + 1, 9, html.Groups[1].Value.Trim())); continue; }
                        var lm = LineMarker().Match(s);
                        if (lm.Success) entries.Add(new OutlineEntry(line + 1, 9, lm.Groups[1].Value.Trim()));
                    }
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
            entriesPer[i] = entries;
            todosPer[i] = todo;
        });

        var all = entriesPer.SelectMany(l => l).OrderBy(e => e.Line).Take(MaxEntries).ToArray();
        return (all, Math.Min(todosPer.Sum(), 100_000));
    }
}
