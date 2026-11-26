using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace SpanForge.Protocols.WebSocket;

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
/// Compact 16-byte, zero-boxing C# 15 custom <c>[Union]</c> modeling every RFC 6455 WebSocket frame kind.
/// Implements <see cref="HasValue"/> and typed <c>TryGetValue</c> overloads so parsing and
/// exhaustively switching on WebSocket frames allocates 0 bytes on the heap and fits in 2 registers.
/// </summary>
[Union]
public readonly struct WebSocketFrame : IUnion
{
    private const byte FlagFin = 1;
    private const byte FlagHasMask = 2;
    private const byte FlagHasStatus = 4;

    private readonly ByteSlice _payload;
    private readonly uint _maskKey;
    private readonly ushort _statusCode;
    private readonly byte _flags;
    private readonly byte _tag; // 1=Cont, 2=Text, 3=Bin, 4=Close, 5=Ping, 6=Pong

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private WebSocketFrame(byte tag, bool fin, Option<uint> maskKey, Option<ushort> statusCode, ByteSlice payload)
    {
        _payload = payload;
        byte flags = fin ? FlagFin : (byte)0;
        if (maskKey.TryGetValue(out Some<uint> mk))
        {
            _maskKey = mk.Value;
            flags |= FlagHasMask;
        }
        else
        {
            _maskKey = 0;
        }

        if (statusCode.TryGetValue(out Some<ushort> sc))
        {
            _statusCode = sc.Value;
            flags |= FlagHasStatus;
        }
        else
        {
            _statusCode = 0;
        }

        _flags = flags;
        _tag = tag;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal WebSocketFrame(byte tag, bool fin, bool hasMask, uint maskKey, ByteSlice payload)
    {
        _payload = payload;
        _maskKey = maskKey;
        _statusCode = 0;
        _flags = (byte)((fin ? FlagFin : 0) | (hasMask ? FlagHasMask : 0));
        _tag = tag;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal WebSocketFrame(byte tag, byte flags, uint maskKey, ByteSlice payload)
    {
        _payload = payload;
        _maskKey = maskKey;
        _statusCode = 0;
        _flags = flags;
        _tag = tag;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public WebSocketFrame(ContinuationFrame f) : this(1, f.Fin, f.MaskKey, Option<ushort>.Nothing, f.Payload) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public WebSocketFrame(TextFrame f) : this(2, f.Fin, f.MaskKey, Option<ushort>.Nothing, f.Payload) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public WebSocketFrame(BinaryFrame f) : this(3, f.Fin, f.MaskKey, Option<ushort>.Nothing, f.Payload) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public WebSocketFrame(CloseFrame f) : this(4, true, f.MaskKey, f.StatusCode, f.Reason) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public WebSocketFrame(PingFrame f) : this(5, true, f.MaskKey, Option<ushort>.Nothing, f.Payload) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public WebSocketFrame(PongFrame f) : this(6, true, f.MaskKey, Option<ushort>.Nothing, f.Payload) { }

    private bool Fin
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_flags & FlagFin) != 0;
    }

    private Option<uint> MaskKey
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_flags & FlagHasMask) != 0 ? Option<uint>.Of(_maskKey) : Option<uint>.Nothing;
    }

    private Option<ushort> StatusCode
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_flags & FlagHasStatus) != 0 ? Option<ushort>.Of(_statusCode) : Option<ushort>.Nothing;
    }

    public object? Value => _tag switch
    {
        1 => new ContinuationFrame(Fin, MaskKey, _payload),
        2 => new TextFrame(Fin, MaskKey, _payload),
        3 => new BinaryFrame(Fin, MaskKey, _payload),
        4 => new CloseFrame(MaskKey, StatusCode, _payload),
        5 => new PingFrame(MaskKey, _payload),
        6 => new PongFrame(MaskKey, _payload),
        _ => null,
    };

    public bool HasValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _tag != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out ContinuationFrame value)
    {
        if (_tag == 1) { value = new ContinuationFrame(Fin, MaskKey, _payload); return true; }
        Unsafe.SkipInit(out value); return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out TextFrame value)
    {
        if (_tag == 2) { value = new TextFrame(Fin, MaskKey, _payload); return true; }
        Unsafe.SkipInit(out value); return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out BinaryFrame value)
    {
        if (_tag == 3) { value = new BinaryFrame(Fin, MaskKey, _payload); return true; }
        Unsafe.SkipInit(out value); return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out CloseFrame value)
    {
        if (_tag == 4) { value = new CloseFrame(MaskKey, StatusCode, _payload); return true; }
        Unsafe.SkipInit(out value); return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out PingFrame value)
    {
        if (_tag == 5) { value = new PingFrame(MaskKey, _payload); return true; }
        Unsafe.SkipInit(out value); return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out PongFrame value)
    {
        if (_tag == 6) { value = new PongFrame(MaskKey, _payload); return true; }
        Unsafe.SkipInit(out value); return false;
    }
}

