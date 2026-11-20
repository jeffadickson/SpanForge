using System.Runtime.CompilerServices;

namespace SpanForge;

/// <summary>Saved bit-level cursor position for O(1) rollback.</summary>
public readonly record struct BitCheckpoint(int BitPosition);

/// <summary>Delegate for bit-level parsers operating on a <see cref="BitCursor"/>.</summary>
public delegate ParseResult<T> BitParser<T>(ref BitCursor bits);

/// <summary>Zero-cost monomorphized bit-level parser interface for <see cref="ByteCursor.ParseBits{TParser, T}"/>.</summary>
public interface IBitParser<T>
{
    ParseResult<T> Parse(ref BitCursor bits);
}

/// <summary>
/// Stack-only, MSB-first bit-level binary cursor over <see cref="ReadOnlySpan{T}"/>
/// (inspired by Rust's <c>nom::bits</c> and <c>deku</c>).
/// Ideal for parsing bit-packed wire headers (WebSocket, DNS, IPv4, TCP, HTTP/2, MPEG).
/// </summary>
public ref struct BitCursor
{
    private readonly ReadOnlySpan<byte> _buffer;
    private readonly int _baseByteOffset;
    private readonly InputMode _mode;
    private int _bitPosition;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BitCursor(ReadOnlySpan<byte> buffer, int baseByteOffset = 0, InputMode mode = InputMode.Complete)
    {
        _buffer = buffer;
        _baseByteOffset = baseByteOffset;
        _mode = mode;
        _bitPosition = 0;
    }

    public readonly int BitPosition => _bitPosition;
    public readonly int TotalBits => _buffer.Length * 8;
    public readonly int RemainingBits => (_buffer.Length * 8) - _bitPosition;

    /// <summary>
    /// Number of whole bytes touched/consumed so far (rounded up to the next byte boundary).
    /// </summary>
    public readonly int ConsumedBytes => (_bitPosition + 7) >> 3;

    public readonly int CurrentByteOffset => _baseByteOffset + (_bitPosition >> 3);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly BitCheckpoint Save() => new(_bitPosition);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset(BitCheckpoint checkpoint) => _bitPosition = checkpoint.BitPosition;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly ParseResult<T> BitShortage<T>(int requiredBits)
    {
        int missingBits = requiredBits - RemainingBits;
        int missingBytes = (missingBits + 7) >> 3;
        return _mode == InputMode.Streaming
            ? Incomplete.AtLeast(missingBytes > 0 ? missingBytes : 1)
            : Backtrack.At(CurrentByteOffset, ErrorKind.BitRead);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<bool> ReadBool()
    {
        if (RemainingBits < 1) return BitShortage<bool>(1);
        int byteIdx = _bitPosition >> 3;
        int bitShift = 7 - (_bitPosition & 7);
        bool value = ((_buffer[byteIdx] >> bitShift) & 1) != 0;
        _bitPosition++;
        return new Parsed<bool>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<byte> ReadU8(int bitCount)
    {
        if ((uint)bitCount > 8u)
            return Backtrack.At(CurrentByteOffset, ErrorKind.BitRead, "bitCount must be in [0..8]");

        if (bitCount == 0) return new Parsed<byte>(0);
        if (RemainingBits < bitCount) return BitShortage<byte>(bitCount);

        int byteIdx = _bitPosition >> 3;
        int bitOffset = _bitPosition & 7;
        int availInByte = 8 - bitOffset;

        byte value;
        if (bitCount <= availInByte)
        {
            int shift = availInByte - bitCount;
            value = (byte)((_buffer[byteIdx] >> shift) & ((1 << bitCount) - 1));
        }
        else
        {
            int nextBits = bitCount - availInByte;
            int hi = _buffer[byteIdx] & ((1 << availInByte) - 1);
            int lo = _buffer[byteIdx + 1] >> (8 - nextBits);
            value = (byte)((hi << nextBits) | lo);
        }

        _bitPosition += bitCount;
        return new Parsed<byte>(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<ushort> ReadU16(int bitCount)
    {
        if (bitCount is < 0 or > 16)
            return Backtrack.At(CurrentByteOffset, ErrorKind.BitRead, "bitCount must be in [0..16]");

        return ReadU64(bitCount) switch
        {
            Parsed<ulong>(var v) => new Parsed<ushort>((ushort)v),
            Incomplete inc => inc,
            Backtrack err => err,
            Failure fail => fail,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<uint> ReadU32(int bitCount)
    {
        if (bitCount is < 0 or > 32)
            return Backtrack.At(CurrentByteOffset, ErrorKind.BitRead, "bitCount must be in [0..32]");

        return ReadU64(bitCount) switch
        {
            Parsed<ulong>(var v) => new Parsed<uint>((uint)v),
            Incomplete inc => inc,
            Backtrack err => err,
            Failure fail => fail,
        };
    }

    /// <summary>
    /// Reads a two's-complement signed integer of <paramref name="bitCount"/> bits (1..32) with sign extension.
    /// </summary>
    public ParseResult<int> ReadI32(int bitCount)
    {
        if (bitCount is < 1 or > 32)
            return Backtrack.At(CurrentByteOffset, ErrorKind.BitRead, "bitCount must be in [1..32]");

        return ReadU64(bitCount) switch
        {
            Parsed<ulong>(var raw) => new Parsed<int>(SignExtend32((uint)raw, bitCount)),
            Incomplete inc => inc,
            Backtrack err => err,
            Failure fail => fail,
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int SignExtend32(uint value, int bits)
        {
            int shift = 32 - bits;
            return (int)(value << shift) >> shift;
        }
    }

    /// <summary>
    /// Reads up to 64 MSB-first bits across arbitrary byte boundaries with zero heap allocations.
    /// </summary>
    public ParseResult<ulong> ReadU64(int bitCount)
    {
        if (bitCount is < 0 or > 64)
            return Backtrack.At(CurrentByteOffset, ErrorKind.BitRead, "bitCount must be in [0..64]");

        if (bitCount == 0) return new Parsed<ulong>(0);
        if (RemainingBits < bitCount) return BitShortage<ulong>(bitCount);

        ulong result = 0;
        int remaining = bitCount;

        while (remaining > 0)
        {
            int byteIdx = _bitPosition >> 3;
            int bitOffset = _bitPosition & 7;
            int availInByte = 8 - bitOffset;
            int take = Math.Min(remaining, availInByte);

            int shift = availInByte - take;
            byte mask = (byte)((1 << take) - 1);
            byte chunk = (byte)((_buffer[byteIdx] >> shift) & mask);

            result = (result << take) | chunk;
            _bitPosition += take;
            remaining -= take;
        }

        return new Parsed<ulong>(result);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseResult<int> SkipBits(int bitCount)
    {
        if (bitCount < 0)
            return Backtrack.At(CurrentByteOffset, ErrorKind.BitRead, "Negative bit skip");

        if (RemainingBits < bitCount) return BitShortage<int>(bitCount);
        _bitPosition += bitCount;
        return new Parsed<int>(bitCount);
    }

    /// <summary>
    /// Advances to the next byte boundary if not already byte-aligned.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AlignToByte()
    {
        int rem = _bitPosition & 7;
        if (rem != 0)
        {
            _bitPosition += 8 - rem;
        }
    }
}
