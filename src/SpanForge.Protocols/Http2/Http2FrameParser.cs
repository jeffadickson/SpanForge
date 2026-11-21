using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace SpanForge.Protocols.Http2;

/// <summary>RFC 9113 §6 HTTP/2 frame types.</summary>
public enum Http2FrameType : byte
{
    Data = 0x0,
    Headers = 0x1,
    Priority = 0x2,
    RstStream = 0x3,
    Settings = 0x4,
    PushPromise = 0x5,
    Ping = 0x6,
    GoAway = 0x7,
    WindowUpdate = 0x8,
    Continuation = 0x9,
}

[Flags]
public enum Http2FrameFlags : byte
{
    None = 0x00,
    Ack = 0x01,         // SETTINGS, PING
    EndStream = 0x01,   // DATA, HEADERS
    EndHeaders = 0x04,  // HEADERS, PUSH_PROMISE, CONTINUATION
    Padded = 0x08,      // DATA, HEADERS, PUSH_PROMISE
    Priority = 0x20,    // HEADERS
}

public readonly record struct Http2PrioritySpec(bool Exclusive, uint StreamDependency, byte Weight);
public readonly record struct Http2SettingEntry(ushort Id, uint Value);

public readonly record struct Http2DataFrame(uint StreamId, Http2FrameFlags Flags, ByteSlice Data, byte PadLength);
public readonly record struct Http2HeadersFrame(uint StreamId, Http2FrameFlags Flags, Option<Http2PrioritySpec> Priority, ByteSlice HeaderBlockFragment, byte PadLength);
public readonly record struct Http2PriorityFrame(uint StreamId, Http2PrioritySpec Priority);
public readonly record struct Http2RstStreamFrame(uint StreamId, uint ErrorCode);
public readonly record struct Http2SettingsFrame(Http2FrameFlags Flags, ByteSlice Entries);
public readonly record struct Http2PushPromiseFrame(uint StreamId, Http2FrameFlags Flags, uint PromisedStreamId, ByteSlice HeaderBlockFragment, byte PadLength);
public readonly record struct Http2PingFrame(Http2FrameFlags Flags, ulong OpaqueData);
public readonly record struct Http2GoAwayFrame(uint LastStreamId, uint ErrorCode, ByteSlice DebugData);
public readonly record struct Http2WindowUpdateFrame(uint StreamId, uint WindowSizeIncrement);
public readonly record struct Http2ContinuationFrame(uint StreamId, Http2FrameFlags Flags, ByteSlice HeaderBlockFragment);
public readonly record struct Http2UnknownFrame(byte RawType, Http2FrameFlags Flags, uint StreamId, ByteSlice Payload);

