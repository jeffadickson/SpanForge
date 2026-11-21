using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SpanForge;

/// <summary>
/// Mutable, stack-only zero-allocation binary writer over <see cref="Span{T}"/>.
/// Symmetric counterpart to <see cref="ByteCursor"/> for roundtrip encoding without exceptions or heap allocations.
/// </summary>
public ref struct ByteWriter
{
    private readonly Span<byte> _buffer;
    private int _position;
    private Endianness _endianness;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ByteWriter(Span<byte> buffer, Endianness endianness = Endianness.Big)
    {
        _buffer = buffer;
        _position = 0;
        _endianness = endianness;
    }

    public readonly Span<byte> Buffer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _buffer;
    }

    public readonly ReadOnlySpan<byte> Written
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _buffer[.._position];
    }

    public readonly Span<byte> WrittenSpan
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _buffer[.._position];
    }

    public readonly Span<byte> Remaining
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _buffer[_position..];
    }

    public readonly int Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _buffer.Length;
    }

    public readonly int RemainingLength
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _buffer.Length - _position;
    }

    public int Position
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get => _position;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            if ((uint)value > (uint)_buffer.Length) ThrowPositionOutOfRange();
            _position = value;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowPositionOutOfRange() =>
        throw new ArgumentOutOfRangeException("value");

    public Endianness Endianness
    {
        readonly get => _endianness;
        set => _endianness = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Checkpoint Save() => new(_position, _endianness);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset(Checkpoint checkpoint)
    {
        _position = checkpoint.Position;
        _endianness = checkpoint.Endianness;
    }

    // -------------------------------------------------------------------------
    // Fixed-width integer & floating-point writers
    // -------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteU8(byte value)
    {
        int pos = _position;
        if (pos >= _buffer.Length) return false;
        Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos) = value;
        _position = pos + 1;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteI8(sbyte value) => TryWriteU8(unchecked((byte)value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteBool(bool value) => TryWriteU8(value ? (byte)1 : (byte)0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteU16BE(ushort value)
    {
        int pos = _position;
        if (_buffer.Length - pos < 2) return false;
        if (BitConverter.IsLittleEndian) value = BinaryPrimitives.ReverseEndianness(value);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos), value);
        _position = pos + 2;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteU16LE(ushort value)
    {
        int pos = _position;
        if (_buffer.Length - pos < 2) return false;
        if (!BitConverter.IsLittleEndian) value = BinaryPrimitives.ReverseEndianness(value);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos), value);
        _position = pos + 2;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteU16(ushort value) =>
        _endianness == Endianness.Big ? TryWriteU16BE(value) : TryWriteU16LE(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteI16BE(short value) => TryWriteU16BE(unchecked((ushort)value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteI16LE(short value) => TryWriteU16LE(unchecked((ushort)value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteI16(short value) =>
        _endianness == Endianness.Big ? TryWriteI16BE(value) : TryWriteI16LE(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteU32BE(uint value)
    {
        int pos = _position;
        if (_buffer.Length - pos < 4) return false;
        if (BitConverter.IsLittleEndian) value = BinaryPrimitives.ReverseEndianness(value);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos), value);
        _position = pos + 4;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteU32LE(uint value)
    {
        int pos = _position;
        if (_buffer.Length - pos < 4) return false;
        if (!BitConverter.IsLittleEndian) value = BinaryPrimitives.ReverseEndianness(value);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos), value);
        _position = pos + 4;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteU32(uint value) =>
        _endianness == Endianness.Big ? TryWriteU32BE(value) : TryWriteU32LE(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteI32BE(int value) => TryWriteU32BE(unchecked((uint)value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteI32LE(int value) => TryWriteU32LE(unchecked((uint)value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteI32(int value) =>
        _endianness == Endianness.Big ? TryWriteI32BE(value) : TryWriteI32LE(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteU64BE(ulong value)
    {
        int pos = _position;
        if (_buffer.Length - pos < 8) return false;
        if (BitConverter.IsLittleEndian) value = BinaryPrimitives.ReverseEndianness(value);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos), value);
        _position = pos + 8;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteU64LE(ulong value)
    {
        int pos = _position;
        if (_buffer.Length - pos < 8) return false;
        if (!BitConverter.IsLittleEndian) value = BinaryPrimitives.ReverseEndianness(value);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos), value);
        _position = pos + 8;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteU64(ulong value) =>
        _endianness == Endianness.Big ? TryWriteU64BE(value) : TryWriteU64LE(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteI64BE(long value) => TryWriteU64BE(unchecked((ulong)value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteI64LE(long value) => TryWriteU64LE(unchecked((ulong)value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteI64(long value) =>
        _endianness == Endianness.Big ? TryWriteI64BE(value) : TryWriteI64LE(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteF32BE(float value) => TryWriteU32BE(BitConverter.SingleToUInt32Bits(value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteF32LE(float value) => TryWriteU32LE(BitConverter.SingleToUInt32Bits(value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteF32(float value) =>
        _endianness == Endianness.Big ? TryWriteF32BE(value) : TryWriteF32LE(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteF64BE(double value) => TryWriteU64BE(BitConverter.DoubleToUInt64Bits(value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteF64LE(double value) => TryWriteU64LE(BitConverter.DoubleToUInt64Bits(value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteF64(double value) =>
        _endianness == Endianness.Big ? TryWriteF64BE(value) : TryWriteF64LE(value);

    // -------------------------------------------------------------------------
    // Variable-length integer writers (LEB128, ZigZag, QUIC RFC 9000)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Writes an unsigned LEB128 varint (1 to 10 bytes). Rolls back on insufficient space.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteVarUInt64(ulong value)
    {
        int start = _position;
        int pos = start;
        int len = _buffer.Length;
        ref byte baseRef = ref MemoryMarshal.GetReference(_buffer);

        while (value >= 0x80)
        {
            if (pos >= len)
            {
                _position = start;
                return false;
            }

            Unsafe.Add(ref baseRef, (nint)(uint)pos++) = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }

        if (pos >= len)
        {
            _position = start;
            return false;
        }

        Unsafe.Add(ref baseRef, (nint)(uint)pos++) = (byte)value;
        _position = pos;
        return true;
    }

    /// <summary>
    /// Writes a ZigZag-encoded signed LEB128 varint (as used in Protobuf <c>sint64</c> and Avro).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteVarInt64(long value)
    {
        ulong zigzag = (ulong)((value << 1) ^ (value >> 63));
        return TryWriteVarUInt64(zigzag);
    }

    /// <summary>
    /// Writes a QUIC (RFC 9000 Section 16) 2-bit-prefixed big-endian variable-length integer (1, 2, 4, or 8 bytes).
    /// Returns <see langword="false"/> if <paramref name="value"/> exceeds the 62-bit QUIC maximum or space is insufficient.
    /// </summary>
    public bool TryWriteQuicVarInt64(ulong value)
    {
        if (value <= 0x3FUL)
        {
            return TryWriteU8((byte)value);
        }

        if (value <= 0x3FFFUL)
        {
            return TryWriteU16BE((ushort)(0x4000UL | value));
        }

        if (value <= 0x3FFF_FFFFUL)
        {
            return TryWriteU32BE((uint)(0x8000_0000UL | value));
        }

        if (value <= 0x3FFF_FFFF_FFFF_FFFFUL)
        {
            return TryWriteU64BE(0xC000_0000_0000_0000UL | value);
        }

        return false;
    }

    // -------------------------------------------------------------------------
    // Raw bytes, padding, alignment, framing, and blittable writes
    // -------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteBytes(ReadOnlySpan<byte> data)
    {
        if (RemainingLength < data.Length) return false;
        data.CopyTo(_buffer.Slice(_position, data.Length));
        _position += data.Length;
        return true;
    }

    public bool TryWritePad(int count, byte fill = 0)
    {
        if (count < 0 || RemainingLength < count) return false;
        _buffer.Slice(_position, count).Fill(fill);
        _position += count;
        return true;
    }

    /// <summary>
    /// Advances to the next power-of-two byte boundary, writing <paramref name="fill"/> bytes.
    /// </summary>
    public bool TryAlign(int alignment, byte fill = 0)
    {
        if (alignment <= 0 || (alignment & (alignment - 1)) != 0) return false;
        int rem = _position & (alignment - 1);
        if (rem == 0) return true;
        int pad = alignment - rem;
        return TryWritePad(pad, fill);
    }

    /// <summary>
    /// Writes a length prefix followed by <paramref name="data"/>. Rolls back if prefix overflows or buffer is too small.
    /// </summary>
    public bool TryWriteLengthPrefixedBytes(LengthPrefix prefix, ReadOnlySpan<byte> data)
    {
        var cp = Save();
        bool headerOk;
        switch (prefix)
        {
            case LengthPrefix.U8:
                if (data.Length > byte.MaxValue) return false;
                headerOk = TryWriteU8((byte)data.Length);
                break;

            case LengthPrefix.U16:
                if (data.Length > ushort.MaxValue) return false;
                headerOk = TryWriteU16((ushort)data.Length);
                break;

            case LengthPrefix.U32:
                headerOk = TryWriteU32((uint)data.Length);
                break;

            case LengthPrefix.VarUInt64:
                headerOk = TryWriteVarUInt64((ulong)data.Length);
                break;

            default:
                return false;
        }

        if (!headerOk || !TryWriteBytes(data))
        {
            Reset(cp);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Zero-copy unmanaged struct encode via <see cref="MemoryMarshal.Write{T}(Span{byte}, in T)"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteBlittable<T>(in T value) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        if (RemainingLength < size) return false;
        MemoryMarshal.Write(_buffer.Slice(_position, size), in value);
        _position += size;
        return true;
    }

    /// <summary>
    /// Creates an MSB-first <see cref="BitWriter"/> positioned at the current byte offset.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly BitWriter CreateBitWriter() => new(_buffer[_position..], _position);
}

/// <summary>
/// C# 15 extension helpers for creating <see cref="ByteWriter"/> instances from writable spans and arrays.
/// </summary>
public static class ByteWriterExtensions
{
    extension(Span<byte> span)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ByteWriter AsWriter(Endianness endianness = Endianness.Big) => new(span, endianness);
    }

    extension(byte[] array)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ByteWriter AsWriter(Endianness endianness = Endianness.Big) => new(array, endianness);
    }
}