/// <summary>
/// Zero-copy RFC 6455 WebSocket frame parser using <see cref="BitCursor"/> for bitfield headers,
/// streaming <see cref="Incomplete"/> support, word-at-a-time unmasking, and 16-byte C# 15 <c>[Union] WebSocketFrame</c>.
/// </summary>
public static class WebSocketFrameParser
{
    // 16-nibble lookup table mapping RFC 6455 fast-path opcodes to WebSocketFrame tags:
    // 0x0->1 (Cont), 0x1->2 (Text), 0x2->3 (Bin), 0x9->5 (Ping), 0xA->6 (Pong); 0x8 (Close) & reserved->0 (slow path).
    private const ulong FastOpcodeTags =
        (1UL << (0 * 4)) |
        (2UL << (1 * 4)) |
        (3UL << (2 * 4)) |
        (5UL << (9 * 4)) |
        (6UL << (10 * 4));

    private readonly struct WsSlowOutcome
    {
        public readonly WebSocketFrame Frame;
        public readonly int NextPos;
        public readonly FastStatus Status;

        public WsSlowOutcome(WebSocketFrame frame, int nextPos, FastStatus status)
        {
            Frame = frame;
            NextPos = nextPos;
            Status = status;
        }
    }

    /// <summary>
    /// High-throughput zero-spill <see cref="FastStatus"/> entry point for RFC 6455 WebSocket frame decoding.
    /// Avoids constructing a 32-byte GC-tracked <see cref="ParseResult{T}"/> on the hot path while preserving
    /// identical RFC 6455 validation and streaming <see cref="FastStatus.IsIncomplete"/> semantics.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FastStatus TryParseFrame(ref ByteCursor cursor, out WebSocketFrame frame)
    {
        int start = cursor.Position;
        ReadOnlySpan<byte> buf = cursor.Buffer;
        int rem = buf.Length - start;
        if (rem >= 2)
        {
            ref byte p = ref Unsafe.Add(ref MemoryMarshal.GetReference(buf), (nint)(uint)start);
            byte b0 = p;
            byte b1 = Unsafe.Add(ref p, 1);
            int opcode = b0 & 0x0F;
            int len7 = b1 & 0x7F;
            int headerLen = 2 + ((b1 >> 5) & 4);
            int totalLen = headerLen + len7;
            byte tag = (byte)((FastOpcodeTags >> (opcode << 2)) & 0xF);
            if (tag != 0 && len7 < 126 && rem >= totalLen && (b0 & 0x88) != 0x08)
            {
                uint maskKeyVal = 0;
                if ((b1 & 0x80) != 0)
                {
                    maskKeyVal = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref p, 2));
                    if (BitConverter.IsLittleEndian) maskKeyVal = BinaryPrimitives.ReverseEndianness(maskKeyVal);
                }

                byte flags = (byte)((b0 >> 7) | ((b1 >> 6) & 2));
                cursor.Rewind(start + totalLen);
                frame = new WebSocketFrame(tag, flags, maskKeyVal, new ByteSlice(start + headerLen, len7));
                return FastStatus.Parsed;
            }
        }

        WsSlowOutcome slow = TryParseFrameSlow(buf, start, cursor.Mode);
        cursor.Rewind(slow.NextPos);
        frame = slow.Frame;
        return slow.Status;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WsSlowOutcome TryParseFrameSlow(ReadOnlySpan<byte> buf, int start, InputMode mode)
    {
        var c = new ByteCursor(buf, Endianness.Big, mode);
        c.Position = start;
        var res = ParseFrame(ref c);
        if (res.TryGetValue(out Parsed<WebSocketFrame> ok))
        {
            return new WsSlowOutcome(ok.Value, c.Position, FastStatus.Parsed);
        }

        FastStatus status;
        if (res.TryGetValue(out Incomplete inc))
        {
            status = inc.Needed.TryGetValue(out ExactBytes exact)
                ? FastStatus.IncompleteBytes(exact.Count)
                : FastStatus.IncompleteUnknown;
        }
        else
        {
            status = res.IsFailure ? FastStatus.Failure : FastStatus.Backtrack;
        }

        return new WsSlowOutcome(default, c.Position, status);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseResult<WebSocketFrame> ParseFrame(ref ByteCursor cursor)
    {
        int start = cursor.Position;
        ReadOnlySpan<byte> buf = cursor.Buffer;
        int rem = buf.Length - start;
        if (rem < 2)
        {
            return HeaderShortage(cursor.Mode, start, rem);
        }

        ref byte p = ref Unsafe.Add(ref MemoryMarshal.GetReference(buf), (nint)(uint)start);
        byte b0 = p;
        byte b1 = Unsafe.Add(ref p, 1);
        bool fin = (b0 & 0x80) != 0;
        byte opcode = (byte)(b0 & 0x0F);
        bool masked = (b1 & 0x80) != 0;
        byte len7 = (byte)(b1 & 0x7F);

        // Fast path: 7-bit payload length (< 126) — single bounds check for header + optional mask + payload!
        if (len7 < 126)
        {
            if ((opcode & 0x08) != 0 && !fin)
            {
                return FailControlFrame(ref cursor, start);
            }

            int maskBytes = masked ? 4 : 0;
            int headerLen = 2 + maskBytes;
            int totalLen = headerLen + len7;
            if (rem < totalLen)
            {
                return FrameShortage(cursor.Mode, start, rem, totalLen, headerLen);
            }

            uint maskKeyVal = 0;
            if (masked)
            {
                maskKeyVal = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref p, 2));
                if (BitConverter.IsLittleEndian)
                {
                    maskKeyVal = BinaryPrimitives.ReverseEndianness(maskKeyVal);
                }
            }

            var payload = new ByteSlice(start + headerLen, len7);
            cursor.Position = start + totalLen;

            return opcode switch
            {
                0x1 => new Parsed<WebSocketFrame>(new WebSocketFrame(2, fin, masked, maskKeyVal, payload)),
                0x2 => new Parsed<WebSocketFrame>(new WebSocketFrame(3, fin, masked, maskKeyVal, payload)),
                0x9 => new Parsed<WebSocketFrame>(new WebSocketFrame(5, true, masked, maskKeyVal, payload)),
                0xA => new Parsed<WebSocketFrame>(new WebSocketFrame(6, true, masked, maskKeyVal, payload)),
                0x0 => new Parsed<WebSocketFrame>(new WebSocketFrame(1, fin, masked, maskKeyVal, payload)),
                0x8 => BuildCloseFrame(ref cursor, start, masked ? Option<uint>.Of(maskKeyVal) : Option<uint>.Nothing, payload),
                _ => FailUnknownOpcode(ref cursor, start, opcode),
            };
        }

        return ParseExtendedFrame(ref cursor, start, fin, opcode, masked, len7);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ParseResult<WebSocketFrame> HeaderShortage(InputMode mode, int start, int rem) =>
        mode == InputMode.Streaming
            ? Incomplete.AtLeast(2 - rem)
            : Backtrack.At(start, ErrorKind.BitRead);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ParseResult<WebSocketFrame> FrameShortage(InputMode mode, int start, int rem, int totalLen, int headerLen) =>
        mode == InputMode.Streaming
            ? Incomplete.AtLeast(totalLen - rem)
            : Backtrack.At(start + (rem < headerLen ? 2 : headerLen), rem < headerLen ? ErrorKind.Eof : ErrorKind.Take);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ParseResult<WebSocketFrame> ParseExtendedFrame(
        ref ByteCursor cursor,
        int start,
        bool fin,
        byte opcode,
        bool masked,
        byte len7)
    {
        cursor.Position = start + 2;
        var extRes = ReadExtendedLength(ref cursor, start, len7);
        if (!extRes.TryGetValue(out Parsed<int> extOk))
        {
            cursor.Position = start;
            return extRes.PropagateNonParsed<WebSocketFrame>();
        }

        int payloadLength = extOk.Value;
        if ((opcode & 0x08) != 0 && (!fin || payloadLength > 125))
        {
            return FailControlFrame(ref cursor, start);
        }

        uint maskKeyVal = 0;
        if (masked)
        {
            var maskRes = cursor.ReadU32BE();
            if (!maskRes.TryGetValue(out Parsed<uint> maskOk))
            {
                cursor.Position = start;
                return maskRes.PropagateNonParsed<WebSocketFrame>();
            }

            maskKeyVal = maskOk.Value;
        }

        var payloadRes = cursor.Take(payloadLength);
        if (!payloadRes.TryGetValue(out Parsed<ByteSlice> payloadOk))
        {
            cursor.Position = start;
            return payloadRes.PropagateNonParsed<WebSocketFrame>();
        }

        var payload = payloadOk.Value;
        return opcode switch
        {
            0x0 => new Parsed<WebSocketFrame>(new WebSocketFrame(1, fin, masked, maskKeyVal, payload)),
            0x1 => new Parsed<WebSocketFrame>(new WebSocketFrame(2, fin, masked, maskKeyVal, payload)),
            0x2 => new Parsed<WebSocketFrame>(new WebSocketFrame(3, fin, masked, maskKeyVal, payload)),
            0x9 => new Parsed<WebSocketFrame>(new WebSocketFrame(5, true, masked, maskKeyVal, payload)),
            0xA => new Parsed<WebSocketFrame>(new WebSocketFrame(6, true, masked, maskKeyVal, payload)),
            0x8 => BuildCloseFrame(ref cursor, start, masked ? Option<uint>.Of(maskKeyVal) : Option<uint>.Nothing, payload),
            _ => FailUnknownOpcode(ref cursor, start, opcode),
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ParseResult<int> ReadExtendedLength(ref ByteCursor cursor, int start, byte len7)
    {
        if (len7 == 126)
        {
            var len16 = cursor.ReadU16BE();
            if (!len16.TryGetValue(out Parsed<ushort> l16))
            {
                return len16.PropagateNonParsed<int>();
            }

            return new Parsed<int>(l16.Value);
        }

        var len64 = cursor.ReadU64BE();
        if (!len64.TryGetValue(out Parsed<ulong> l64))
        {
            return len64.PropagateNonParsed<int>();
        }

        if ((l64.Value & 0x8000_0000_0000_0000UL) != 0)
        {
            return Failure.At(start + 2, ErrorKind.Protocol, "RFC 6455 forbids MSB=1 in 64-bit payload length");
        }

        if (l64.Value > int.MaxValue)
        {
            return Failure.At(start + 2, ErrorKind.LengthValue, "WebSocket payload exceeds 2 GiB span limit");
        }

        return new Parsed<int>((int)l64.Value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ParseResult<WebSocketFrame> BuildCloseFrame(
        ref ByteCursor cursor,
        int start,
        Option<uint> maskKey,
        ByteSlice payload)
    {
        if (payload.Length == 1)
        {
            cursor.Position = start;
            return Failure.At(payload.Offset, ErrorKind.Protocol, "Close frame payload cannot be 1 byte");
        }

        if (payload.Length >= 2)
        {
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
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ParseResult<WebSocketFrame> FailControlFrame(ref ByteCursor cursor, int start)
    {
        cursor.Position = start;
        return Failure.At(start, ErrorKind.Protocol, "RFC 6455 control frames must be unfragmented and <= 125 bytes");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ParseResult<WebSocketFrame> FailUnknownOpcode(ref ByteCursor cursor, int start, byte opcode)
    {
        cursor.Position = start;
        return Failure.At(start, ErrorKind.Protocol, $"Reserved/unknown WebSocket opcode 0x{opcode:X}");
    }

    /// <summary>
    /// Unmasks a WebSocket payload slice in-place or into a caller-provided destination span
    /// using word-at-a-time 64-bit/32-bit XOR (zero allocations).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UnmaskInto(
        ReadOnlySpan<byte> sourceBuffer,
        ByteSlice payload,
        Option<uint> maskKey,
        Span<byte> destination,
        int maskOffset = 0)
    {
        var raw = sourceBuffer[payload];
        int len = raw.Length;
        if (destination.Length < len)
            ThrowDestinationTooSmall();

        if (!maskKey.TryGetValue(out Some<uint> key))
        {
            raw.CopyTo(destination);
            return;
        }

        // Rotate big-endian mask key so byte 0 aligns with `maskOffset & 3`, then convert to native endianness for word/SIMD XOR.
        uint mBe = BitOperations.RotateLeft(key.Value, (maskOffset & 3) * 8);
        uint m32 = BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(mBe) : mBe;

        ref byte src = ref MemoryMarshal.GetReference(raw);
        ref byte dst = ref MemoryMarshal.GetReference(destination);
        nuint i = 0;
        nuint uLen = (nuint)(uint)len;

        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<byte> mask256 = Vector256.Create(m32).AsByte();
            while (i + 32 <= uLen)
            {
                (Vector256.LoadUnsafe(ref src, i) ^ mask256).StoreUnsafe(ref dst, i);
                i += 32;
            }
        }

        if (Vector128.IsHardwareAccelerated && i + 16 <= uLen)
        {
            Vector128<byte> mask128 = Vector128.Create(m32).AsByte();
            (Vector128.LoadUnsafe(ref src, i) ^ mask128).StoreUnsafe(ref dst, i);
            i += 16;
        }

        ulong m64 = ((ulong)m32 << 32) | m32;
        while (i + 8 <= uLen)
        {
            ulong v = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, i)) ^ m64;
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, i), v);
            i += 8;
        }

        int shift = 24;
        while (i < uLen)
        {
            Unsafe.Add(ref dst, i) = (byte)(Unsafe.Add(ref src, i) ^ (byte)(mBe >> shift));
            shift = (shift - 8) & 24;
            i++;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDestinationTooSmall() =>
        throw new ArgumentException("Destination span is too small.", "destination");
}
