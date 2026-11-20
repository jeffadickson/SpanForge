using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace SpanForge;

public ref partial struct ByteCursor
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly ParseResult<T, TError> Shortage<T, TError>(int requiredBytes, ErrorKind kind = ErrorKind.Eof)
        where TError : struct, IParseError<TError> =>
        ShortageCore<T, TError>(_mode, _position, RemainingLength, requiredBytes, kind);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ParseResult<T, TError> ShortageCore<T, TError>(
        InputMode mode,
        int position,
        int remainingLength,
        int requiredBytes,
        ErrorKind kind)
        where TError : struct, IParseError<TError>
    {
        int missing = requiredBytes - remainingLength;
        return mode == InputMode.Streaming
            ? Incomplete.AtLeast(missing > 0 ? missing : 1)
            : new Backtrack<TError>(TError.Create(position, kind));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<byte, TError> ReadU8<TError>()
        where TError : struct, IParseError<TError>
    {
        int pos = _position;
        if ((uint)pos >= (uint)_buffer.Length) return Shortage<byte, TError>(1);
        _position = pos + 1;
        return new Parsed<byte>(_buffer[pos]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<ushort, TError> ReadU16BE<TError>()
        where TError : struct, IParseError<TError>
    {
        if (RemainingLength < 2) return Shortage<ushort, TError>(2);
        ushort value = BinaryPrimitives.ReadUInt16BigEndian(_buffer.Slice(_position, 2));
        _position += 2;
        return new Parsed<ushort>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<ushort, TError> ReadU16LE<TError>()
        where TError : struct, IParseError<TError>
    {
        if (RemainingLength < 2) return Shortage<ushort, TError>(2);
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.Slice(_position, 2));
        _position += 2;
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
        if (RemainingLength < 4) return Shortage<uint, TError>(4);
        uint value = BinaryPrimitives.ReadUInt32BigEndian(_buffer.Slice(_position, 4));
        _position += 4;
        return new Parsed<uint>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<uint, TError> ReadU32LE<TError>()
        where TError : struct, IParseError<TError>
    {
        if (RemainingLength < 4) return Shortage<uint, TError>(4);
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.Slice(_position, 4));
        _position += 4;
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
        if (RemainingLength < 8) return Shortage<ulong, TError>(8);
        ulong value = BinaryPrimitives.ReadUInt64BigEndian(_buffer.Slice(_position, 8));
        _position += 8;
        return new Parsed<ulong>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<ulong, TError> ReadU64LE<TError>()
        where TError : struct, IParseError<TError>
    {
        if (RemainingLength < 8) return Shortage<ulong, TError>(8);
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(_buffer.Slice(_position, 8));
        _position += 8;
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
        if (RemainingLength < 8) return Shortage<double, TError>(8);
        double value = BinaryPrimitives.ReadDoubleBigEndian(_buffer.Slice(_position, 8));
        _position += 8;
        return new Parsed<double>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<double, TError> ReadF64LE<TError>()
        where TError : struct, IParseError<TError>
    {
        if (RemainingLength < 8) return Shortage<double, TError>(8);
        double value = BinaryPrimitives.ReadDoubleLittleEndian(_buffer.Slice(_position, 8));
        _position += 8;
        return new Parsed<double>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<double, TError> ReadF64<TError>()
        where TError : struct, IParseError<TError> =>
        _endianness == Endianness.Big ? ReadF64BE<TError>() : ReadF64LE<TError>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<byte, TError> TagByte<TError>(byte expected)
        where TError : struct, IParseError<TError>
    {
        if (RemainingLength < 1) return Shortage<byte, TError>(1, ErrorKind.Tag);
        if (_buffer[_position] != expected) return new Backtrack<TError>(TError.Create(_position, ErrorKind.Tag));
        _position++;
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
        if (RemainingLength < count) return Shortage<ByteSlice, TError>(count, ErrorKind.Take);
        var slice = new ByteSlice(_position, count);
        _position += count;
        return new Parsed<ByteSlice>(slice);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<ulong, TError> ReadVarUInt64<TError>()
        where TError : struct, IParseError<TError>
    {
        int start = _position;
        int pos = start;
        ReadOnlySpan<byte> buf = _buffer;
        if ((uint)pos >= (uint)buf.Length)
        {
            return Shortage<ulong, TError>(1, ErrorKind.Eof);
        }

        byte b = buf[pos++];
        if (b < 0x80)
        {
            _position = pos;
            return new Parsed<ulong>(b);
        }

        ulong result = (ulong)(b & 0x7F);
        int shift = 7;

        for (int i = 1; i < 10; i++)
        {
            if ((uint)pos >= (uint)buf.Length)
            {
                return _mode == InputMode.Streaming
                    ? Incomplete.AtLeast(1)
                    : new Backtrack<TError>(TError.Create(start, ErrorKind.Eof));
            }

            b = buf[pos++];
            if (i == 9 && b > 1)
            {
                return new Backtrack<TError>(TError.Create(start, ErrorKind.VarInt));
            }

            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                _position = pos;
                return new Parsed<ulong>(result);
            }

            shift += 7;
        }

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