/// <summary>
/// Compact zero-boxing C# 15 custom <c>[Union]</c> modeling all RFC 9113 HTTP/2 frame variants
/// with zero heap allocations and compiler-enforced exhaustive switching.
/// </summary>
[Union]
public readonly struct Http2Frame : IUnion
{
    private readonly ByteSlice _slice;
    private readonly ulong _extra64;   // Ping OpaqueData, or (auxUint << 32) | streamId
    private readonly uint _aux32;      // ErrorCode / StreamDependency / WindowIncrement / PromisedStreamId / LastStreamId
    private readonly Http2FrameFlags _flags;
    private readonly byte _auxByte;    // PadLength or Weight or RawType
    private readonly byte _auxByte2;   // Bit0=HasPriority, Bit1=Exclusive, high bits unused
    private readonly byte _tag;        // 1..11

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Http2Frame(byte tag, ByteSlice slice, ulong extra64, uint aux32, Http2FrameFlags flags, byte auxByte, byte auxByte2)
    {
        _tag = tag;
        _slice = slice;
        _extra64 = extra64;
        _aux32 = aux32;
        _flags = flags;
        _auxByte = auxByte;
        _auxByte2 = auxByte2;
    }

    public Http2Frame(Http2DataFrame f)
        : this(1, f.Data, f.StreamId, 0, f.Flags, f.PadLength, 0) { }

    public Http2Frame(Http2HeadersFrame f)
    {
        _tag = 2;
        _slice = f.HeaderBlockFragment;
        _flags = f.Flags;
        _auxByte = f.PadLength;
        if (f.Priority.TryGetValue(out Some<Http2PrioritySpec> p))
        {
            _extra64 = ((ulong)p.Value.Weight << 32) | f.StreamId;
            _aux32 = p.Value.StreamDependency;
            _auxByte2 = (byte)(1 | (p.Value.Exclusive ? 2 : 0));
        }
        else
        {
            _extra64 = f.StreamId;
            _aux32 = 0;
            _auxByte2 = 0;
        }
    }

    public Http2Frame(Http2PriorityFrame f)
        : this(3, default, f.StreamId, f.Priority.StreamDependency, Http2FrameFlags.None, f.Priority.Weight, (byte)(f.Priority.Exclusive ? 2 : 0)) { }

    public Http2Frame(Http2RstStreamFrame f)
        : this(4, default, f.StreamId, f.ErrorCode, Http2FrameFlags.None, 0, 0) { }

    public Http2Frame(Http2SettingsFrame f)
        : this(5, f.Entries, 0, 0, f.Flags, 0, 0) { }

    public Http2Frame(Http2PushPromiseFrame f)
        : this(6, f.HeaderBlockFragment, f.StreamId, f.PromisedStreamId, f.Flags, f.PadLength, 0) { }

    public Http2Frame(Http2PingFrame f)
        : this(7, default, f.OpaqueData, 0, f.Flags, 0, 0) { }

    public Http2Frame(Http2GoAwayFrame f)
        : this(8, f.DebugData, f.LastStreamId, f.ErrorCode, Http2FrameFlags.None, 0, 0) { }

    public Http2Frame(Http2WindowUpdateFrame f)
        : this(9, default, f.StreamId, f.WindowSizeIncrement, Http2FrameFlags.None, 0, 0) { }

    public Http2Frame(Http2ContinuationFrame f)
        : this(10, f.HeaderBlockFragment, f.StreamId, 0, f.Flags, 0, 0) { }

    public Http2Frame(Http2UnknownFrame f)
        : this(11, f.Payload, f.StreamId, 0, f.Flags, f.RawType, 0) { }

    public object? Value => _tag switch
    {
        1 => new Http2DataFrame((uint)_extra64, _flags, _slice, _auxByte),
        2 => new Http2HeadersFrame((uint)_extra64, _flags, UnpackPriority(), _slice, _auxByte),
        3 => new Http2PriorityFrame((uint)_extra64, new Http2PrioritySpec((_auxByte2 & 2) != 0, _aux32, _auxByte)),
        4 => new Http2RstStreamFrame((uint)_extra64, _aux32),
        5 => new Http2SettingsFrame(_flags, _slice),
        6 => new Http2PushPromiseFrame((uint)_extra64, _flags, _aux32, _slice, _auxByte),
        7 => new Http2PingFrame(_flags, _extra64),
        8 => new Http2GoAwayFrame((uint)_extra64, _aux32, _slice),
        9 => new Http2WindowUpdateFrame((uint)_extra64, _aux32),
        10 => new Http2ContinuationFrame((uint)_extra64, _flags, _slice),
        11 => new Http2UnknownFrame(_auxByte, _flags, (uint)_extra64, _slice),
        _ => null,
    };

    public bool HasValue => _tag != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Option<Http2PrioritySpec> UnpackPriority() =>
        (_auxByte2 & 1) != 0
            ? Option<Http2PrioritySpec>.Of(new Http2PrioritySpec((_auxByte2 & 2) != 0, _aux32, (byte)(_extra64 >> 32)))
            : Option<Http2PrioritySpec>.Nothing;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out Http2DataFrame value)
    {
        if (_tag == 1) { value = new Http2DataFrame((uint)_extra64, _flags, _slice, _auxByte); return true; }
        Unsafe.SkipInit(out value); return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out Http2HeadersFrame value)
    {
        if (_tag == 2) { value = new Http2HeadersFrame((uint)_extra64, _flags, UnpackPriority(), _slice, _auxByte); return true; }
        Unsafe.SkipInit(out value); return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out Http2PriorityFrame value)
    {
        if (_tag == 3) { value = new Http2PriorityFrame((uint)_extra64, new Http2PrioritySpec((_auxByte2 & 2) != 0, _aux32, _auxByte)); return true; }
        Unsafe.SkipInit(out value); return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out Http2RstStreamFrame value)
    {
        if (_tag == 4) { value = new Http2RstStreamFrame((uint)_extra64, _aux32); return true; }
        Unsafe.SkipInit(out value); return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out Http2SettingsFrame value)
    {
        if (_tag == 5) { value = new Http2SettingsFrame(_flags, _slice); return true; }
        Unsafe.SkipInit(out value); return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out Http2PushPromiseFrame value)
    {
        if (_tag == 6) { value = new Http2PushPromiseFrame((uint)_extra64, _flags, _aux32, _slice, _auxByte); return true; }
        Unsafe.SkipInit(out value); return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out Http2PingFrame value)
    {
        if (_tag == 7) { value = new Http2PingFrame(_flags, _extra64); return true; }
        Unsafe.SkipInit(out value); return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out Http2GoAwayFrame value)
    {
        if (_tag == 8) { value = new Http2GoAwayFrame((uint)_extra64, _aux32, _slice); return true; }
        Unsafe.SkipInit(out value); return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out Http2WindowUpdateFrame value)
    {
        if (_tag == 9) { value = new Http2WindowUpdateFrame((uint)_extra64, _aux32); return true; }
        Unsafe.SkipInit(out value); return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out Http2ContinuationFrame value)
    {
        if (_tag == 10) { value = new Http2ContinuationFrame((uint)_extra64, _flags, _slice); return true; }
        Unsafe.SkipInit(out value); return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out Http2UnknownFrame value)
    {
        if (_tag == 11) { value = new Http2UnknownFrame(_auxByte, _flags, (uint)_extra64, _slice); return true; }
        Unsafe.SkipInit(out value); return false;
    }
}

