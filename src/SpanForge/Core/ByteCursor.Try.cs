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
    public FastStatus TryReadU8(out byte value)
    {
        int pos = _position;
        if (pos >= _buffer.Length) { Unsafe.SkipInit(out value); return TryShortage(0, 1); }
        value = Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos);
        _position = pos + 1;
        return FastStatus.Parsed;
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
    public FastStatus TryReadF64BE(out double value)
    {
        int pos = _position;
        int rem = _buffer.Length - pos;
        if (rem < 8) { Unsafe.SkipInit(out value); return TryShortage(rem, 8); }
        ulong bits = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos));
        if (BitConverter.IsLittleEndian) bits = BinaryPrimitives.ReverseEndianness(bits);
        _position = pos + 8;
        value = BitConverter.UInt64BitsToDouble(bits);
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
    public FastStatus TryReadVarUInt64(out ulong value)
    {
        int pos = _position;
        int len = _buffer.Length;
        if (pos >= len) goto shortage;

        ref byte p = ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos);
        uint b = p;
        ulong result = b;
        int nextPos = pos + 1;
        if ((b & 0x80) == 0) goto done;

        int rem = len - pos;
        result = b & 0x7Fu;

        if (rem <= 1) goto shortage;
        b = Unsafe.Add(ref p, 1);
        result |= (ulong)(b & 0x7Fu) << 7;
        nextPos = pos + 2;
        if ((b & 0x80) == 0) goto done;

        if (rem <= 2) goto shortage;
        b = Unsafe.Add(ref p, 2);
        result |= (ulong)(b & 0x7Fu) << 14;
        nextPos = pos + 3;
        if ((b & 0x80) == 0) goto done;

        if (rem <= 3) goto shortage;
        b = Unsafe.Add(ref p, 3);
        result |= (ulong)(b & 0x7Fu) << 21;
        nextPos = pos + 4;
        if ((b & 0x80) == 0) goto done;

        if (rem <= 4) goto shortage;
        b = Unsafe.Add(ref p, 4);
        result |= (ulong)(b & 0x7Fu) << 28;
        nextPos = pos + 5;
        if ((b & 0x80) == 0) goto done;

        if (rem <= 5) goto shortage;
        b = Unsafe.Add(ref p, 5);
        result |= (ulong)(b & 0x7Fu) << 35;
        nextPos = pos + 6;
        if ((b & 0x80) == 0) goto done;

        if (rem <= 6) goto shortage;
        b = Unsafe.Add(ref p, 6);
        result |= (ulong)(b & 0x7Fu) << 42;
        nextPos = pos + 7;
        if ((b & 0x80) == 0) goto done;

        if (rem <= 7) goto shortage;
        b = Unsafe.Add(ref p, 7);
        result |= (ulong)(b & 0x7Fu) << 49;
        nextPos = pos + 8;
        if ((b & 0x80) == 0) goto done;

        if (rem <= 8) goto shortage;
        b = Unsafe.Add(ref p, 8);
        result |= (ulong)(b & 0x7Fu) << 56;
        nextPos = pos + 9;
        if ((b & 0x80) == 0) goto done;

        if (rem <= 9) goto shortage;
        b = Unsafe.Add(ref p, 9);
        if (b <= 1) { result |= (ulong)b << 63; nextPos = pos + 10; goto done; }

        Unsafe.SkipInit(out value);
        return FastStatus.Backtrack;

    done:
        _position = nextPos;
        value = result;
        return FastStatus.Parsed;

    shortage:
        Unsafe.SkipInit(out value);
        return TryShortage(0, 1);
    }
}
