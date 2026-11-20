using System.Runtime.CompilerServices;

namespace SpanNom.Protocols.WebSocket;

public readonly record struct WebSocketHeaderBits(
    bool Fin,
    bool Rsv1,
    bool Rsv2,
    bool Rsv3,
    byte Opcode,
    bool Masked,
    byte Length7);

public readonly record struct ContinuationFrame(bool Fin, Option<uint> MaskKey, ByteSlice Payload);
public readonly record struct TextFrame(bool Fin, Option<uint> MaskKey, ByteSlice Payload);
public readonly record struct BinaryFrame(bool Fin, Option<uint> MaskKey, ByteSlice Payload);
public readonly record struct CloseFrame(Option<uint> MaskKey, Option<ushort> StatusCode, ByteSlice Reason);
public readonly record struct PingFrame(Option<uint> MaskKey, ByteSlice Payload);
public readonly record struct PongFrame(Option<uint> MaskKey, ByteSlice Payload);

/// <summary>
/// Zero-boxing C# 15 custom <c>[Union]</c> modeling every RFC 6455 WebSocket frame kind.
/// Implements <see cref="HasValue"/> and typed <c>TryGetValue</c> overloads so parsing and
/// exhaustively switching on WebSocket frames allocates 0 bytes on the heap.
/// </summary>
[Union]
public readonly struct WebSocketFrame : IUnion
{
    private readonly ByteSlice _payload;
    private readonly Option<uint> _maskKey;
    private readonly Option<ushort> _statusCode;
    private readonly bool _fin;
    private readonly byte _tag; // 1=Cont, 2=Text, 3=Bin, 4=Close, 5=Ping, 6=Pong

    public WebSocketFrame(ContinuationFrame f) => (_tag, _fin, _maskKey, _payload, _statusCode) = (1, f.Fin, f.MaskKey, f.Payload, default);
    public WebSocketFrame(TextFrame f) => (_tag, _fin, _maskKey, _payload, _statusCode) = (2, f.Fin, f.MaskKey, f.Payload, default);
    public WebSocketFrame(BinaryFrame f) => (_tag, _fin, _maskKey, _payload, _statusCode) = (3, f.Fin, f.MaskKey, f.Payload, default);
    public WebSocketFrame(CloseFrame f) => (_tag, _fin, _maskKey, _payload, _statusCode) = (4, true, f.MaskKey, f.Reason, f.StatusCode);
    public WebSocketFrame(PingFrame f) => (_tag, _fin, _maskKey, _payload, _statusCode) = (5, true, f.MaskKey, f.Payload, default);
    public WebSocketFrame(PongFrame f) => (_tag, _fin, _maskKey, _payload, _statusCode) = (6, true, f.MaskKey, f.Payload, default);

    public object? Value => _tag switch
    {
        1 => new ContinuationFrame(_fin, _maskKey, _payload),
        2 => new TextFrame(_fin, _maskKey, _payload),
        3 => new BinaryFrame(_fin, _maskKey, _payload),
        4 => new CloseFrame(_maskKey, _statusCode, _payload),
        5 => new PingFrame(_maskKey, _payload),
        6 => new PongFrame(_maskKey, _payload),
        _ => null,
    };

    public bool HasValue => _tag != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out ContinuationFrame value)
    {
        if (_tag == 1) { value = new ContinuationFrame(_fin, _maskKey, _payload); return true; }
        value = default; return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out TextFrame value)
    {
        if (_tag == 2) { value = new TextFrame(_fin, _maskKey, _payload); return true; }
        value = default; return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out BinaryFrame value)
    {
        if (_tag == 3) { value = new BinaryFrame(_fin, _maskKey, _payload); return true; }
        value = default; return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out CloseFrame value)
    {
        if (_tag == 4) { value = new CloseFrame(_maskKey, _statusCode, _payload); return true; }
        value = default; return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out PingFrame value)
    {
        if (_tag == 5) { value = new PingFrame(_maskKey, _payload); return true; }
        value = default; return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out PongFrame value)
    {
        if (_tag == 6) { value = new PongFrame(_maskKey, _payload); return true; }
        value = default; return false;
    }
}