/// <summary>
/// Zero-allocation RFC 9113 HTTP/2 binary frame parser supporting client preface validation,
/// streaming <see cref="Incomplete"/> decoding, strict frame-size / stream-ID / padding guards,
/// and zero-copy <see cref="ByteSlice"/> payload views.
/// </summary>
public static class Http2FrameParser
{
    public const int FrameHeaderSize = 9;
    public const int DefaultMaxFrameSize = 16_384; // RFC 9113 §4.2 initial SETTINGS_MAX_FRAME_SIZE

    public static ReadOnlySpan<byte> ClientConnectionPreface => "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8;

    public static ParseResult<ByteSlice> ParseClientPreface(ref ByteCursor cursor) =>
        cursor.Tag(ClientConnectionPreface);

    public static ParseResult<Http2Frame> ParseFrame(
        ref ByteCursor cursor,
        int maxFrameSize = DefaultMaxFrameSize)
    {
        int start = cursor.Position;
        ReadOnlySpan<byte> buf = cursor.Buffer;
        int rem = buf.Length - start;
        if (rem < FrameHeaderSize)
        {
            return cursor.Mode == InputMode.Streaming
                ? Incomplete.AtLeast(FrameHeaderSize - rem)
                : Backtrack.At(start, ErrorKind.Eof, "Truncated HTTP/2 9-byte frame header");
        }

        ReadOnlySpan<byte> hdr = buf.Slice(start, FrameHeaderSize);
        int payloadLength = (hdr[0] << 16) | (hdr[1] << 8) | hdr[2];
        byte rawType = hdr[3];
        var flags = (Http2FrameFlags)hdr[4];
        uint streamId = BinaryPrimitives.ReadUInt32BigEndian(hdr.Slice(5, 4)) & 0x7FFF_FFFFu;

        if (payloadLength > maxFrameSize)
        {
            return Failure.At(start, ErrorKind.LengthValue, $"HTTP/2 frame payload {payloadLength} exceeds maxFrameSize {maxFrameSize}");
        }

        int totalLen = FrameHeaderSize + payloadLength;
        if (rem < totalLen)
        {
            return cursor.Mode == InputMode.Streaming
                ? Incomplete.AtLeast(totalLen - rem)
                : Backtrack.At(start + FrameHeaderSize, ErrorKind.Take, "Truncated HTTP/2 frame payload");
        }

        int payloadStart = start + FrameHeaderSize;
        ReadOnlySpan<byte> payload = buf.Slice(payloadStart, payloadLength);

        switch ((Http2FrameType)rawType)
        {
            case Http2FrameType.Data:
            {
                if (streamId == 0)
                    return Failure.At(start + 5, ErrorKind.Protocol, "DATA frame requires non-zero stream ID");

                if (!TryStripPadding(flags, payload, payloadStart, out byte padLen, out ByteSlice dataSlice, out ParseError padErr))
                    return new Failure(padErr);

                cursor.Position = start + totalLen;
                return new Parsed<Http2Frame>(new Http2DataFrame(streamId, flags, dataSlice, padLen));
            }

            case Http2FrameType.Headers:
            {
                if (streamId == 0)
                    return Failure.At(start + 5, ErrorKind.Protocol, "HEADERS frame requires non-zero stream ID");

                int bodyOffset = 0;
                int bodyEnd = payloadLength;
                byte padLen = 0;

                if ((flags & Http2FrameFlags.Padded) != 0)
                {
                    if (payloadLength < 1)
                        return Failure.At(payloadStart, ErrorKind.Protocol, "Padded HEADERS frame missing pad length");
                    padLen = payload[0];
                    bodyOffset = 1;
                    bodyEnd = payloadLength - padLen;
                }

                Option<Http2PrioritySpec> priority = Option<Http2PrioritySpec>.Nothing;
                if ((flags & Http2FrameFlags.Priority) != 0)
                {
                    if (bodyEnd - bodyOffset < 5)
                        return Failure.At(payloadStart + bodyOffset, ErrorKind.Protocol, "HEADERS frame with PRIORITY flag too short");

                    uint depRaw = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(bodyOffset, 4));
                    bool excl = (depRaw & 0x8000_0000u) != 0;
                    uint dep = depRaw & 0x7FFF_FFFFu;
                    byte weight = payload[bodyOffset + 4];
                    priority = Option<Http2PrioritySpec>.Of(new Http2PrioritySpec(excl, dep, weight));
                    bodyOffset += 5;
                }

                if (bodyEnd < bodyOffset)
                    return Failure.At(payloadStart, ErrorKind.Protocol, "HTTP/2 HEADERS pad length exceeds payload");

                cursor.Position = start + totalLen;
                return new Parsed<Http2Frame>(new Http2HeadersFrame(
                    streamId,
                    flags,
                    priority,
                    new ByteSlice(payloadStart + bodyOffset, bodyEnd - bodyOffset),
                    padLen));
            }

            case Http2FrameType.Priority:
            {
                if (streamId == 0 || payloadLength != 5)
                    return Failure.At(start, ErrorKind.Protocol, "PRIORITY frame requires non-zero stream ID and length 5");

                uint depRaw = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(0, 4));
                var spec = new Http2PrioritySpec((depRaw & 0x8000_0000u) != 0, depRaw & 0x7FFF_FFFFu, payload[4]);
                cursor.Position = start + totalLen;
                return new Parsed<Http2Frame>(new Http2PriorityFrame(streamId, spec));
            }

