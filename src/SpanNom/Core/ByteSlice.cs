using System.Runtime.CompilerServices;
using System.Text;

namespace SpanNom;

/// <summary>
/// Zero-copy, non-ref-struct reference to a contiguous byte range within a source buffer.
/// Because <see cref="ByteSlice"/> is an 8-byte value type rather than a <c>ref struct</c>,
/// it can live directly inside C# 15 <c>union</c> types, custom <c>[Union]</c> structs,
/// records, and collections without copying a single payload byte.
/// </summary>
public readonly record struct ByteSlice(int Offset, int Length)
{
    public static ByteSlice Empty => new(0, 0);

    public int End => Offset + Length;
    public bool IsEmpty => Length == 0;
    public Range AsRange => Offset..(Offset + Length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> AsSpan(ReadOnlySpan<byte> source) => source.Slice(Offset, Length);

    public string AsUtf8String(ReadOnlySpan<byte> source) => Encoding.UTF8.GetString(AsSpan(source));

    public string AsAsciiString(ReadOnlySpan<byte> source) => Encoding.ASCII.GetString(AsSpan(source));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SequenceEqual(ReadOnlySpan<byte> source, ReadOnlySpan<byte> expected) =>
        AsSpan(source).SequenceEqual(expected);

    public string ToHexString(ReadOnlySpan<byte> source) => Convert.ToHexString(AsSpan(source));

    public override string ToString() => $"[{Offset}..{End}] ({Length} bytes)";
}

/// <summary>
/// C# 15 extension block and extension indexer enabling zero-copy <c>buffer[slice]</c>
/// indexing and fluent cursor creation directly on <see cref="ReadOnlySpan{T}"/>.
/// </summary>
public static class ByteSpanExtensions
{
    extension(ReadOnlySpan<byte> span)
    {
        /// <summary>
        /// C# 15 extension indexer: slices a <see cref="ReadOnlySpan{T}"/> directly using a <see cref="ByteSlice"/>.
        /// </summary>
        public ReadOnlySpan<byte> this[ByteSlice slice]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => span.Slice(slice.Offset, slice.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ByteCursor AsCursor(
            Endianness endianness = Endianness.Big,
            InputMode mode = InputMode.Complete) =>
            new(span, endianness, mode);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ByteCursor AsStreamingCursor(Endianness endianness = Endianness.Big) =>
            new(span, endianness, InputMode.Streaming);
    }

    extension(byte[] array)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ByteCursor AsCursor(
            Endianness endianness = Endianness.Big,
            InputMode mode = InputMode.Complete) =>
            new(array, endianness, mode);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ByteCursor AsStreamingCursor(Endianness endianness = Endianness.Big) =>
            new(array, endianness, InputMode.Streaming);
    }
}