/// <summary>
/// Zero-copy RFC 6455 WebSocket frame parser using <see cref="BitCursor"/> for bitfield headers,
/// streaming <see cref="Incomplete"/> support, and C# 15 <c>union WebSocketFrame</c>.
/// </summary>
public static class WebSocketFrameParser
{
    private readonly struct HeaderBitParser : IBitParser<WebSocketHeaderBits>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParseResult<WebSocketHeaderBits> Parse(ref BitCursor bits)
        {
            var rFin = bits.ReadBool();
            if (!rFin.TryGetValue(out Parsed<bool> fin)) return rFin.PropagateNonParsed<WebSocketHeaderBits>();
            var rRsv = bits.ReadU8(3);
            if (!rRsv.TryGetValue(out Parsed<byte> rsv)) return rRsv.PropagateNonParsed<WebSocketHeaderBits>();
            var rOp = bits.ReadU8(4);
            if (!rOp.TryGetValue(out Parsed<byte> op)) return rOp.PropagateNonParsed<WebSocketHeaderBits>();
            var rMask = bits.ReadBool();
            if (!rMask.TryGetValue(out Parsed<bool> mask)) return rMask.PropagateNonParsed<WebSocketHeaderBits>();
            var rLen7 = bits.ReadU8(7);
            if (!rLen7.TryGetValue(out Parsed<byte> len7)) return rLen7.PropagateNonParsed<WebSocketHeaderBits>();

            return new Parsed<WebSocketHeaderBits>(new WebSocketHeaderBits(
                fin.Value,
                (rsv.Value & 0b100) != 0,
                (rsv.Value & 0b010) != 0,
                (rsv.Value & 0b001) != 0,
                op.Value,
                mask.Value,
                len7.Value));
        }
    }

    public static ParseResult<WebSocketFrame> ParseFrame(ref ByteCursor cursor)
    {
        var cp = cursor.Save();
        cursor.Endianness = Endianness.Big;

        // 1. Parse the 2-byte bit-packed header via monomorphized BitCursor parser (inlines cleanly).
        var bitsRes = cursor.ParseBits<HeaderBitParser, WebSocketHeaderBits>(default);

        if (!bitsRes.TryGetValue(out Parsed<WebSocketHeaderBits> hdrOk))
        {
            cursor.Reset(cp);
            return bitsRes.PropagateNonParsed<WebSocketFrame>();
        }

        var hdr = hdrOk.Value;

        // 2. Decode extended payload length (7-bit / 16-bit / 64-bit).
        ulong payloadLength;
        if (hdr.Length7 < 126)
        {
            payloadLength = hdr.Length7;
        }
        else if (hdr.Length7 == 126)
        {
            var len16 = cursor.ReadU16BE();
            if (!len16.TryGetValue(out Parsed<ushort> l16))
            {
                cursor.Reset(cp);
                return len16.PropagateNonParsed<WebSocketFrame>();
            }

            payloadLength = l16.Value;
        }
        else
        {
            var len64 = cursor.ReadU64BE();
            if (!len64.TryGetValue(out Parsed<ulong> l64))
            {
                cursor.Reset(cp);
                return len64.PropagateNonParsed<WebSocketFrame>();
            }

            if ((l64.Value & 0x8000_0000_0000_0000UL) != 0)
            {
                cursor.Reset(cp);
                return Failure.At(cp.Position + 2, ErrorKind.Protocol, "RFC 6455 forbids MSB=1 in 64-bit payload length");
            }

            payloadLength = l64.Value;
        }

        if (payloadLength > int.MaxValue)
        {
            cursor.Reset(cp);
            return Failure.At(cp.Position + 2, ErrorKind.LengthValue, "WebSocket payload exceeds 2 GiB span limit");
        }

        // Control frames (opcode >= 8) must have FIN=1 and payload <= 125 bytes per RFC 6455 §5.5.
        bool isControl = (hdr.Opcode & 0x08) != 0;
        if (isControl && (!hdr.Fin || payloadLength > 125))
        {
            cursor.Reset(cp);
            return Failure.At(cp.Position, ErrorKind.Protocol, "RFC 6455 control frames must be unfragmented and <= 125 bytes");
        }

        // 3. Optional 4-byte masking key.
        Option<uint> maskKey = Option<uint>.Nothing;
        if (hdr.Masked)
        {
            var maskRes = cursor.ReadU32BE();
            if (!maskRes.TryGetValue(out Parsed<uint> maskOk))
            {
                cursor.Reset(cp);
                return maskRes.PropagateNonParsed<WebSocketFrame>();
            }

            maskKey = Option<uint>.Of(maskOk.Value);
        }

        // 4. Zero-copy payload slice.
        var payloadRes = cursor.Take((int)payloadLength);
        if (!payloadRes.TryGetValue(out Parsed<ByteSlice> payloadOk))
        {
            cursor.Reset(cp);
            return payloadRes.PropagateNonParsed<WebSocketFrame>();
        }

        var payload = payloadOk.Value;

        // 5. Construct the strongly typed C# 15 union frame variant.
        switch (hdr.Opcode)
        {
            case 0x0:
                return new Parsed<WebSocketFrame>(new ContinuationFrame(hdr.Fin, maskKey, payload));
            case 0x1:
                return new Parsed<WebSocketFrame>(new TextFrame(hdr.Fin, maskKey, payload));
            case 0x2:
                return new Parsed<WebSocketFrame>(new BinaryFrame(hdr.Fin, maskKey, payload));
            case 0x8:
                if (payload.Length == 1)
                {
                    cursor.Reset(cp);
                    return Failure.At(payload.Offset, ErrorKind.Protocol, "Close frame payload cannot be 1 byte");
                }

                if (payload.Length >= 2)
                {
                    // Decode optional status code (accounting for mask if present).
                    ReadOnlySpan<byte> raw = cursor[payload];
                    byte b0 = raw[0];
                    byte b1 = raw[1];
                    if (maskKey.TryGetValue(out Some<uint> mk))
                    {
                        b0 ^= (byte)(mk.Value >> 24);
                        b1 ^= (byte)(mk.Value >> 16);
                    }

                    ushort code = (ushort)((b0 << 8) | b1);
                    var reason = new ByteSlice(payload.Offset + 2, payload.Length - 2);
                    return new Parsed<WebSocketFrame>(new CloseFrame(maskKey, Option<ushort>.Of(code), reason));
                }

                return new Parsed<WebSocketFrame>(new CloseFrame(maskKey, Option<ushort>.Nothing, ByteSlice.Empty));
            case 0x9:
                return new Parsed<WebSocketFrame>(new PingFrame(maskKey, payload));
            case 0xA:
                return new Parsed<WebSocketFrame>(new PongFrame(maskKey, payload));
            default:
                cursor.Reset(cp);
                return Failure.At(cp.Position, ErrorKind.Protocol, $"Reserved/unknown WebSocket opcode 0x{hdr.Opcode:X}");
        }
    }

    /// <summary>
    /// Unmasks a WebSocket payload slice in-place or into a caller-provided destination span (zero allocations).
    /// </summary>
    public static void UnmaskInto(
        ReadOnlySpan<byte> sourceBuffer,
        ByteSlice payload,
        Option<uint> maskKey,
        Span<byte> destination,
        int maskOffset = 0)
    {
        var raw = sourceBuffer[payload];
        if (destination.Length < raw.Length)
            throw new ArgumentException("Destination span is too small.", nameof(destination));

        if (!maskKey.TryGetValue(out Some<uint> key))
        {
            raw.CopyTo(destination);
            return;
        }

        Span<byte> maskBytes =
        [
            (byte)(key.Value >> 24),
            (byte)(key.Value >> 16),
            (byte)(key.Value >> 8),
            (byte)key.Value,
        ];

        for (int i = 0; i < raw.Length; i++)
        {
            destination[i] = (byte)(raw[i] ^ maskBytes[(i + maskOffset) & 3]);
        }
    }
}
