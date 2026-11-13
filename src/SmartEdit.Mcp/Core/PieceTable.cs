using System.Text;

namespace SmartEdit.Mcp.Core;

/// <summary>
/// Кусок логического документа. BufferIndex = -1 → строки базы (FirstLine — номер строки
/// в исходном файле); иначе индекс в списке вставленных буферов (FirstLine всегда 0).
/// Все правки выровнены по границам строк, поэтому кусок задаётся диапазоном строк.
/// </summary>
public sealed record Piece(int BufferIndex, int FirstLine, int LineCount);

/// <summary>
/// Piece table поверх неизменяемой базы: правки — вставленные UTF-8 буферы.
/// Кусков всегда O(числа правок) — отображение строки на кусок линейным сканом дёшево.
/// </summary>
public sealed class PieceTable(int baseLineCount) {
    // #21: нулевая база (create нового файла) = НИ ОДНОГО куска; кусок(-1,0,0) давал
    // IndexOutOfRange в BasePieceSpan по пустому _baseStarts
    List<Piece> _pieces = baseLineCount > 0 ? [new(-1, 0, baseLineCount)] : [];
    public IReadOnlyList<Piece> Pieces => _pieces;
    public int LineCount { get; private set; } = baseLineCount;

    public (Piece Piece, int LineInPiece) MapLine(int logicalLine) {
        int acc = 0;
        foreach (var p in _pieces) {
            if (logicalLine < acc + p.LineCount) return (p, logicalLine - acc);
            acc += p.LineCount;
        }
        throw new ArgumentOutOfRangeException(nameof(logicalLine));
    }

    /// <summary>
    /// Заменить строки [startLine, startLine+count) на insertLineCount строк буфера bufferIndex
    /// (insertLineCount=0 → чистое удаление; count=0 → чистая вставка, startLine = позиция).
    /// </summary>
    public void ReplaceRange(int startLine, int count, int bufferIndex, int insertLineCount) {
        if (startLine < 0 || count < 0 || startLine + count > LineCount)
            throw new ArgumentOutOfRangeException($"range [{startLine}, {startLine + count}) is outside 0..{LineCount}");
        int end = startLine + count;
        bool placed = false;
        var neu = new List<Piece>(_pieces.Count + 2);
        int acc = 0;
        foreach (var p in _pieces) {
            int pStart = acc, pEnd = acc + p.LineCount;
            // count>0: кусок пересекает заменяемый диапазон; count==0: кусок содержит точку
            // вставки (или начинается ровно на ней — тогда вставка перед ним)
            bool hit = count > 0
                ? pStart < end && pEnd > startLine
                : pStart <= startLine && startLine < pEnd || pStart == startLine;
            if (hit) {
                int head = startLine - pStart;
                if (head > 0) neu.Add(p with { LineCount = head });
                if (insertLineCount > 0 && !placed) neu.Add(new Piece(bufferIndex, 0, insertLineCount));
                int tail = pEnd - end;
                if (tail > 0) neu.Add(p with { FirstLine = p.FirstLine + (p.LineCount - tail), LineCount = tail });
                placed = true;
            }
            else neu.Add(p);
            acc = pEnd;
        }
        if (!placed && insertLineCount > 0) neu.Add(new Piece(bufferIndex, 0, insertLineCount)); // вставка в самый конец
        _pieces = neu;
        LineCount = neu.Sum(p => p.LineCount);
    }

    public void Reset(int newBaseLineCount) {
        _pieces = [new(-1, 0, newBaseLineCount)];
        LineCount = newBaseLineCount;
    }
}

/// <summary>Вставленный текст: UTF-8 байты + стартовые офсеты строк внутри буфера.</summary>
public sealed record InsertBuffer(byte[] Data, long[] Starts);

public static class InsertBuffers {
    /// <summary>Строки склеиваются стилем файла, завершающий перевод строки добавляется каждой (включая последнюю).</summary>
    public static InsertBuffer Build(IReadOnlyList<string> lines, bool crlf) {
        int nl = crlf ? 2 : 1;
        var starts = new long[lines.Count];
        var enc = Encoding.UTF8;
        int total = lines.Sum(l => enc.GetByteCount(l)) + lines.Count * nl;
        var data = new byte[total];
        int off = 0;
        for (int i = 0; i < lines.Count; i++) {
            starts[i] = off;
            off += enc.GetBytes(lines[i], data.AsSpan(off));
            if (crlf) { data[off++] = (byte)'\r'; data[off++] = (byte)'\n'; }
            else data[off++] = (byte)'\n';
        }
        return new InsertBuffer(data, starts);
    }

    /// <summary>Диапазон байтов содержимого строки (без перевода строки) внутри буфера.</summary>
    public static (int Start, int Len) SpanOf(InsertBuffer b, int line, int nlLen) {
        long s = b.Starts[line];
        long next = line + 1 < b.Starts.Length ? b.Starts[line + 1] : b.Data.Length;
        return ((int)s, (int)(next - s - nlLen));
    }
}
