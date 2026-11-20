namespace SpanForge.Streaming;

/// <summary>
/// Incremental streaming frame decoder that bridges chunked network/file I/O with
/// <see cref="SpanParser{T}"/> parsers returning C# 15 <see cref="ParseResult{T}"/> unions.
/// <para>
/// Feed arbitrary chunks (even 1 byte at a time) via <see cref="Append"/> and pull decoded frames
/// via <see cref="TryDecodeNext"/>. When the parser returns <see cref="Incomplete"/>, unconsumed bytes
/// remain buffered automatically until more data arrives.
/// </para>
/// </summary>
public sealed class StreamingDecoder<T>
{
    private readonly SpanParser<T> _parser;
    private readonly Endianness _endianness;
    private byte[] _buffer;
    private int _readOffset;
    private int _writeOffset;

    public StreamingDecoder(
        SpanParser<T> parser,
        Endianness endianness = Endianness.Big,
        int initialCapacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCapacity);
        _parser = parser;
        _endianness = endianness;
        _buffer = new byte[initialCapacity];
    }

    /// <summary>Currently buffered unread bytes.</summary>
    public ReadOnlySpan<byte> BufferedBytes => _buffer.AsSpan(_readOffset, _writeOffset - _readOffset);

    public int BufferedCount => _writeOffset - _readOffset;

    /// <summary>Appends an incoming chunk of bytes to the decoder's buffer.</summary>
    public void Append(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty) return;

        EnsureCapacity(chunk.Length);
        chunk.CopyTo(_buffer.AsSpan(_writeOffset));
        _writeOffset += chunk.Length;
    }

    /// <summary>
    /// Attempts to decode the next item in <see cref="InputMode.Streaming"/> (or <see cref="InputMode.Complete"/>
    /// when <paramref name="isFinalChunk"/> is <see langword="true"/>).
    /// <para>
    /// Notice: If <typeparamref name="T"/> contains <see cref="ByteSlice"/> offsets, inspect them against
    /// <paramref name="consumedFrame"/> before calling <see cref="TryDecodeNext"/> again.
    /// </para>
    /// </summary>
    public ParseResult<T> TryDecodeNext(out ReadOnlySpan<byte> consumedFrame, bool isFinalChunk = false)
    {
        var available = _buffer.AsSpan(_readOffset, _writeOffset - _readOffset);
        var mode = isFinalChunk ? InputMode.Complete : InputMode.Streaming;
        var cursor = new ByteCursor(available, _endianness, mode);

        var result = _parser(ref cursor);
        if (result.IsParsed)
        {
            consumedFrame = available[..cursor.Position];
            _readOffset += cursor.Position;
            if (_readOffset == _writeOffset)
            {
                _readOffset = 0;
                _writeOffset = 0;
            }

            return result;
        }

        consumedFrame = default;
        return result;
    }

    /// <summary>Convenience overload when the caller does not need the raw consumed frame span.</summary>
    public ParseResult<T> TryDecodeNext(bool isFinalChunk = false) =>
        TryDecodeNext(out _, isFinalChunk);

    public void Clear()
    {
        _readOffset = 0;
        _writeOffset = 0;
    }

    private void EnsureCapacity(int incomingBytes)
    {
        int freeAtTail = _buffer.Length - _writeOffset;
        if (freeAtTail >= incomingBytes) return;

        int buffered = _writeOffset - _readOffset;
        if (_readOffset > 0 && (_buffer.Length - buffered) >= incomingBytes)
        {
            // Compact in-place without allocating.
            Buffer.BlockCopy(_buffer, _readOffset, _buffer, 0, buffered);
            _readOffset = 0;
            _writeOffset = buffered;
            return;
        }

        int required = buffered + incomingBytes;
        int newSize = Math.Max(_buffer.Length * 2, required);
        var next = new byte[newSize];
        if (buffered > 0)
        {
            Buffer.BlockCopy(_buffer, _readOffset, next, 0, buffered);
        }

        _buffer = next;
        _readOffset = 0;
        _writeOffset = buffered;
    }
}
