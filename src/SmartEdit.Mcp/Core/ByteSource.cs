using System.IO.MemoryMappedFiles;

namespace SmartEdit.Mcp.Core;

/// <summary>
/// Байтовый источник базового файла. Два варианта: memory-mapped (гигантские UTF-8 файлы,
/// стиль Notepad++ — в ОЗУ держим только страницу) и массив в RAM (результат транскода
/// CP1251/UTF-16 → UTF-8). К правкам это не имеет отношения: правки всегда в piece table.
/// </summary>
public abstract class ByteSource : IDisposable {
    public abstract long Length { get; }
    /// <summary>Прочитать dst.Length байт с offset (offset+length обязан быть внутри файла).</summary>
    public abstract void Read(long offset, Span<byte> dst);
    public abstract byte ByteAt(long offset);
    public abstract void Dispose();
}

public sealed unsafe class MmapSource : ByteSource {
    readonly FileStream _fs;
    readonly MemoryMappedFile? _mmf;   // null для пустого файла
    readonly MemoryMappedViewAccessor? _view;
    readonly byte* _ptr;
    readonly long _length;

    public MmapSource(string path) {
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16);
        _length = _fs.Length;
        if (_length == 0) return; // пустой файл: MMF не создаётся («positive capacity»)
        _mmf = MemoryMappedFile.CreateFromFile(_fs, null, _length, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
        _view = _mmf.CreateViewAccessor(0, _length, MemoryMappedFileAccess.Read);
        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _ptr = p;
    }

    public override long Length => _length;
    public override void Read(long offset, Span<byte> dst) {
        if (_ptr == null) throw new InvalidOperationException("read on empty source");
        new Span<byte>(_ptr + offset, dst.Length).CopyTo(dst);
    }
    public override byte ByteAt(long offset) => _ptr == null ? (byte)0 : _ptr[offset];

    public override void Dispose() {
        if (_view is not null) {
            try { _view.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { /* уже Released — не падать */ }
            _view.Dispose();
        }
        _mmf?.Dispose();
        _fs.Dispose();
    }
}

public sealed class MemorySource(byte[] data) : ByteSource {
    public override long Length => data.Length;
    public override void Read(long offset, Span<byte> dst)
        => data.AsSpan((int)offset, dst.Length).CopyTo(dst);
    public override byte ByteAt(long offset) => data[offset];
    public override void Dispose() { /* ничего: GC заберёт */ }
}
