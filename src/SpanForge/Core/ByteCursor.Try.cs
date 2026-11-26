using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SpanForge;

public ref partial struct ByteCursor
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly FastStatus TryShortage(int remainingLength, int requiredBytes) =>
        _mode == InputMode.Complete
            ? FastStatus.Backtrack
            : TryShortageStreaming(requiredBytes - remainingLength);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FastStatus TryShortageStreaming(int missing) => FastStatus.IncompleteBytes(missing);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryTagByte(byte expected, out byte value)
    {
        int pos = _position;
        if (pos < _buffer.Length && Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos) == expected)
        {
            _position = pos + 1;
            value = expected;
            return FastStatus.Parsed;
        }

        Unsafe.SkipInit(out value);
        return pos >= _buffer.Length ? TryShortage(0, 1) : FastStatus.Backtrack;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryTag(ReadOnlySpan<byte> expected)
    {
        int start = _position;
        int rem = _buffer.Length - start;
        if (rem < expected.Length)
        {
            if (_mode == InputMode.Streaming && _buffer.Slice(start, rem).SequenceEqual(expected[..rem]))
            {
                return TryShortageStreaming(expected.Length - rem);
            }

            return FastStatus.Backtrack;
        }

        if (!_buffer.Slice(start, expected.Length).SequenceEqual(expected))
        {
            return FastStatus.Backtrack;
        }

        _position = start + expected.Length;
        return FastStatus.Parsed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryTake(int count, out ByteSlice value)
    {
        if (count < 0) { Unsafe.SkipInit(out value); return FastStatus.Backtrack; }
        int pos = _position;
        int rem = _buffer.Length - pos;
        if (rem < count) { Unsafe.SkipInit(out value); return TryShortage(rem, count); }
        value = new ByteSlice(pos, count);
        _position = pos + count;
        return FastStatus.Parsed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryTakeSpan(int count, out ReadOnlySpan<byte> value)
    {
        if (count < 0) { value = default; return FastStatus.Backtrack; }
        int pos = _position;
        int rem = _buffer.Length - pos;
        if (rem < count) { value = default; return TryShortage(rem, count); }
        value = MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos),
            count);
        _position = pos + count;
        return FastStatus.Parsed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TrySkip(int count)
    {
        if (count < 0) return FastStatus.Backtrack;
        int pos = _position;
        int rem = _buffer.Length - pos;
        if (rem < count) return TryShortage(rem, count);
        _position = pos + count;
        return FastStatus.Parsed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryReadU8(out byte value)
    {
        int pos = _position;
        if (pos >= _buffer.Length) { Unsafe.SkipInit(out value); return TryShortage(0, 1); }
        value = Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos);
        _position = pos + 1;
        return FastStatus.Parsed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryReadI8(out sbyte value)
    {
        var st = TryReadU8(out byte raw);
        value = unchecked((sbyte)raw);
        return st;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryReadBool(out bool value)
    {
        var st = TryReadU8(out byte raw);
        value = raw != 0;
        return st;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryReadU16BE(out ushort value)
    {
        int pos = _position;
        int rem = _buffer.Length - pos;
        if (rem < 2) { Unsafe.SkipInit(out value); return TryShortage(rem, 2); }
        ushort v = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos));
        if (BitConverter.IsLittleEndian) v = BinaryPrimitives.ReverseEndianness(v);
        _position = pos + 2;
        value = v;
        return FastStatus.Parsed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryReadU16LE(out ushort value)
    {
        int pos = _position;
        int rem = _buffer.Length - pos;
        if (rem < 2) { Unsafe.SkipInit(out value); return TryShortage(rem, 2); }
        ushort v = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos));
        if (!BitConverter.IsLittleEndian) v = BinaryPrimitives.ReverseEndianness(v);
        _position = pos + 2;
        value = v;
        return FastStatus.Parsed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryReadI16BE(out short value)
    {
        var st = TryReadU16BE(out ushort raw);
        value = unchecked((short)raw);
        return st;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryReadI16LE(out short value)
    {
        var st = TryReadU16LE(out ushort raw);
        value = unchecked((short)raw);
        return st;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryReadU32BE(out uint value)
    {
        int pos = _position;
        int rem = _buffer.Length - pos;
        if (rem < 4) { Unsafe.SkipInit(out value); return TryShortage(rem, 4); }
        uint v = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos));
        if (BitConverter.IsLittleEndian) v = BinaryPrimitives.ReverseEndianness(v);
        _position = pos + 4;
        value = v;
        return FastStatus.Parsed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryReadU32LE(out uint value)
    {
        int pos = _position;
        int rem = _buffer.Length - pos;
        if (rem < 4) { Unsafe.SkipInit(out value); return TryShortage(rem, 4); }
        uint v = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos));
        if (!BitConverter.IsLittleEndian) v = BinaryPrimitives.ReverseEndianness(v);
        _position = pos + 4;
        value = v;
        return FastStatus.Parsed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryReadI32BE(out int value)
    {
        var st = TryReadU32BE(out uint raw);
        value = unchecked((int)raw);
        return st;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryReadI32LE(out int value)
    {
        var st = TryReadU32LE(out uint raw);
        value = unchecked((int)raw);
        return st;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryReadU64BE(out ulong value)
    {
        int pos = _position;
        int rem = _buffer.Length - pos;
        if (rem < 8) { Unsafe.SkipInit(out value); return TryShortage(rem, 8); }
        ulong v = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos));
        if (BitConverter.IsLittleEndian) v = BinaryPrimitives.ReverseEndianness(v);
        _position = pos + 8;
        value = v;
        return FastStatus.Parsed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryReadU64LE(out ulong value)
    {
        int pos = _position;
        int rem = _buffer.Length - pos;
        if (rem < 8) { Unsafe.SkipInit(out value); return TryShortage(rem, 8); }
        ulong v = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos));
        if (!BitConverter.IsLittleEndian) v = BinaryPrimitives.ReverseEndianness(v);
        _position = pos + 8;
        value = v;
        return FastStatus.Parsed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryReadI64BE(out long value)
    {
        var st = TryReadU64BE(out ulong raw);
        value = unchecked((long)raw);
        return st;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryReadI64LE(out long value)
    {
        var st = TryReadU64LE(out ulong raw);
        value = unchecked((long)raw);
        return st;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryReadF32BE(out float value)
    {
        var st = TryReadU32BE(out uint bits);
        value = BitConverter.UInt32BitsToSingle(bits);
        return st;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryReadF32LE(out float value)
    {
        var st = TryReadU32LE(out uint bits);
        value = BitConverter.UInt32BitsToSingle(bits);
        return st;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryReadF64BE(out double value)
    {
        var st = TryReadU64BE(out ulong bits);
        value = BitConverter.UInt64BitsToDouble(bits);
        return st;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryReadF64LE(out double value)
    {
        var st = TryReadU64LE(out ulong bits);
        value = BitConverter.UInt64BitsToDouble(bits);
        return st;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryReadLengthPrefixedU8(out ByteSlice value)
    {
        int pos = _position;
        int rem = _buffer.Length - pos;
        if (rem < 1) { Unsafe.SkipInit(out value); return TryShortage(rem, 1); }
        int len = Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos);
        int total = 1 + len;
        if (rem < total) { Unsafe.SkipInit(out value); return TryShortage(rem, total); }
        value = new ByteSlice(pos + 1, len);
        _position = pos + total;
        return FastStatus.Parsed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryReadLengthPrefixedU16BE(out ByteSlice value)
    {
        int pos = _position;
        int rem = _buffer.Length - pos;
        if (rem < 2) { Unsafe.SkipInit(out value); return TryShortage(rem, 2); }
        ushort len = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos));
        if (BitConverter.IsLittleEndian) len = BinaryPrimitives.ReverseEndianness(len);
        int total = 2 + len;
        if (rem < total) { Unsafe.SkipInit(out value); return TryShortage(rem, total); }
        value = new ByteSlice(pos + 2, len);
        _position = pos + total;
        return FastStatus.Parsed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryReadLengthPrefixedU16LE(out ByteSlice value)
    {
        int pos = _position;
        int rem = _buffer.Length - pos;
        if (rem < 2) { Unsafe.SkipInit(out value); return TryShortage(rem, 2); }
        ushort len = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos));
        if (!BitConverter.IsLittleEndian) len = BinaryPrimitives.ReverseEndianness(len);
        int total = 2 + len;
        if (rem < total) { Unsafe.SkipInit(out value); return TryShortage(rem, total); }
        value = new ByteSlice(pos + 2, len);
        _position = pos + total;
        return FastStatus.Parsed;
    }

    private readonly struct VarIntSlowOutcome
    {
        public readonly ulong Value;
        public readonly int NextPos;
        public readonly FastStatus Status;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public VarIntSlowOutcome(ulong value, int nextPos, FastStatus status)
        {
            Value = value;
            NextPos = nextPos;
            Status = status;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryReadVarUInt64(out ulong value)
    {
        int pos = _position;
        if (_buffer.Length - pos >= 10)
        {
            ref byte p = ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos);
            ulong b = p;
            if (b < 0x80) { _position = pos + 1; value = b; return FastStatus.Parsed; }

            ulong result = b;
            b = Unsafe.Add(ref p, 1); result = unchecked(result + ((b - 1UL) << 7)); if (b < 0x80) { _position = pos + 2; value = result; return FastStatus.Parsed; }
            b = Unsafe.Add(ref p, 2); result = unchecked(result + ((b - 1UL) << 14)); if (b < 0x80) { _position = pos + 3; value = result; return FastStatus.Parsed; }
            b = Unsafe.Add(ref p, 3); result = unchecked(result + ((b - 1UL) << 21)); if (b < 0x80) { _position = pos + 4; value = result; return FastStatus.Parsed; }
            b = Unsafe.Add(ref p, 4); result = unchecked(result + ((b - 1UL) << 28)); if (b < 0x80) { _position = pos + 5; value = result; return FastStatus.Parsed; }
            b = Unsafe.Add(ref p, 5); result = unchecked(result + ((b - 1UL) << 35)); if (b < 0x80) { _position = pos + 6; value = result; return FastStatus.Parsed; }
            b = Unsafe.Add(ref p, 6); result = unchecked(result + ((b - 1UL) << 42)); if (b < 0x80) { _position = pos + 7; value = result; return FastStatus.Parsed; }
            b = Unsafe.Add(ref p, 7); result = unchecked(result + ((b - 1UL) << 49)); if (b < 0x80) { _position = pos + 8; value = result; return FastStatus.Parsed; }
            b = Unsafe.Add(ref p, 8); result = unchecked(result + ((b - 1UL) << 56)); if (b < 0x80) { _position = pos + 9; value = result; return FastStatus.Parsed; }
            b = Unsafe.Add(ref p, 9);
            if (b <= 1) { _position = pos + 10; value = unchecked(result + ((b - 1UL) << 63)); return FastStatus.Parsed; }
            Unsafe.SkipInit(out value);
            return FastStatus.Backtrack;
        }

        VarIntSlowOutcome slow = TryReadVarUInt64Slow(_buffer, pos, _mode);
        _position = slow.NextPos;
        value = slow.Value;
        return slow.Status;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static VarIntSlowOutcome TryReadVarUInt64Slow(
        ReadOnlySpan<byte> buf,
        int start,
        InputMode mode)
    {
        int pos = start;
        ulong result = 0;
        int shift = 0;

        for (int i = 0; i < 10; i++)
        {
            if ((uint)pos >= (uint)buf.Length)
            {
                return new VarIntSlowOutcome(
                    0,
                    start,
                    mode == InputMode.Streaming ? FastStatus.IncompleteBytes(1) : FastStatus.Backtrack);
            }

            byte b = buf[pos++];
            if (i == 9 && b > 1)
            {
                return new VarIntSlowOutcome(0, start, FastStatus.Backtrack);
            }

            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return new VarIntSlowOutcome(result, pos, FastStatus.Parsed);
            }

            shift += 7;
        }

        return new VarIntSlowOutcome(0, start, FastStatus.Backtrack);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryReadVarInt64(out long value)
    {
        var st = TryReadVarUInt64(out ulong u);
        value = (long)(u >> 1) ^ -(long)(u & 1);
        return st;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastStatus TryReadQuicVarInt64(out ulong value)
    {
        int pos = _position;
        int rem = _buffer.Length - pos;
        if (rem < 1) { Unsafe.SkipInit(out value); return TryShortage(rem, 1); }

        ref byte p = ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos);
        byte first = p;
        int tag = first >> 6;
        if (tag == 0)
        {
            value = first;
            _position = pos + 1;
            return FastStatus.Parsed;
        }

        int len = 1 << tag;
        if (rem < len) { Unsafe.SkipInit(out value); return TryShortage(rem, len); }

        if (tag == 1)
        {
            ushort raw = Unsafe.ReadUnaligned<ushort>(ref p);
            if (BitConverter.IsLittleEndian) raw = BinaryPrimitives.ReverseEndianness(raw);
            value = (ulong)(raw & 0x3FFF);
            _position = pos + 2;
            return FastStatus.Parsed;
        }

        if (tag == 2)
        {
            uint raw = Unsafe.ReadUnaligned<uint>(ref p);
            if (BitConverter.IsLittleEndian) raw = BinaryPrimitives.ReverseEndianness(raw);
            value = raw & 0x3FFF_FFFFu;
            _position = pos + 4;
            return FastStatus.Parsed;
        }

        ulong raw64 = Unsafe.ReadUnaligned<ulong>(ref p);
        if (BitConverter.IsLittleEndian) raw64 = BinaryPrimitives.ReverseEndianness(raw64);
        value = raw64 & 0x3FFF_FFFF_FFFF_FFFFUL;
        _position = pos + 8;
        return FastStatus.Parsed;
    }
}
