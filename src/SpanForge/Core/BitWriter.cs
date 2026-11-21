using System.Runtime.CompilerServices;

namespace SpanForge;

/// <summary>
/// Stack-only, MSB-first bit-level binary writer over <see cref="Span{T}"/>.
/// Symmetric counterpart to <see cref="BitCursor"/> for encoding bit-packed wire headers.
/// </summary>
public ref struct BitWriter
{
    private readonly Span<byte> _buffer;
    private readonly int _baseByteOffset;
    private int _bitPosition;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BitWriter(Span<byte> buffer, int baseByteOffset = 0)
    {
        _buffer = buffer;
        _baseByteOffset = baseByteOffset;
        _bitPosition = 0;
    }

    public readonly int BitPosition => _bitPosition;
    public readonly int TotalBits => _buffer.Length * 8;
    public readonly int RemainingBits => (_buffer.Length * 8) - _bitPosition;

    /// <summary>
    /// Number of whole bytes touched/written so far (rounded up to the next byte boundary).
    /// </summary>
    public readonly int WrittenBytes => (_bitPosition + 7) >> 3;

    public readonly int CurrentByteOffset => _baseByteOffset + (_bitPosition >> 3);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly BitCheckpoint Save() => new(_bitPosition);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset(BitCheckpoint checkpoint) => _bitPosition = checkpoint.BitPosition;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteBool(bool value) => TryWriteU64(1, value ? 1UL : 0UL);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteU8(int bitCount, byte value) =>
        (uint)bitCount <= 8u && TryWriteU64(bitCount, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteU16(int bitCount, ushort value) =>
        (uint)bitCount <= 16u && TryWriteU64(bitCount, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteU32(int bitCount, uint value) =>
        (uint)bitCount <= 32u && TryWriteU64(bitCount, value);

    /// <summary>
    /// Writes the low <paramref name="bitCount"/> bits (1..32) of a two's-complement signed integer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteI32(int bitCount, int value) =>
        bitCount is >= 1 and <= 32 && TryWriteU64(bitCount, unchecked((uint)value));

    /// <summary>
    /// Writes up to 64 MSB-first bits across arbitrary byte boundaries, overwriting only the targeted bit range.
    /// </summary>
    public bool TryWriteU64(int bitCount, ulong value)
    {
        if ((uint)bitCount > 64u || RemainingBits < bitCount) return false;
        if (bitCount == 0) return true;

        int remaining = bitCount;
        while (remaining > 0)
        {
            int byteIdx = _bitPosition >> 3;
            int bitOffset = _bitPosition & 7;
            int availInByte = 8 - bitOffset;
            int put = Math.Min(remaining, availInByte);

            int valueShift = remaining - put;
            byte chunk = (byte)((value >> valueShift) & ((1UL << put) - 1UL));

            int destShift = availInByte - put;
            byte clearMask = (byte)~(((1 << put) - 1) << destShift);
            _buffer[byteIdx] = (byte)((_buffer[byteIdx] & clearMask) | (chunk << destShift));

            _bitPosition += put;
            remaining -= put;
        }

        return true;
    }

    /// <summary>
    /// Zero-pads the remaining bits of the current partial byte (if any) and advances to the next byte boundary.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AlignToByte()
    {
        int rem = _bitPosition & 7;
        if (rem != 0)
        {
            int padBits = 8 - rem;
            int byteIdx = _bitPosition >> 3;
            byte keepMask = (byte)~((1 << padBits) - 1);
            _buffer[byteIdx] = (byte)(_buffer[byteIdx] & keepMask);
            _bitPosition += padBits;
        }
    }
}
