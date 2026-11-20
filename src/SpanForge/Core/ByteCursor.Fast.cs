using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SpanForge;

public ref partial struct ByteCursor
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly ParseResult<T, TError> Shortage<T, TError>(int requiredBytes, ErrorKind kind = ErrorKind.Eof)
        where TError : struct, IParseError<TError> =>
        _mode == InputMode.Complete
            ? new Backtrack<TError>(TError.Create(_position, kind))
            : ShortageStreaming<T, TError>(requiredBytes - RemainingLength);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ParseResult<T, TError> ShortageStreaming<T, TError>(int missing)
        where TError : struct, IParseError<TError> =>
        Incomplete.AtLeast(missing > 0 ? missing : 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<byte, TError> ReadU8<TError>()
        where TError : struct, IParseError<TError>
    {
        int pos = _position;
        if (pos >= _buffer.Length) return Shortage<byte, TError>(1);
        _position = pos + 1;
        return new Parsed<byte>(Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<ushort, TError> ReadU16BE<TError>()
        where TError : struct, IParseError<TError>
    {
        int pos = _position;
        if (_buffer.Length - pos < 2) return Shortage<ushort, TError>(2);
        ushort value = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos));
        if (BitConverter.IsLittleEndian) value = BinaryPrimitives.ReverseEndianness(value);
        _position = pos + 2;
        return new Parsed<ushort>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<ushort, TError> ReadU16LE<TError>()
        where TError : struct, IParseError<TError>
    {
        int pos = _position;
        if (_buffer.Length - pos < 2) return Shortage<ushort, TError>(2);
        ushort value = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos));
        if (!BitConverter.IsLittleEndian) value = BinaryPrimitives.ReverseEndianness(value);
        _position = pos + 2;
        return new Parsed<ushort>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<ushort, TError> ReadU16<TError>()
        where TError : struct, IParseError<TError> =>
        _endianness == Endianness.Big ? ReadU16BE<TError>() : ReadU16LE<TError>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<uint, TError> ReadU32BE<TError>()
        where TError : struct, IParseError<TError>
    {
        int pos = _position;
        if (_buffer.Length - pos < 4) return Shortage<uint, TError>(4);
        uint value = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos));
        if (BitConverter.IsLittleEndian) value = BinaryPrimitives.ReverseEndianness(value);
        _position = pos + 4;
        return new Parsed<uint>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<uint, TError> ReadU32LE<TError>()
        where TError : struct, IParseError<TError>
    {
        int pos = _position;
        if (_buffer.Length - pos < 4) return Shortage<uint, TError>(4);
        uint value = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos));
        if (!BitConverter.IsLittleEndian) value = BinaryPrimitives.ReverseEndianness(value);
        _position = pos + 4;
        return new Parsed<uint>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<uint, TError> ReadU32<TError>()
        where TError : struct, IParseError<TError> =>
        _endianness == Endianness.Big ? ReadU32BE<TError>() : ReadU32LE<TError>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<ulong, TError> ReadU64BE<TError>()
        where TError : struct, IParseError<TError>
    {
        int pos = _position;
        if (_buffer.Length - pos < 8) return Shortage<ulong, TError>(8);
        ulong value = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos));
        if (BitConverter.IsLittleEndian) value = BinaryPrimitives.ReverseEndianness(value);
        _position = pos + 8;
        return new Parsed<ulong>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<ulong, TError> ReadU64LE<TError>()
        where TError : struct, IParseError<TError>
    {
        int pos = _position;
        if (_buffer.Length - pos < 8) return Shortage<ulong, TError>(8);
        ulong value = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos));
        if (!BitConverter.IsLittleEndian) value = BinaryPrimitives.ReverseEndianness(value);
        _position = pos + 8;
        return new Parsed<ulong>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<ulong, TError> ReadU64<TError>()
        where TError : struct, IParseError<TError> =>
        _endianness == Endianness.Big ? ReadU64BE<TError>() : ReadU64LE<TError>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<double, TError> ReadF64BE<TError>()
        where TError : struct, IParseError<TError>
    {
        int pos = _position;
        if (_buffer.Length - pos < 8) return Shortage<double, TError>(8);
        ulong bits = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos));
        if (BitConverter.IsLittleEndian) bits = BinaryPrimitives.ReverseEndianness(bits);
        _position = pos + 8;
        return new Parsed<double>(BitConverter.UInt64BitsToDouble(bits));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<double, TError> ReadF64LE<TError>()
        where TError : struct, IParseError<TError>
    {
        int pos = _position;
        if (_buffer.Length - pos < 8) return Shortage<double, TError>(8);
        ulong bits = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos));
        if (!BitConverter.IsLittleEndian) bits = BinaryPrimitives.ReverseEndianness(bits);
        _position = pos + 8;
        return new Parsed<double>(BitConverter.UInt64BitsToDouble(bits));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<double, TError> ReadF64<TError>()
        where TError : struct, IParseError<TError> =>
        _endianness == Endianness.Big ? ReadF64BE<TError>() : ReadF64LE<TError>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<byte, TError> TagByte<TError>(byte expected)
        where TError : struct, IParseError<TError>
    {
        int pos = _position;
        if (pos >= _buffer.Length) return Shortage<byte, TError>(1, ErrorKind.Tag);
        if (Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos) != expected)
            return new Backtrack<TError>(TError.Create(pos, ErrorKind.Tag));
        _position = pos + 1;
        return new Parsed<byte>(expected);
    }

    public ParseResult<ByteSlice, TError> Tag<TError>(ReadOnlySpan<byte> expected)
        where TError : struct, IParseError<TError>
    {
        int start = _position;
        int remLen = _buffer.Length - start;
        if (remLen < expected.Length)
        {
            if (_mode == InputMode.Streaming && _buffer.Slice(start, remLen).SequenceEqual(expected[..remLen]))
            {
                return Incomplete.AtLeast(expected.Length - remLen);
            }

            return new Backtrack<TError>(TError.Create(start, ErrorKind.Tag));
        }

        if (!_buffer.Slice(start, expected.Length).SequenceEqual(expected))
        {
            return new Backtrack<TError>(TError.Create(start, ErrorKind.Tag));
        }

        _position = start + expected.Length;
        return new Parsed<ByteSlice>(new ByteSlice(start, expected.Length));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<ByteSlice, TError> Take<TError>(int count)
        where TError : struct, IParseError<TError>
    {
        if (count < 0) return new Backtrack<TError>(TError.Create(_position, ErrorKind.Take));
        int pos = _position;
        if (_buffer.Length - pos < count) return Shortage<ByteSlice, TError>(count, ErrorKind.Take);
        _position = pos + count;
        return new Parsed<ByteSlice>(new ByteSlice(pos, count));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<ulong, TError> ReadVarUInt64<TError>()
        where TError : struct, IParseError<TError>
    {
        int pos = _position;
        if (_buffer.Length - pos >= 10)
        {
            ref byte p = ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos);
            ulong b = p;
            if (b < 0x80) { _position = pos + 1; return new Parsed<ulong>(b); }
            ulong result = b & 0x7F;
            b = Unsafe.Add(ref p, 1); result |= (b & 0x7F) << 7; if (b < 0x80) { _position = pos + 2; return new Parsed<ulong>(result); }
            b = Unsafe.Add(ref p, 2); result |= (b & 0x7F) << 14; if (b < 0x80) { _position = pos + 3; return new Parsed<ulong>(result); }
            b = Unsafe.Add(ref p, 3); result |= (b & 0x7F) << 21; if (b < 0x80) { _position = pos + 4; return new Parsed<ulong>(result); }
            b = Unsafe.Add(ref p, 4); result |= (b & 0x7F) << 28; if (b < 0x80) { _position = pos + 5; return new Parsed<ulong>(result); }
            b = Unsafe.Add(ref p, 5); result |= (b & 0x7F) << 35; if (b < 0x80) { _position = pos + 6; return new Parsed<ulong>(result); }
            b = Unsafe.Add(ref p, 6); result |= (b & 0x7F) << 42; if (b < 0x80) { _position = pos + 7; return new Parsed<ulong>(result); }
            b = Unsafe.Add(ref p, 7); result |= (b & 0x7F) << 49; if (b < 0x80) { _position = pos + 8; return new Parsed<ulong>(result); }
            b = Unsafe.Add(ref p, 8); result |= (b & 0x7F) << 56; if (b < 0x80) { _position = pos + 9; return new Parsed<ulong>(result); }
            b = Unsafe.Add(ref p, 9);
            if (b <= 1) { _position = pos + 10; return new Parsed<ulong>(result | (b << 63)); }
            return new Backtrack<TError>(TError.Create(pos, ErrorKind.VarInt));
        }

        var tailRes = ReadVarUInt64Tail<TError>(_buffer, pos, _mode, out int nextPos);
        if (tailRes.IsParsed) _position = nextPos;
        return tailRes;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ParseResult<ulong, TError> ReadVarUInt64Tail<TError>(
        ReadOnlySpan<byte> buf,
        int start,
        InputMode mode,
        out int nextPos)
        where TError : struct, IParseError<TError>
    {
        int pos = start;
        ulong result = 0;
        int shift = 0;

        for (int i = 0; i < 10; i++)
        {
            if ((uint)pos >= (uint)buf.Length)
            {
                nextPos = start;
                return mode == InputMode.Streaming
                    ? Incomplete.AtLeast(1)
                    : new Backtrack<TError>(TError.Create(start, ErrorKind.Eof));
            }

            byte b = buf[pos++];
            if (i == 9 && b > 1)
            {
                nextPos = start;
                return new Backtrack<TError>(TError.Create(start, ErrorKind.VarInt));
            }

            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                nextPos = pos;
                return new Parsed<ulong>(result);
            }

            shift += 7;
        }

        nextPos = start;
        return new Backtrack<TError>(TError.Create(start, ErrorKind.VarInt));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<ByteSlice, TError> ReadLengthPrefixedBytes<TError>(LengthPrefix prefix)
        where TError : struct, IParseError<TError>
    {
        var cp = Save();
        ulong length;
        switch (prefix)
        {
            case LengthPrefix.U8:
                var r8 = ReadU8<TError>();
                if (!r8.TryGetValue(out Parsed<byte> p8)) { Reset(cp); return r8.PropagateNonParsed<ByteSlice>(); }
                length = p8.Value;
                break;

            case LengthPrefix.U16:
                var r16 = ReadU16<TError>();
                if (!r16.TryGetValue(out Parsed<ushort> p16)) { Reset(cp); return r16.PropagateNonParsed<ByteSlice>(); }
                length = p16.Value;
                break;

            case LengthPrefix.U32:
                var r32 = ReadU32<TError>();
                if (!r32.TryGetValue(out Parsed<uint> p32)) { Reset(cp); return r32.PropagateNonParsed<ByteSlice>(); }
                length = p32.Value;
                break;

            case LengthPrefix.VarUInt64:
                var rVar = ReadVarUInt64<TError>();
                if (!rVar.TryGetValue(out Parsed<ulong> pVar)) { Reset(cp); return rVar.PropagateNonParsed<ByteSlice>(); }
                length = pVar.Value;
                break;

            default:
                return new Backtrack<TError>(TError.Create(cp.Position, ErrorKind.LengthValue));
        }

        if (length > int.MaxValue)
        {
            Reset(cp);
            return new Backtrack<TError>(TError.Create(cp.Position, ErrorKind.LengthValue));
        }

        var dataResult = Take<TError>((int)length);
        if (!dataResult.IsParsed)
        {
            Reset(cp);
        }

        return dataResult;
    }
}
