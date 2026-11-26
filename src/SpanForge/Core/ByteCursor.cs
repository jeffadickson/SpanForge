using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;

namespace SpanForge;

/// <summary>
/// Saved cursor state for O(1) zero-allocation backtracking (inspired by Rust's <c>winnow::stream::Checkpoint</c>
/// and <c>binrw</c> scoped endianness).
/// </summary>
public readonly record struct Checkpoint(int Position, Endianness Endianness);

/// <summary>
/// Mutable, stack-only zero-copy binary reader over <see cref="ReadOnlySpan{T}"/>.
/// <para>
/// Combines Rust <c>winnow</c>'s mutable-cursor + checkpoint rollback model, <c>nom</c>'s streaming/complete
/// modal errors, <c>binrw</c>'s scoped endianness &amp; alignment, <c>zerocopy</c>'s blittable casting,
/// and .NET 11 SIMD (<see cref="SearchValues{T}"/>) + <see cref="BinaryPrimitives"/>.
/// </para>
/// </summary>
public ref partial struct ByteCursor
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;
    private Endianness _endianness;
    private readonly InputMode _mode;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ByteCursor(
        ReadOnlySpan<byte> buffer,
        Endianness endianness = Endianness.Big,
        InputMode mode = InputMode.Complete)
    {
        _buffer = buffer;
        _position = 0;
        _endianness = endianness;
        _mode = mode;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ByteCursor(
        ReadOnlySpan<byte> buffer,
        int startPosition,
        Endianness endianness,
        InputMode mode)
    {
        _buffer = buffer;
        _position = startPosition;
        _endianness = endianness;
        _mode = mode;
    }

    public readonly ReadOnlySpan<byte> Buffer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _buffer;
    }

    public readonly ReadOnlySpan<byte> Remaining
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

    public readonly bool IsAtEnd
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _position >= _buffer.Length;
    }

    public readonly InputMode Mode
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _mode;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Rewind(int position) => _position = position;

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

    /// <summary>
    /// Zero-copy span lookup for any <see cref="ByteSlice"/> parsed from this cursor.
    /// </summary>
    public readonly ReadOnlySpan<byte> this[ByteSlice slice]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _buffer.Slice(slice.Offset, slice.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Checkpoint Save() => new(_position, _endianness);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset(Checkpoint checkpoint)
    {
        _position = checkpoint.Position;
        _endianness = checkpoint.Endianness;
    }

    /// <summary>
    /// Creates a bounded sub-cursor covering <paramref name="slice"/> while preserving exact absolute
    /// byte offsets relative to the outer <see cref="Buffer"/> (so nested <see cref="ByteSlice"/> values
    /// and <see cref="ParseError.Offset"/> locations remain valid against the root buffer).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ByteCursor CreateSubCursor(ByteSlice slice, InputMode mode = InputMode.Complete) =>
        new(_buffer[..slice.End], slice.Offset, _endianness, mode);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly ParseResult<T> Shortage<T>(int requiredBytes, ErrorKind kind = ErrorKind.Eof) =>
        ShortageCore<T>(_mode, _position, RemainingLength, requiredBytes, kind);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ParseResult<T> ShortageCore<T>(InputMode mode, int position, int remainingLength, int requiredBytes, ErrorKind kind)
    {
        int missing = requiredBytes - remainingLength;
        return mode == InputMode.Streaming
            ? Incomplete.AtLeast(missing > 0 ? missing : 1)
            : Backtrack.At(position, kind);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly ParseResult<T> UnknownShortage<T>(ErrorKind kind = ErrorKind.Eof) =>
        UnknownShortageCore<T>(_mode, _position, kind);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ParseResult<T> UnknownShortageCore<T>(InputMode mode, int position, ErrorKind kind) =>
        mode == InputMode.Streaming
            ? Incomplete.Unknown
            : Backtrack.At(position, kind);

    // -------------------------------------------------------------------------
    // Fixed-width integer & floating-point readers
    // -------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<byte> ReadU8()
    {
        int pos = _position;
        if (pos >= _buffer.Length) return Shortage<byte>(1);
        _position = pos + 1;
        return new Parsed<byte>(Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<sbyte> ReadI8()
    {
        if (RemainingLength < 1) return Shortage<sbyte>(1);
        return new Parsed<sbyte>(unchecked((sbyte)_buffer[_position++]));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<bool> ReadBool(bool strict = false)
    {
        if (RemainingLength < 1) return Shortage<bool>(1);
        byte b = _buffer[_position];
        if (strict && b > 1)
        {
            return Backtrack.At(_position, ErrorKind.Verify, "Expected boolean byte 0x00 or 0x01");
        }

        _position++;
        return new Parsed<bool>(b != 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<ushort> ReadU16BE()
    {
        int pos = _position;
        if (_buffer.Length - pos < 2) return Shortage<ushort>(2);
        ushort value = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos));
        if (BitConverter.IsLittleEndian) value = BinaryPrimitives.ReverseEndianness(value);
        _position = pos + 2;
        return new Parsed<ushort>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<ushort> ReadU16LE()
    {
        int pos = _position;
        if (_buffer.Length - pos < 2) return Shortage<ushort>(2);
        ushort value = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos));
        if (!BitConverter.IsLittleEndian) value = BinaryPrimitives.ReverseEndianness(value);
        _position = pos + 2;
        return new Parsed<ushort>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<ushort> ReadU16() =>
        _endianness == Endianness.Big ? ReadU16BE() : ReadU16LE();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<short> ReadI16BE()
    {
        if (RemainingLength < 2) return Shortage<short>(2);
        short value = BinaryPrimitives.ReadInt16BigEndian(_buffer.Slice(_position, 2));
        _position += 2;
        return new Parsed<short>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<short> ReadI16LE()
    {
        if (RemainingLength < 2) return Shortage<short>(2);
        short value = BinaryPrimitives.ReadInt16LittleEndian(_buffer.Slice(_position, 2));
        _position += 2;
        return new Parsed<short>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<short> ReadI16() =>
        _endianness == Endianness.Big ? ReadI16BE() : ReadI16LE();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<uint> ReadU24BE()
    {
        if (RemainingLength < 3) return Shortage<uint>(3);
        uint value = ((uint)_buffer[_position] << 16)
                   | ((uint)_buffer[_position + 1] << 8)
                   | _buffer[_position + 2];
        _position += 3;
        return new Parsed<uint>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<uint> ReadU24LE()
    {
        if (RemainingLength < 3) return Shortage<uint>(3);
        uint value = _buffer[_position]
                   | ((uint)_buffer[_position + 1] << 8)
                   | ((uint)_buffer[_position + 2] << 16);
        _position += 3;
        return new Parsed<uint>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<uint> ReadU24() =>
        _endianness == Endianness.Big ? ReadU24BE() : ReadU24LE();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<uint> ReadU32BE()
    {
        int pos = _position;
        if (_buffer.Length - pos < 4) return Shortage<uint>(4);
        uint value = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos));
        if (BitConverter.IsLittleEndian) value = BinaryPrimitives.ReverseEndianness(value);
        _position = pos + 4;
        return new Parsed<uint>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<uint> ReadU32LE()
    {
        int pos = _position;
        if (_buffer.Length - pos < 4) return Shortage<uint>(4);
        uint value = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos));
        if (!BitConverter.IsLittleEndian) value = BinaryPrimitives.ReverseEndianness(value);
        _position = pos + 4;
        return new Parsed<uint>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<uint> ReadU32() =>
        _endianness == Endianness.Big ? ReadU32BE() : ReadU32LE();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<int> ReadI32BE()
    {
        if (RemainingLength < 4) return Shortage<int>(4);
        int value = BinaryPrimitives.ReadInt32BigEndian(_buffer.Slice(_position, 4));
        _position += 4;
        return new Parsed<int>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<int> ReadI32LE()
    {
        if (RemainingLength < 4) return Shortage<int>(4);
        int value = BinaryPrimitives.ReadInt32LittleEndian(_buffer.Slice(_position, 4));
        _position += 4;
        return new Parsed<int>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<int> ReadI32() =>
        _endianness == Endianness.Big ? ReadI32BE() : ReadI32LE();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<ulong> ReadU64BE()
    {
        int pos = _position;
        if (_buffer.Length - pos < 8) return Shortage<ulong>(8);
        ulong value = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos));
        if (BitConverter.IsLittleEndian) value = BinaryPrimitives.ReverseEndianness(value);
        _position = pos + 8;
        return new Parsed<ulong>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<ulong> ReadU64LE()
    {
        int pos = _position;
        if (_buffer.Length - pos < 8) return Shortage<ulong>(8);
        ulong value = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos));
        if (!BitConverter.IsLittleEndian) value = BinaryPrimitives.ReverseEndianness(value);
        _position = pos + 8;
        return new Parsed<ulong>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<ulong> ReadU64() =>
        _endianness == Endianness.Big ? ReadU64BE() : ReadU64LE();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<long> ReadI64BE()
    {
        if (RemainingLength < 8) return Shortage<long>(8);
        long value = BinaryPrimitives.ReadInt64BigEndian(_buffer.Slice(_position, 8));
        _position += 8;
        return new Parsed<long>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<long> ReadI64LE()
    {
        if (RemainingLength < 8) return Shortage<long>(8);
        long value = BinaryPrimitives.ReadInt64LittleEndian(_buffer.Slice(_position, 8));
        _position += 8;
        return new Parsed<long>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<long> ReadI64() =>
        _endianness == Endianness.Big ? ReadI64BE() : ReadI64LE();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<UInt128> ReadU128BE()
    {
        if (RemainingLength < 16) return Shortage<UInt128>(16);
        UInt128 value = BinaryPrimitives.ReadUInt128BigEndian(_buffer.Slice(_position, 16));
        _position += 16;
        return new Parsed<UInt128>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<UInt128> ReadU128LE()
    {
        if (RemainingLength < 16) return Shortage<UInt128>(16);
        UInt128 value = BinaryPrimitives.ReadUInt128LittleEndian(_buffer.Slice(_position, 16));
        _position += 16;
        return new Parsed<UInt128>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<UInt128> ReadU128() =>
        _endianness == Endianness.Big ? ReadU128BE() : ReadU128LE();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<Half> ReadF16()
    {
        if (RemainingLength < 2) return Shortage<Half>(2);
        Half value = _endianness == Endianness.Big
            ? BinaryPrimitives.ReadHalfBigEndian(_buffer.Slice(_position, 2))
            : BinaryPrimitives.ReadHalfLittleEndian(_buffer.Slice(_position, 2));
        _position += 2;
        return new Parsed<Half>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<float> ReadF32BE()
    {
        if (RemainingLength < 4) return Shortage<float>(4);
        float value = BinaryPrimitives.ReadSingleBigEndian(_buffer.Slice(_position, 4));
        _position += 4;
        return new Parsed<float>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<float> ReadF32LE()
    {
        if (RemainingLength < 4) return Shortage<float>(4);
        float value = BinaryPrimitives.ReadSingleLittleEndian(_buffer.Slice(_position, 4));
        _position += 4;
        return new Parsed<float>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<float> ReadF32() =>
        _endianness == Endianness.Big ? ReadF32BE() : ReadF32LE();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<double> ReadF64BE()
    {
        int pos = _position;
        if (_buffer.Length - pos < 8) return Shortage<double>(8);
        ulong bits = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos));
        if (BitConverter.IsLittleEndian) bits = BinaryPrimitives.ReverseEndianness(bits);
        _position = pos + 8;
        return new Parsed<double>(BitConverter.UInt64BitsToDouble(bits));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<double> ReadF64LE()
    {
        int pos = _position;
        if (_buffer.Length - pos < 8) return Shortage<double>(8);
        ulong bits = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos));
        if (!BitConverter.IsLittleEndian) bits = BinaryPrimitives.ReverseEndianness(bits);
        _position = pos + 8;
        return new Parsed<double>(BitConverter.UInt64BitsToDouble(bits));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<double> ReadF64() =>
        _endianness == Endianness.Big ? ReadF64BE() : ReadF64LE();

    // -------------------------------------------------------------------------
    // Variable-length integer readers (LEB128, ZigZag, QUIC RFC 9000)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Reads an unsigned LEB128 varint (up to 64 bits / 10 bytes, as used in Protobuf, WASM, and DWARF).
    /// Uses a single-bounds-check unrolled fast path when at least 10 bytes remain.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<ulong> ReadVarUInt64()
    {
        int pos = _position;
        if (_buffer.Length - pos >= 10)
        {
            ref byte p = ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos);
            ulong b = p;
            if (b < 0x80) { _position = pos + 1; return new Parsed<ulong>(b); }
            ulong result = b;
            b = Unsafe.Add(ref p, 1); result = unchecked(result + ((b - 1UL) << 7)); if (b < 0x80) { _position = pos + 2; return new Parsed<ulong>(result); }
            b = Unsafe.Add(ref p, 2); result = unchecked(result + ((b - 1UL) << 14)); if (b < 0x80) { _position = pos + 3; return new Parsed<ulong>(result); }
            b = Unsafe.Add(ref p, 3); result = unchecked(result + ((b - 1UL) << 21)); if (b < 0x80) { _position = pos + 4; return new Parsed<ulong>(result); }
            b = Unsafe.Add(ref p, 4); result = unchecked(result + ((b - 1UL) << 28)); if (b < 0x80) { _position = pos + 5; return new Parsed<ulong>(result); }
            b = Unsafe.Add(ref p, 5); result = unchecked(result + ((b - 1UL) << 35)); if (b < 0x80) { _position = pos + 6; return new Parsed<ulong>(result); }
            b = Unsafe.Add(ref p, 6); result = unchecked(result + ((b - 1UL) << 42)); if (b < 0x80) { _position = pos + 7; return new Parsed<ulong>(result); }
            b = Unsafe.Add(ref p, 7); result = unchecked(result + ((b - 1UL) << 49)); if (b < 0x80) { _position = pos + 8; return new Parsed<ulong>(result); }
            b = Unsafe.Add(ref p, 8); result = unchecked(result + ((b - 1UL) << 56)); if (b < 0x80) { _position = pos + 9; return new Parsed<ulong>(result); }
            b = Unsafe.Add(ref p, 9);
            if (b <= 1) { _position = pos + 10; return new Parsed<ulong>(unchecked(result + ((b - 1UL) << 63))); }
            return VarIntOverflow(pos, b);
        }

        var slowRes = ReadVarUInt64Slow(_buffer, pos, _mode, out int nextPos);
        if (slowRes.IsParsed) _position = nextPos;
        return slowRes;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ParseResult<ulong> VarIntOverflow(int start, ulong tenthByte) =>
        Backtrack.At(
            start,
            ErrorKind.VarInt,
            (tenthByte & 0x80) != 0 ? "LEB128 varint exceeds 10 bytes" : "LEB128 varint overflows UInt64");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ParseResult<ulong> ReadVarUInt64Slow(ReadOnlySpan<byte> buf, int start, InputMode mode, out int nextPos)
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
                    : Backtrack.At(start, ErrorKind.Eof, "Unexpected EOF inside LEB128 varint");
            }

            byte b = buf[pos++];
            if (i == 9 && b > 1)
            {
                nextPos = start;
                return VarIntOverflow(start, b);
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
        return Backtrack.At(start, ErrorKind.VarInt, "LEB128 varint exceeds 10 bytes");
    }

    /// <summary>
    /// Reads a ZigZag-encoded signed LEB128 varint (as used in Protobuf <c>sint64</c> and Avro).
    /// </summary>
    public ParseResult<long> ReadVarInt64()
    {
        return ReadVarUInt64() switch
        {
            Parsed<ulong>(var u) => new Parsed<long>((long)(u >> 1) ^ -(long)(u & 1)),
            Incomplete inc => inc,
            Backtrack err => err,
            Failure fail => fail,
        };
    }

    /// <summary>
    /// Reads a QUIC (RFC 9000 Section 16) 2-bit-prefixed big-endian variable-length integer (1, 2, 4, or 8 bytes).
    /// </summary>
    public ParseResult<ulong> ReadQuicVarInt64()
    {
        if (RemainingLength < 1) return Shortage<ulong>(1);

        byte first = _buffer[_position];
        int length = 1 << (first >> 6);
        if (RemainingLength < length) return Shortage<ulong>(length);

        ulong value = (ulong)(first & 0x3F);
        for (int i = 1; i < length; i++)
        {
            value = (value << 8) | _buffer[_position + i];
        }

        _position += length;
        return new Parsed<ulong>(value);
    }

    // -------------------------------------------------------------------------
    // Tag, Take, SIMD Scan, Framing, Alignment, and Blittable Casting
    // -------------------------------------------------------------------------

    /// <summary>
    /// Matches an exact sequence of magic/tag bytes (equivalent to <c>nom::bytes::tag</c> / <c>binrw</c> magic).
    /// In <see cref="InputMode.Streaming"/>, detects early mismatches immediately while returning
    /// <see cref="Incomplete"/> if the available prefix matches.
    /// </summary>
    public ParseResult<ByteSlice> Tag(ReadOnlySpan<byte> expected)
    {
        int start = _position;
        if (RemainingLength < expected.Length)
        {
            // Even in streaming mode, if the already-available bytes disagree, fail fast!
            if (_mode == InputMode.Streaming)
            {
                if (_buffer.Slice(start, RemainingLength).SequenceEqual(expected[..RemainingLength]))
                {
                    return Incomplete.AtLeast(expected.Length - RemainingLength);
                }

                return Backtrack.At(start, ErrorKind.Tag);
            }

            return Backtrack.At(start, ErrorKind.Tag);
        }

        if (!_buffer.Slice(start, expected.Length).SequenceEqual(expected))
        {
            return Backtrack.At(start, ErrorKind.Tag);
        }

        _position += expected.Length;
        return new Parsed<ByteSlice>(new ByteSlice(start, expected.Length));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<byte> TagByte(byte expected)
    {
        int pos = _position;
        if (pos >= _buffer.Length) return Shortage<byte>(1, ErrorKind.Tag);
        if (Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)pos) != expected)
            return Backtrack.At(pos, ErrorKind.Tag);
        _position = pos + 1;
        return new Parsed<byte>(expected);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<ByteSlice> Take(int count)
    {
        if (count < 0)
        {
            return Backtrack.At(_position, ErrorKind.Take, "Negative byte count");
        }

        if (RemainingLength < count)
        {
            return Shortage<ByteSlice>(count, ErrorKind.Take);
        }

        var slice = new ByteSlice(_position, count);
        _position += count;
        return new Parsed<ByteSlice>(slice);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<int> Skip(int count)
    {
        if (count < 0)
        {
            return Backtrack.At(_position, ErrorKind.Take, "Negative skip count");
        }

        if (RemainingLength < count)
        {
            return Shortage<int>(count, ErrorKind.Take);
        }

        _position += count;
        return new Parsed<int>(count);
    }

    /// <summary>
    /// Advances the cursor to the next multiple of <paramref name="alignment"/> bytes
    /// (inspired by Rust <c>binrw</c>'s <c>align_before</c>/<c>align_after</c>).
    /// </summary>
    public ParseResult<int> Align(int alignment)
    {
        if (alignment <= 0)
        {
            return Backtrack.At(_position, ErrorKind.Alignment, "Alignment must be positive");
        }

        int remainder = _position % alignment;
        int padding = remainder == 0 ? 0 : alignment - remainder;
        return Skip(padding);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<ByteSlice> TakeRest()
    {
        var slice = new ByteSlice(_position, RemainingLength);
        _position = _buffer.Length;
        return new Parsed<ByteSlice>(slice);
    }

    /// <summary>
    /// SIMD-accelerated <c>take_while0</c> using <see cref="SearchValues{T}"/>.
    /// </summary>
    public ParseResult<ByteSlice> TakeWhile(SearchValues<byte> allowed)
    {
        int start = _position;
        int idx = Remaining.IndexOfAnyExcept(allowed);
        if (idx < 0)
        {
            if (_mode == InputMode.Streaming)
            {
                return Incomplete.Unknown;
            }

            int len = RemainingLength;
            _position = _buffer.Length;
            return new Parsed<ByteSlice>(new ByteSlice(start, len));
        }

        _position += idx;
        return new Parsed<ByteSlice>(new ByteSlice(start, idx));
    }

    /// <summary>
    /// SIMD-accelerated <c>take_while1</c> using <see cref="SearchValues{T}"/>.
    /// </summary>
    public ParseResult<ByteSlice> TakeWhile1(SearchValues<byte> allowed)
    {
        int start = _position;
        return TakeWhile(allowed) switch
        {
            Parsed<ByteSlice>(var slice) when slice.Length > 0 => new Parsed<ByteSlice>(slice),
            Parsed<ByteSlice> => Backtrack.At(start, ErrorKind.TakeWhile),
            Incomplete inc => inc,
            Backtrack err => err,
            Failure fail => fail,
        };
    }

    public ParseResult<ByteSlice> TakeWhile(Func<byte, bool> predicate)
    {
        int start = _position;
        var rem = Remaining;
        int i = 0;
        while (i < rem.Length && predicate(rem[i]))
        {
            i++;
        }

        if (i == rem.Length && _mode == InputMode.Streaming)
        {
            return Incomplete.Unknown;
        }

        _position += i;
        return new Parsed<ByteSlice>(new ByteSlice(start, i));
    }

    public ParseResult<ByteSlice> TakeWhile1(Func<byte, bool> predicate)
    {
        int start = _position;
        return TakeWhile(predicate) switch
        {
            Parsed<ByteSlice>(var slice) when slice.Length > 0 => new Parsed<ByteSlice>(slice),
            Parsed<ByteSlice> => Backtrack.At(start, ErrorKind.TakeWhile),
            Incomplete inc => inc,
            Backtrack err => err,
            Failure fail => fail,
        };
    }

    /// <summary>
    /// SIMD-accelerated scan up to (not consuming) <paramref name="delimiter"/>.
    /// </summary>
    public ParseResult<ByteSlice> TakeUntil(byte delimiter)
    {
        int idx = Remaining.IndexOf(delimiter);
        if (idx < 0)
        {
            return UnknownShortage<ByteSlice>(ErrorKind.TakeUntil);
        }

        var slice = new ByteSlice(_position, idx);
        _position += idx;
        return new Parsed<ByteSlice>(slice);
    }

    /// <summary>
    /// SIMD-accelerated scan up to (not consuming) a multi-byte <paramref name="delimiter"/>.
    /// </summary>
    public ParseResult<ByteSlice> TakeUntil(ReadOnlySpan<byte> delimiter)
    {
        if (delimiter.IsEmpty)
        {
            return new Parsed<ByteSlice>(new ByteSlice(_position, 0));
        }

        int idx = Remaining.IndexOf(delimiter);
        if (idx < 0)
        {
            return UnknownShortage<ByteSlice>(ErrorKind.TakeUntil);
        }

        var slice = new ByteSlice(_position, idx);
        _position += idx;
        return new Parsed<ByteSlice>(slice);
    }

    /// <summary>
    /// SIMD-accelerated scan up to (not consuming) any byte in <paramref name="delimiters"/>.
    /// </summary>
    public ParseResult<ByteSlice> TakeUntilAny(SearchValues<byte> delimiters)
    {
        int idx = Remaining.IndexOfAny(delimiters);
        if (idx < 0)
        {
            return UnknownShortage<ByteSlice>(ErrorKind.TakeUntil);
        }

        var slice = new ByteSlice(_position, idx);
        _position += idx;
        return new Parsed<ByteSlice>(slice);
    }

    /// <summary>
    /// Zero-copy CRLF-terminated line reader (as used in Redis RESP, HTTP, SMTP).
    /// Consumes the trailing <c>\r\n</c> and returns the line body slice.
    /// </summary>
    public ParseResult<ByteSlice> TakeCrlfLine()
    {
        int idx = Remaining.IndexOf("\r\n"u8);
        if (idx < 0)
        {
            return UnknownShortage<ByteSlice>(ErrorKind.TakeUntil);
        }

        var slice = new ByteSlice(_position, idx);
        _position += idx + 2;
        return new Parsed<ByteSlice>(slice);
    }

    /// <summary>
    /// Reads a null-terminated C-string as a zero-copy <see cref="ByteSlice"/>, consuming the trailing <c>0x00</c>.
    /// </summary>
    public ParseResult<ByteSlice> ReadCString()
    {
        int idx = Remaining.IndexOf((byte)0);
        if (idx < 0)
        {
            return UnknownShortage<ByteSlice>(ErrorKind.TakeUntil);
        }

        var slice = new ByteSlice(_position, idx);
        _position += idx + 1;
        return new Parsed<ByteSlice>(slice);
    }

    /// <summary>
    /// Reads a length-prefixed binary slice in one step (equivalent to <c>nom::multi::length_data</c>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<ByteSlice> ReadLengthPrefixedBytes(LengthPrefix prefix)
    {
        var cp = Save();
        ulong length;
        switch (prefix)
        {
            case LengthPrefix.U8:
                var r8 = ReadU8();
                if (!r8.TryGetValue(out Parsed<byte> p8)) { Reset(cp); return r8.PropagateNonParsed<ByteSlice>(); }
                length = p8.Value;
                break;

            case LengthPrefix.U16:
                var r16 = ReadU16();
                if (!r16.TryGetValue(out Parsed<ushort> p16)) { Reset(cp); return r16.PropagateNonParsed<ByteSlice>(); }
                length = p16.Value;
                break;

            case LengthPrefix.U32:
                var r32 = ReadU32();
                if (!r32.TryGetValue(out Parsed<uint> p32)) { Reset(cp); return r32.PropagateNonParsed<ByteSlice>(); }
                length = p32.Value;
                break;

            case LengthPrefix.VarUInt64:
                var rVar = ReadVarUInt64();
                if (!rVar.TryGetValue(out Parsed<ulong> pVar)) { Reset(cp); return rVar.PropagateNonParsed<ByteSlice>(); }
                length = pVar.Value;
                break;

            default:
                return Backtrack.At(cp.Position, ErrorKind.LengthValue, "Unsupported length prefix");
        }

        if (length > int.MaxValue)
        {
            Reset(cp);
            return Backtrack.At(cp.Position, ErrorKind.LengthValue, "Length exceeds Int32.MaxValue");
        }

        var dataResult = Take((int)length);
        if (!dataResult.IsParsed)
        {
            Reset(cp);
        }

        return dataResult;
    }

    /// <summary>
    /// Reads and validates a UTF-8 string of <paramref name="byteCount"/> bytes.
    /// </summary>
    public ParseResult<string> ReadUtf8String(int byteCount)
    {
        var cp = Save();
        return Take(byteCount) switch
        {
            Parsed<ByteSlice>(var slice) when Utf8.IsValid(this[slice]) =>
                new Parsed<string>(Encoding.UTF8.GetString(this[slice])),
            Parsed<ByteSlice> => ResetAndBacktrack(ref this, cp, ErrorKind.InvalidUtf8, "Invalid UTF-8 byte sequence"),
            Incomplete inc => inc,
            Backtrack err => err,
            Failure fail => fail,
        };

        static ParseResult<string> ResetAndBacktrack(ref ByteCursor c, Checkpoint checkpoint, ErrorKind kind, string msg)
        {
            c.Reset(checkpoint);
            return Backtrack.At(checkpoint.Position, kind, msg);
        }
    }

    /// <summary>
    /// Zero-copy unmanaged struct decode via <see cref="MemoryMarshal.Read{T}"/>
    /// (inspired by Rust's <c>zerocopy::FromBytes</c>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<T> ReadBlittable<T>() where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        if (RemainingLength < size) return Shortage<T>(size);
        T value = MemoryMarshal.Read<T>(_buffer.Slice(_position, size));
        _position += size;
        return new Parsed<T>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ParseResult<byte> PeekU8() =>
        RemainingLength < 1 ? Shortage<byte>(1) : new Parsed<byte>(_buffer[_position]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ParseResult<ByteSlice> PeekSlice(int count)
    {
        if (count < 0) return Backtrack.At(_position, ErrorKind.Take, "Negative peek count");
        if (RemainingLength < count) return Shortage<ByteSlice>(count, ErrorKind.Take);
        return new Parsed<ByteSlice>(new ByteSlice(_position, count));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool StartsWith(ReadOnlySpan<byte> prefix) => Remaining.StartsWith(prefix);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ParseResult<None> ExpectEof() =>
        IsAtEnd
            ? new Parsed<None>(default)
            : Backtrack.At(_position, ErrorKind.ExpectedEof, $"Expected EOF with {RemainingLength} trailing bytes");

    /// <summary>
    /// Executes a bit-level parser (<see cref="BitCursor"/>) starting at the current byte position
    /// and advances <see cref="ByteCursor"/> by the byte-aligned count consumed on success.
    /// Equivalent to Rust's <c>nom::bits::bits</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<T> ParseBits<T>(BitParser<T> parser)
    {
        var bits = new BitCursor(_buffer[_position..], _position, _mode);
        var result = parser(ref bits);
        if (result.IsParsed)
        {
            _position += bits.ConsumedBytes;
        }

        return result;
    }

    /// <summary>
    /// Zero-cost monomorphized <see cref="IBitParser{T}"/> overload that allows full JIT inlining
    /// of bitfield headers without delegate indirection.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<T> ParseBits<TBitParser, T>(TBitParser parser)
        where TBitParser : struct, IBitParser<T>
    {
        var bits = new BitCursor(_buffer[_position..], _position, _mode);
        var result = parser.Parse(ref bits);
        if (result.IsParsed)
        {
            _position += bits.ConsumedBytes;
        }

        return result;
    }
}
