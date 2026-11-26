using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;

namespace SpanForge.Pipelines;

/// <summary>
/// Projects a parsed value together with its contiguous consumed frame bytes (allowing callers to
/// materialize or copy <see cref="ByteSlice"/> payloads before <see cref="PipeReader.AdvanceTo(SequencePosition)"/>
/// releases the underlying buffer segments).
/// </summary>
public delegate TOut SpanFrameProjector<in T, out TOut>(T value, ReadOnlySpan<byte> frameBytes);

/// <summary>
/// Low-level bridge from <see cref="ReadOnlySequence{T}"/> to SpanForge <see cref="ByteCursor"/>.
/// Uses a zero-copy, zero-allocation fast path when <see cref="ReadOnlySequence{T}.IsSingleSegment"/>
/// is <see langword="true"/>, and rents from <see cref="ArrayPool{T}.Shared"/> when a frame spans multiple segments.
/// </summary>
public static class PipeSequenceAdapter
{
    public const int DefaultMaxBufferSize = 1_048_576; // 1 MiB
    public const int StackAllocThreshold = 256;

    /// <summary>
    /// Attempts to parse one item from <paramref name="sequence"/> using a <see cref="SpanParser{T}"/>.
    /// </summary>
    public static bool TryParseSequence<T>(
        in ReadOnlySequence<byte> sequence,
        bool isCompleted,
        SpanParser<T> parser,
        out SequencePosition consumed,
        out ParseResult<T> result,
        Endianness endianness = Endianness.Big,
        int maxBufferSize = DefaultMaxBufferSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBufferSize);
        InputMode mode = isCompleted ? InputMode.Complete : InputMode.Streaming;

        if (sequence.IsSingleSegment)
        {
            ReadOnlySpan<byte> span = sequence.FirstSpan;
            var cursor = new ByteCursor(span, endianness, mode);
            result = parser(ref cursor);
            if (result.IsParsed)
            {
                consumed = sequence.GetPosition(cursor.Position);
                return true;
            }

            consumed = sequence.Start;
            return false;
        }

        int copyLen = (int)Math.Min(sequence.Length, maxBufferSize);
        InputMode effectiveMode = (isCompleted && copyLen == sequence.Length) ? InputMode.Complete : InputMode.Streaming;

