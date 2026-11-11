using System.Buffers;

namespace SmartEdit.Mcp.Core;

/// <summary>
/// Сканер переводов строк: собирает стартовые офсеты всех строк (0x0A-семантика —
/// безопасно на уровне байтов UTF-8: байт 0x0A никогда не встречается внутри многобайтовых
/// последовательностей). Параллельный по кускам — ~1с/ГБ.
/// </summary>
public static class LineScanner {
    /// <summary>start-офсеты строк (позиция сразу после предыдущего \n); строка без завершающего \n в конце тоже считается.</summary>
    public static (long[] Starts, bool Crlf) Scan(ByteSource src, long from) {
        long len = src.Length;
        if (len <= from) return (Array.Empty<long>(), false);

        long total = len - from;
        int chunks = (int)Math.Clamp(total / (4L << 20), 1, Environment.ProcessorCount);
        long step = (total + chunks - 1) / chunks;
        var hitsPerChunk = new List<long>[chunks];

        Parallel.For(0, chunks, i => {
            long a = from + (long)i * step;
            long b = Math.Min(a + step, len);
            var hits = new List<long>(capacity: 4096);
            byte[] buf = ArrayPool<byte>.Shared.Rent(1 << 20);
            try {
                long pos = a;
                while (pos < b) {
                    int n = (int)Math.Min(buf.Length, b - pos);
                    src.Read(pos, buf.AsSpan(0, n));
                    for (int j = 0; j < n; j++)
                        if (buf[j] == 0x0A) hits.Add(pos + j);
                    pos += n;
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
            hitsPerChunk[i] = hits;
        });

        int nl = hitsPerChunk.Sum(l => l.Count);
        var starts = new long[nl + 1];
        starts[0] = from;
        int k = 1;
        // CRLF-детект: большинство по выборке первых 400 переводов
        int cr = 0, lf = 0, sampled = 0;
        for (int i = 0; i < chunks; i++)
            foreach (long p in hitsPerChunk[i]) {
                if (p + 1 < len && k < starts.Length) starts[k++] = p + 1;
                if (sampled < 400 && p > from) {
                    sampled++;
                    if (src.ByteAt(p - 1) == 0x0D) cr++; else lf++;
                }
            }
        if (k < starts.Length) Array.Resize(ref starts, k);
        return (starts, sampled > 0 && cr > lf);
    }
}