            case Http2FrameType.RstStream:
            {
                if (streamId == 0 || payloadLength != 4)
                    return Failure.At(start, ErrorKind.Protocol, "RST_STREAM frame requires non-zero stream ID and length 4");

                uint errorCode = BinaryPrimitives.ReadUInt32BigEndian(payload);
                cursor.Position = start + totalLen;
                return new Parsed<Http2Frame>(new Http2RstStreamFrame(streamId, errorCode));
            }

            case Http2FrameType.Settings:
            {
                if (streamId != 0)
                    return Failure.At(start + 5, ErrorKind.Protocol, "SETTINGS frame must use stream ID 0");
                if ((flags & Http2FrameFlags.Ack) != 0 && payloadLength != 0)
                    return Failure.At(start, ErrorKind.Protocol, "SETTINGS ACK frame must have empty payload");
                if ((payloadLength % 6) != 0)
                    return Failure.At(start, ErrorKind.Protocol, "SETTINGS payload length must be a multiple of 6");

                cursor.Position = start + totalLen;
                return new Parsed<Http2Frame>(new Http2SettingsFrame(flags, new ByteSlice(payloadStart, payloadLength)));
            }

            case Http2FrameType.PushPromise:
            {
                if (streamId == 0)
                    return Failure.At(start + 5, ErrorKind.Protocol, "PUSH_PROMISE frame requires non-zero stream ID");

                int bodyOffset = 0;
                int bodyEnd = payloadLength;
                byte padLen = 0;
                if ((flags & Http2FrameFlags.Padded) != 0)
                {
                    if (payloadLength < 1)
                        return Failure.At(payloadStart, ErrorKind.Protocol, "Padded PUSH_PROMISE missing pad length");
                    padLen = payload[0];
                    bodyOffset = 1;
                    bodyEnd = payloadLength - padLen;
                }

                if (bodyEnd - bodyOffset < 4)
                    return Failure.At(payloadStart + bodyOffset, ErrorKind.Protocol, "PUSH_PROMISE frame too short for promised stream ID");

                uint promisedId = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(bodyOffset, 4)) & 0x7FFF_FFFFu;
                bodyOffset += 4;
                cursor.Position = start + totalLen;
                return new Parsed<Http2Frame>(new Http2PushPromiseFrame(
                    streamId,
                    flags,
                    promisedId,
                    new ByteSlice(payloadStart + bodyOffset, bodyEnd - bodyOffset),
                    padLen));
            }

            case Http2FrameType.Ping:
            {
                if (streamId != 0 || payloadLength != 8)
                    return Failure.At(start, ErrorKind.Protocol, "PING frame requires stream ID 0 and 8-byte opaque payload");

                ulong opaque = BinaryPrimitives.ReadUInt64BigEndian(payload);
                cursor.Position = start + totalLen;
                return new Parsed<Http2Frame>(new Http2PingFrame(flags, opaque));
            }

            case Http2FrameType.GoAway:
            {
                if (streamId != 0 || payloadLength < 8)
                    return Failure.At(start, ErrorKind.Protocol, "GOAWAY frame requires stream ID 0 and >= 8-byte payload");

                uint lastStreamId = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(0, 4)) & 0x7FFF_FFFFu;
                uint errorCode = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4));
                cursor.Position = start + totalLen;
                return new Parsed<Http2Frame>(new Http2GoAwayFrame(
                    lastStreamId,
                    errorCode,
                    new ByteSlice(payloadStart + 8, payloadLength - 8)));
            }

            case Http2FrameType.WindowUpdate:
            {
                if (payloadLength != 4)
                    return Failure.At(start, ErrorKind.Protocol, "WINDOW_UPDATE frame requires 4-byte payload");

                uint inc = BinaryPrimitives.ReadUInt32BigEndian(payload) & 0x7FFF_FFFFu;
                if (inc == 0)
                    return Failure.At(payloadStart, ErrorKind.Protocol, "WINDOW_UPDATE increment must be non-zero");

                cursor.Position = start + totalLen;
                return new Parsed<Http2Frame>(new Http2WindowUpdateFrame(streamId, inc));
            }

            case Http2FrameType.Continuation:
            {
                if (streamId == 0)
                    return Failure.At(start + 5, ErrorKind.Protocol, "CONTINUATION frame requires non-zero stream ID");

                cursor.Position = start + totalLen;
                return new Parsed<Http2Frame>(new Http2ContinuationFrame(streamId, flags, new ByteSlice(payloadStart, payloadLength)));
            }

            default:
                cursor.Position = start + totalLen;
                return new Parsed<Http2Frame>(new Http2UnknownFrame(rawType, flags, streamId, new ByteSlice(payloadStart, payloadLength)));
        }
    }

    /// <summary>Zero-copy indexed accessor for 6-byte entries inside an <see cref="Http2SettingsFrame"/>.</summary>
    public static bool TryReadSetting(
        ReadOnlySpan<byte> buffer,
        ByteSlice entries,
        int index,
        out Http2SettingEntry entry)
    {
        int count = entries.Length / 6;
        if ((uint)index >= (uint)count)
        {
            entry = default;
            return false;
        }

        ReadOnlySpan<byte> raw = buffer.Slice(entries.Offset + (index * 6), 6);
        entry = new Http2SettingEntry(
            BinaryPrimitives.ReadUInt16BigEndian(raw.Slice(0, 2)),
            BinaryPrimitives.ReadUInt32BigEndian(raw.Slice(2, 4)));
        return true;
    }

    public static byte[] BuildFrame(
        Http2FrameType type,
        Http2FrameFlags flags,
        uint streamId,
        ReadOnlySpan<byte> payload)
    {
        int len = payload.Length;
        byte[] frame = new byte[FrameHeaderSize + len];
        frame[0] = (byte)(len >> 16);
        frame[1] = (byte)(len >> 8);
        frame[2] = (byte)len;
        frame[3] = (byte)type;
        frame[4] = (byte)flags;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(5, 4), streamId & 0x7FFF_FFFFu);
        payload.CopyTo(frame.AsSpan(FrameHeaderSize));
        return frame;
    }

    private static bool TryStripPadding(
        Http2FrameFlags flags,
        ReadOnlySpan<byte> payload,
        int payloadStart,
        out byte padLength,
        out ByteSlice dataSlice,
        out ParseError error)
    {
        if ((flags & Http2FrameFlags.Padded) == 0)
        {
            padLength = 0;
            dataSlice = new ByteSlice(payloadStart, payload.Length);
            error = default;
            return true;
        }

        if (payload.Length < 1)
        {
            padLength = 0;
            dataSlice = default;
            error = new ParseError(payloadStart, ErrorKind.Protocol, "Padded HTTP/2 frame missing pad length");
            return false;
        }

        padLength = payload[0];
        int dataLen = payload.Length - 1 - padLength;
        if (dataLen < 0)
        {
            dataSlice = default;
            error = new ParseError(payloadStart, ErrorKind.Protocol, "HTTP/2 pad length exceeds frame payload");
            return false;
        }

        dataSlice = new ByteSlice(payloadStart + 1, dataLen);
        error = default;
        return true;
    }
}
