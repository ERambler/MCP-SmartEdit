namespace SmartEdit.Mcp.Core;

/// <summary>
/// xxHash64 (seed 0): свой — ноль внешних зависимостей, скорость ~RAM-потока.
/// One-shot + streaming (32-байтовые «полосы» по спецификации).
/// </summary>
public static class XxHash64 {
    // static readonly, не const: константная арифметика P1+P2 ловит CS0220 (checked-контекст)
    static readonly ulong P1 = 11400714785074694791UL, P2 = 14029467366897019727UL,
                          P3 = 1609587929392839161UL,  P4 = 9650029242287828579UL,
                          P5 = 2870177450012600261UL;

    public static ulong Hash(ReadOnlySpan<byte> data)
        => new Streamer().Feed(data).Finish();

    /// <summary>Потоковый подсчёт: Feed по кускам, Finish один раз в конце.</summary>
    public sealed class Streamer {
        readonly ulong[] _v = [P1 + P2, P2, 0, unchecked(0 - P1)];
        readonly byte[] _staging = new byte[32];
        int _staged;
        long _totalLen;

        public Streamer Feed(ReadOnlySpan<byte> data) {
            _totalLen += data.Length;
            if (_staged > 0) {
                int take = Math.Min(32 - _staged, data.Length);
                data.Slice(0, take).CopyTo(_staging.AsSpan(_staged));
                _staged += take;
                data = data.Slice(take);
                if (_staged < 32) return this;
                ProcessLane(_staging);
                _staged = 0;
            }
            while (data.Length >= 32) { ProcessLane(data); data = data.Slice(32); }
            if (data.Length > 0) { data.CopyTo(_staging.AsSpan()); _staged = data.Length; }
            return this;
        }

        void ProcessLane(ReadOnlySpan<byte> b) {
            _v[0] = Round(_v[0], Read64(b, 0));
            _v[1] = Round(_v[1], Read64(b, 8));
            _v[2] = Round(_v[2], Read64(b, 16));
            _v[3] = Round(_v[3], Read64(b, 24));
        }

        public ulong Finish() {
            ulong h;
            if (_totalLen >= 32) {
                h = Rol(_v[0], 1) + Rol(_v[1], 7) + Rol(_v[2], 12) + Rol(_v[3], 18);
                h = Merge(h, _v[0]); h = Merge(h, _v[1]); h = Merge(h, _v[2]); h = Merge(h, _v[3]);
            }
            else h = P5;
            h += (ulong)_totalLen;
            var tail = _staging.AsSpan(0, _staged);
            int pos = 0;
            while (pos + 8 <= tail.Length) {
                h = Rol(h ^ Round(Read64(tail, pos), P1 + P2), 27) * P1 + P4;
                pos += 8;
            }
            if (pos + 4 <= tail.Length) {
                h = Rol(h ^ (Read32(tail, pos) * P1), 23) * P2 + P3;
                pos += 4;
            }
            while (pos < tail.Length) {
                h = Rol(h ^ (tail[pos] * P5), 11) * P1;
                pos++;
            }
            h ^= h >> 33; h *= P2; h ^= h >> 29; h *= P3; h ^= h >> 32;
            return h;
        }

        static ulong Round(ulong acc, ulong input) => Rol(acc + input * P2, 31) * P1;
        static ulong Merge(ulong acc, ulong val) => (acc ^ Round(0, val)) * P1 + P4;
        static ulong Rol(ulong v, int r) => v << r | v >> 64 - r;
        static ulong Read64(ReadOnlySpan<byte> b, int i) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(i));
        static uint Read32(ReadOnlySpan<byte> b, int i) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(i));
    }
}