        if (copyLen <= StackAllocThreshold)
        {
            Span<byte> stackBuf = stackalloc byte[StackAllocThreshold];
            Span<byte> slice = stackBuf[..copyLen];
            sequence.Slice(0, copyLen).CopyTo(slice);
            var cursor = new ByteCursor(slice, endianness, effectiveMode);
            result = parser(ref cursor);
            if (result.IsParsed)
            {
                consumed = sequence.GetPosition(cursor.Position);
                return true;
            }

            consumed = sequence.Start;
            return false;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(copyLen);
        try
        {
            sequence.Slice(0, copyLen).CopyTo(rented);
            var cursor = new ByteCursor(rented.AsSpan(0, copyLen), endianness, effectiveMode);
            result = parser(ref cursor);
            if (result.IsParsed)
            {
                consumed = sequence.GetPosition(cursor.Position);
                return true;
            }

            consumed = sequence.Start;
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Attempts to parse one item from <paramref name="sequence"/> and immediately projects it with its
    /// contiguous consumed frame span while the buffer is still pinned/alive.
    /// </summary>
    public static bool TryParseSequence<T, TOut>(
        in ReadOnlySequence<byte> sequence,
        bool isCompleted,
        SpanParser<T> parser,
        SpanFrameProjector<T, TOut> projector,
        out SequencePosition consumed,
        out ParseResult<TOut> result,
        Endianness endianness = Endianness.Big,
        int maxBufferSize = DefaultMaxBufferSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBufferSize);
        InputMode mode = isCompleted ? InputMode.Complete : InputMode.Streaming;

        if (sequence.IsSingleSegment)
        {
            ReadOnlySpan<byte> span = sequence.FirstSpan;
            var cursor = new ByteCursor(span, endianness, mode);
            var raw = parser(ref cursor);
            if (raw.TryGetValue(out Parsed<T> ok))
            {
                TOut projected = projector(ok.Value, span[..cursor.Position]);
                consumed = sequence.GetPosition(cursor.Position);
                result = new Parsed<TOut>(projected);
                return true;
            }

            consumed = sequence.Start;
            result = raw.PropagateNonParsed<TOut>();
            return false;
        }

        int copyLen = (int)Math.Min(sequence.Length, maxBufferSize);
        InputMode effectiveMode = (isCompleted && copyLen == sequence.Length) ? InputMode.Complete : InputMode.Streaming;

        if (copyLen <= StackAllocThreshold)
        {
            Span<byte> stackBuf = stackalloc byte[StackAllocThreshold];
            Span<byte> slice = stackBuf[..copyLen];
            sequence.Slice(0, copyLen).CopyTo(slice);
            var cursor = new ByteCursor(slice, endianness, effectiveMode);
            var raw = parser(ref cursor);
            if (raw.TryGetValue(out Parsed<T> ok))
            {
                TOut projected = projector(ok.Value, slice[..cursor.Position]);
                consumed = sequence.GetPosition(cursor.Position);
                result = new Parsed<TOut>(projected);
                return true;
            }

            consumed = sequence.Start;
            result = raw.PropagateNonParsed<TOut>();
            return false;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(copyLen);
        try
        {
            sequence.Slice(0, copyLen).CopyTo(rented);
            ReadOnlySpan<byte> span = rented.AsSpan(0, copyLen);
            var cursor = new ByteCursor(span, endianness, effectiveMode);
            var raw = parser(ref cursor);
            if (raw.TryGetValue(out Parsed<T> ok))
            {
                TOut projected = projector(ok.Value, span[..cursor.Position]);
                consumed = sequence.GetPosition(cursor.Position);
                result = new Parsed<TOut>(projected);
                return true;
            }

            consumed = sequence.Start;
            result = raw.PropagateNonParsed<TOut>();
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Monomorphized high-throughput <see cref="ITryParser{T}"/> overload over <see cref="ReadOnlySequence{T}"/>.
    /// </summary>
    public static bool TryParseSequence<TParser, T>(
        in ReadOnlySequence<byte> sequence,
        bool isCompleted,
        TParser parser,
        out SequencePosition consumed,
        out FastStatus status,
        out T value,
        Endianness endianness = Endianness.Big,
        int maxBufferSize = DefaultMaxBufferSize)
        where TParser : struct, ITryParser<T>
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBufferSize);
        InputMode mode = isCompleted ? InputMode.Complete : InputMode.Streaming;

        if (sequence.IsSingleSegment)
        {
            var cursor = new ByteCursor(sequence.FirstSpan, endianness, mode);
            status = parser.TryParse(ref cursor, out value);
            if (status.IsParsed)
            {
                consumed = sequence.GetPosition(cursor.Position);
                return true;
            }

            consumed = sequence.Start;
            return false;
        }

        int copyLen = (int)Math.Min(sequence.Length, maxBufferSize);
        InputMode effectiveMode = (isCompleted && copyLen == sequence.Length) ? InputMode.Complete : InputMode.Streaming;

        if (copyLen <= StackAllocThreshold)
        {
            Span<byte> stackBuf = stackalloc byte[StackAllocThreshold];
            Span<byte> slice = stackBuf[..copyLen];
            sequence.Slice(0, copyLen).CopyTo(slice);
            var cursor = new ByteCursor(slice, endianness, effectiveMode);
            status = parser.TryParse(ref cursor, out value);
            if (status.IsParsed)
            {
                consumed = sequence.GetPosition(cursor.Position);
                return true;
            }

            consumed = sequence.Start;
            return false;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(copyLen);
        try
        {
            sequence.Slice(0, copyLen).CopyTo(rented);
            var cursor = new ByteCursor(rented.AsSpan(0, copyLen), endianness, effectiveMode);
            status = parser.TryParse(ref cursor, out value);
            if (status.IsParsed)
            {
                consumed = sequence.GetPosition(cursor.Position);
                return true;
            }

            consumed = sequence.Start;
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}

/// <summary>
/// High-level async framing reader bridging <see cref="PipeReader"/> with SpanForge parsers.
/// Handles backpressure via exact <see cref="PipeReader.AdvanceTo(SequencePosition, SequencePosition)"/>,
/// DoS buffer caps, multi-segment sequences, and zero-boxing C# 15 <see cref="PipeParseOutcome{T}"/> results.
/// </summary>
public static class PipeParser
{
    /// <summary>
    /// Reads and parses the next frame from <paramref name="reader"/> using <paramref name="parser"/>.
    /// </summary>
    public static async ValueTask<PipeParseOutcome<T>> ReadOneAsync<T>(
        PipeReader reader,
        SpanParser<T> parser,
        Endianness endianness = Endianness.Big,
        int maxBufferSize = PipeSequenceAdapter.DefaultMaxBufferSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBufferSize);

        while (true)
        {
            ReadResult read = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = read.Buffer;

            if (buffer.IsEmpty)
            {
                reader.AdvanceTo(buffer.Start, buffer.End);
                if (read.IsCompleted)
                {
                    return new PipeEndOfStream();
                }

                continue;
            }

            if (PipeSequenceAdapter.TryParseSequence(
                in buffer,
                read.IsCompleted,
                parser,
                out SequencePosition consumed,
                out ParseResult<T> parseResult,
                endianness,
                maxBufferSize))
            {
                reader.AdvanceTo(consumed);
                return new PipeParsed<T>(parseResult.Unwrap());
            }

            if (parseResult.TryGetValue(out Incomplete inc))
            {
                if (buffer.Length >= maxBufferSize)
                {
                    reader.AdvanceTo(buffer.Start);
                    return new PipeFailed(new ParseError(0, ErrorKind.LengthValue, $"Frame exceeded maxBufferSize ({maxBufferSize} bytes)"));
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                if (read.IsCompleted)
                {
                    return new PipeIncompleteAtEof(inc.Needed);
                }

                continue;
            }

            reader.AdvanceTo(buffer.Start);
            if (parseResult.TryGetValue(out Failure fail))
            {
                return new PipeFailed(fail.Error);
            }

            if (parseResult.TryGetValue(out Backtrack bt))
            {
                return new PipeFailed(bt.Error);
            }

            return new PipeFailed(new ParseError(0, ErrorKind.Custom, "Unknown parse error"));
        }
    }

    /// <summary>
    /// Reads and parses the next frame from <paramref name="reader"/>, invoking <paramref name="projector"/>
    /// with the parsed value and its contiguous frame bytes before advancing the pipe.
    /// Ideal for <see cref="ByteSlice"/>-bearing frames such as <c>WebSocketFrame</c>, <c>PngChunk</c>, or <c>Http2Frame</c>.
    /// </summary>
    public static async ValueTask<PipeParseOutcome<TOut>> ReadOneAsync<T, TOut>(
        PipeReader reader,
        SpanParser<T> parser,
        SpanFrameProjector<T, TOut> projector,
        Endianness endianness = Endianness.Big,
        int maxBufferSize = PipeSequenceAdapter.DefaultMaxBufferSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(projector);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBufferSize);

        while (true)
        {
            ReadResult read = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = read.Buffer;

            if (buffer.IsEmpty)
            {
                reader.AdvanceTo(buffer.Start, buffer.End);
                if (read.IsCompleted)
                {
                    return new PipeEndOfStream();
                }

                continue;
            }

            if (PipeSequenceAdapter.TryParseSequence(
                in buffer,
                read.IsCompleted,
                parser,
                projector,
                out SequencePosition consumed,
                out ParseResult<TOut> parseResult,
                endianness,
                maxBufferSize))
            {
                reader.AdvanceTo(consumed);
                return new PipeParsed<TOut>(parseResult.Unwrap());
            }

            if (parseResult.TryGetValue(out Incomplete inc))
            {
                if (buffer.Length >= maxBufferSize)
                {
                    reader.AdvanceTo(buffer.Start);
                    return new PipeFailed(new ParseError(0, ErrorKind.LengthValue, $"Frame exceeded maxBufferSize ({maxBufferSize} bytes)"));
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                if (read.IsCompleted)
                {
                    return new PipeIncompleteAtEof(inc.Needed);
                }

                continue;
            }

            reader.AdvanceTo(buffer.Start);
            if (parseResult.TryGetValue(out Failure fail))
            {
                return new PipeFailed(fail.Error);
            }

            if (parseResult.TryGetValue(out Backtrack bt))
            {
                return new PipeFailed(bt.Error);
            }

            return new PipeFailed(new ParseError(0, ErrorKind.Custom, "Unknown parse error"));
        }
    }

    /// <summary>
    /// Monomorphized zero-delegate <see cref="ITryParser{T}"/> async reader over <see cref="PipeReader"/>.
    /// </summary>
    public static async ValueTask<PipeParseOutcome<T>> ReadOneAsync<TParser, T>(
        PipeReader reader,
        TParser parser,
        Endianness endianness = Endianness.Big,
        int maxBufferSize = PipeSequenceAdapter.DefaultMaxBufferSize,
        CancellationToken cancellationToken = default)
        where TParser : struct, ITryParser<T>
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBufferSize);

        while (true)
        {
            ReadResult read = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = read.Buffer;

            if (buffer.IsEmpty)
            {
                reader.AdvanceTo(buffer.Start, buffer.End);
                if (read.IsCompleted)
                {
                    return new PipeEndOfStream();
                }

                continue;
            }

            if (PipeSequenceAdapter.TryParseSequence(
                in buffer,
                read.IsCompleted,
                parser,
                out SequencePosition consumed,
                out FastStatus status,
                out T value,
                endianness,
                maxBufferSize))
            {
                reader.AdvanceTo(consumed);
                return new PipeParsed<T>(value);
            }

            if (status.IsIncomplete)
            {
                if (buffer.Length >= maxBufferSize)
                {
                    reader.AdvanceTo(buffer.Start);
                    return new PipeFailed(new ParseError(0, ErrorKind.LengthValue, $"Frame exceeded maxBufferSize ({maxBufferSize} bytes)"));
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                if (read.IsCompleted)
                {
                    return new PipeIncompleteAtEof(status.UnpackNeeded());
                }

                continue;
            }

            reader.AdvanceTo(buffer.Start);
            return new PipeFailed(new ParseError(0, status.IsFailure ? ErrorKind.Protocol : ErrorKind.Eof));
        }
    }

    /// <summary>
    /// Streams parsed frames asynchronously as an <see cref="IAsyncEnumerable{T}"/> of
    /// <see cref="PipeParseOutcome{T}"/> (railroad errors-as-values: stops cleanly on
    /// <see cref="PipeEndOfStream"/>, or yields a terminal <see cref="PipeIncompleteAtEof"/> / <see cref="PipeFailed"/>).
    /// </summary>
    public static async IAsyncEnumerable<PipeParseOutcome<T>> ReadAllAsync<T>(
        PipeReader reader,
        SpanParser<T> parser,
        Endianness endianness = Endianness.Big,
        int maxBufferSize = PipeSequenceAdapter.DefaultMaxBufferSize,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var outcome = await ReadOneAsync(reader, parser, endianness, maxBufferSize, cancellationToken)
                .ConfigureAwait(false);

            if (outcome.IsEndOfStream)
            {
                yield break;
            }

            yield return outcome;

            if (!outcome.IsParsed)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Streams projected frames asynchronously as an <see cref="IAsyncEnumerable{T}"/> of
    /// <see cref="PipeParseOutcome{TOut}"/>, allowing <see cref="ByteSlice"/> payloads to be decoded
    /// safely before each pipe advance.
    /// </summary>
    public static async IAsyncEnumerable<PipeParseOutcome<TOut>> ReadAllAsync<T, TOut>(
        PipeReader reader,
        SpanParser<T> parser,
        SpanFrameProjector<T, TOut> projector,
        Endianness endianness = Endianness.Big,
        int maxBufferSize = PipeSequenceAdapter.DefaultMaxBufferSize,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var outcome = await ReadOneAsync(reader, parser, projector, endianness, maxBufferSize, cancellationToken)
                .ConfigureAwait(false);

            if (outcome.IsEndOfStream)
            {
                yield break;
            }

            yield return outcome;

            if (!outcome.IsParsed)
            {
                yield break;
            }
        }
    }
}
