using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace SpanForge.Protocols.Mqtt;

/// <summary>OASIS MQTT v5.0 §2.1.2 control packet types.</summary>
public enum MqttPacketType : byte
{
    Connect = 1,
    ConnAck = 2,
    Publish = 3,
    PubAck = 4,
    PubRec = 5,
    PubRel = 6,
    PubComp = 7,
    Subscribe = 8,
    SubAck = 9,
    Unsubscribe = 10,
    UnsubAck = 11,
    PingReq = 12,
    PingResp = 13,
    Disconnect = 14,
    Auth = 15,
}

public readonly record struct MqttConnectPacket(
    byte ProtocolLevel,
    byte ConnectFlags,
    ushort KeepAlive,
    ByteSlice Properties,
    ByteSlice Payload);

public readonly record struct MqttConnAckPacket(
    bool SessionPresent,
    byte ReasonCode,
    ByteSlice Properties);

public readonly record struct MqttPublishPacket(
    bool Dup,
    byte Qos,
    bool Retain,
    ByteSlice TopicName,
    Option<ushort> PacketId,
    ByteSlice Properties,
    ByteSlice Payload);

public readonly record struct MqttPubAckPacket(ushort PacketId, byte ReasonCode, ByteSlice Properties);
public readonly record struct MqttSubscribePacket(ushort PacketId, ByteSlice Properties, ByteSlice TopicFilters);
public readonly record struct MqttSubAckPacket(ushort PacketId, ByteSlice Properties, ByteSlice ReasonCodes);
public readonly record struct MqttPingReqPacket;
public readonly record struct MqttPingRespPacket;
public readonly record struct MqttDisconnectPacket(byte ReasonCode, ByteSlice Properties);

/// <summary>
/// Zero-boxing C# 15 custom <c>[Union]</c> modeling MQTT v5.0 control packets with zero heap allocations
/// and compiler-enforced exhaustive switching.
/// </summary>
[Union]
public readonly struct MqttPacket : IUnion
{
    private readonly ByteSlice _slice1;  // TopicName or Properties
    private readonly ByteSlice _slice2;  // Properties or Payload or TopicFilters or ReasonCodes
    private readonly ByteSlice _slice3;  // Publish Payload
    private readonly ushort _u16;        // KeepAlive or PacketId
    private readonly byte _b0;           // ProtocolLevel / SessionPresent(0/1) / Publish flags(dup|qos|retain) / ReasonCode
    private readonly byte _b1;           // ConnectFlags / ReasonCode / HasPacketId(0/1)
    private readonly byte _tag;          // 1..9

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private MqttPacket(byte tag, ByteSlice s1, ByteSlice s2, ByteSlice s3, ushort u16, byte b0, byte b1)
    {
        _tag = tag;
        _slice1 = s1;
        _slice2 = s2;
        _slice3 = s3;
        _u16 = u16;
        _b0 = b0;
        _b1 = b1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MqttPacket(MqttConnectPacket p)
        : this(1, p.Properties, p.Payload, default, p.KeepAlive, p.ProtocolLevel, p.ConnectFlags) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MqttPacket(MqttConnAckPacket p)
        : this(2, p.Properties, default, default, 0, p.SessionPresent ? (byte)1 : (byte)0, p.ReasonCode) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MqttPacket(MqttPublishPacket p)
    {
        _tag = 3;
        _slice1 = p.TopicName;
        _slice2 = p.Properties;
        _slice3 = p.Payload;
        _b0 = (byte)((p.Dup ? 8 : 0) | ((p.Qos & 3) << 1) | (p.Retain ? 1 : 0));
        if (p.PacketId.TryGetValue(out Some<ushort> pid))
        {
            _u16 = pid.Value;
            _b1 = 1;
        }
        else
        {
            _u16 = 0;
            _b1 = 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MqttPacket(MqttPubAckPacket p)
        : this(4, p.Properties, default, default, p.PacketId, p.ReasonCode, 0) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MqttPacket(MqttSubscribePacket p)
        : this(5, p.Properties, p.TopicFilters, default, p.PacketId, 0, 0) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MqttPacket(MqttSubAckPacket p)
        : this(6, p.Properties, p.ReasonCodes, default, p.PacketId, 0, 0) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MqttPacket(MqttPingReqPacket _)
        : this(7, default, default, default, 0, 0, 0) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MqttPacket(MqttPingRespPacket _)
        : this(8, default, default, default, 0, 0, 0) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MqttPacket(MqttDisconnectPacket p)
        : this(9, p.Properties, default, default, 0, p.ReasonCode, 0) { }

    public object? Value => _tag switch
    {
        1 => new MqttConnectPacket(_b0, _b1, _u16, _slice1, _slice2),
        2 => new MqttConnAckPacket(_b0 != 0, _b1, _slice1),
        3 => new MqttPublishPacket((_b0 & 8) != 0, (byte)((_b0 >> 1) & 3), (_b0 & 1) != 0, _slice1, _b1 != 0 ? Option<ushort>.Of(_u16) : Option<ushort>.Nothing, _slice2, _slice3),
        4 => new MqttPubAckPacket(_u16, _b0, _slice1),
        5 => new MqttSubscribePacket(_u16, _slice1, _slice2),
        6 => new MqttSubAckPacket(_u16, _slice1, _slice2),
        7 => new MqttPingReqPacket(),
        8 => new MqttPingRespPacket(),
        9 => new MqttDisconnectPacket(_b0, _slice1),
        _ => null,
    };

    public bool HasValue => _tag != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out MqttConnectPacket value)
    {
        if (_tag == 1) { value = new MqttConnectPacket(_b0, _b1, _u16, _slice1, _slice2); return true; }
        Unsafe.SkipInit(out value); return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out MqttConnAckPacket value)
    {
        if (_tag == 2) { value = new MqttConnAckPacket(_b0 != 0, _b1, _slice1); return true; }
        Unsafe.SkipInit(out value); return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out MqttPublishPacket value)
    {
        if (_tag == 3)
        {
            value = new MqttPublishPacket(
                (_b0 & 8) != 0,
                (byte)((_b0 >> 1) & 3),
                (_b0 & 1) != 0,
                _slice1,
                _b1 != 0 ? Option<ushort>.Of(_u16) : Option<ushort>.Nothing,
                _slice2,
                _slice3);
            return true;
        }
        Unsafe.SkipInit(out value); return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out MqttPubAckPacket value)
    {
        if (_tag == 4) { value = new MqttPubAckPacket(_u16, _b0, _slice1); return true; }
        Unsafe.SkipInit(out value); return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out MqttSubscribePacket value)
    {
        if (_tag == 5) { value = new MqttSubscribePacket(_u16, _slice1, _slice2); return true; }
        Unsafe.SkipInit(out value); return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out MqttSubAckPacket value)
    {
        if (_tag == 6) { value = new MqttSubAckPacket(_u16, _slice1, _slice2); return true; }
        Unsafe.SkipInit(out value); return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out MqttPingReqPacket value)
    {
        value = default;
        return _tag == 7;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out MqttPingRespPacket value)
    {
        value = default;
        return _tag == 8;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out MqttDisconnectPacket value)
    {
        if (_tag == 9) { value = new MqttDisconnectPacket(_b0, _slice1); return true; }
        Unsafe.SkipInit(out value); return false;
    }
}

/// <summary>
/// Zero-allocation OASIS MQTT v5.0 control packet parser supporting streaming <see cref="Incomplete"/>
/// decoding, unrolled MQTT 1–4 byte Variable Byte Integer remaining length, strict fixed-header flag validation,
/// MQTT v5 property slices, and zero-GC-ref <see cref="TryParsePacket"/> fast paths.
/// </summary>
public static class MqttV5Parser
{
    public const int MaxVariableByteInt = 268_435_455; // (1 << 28) - 1 per MQTT 5.0 §1.5.5

    /// <summary>
    /// Unrolled 1–4 byte MQTT 5.0 Variable Byte Integer decoder returning zero-GC-ref <see cref="FastStatus"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FastStatus TryReadVariableByteInt(ref ByteCursor cursor, out int value)
    {
        int pos = cursor.Position;
        ReadOnlySpan<byte> buf = cursor.Buffer;
        ref byte baseRef = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(buf);
        var st = TryReadVbiCore(ref baseRef, pos, buf.Length, cursor.Mode, out value, out int nextPos);
        if (st.IsParsed)
        {
            cursor.Position = nextPos;
        }
        return st;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static FastStatus TryReadVbiCore(
        ref byte baseRef,
        int pos,
        int end,
        InputMode mode,
        out int value,
        out int nextPos)
    {
        int rem = end - pos;
        if (rem < 1)
        {
            Unsafe.SkipInit(out value);
            Unsafe.SkipInit(out nextPos);
            return mode == InputMode.Streaming ? FastStatus.IncompleteBytes(1) : FastStatus.Backtrack;
        }

        ref byte p = ref Unsafe.Add(ref baseRef, (nint)(uint)pos);

        int b0 = p;
        if (b0 < 0x80)
        {
            value = b0;
            nextPos = pos + 1;
            return FastStatus.Parsed;
        }

        if (rem < 2)
        {
            Unsafe.SkipInit(out value);
            Unsafe.SkipInit(out nextPos);
            return mode == InputMode.Streaming ? FastStatus.IncompleteBytes(1) : FastStatus.Backtrack;
        }

        int b1 = Unsafe.Add(ref p, 1);
        int v = (b0 & 0x7F) | ((b1 & 0x7F) << 7);
        if (b1 < 0x80)
        {
            value = v;
            nextPos = pos + 2;
            return FastStatus.Parsed;
        }

        if (rem < 3)
        {
            Unsafe.SkipInit(out value);
            Unsafe.SkipInit(out nextPos);
            return mode == InputMode.Streaming ? FastStatus.IncompleteBytes(1) : FastStatus.Backtrack;
        }

        int b2 = Unsafe.Add(ref p, 2);
        v |= (b2 & 0x7F) << 14;
        if (b2 < 0x80)
        {
            value = v;
            nextPos = pos + 3;
            return FastStatus.Parsed;
        }

        if (rem < 4)
        {
            Unsafe.SkipInit(out value);
            Unsafe.SkipInit(out nextPos);
            return mode == InputMode.Streaming ? FastStatus.IncompleteBytes(1) : FastStatus.Backtrack;
        }

        int b3 = Unsafe.Add(ref p, 3);
        if (b3 >= 0x80)
        {
            Unsafe.SkipInit(out value);
            Unsafe.SkipInit(out nextPos);
            return FastStatus.Failure;
        }

        value = v | (b3 << 21);
        nextPos = pos + 4;
        return FastStatus.Parsed;
    }

    /// <summary>Reads an MQTT 5.0 1..4 byte Variable Byte Integer (up to 268,435,455).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseResult<int> ReadVariableByteInt(ref ByteCursor cursor)
    {
        int start = cursor.Position;
        var st = TryReadVariableByteInt(ref cursor, out int value);
        if (st.IsParsed) return new Parsed<int>(value);
        if (st.IsIncomplete) return Incomplete.AtLeast(1);
        return st.IsFailure
            ? Failure.At(start, ErrorKind.VarInt, "MQTT Variable Byte Integer exceeds 4 bytes")
            : Backtrack.At(start, ErrorKind.Eof);
    }

    /// <summary>
    /// High-throughput zero-GC-ref <see cref="FastStatus"/> entry point for decoding an OASIS MQTT v5.0 control packet.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FastStatus TryParsePacket(ref ByteCursor cursor, out MqttPacket packet)
    {
        int start = cursor.Position;
        ReadOnlySpan<byte> buf = cursor.Buffer;
        int len = buf.Length;
        if (start >= len)
        {
            Unsafe.SkipInit(out packet);
            return cursor.Mode == InputMode.Streaming ? FastStatus.IncompleteBytes(1) : FastStatus.Backtrack;
        }

        ref byte baseRef = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(buf);
        byte firstByte = Unsafe.Add(ref baseRef, (nint)(uint)start);
        var packetType = (MqttPacketType)(firstByte >> 4);
        byte flags = (byte)(firstByte & 0x0F);

        cursor.Position = start + 1;
        var remLenSt = TryReadVariableByteInt(ref cursor, out int remainingLength);
        if (!remLenSt.IsParsed)
        {
            cursor.Position = start;
            Unsafe.SkipInit(out packet);
            return remLenSt;
        }

        int bodyBase = cursor.Position;
        int avail = len - bodyBase;
        if (avail < remainingLength)
        {
            cursor.Position = start;
            Unsafe.SkipInit(out packet);
            return cursor.Mode == InputMode.Streaming
                ? FastStatus.IncompleteBytes(remainingLength - avail)
                : FastStatus.Backtrack;
        }

        int bodyEnd = bodyBase + remainingLength;
        int pPos = bodyBase;

        // Fast hot path: MQTT v5 PUBLISH
        if (packetType == MqttPacketType.Publish)
        {
            bool dup = (flags & 0x08) != 0;
            byte qos = (byte)((flags >> 1) & 0x03);
            bool retain = (flags & 0x01) != 0;
            if (qos == 3 || (qos == 0 && dup) || remainingLength < 2)
            {
                cursor.Position = start;
                Unsafe.SkipInit(out packet);
                return FastStatus.Failure;
            }

            ushort topicLen = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref baseRef, (nint)(uint)pPos));
            if (BitConverter.IsLittleEndian) topicLen = BinaryPrimitives.ReverseEndianness(topicLen);
            pPos += 2;
            if (bodyEnd - pPos < topicLen)
            {
                cursor.Position = start;
                Unsafe.SkipInit(out packet);
                return FastStatus.Failure;
            }

            var topicSlice = new ByteSlice(pPos, topicLen);
            pPos += topicLen;

            Option<ushort> packetId = Option<ushort>.Nothing;
            if (qos > 0)
            {
                if (bodyEnd - pPos < 2)
                {
                    cursor.Position = start;
                    Unsafe.SkipInit(out packet);
                    return FastStatus.Failure;
                }

                ushort pid = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref baseRef, (nint)(uint)pPos));
                if (BitConverter.IsLittleEndian) pid = BinaryPrimitives.ReverseEndianness(pid);
                if (pid == 0)
                {
                    cursor.Position = start;
                    Unsafe.SkipInit(out packet);
                    return FastStatus.Failure;
                }

                packetId = Option<ushort>.Of(pid);
                pPos += 2;
            }

            if (!TryReadPropertiesSpan(ref baseRef, ref pPos, bodyEnd, out ByteSlice props))
            {
                cursor.Position = start;
                Unsafe.SkipInit(out packet);
                return FastStatus.Failure;
            }

            var payload = new ByteSlice(pPos, bodyEnd - pPos);
            cursor.Position = bodyEnd;
            packet = new MqttPublishPacket(dup, qos, retain, topicSlice, packetId, props, payload);
            return FastStatus.Parsed;
        }

        return TryParseNonPublishPacket(ref cursor, start, packetType, flags, bodyBase, remainingLength, out packet);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FastStatus TryParseNonPublishPacket(
        ref ByteCursor cursor,
        int start,
        MqttPacketType packetType,
        byte flags,
        int bodyBase,
        int remainingLength,
        out MqttPacket packet)
    {
        ReadOnlySpan<byte> buf = cursor.Buffer;
        ref byte baseRef = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(buf);
        int bodyEnd = bodyBase + remainingLength;
        int pPos = bodyBase;

        switch (packetType)
        {
            case MqttPacketType.Connect:
            {
                if (flags != 0 || remainingLength < 10) goto fail;
                ushort protoLen = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref baseRef, (nint)(uint)pPos));
                if (BitConverter.IsLittleEndian) protoLen = BinaryPrimitives.ReverseEndianness(protoLen);
                pPos += 2;
                if (protoLen != 4 || bodyEnd - pPos < 8 || !buf.Slice(pPos, 4).SequenceEqual("MQTT"u8)) goto fail;
                pPos += 4;
                byte lvl = Unsafe.Add(ref baseRef, (nint)(uint)pPos++);
                byte cf = Unsafe.Add(ref baseRef, (nint)(uint)pPos++);
                ushort ka = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref baseRef, (nint)(uint)pPos));
                if (BitConverter.IsLittleEndian) ka = BinaryPrimitives.ReverseEndianness(ka);
                pPos += 2;
                if (lvl != 5 || (cf & 0x01) != 0) goto fail;
                if (!TryReadPropertiesSpan(ref baseRef, ref pPos, bodyEnd, out ByteSlice props)) goto fail;
                cursor.Position = bodyEnd;
                packet = new MqttConnectPacket(lvl, cf, ka, props, new ByteSlice(pPos, bodyEnd - pPos));
                return FastStatus.Parsed;
            }

            case MqttPacketType.ConnAck:
            {
                if (flags != 0 || remainingLength < 2) goto fail;
                byte ackFlags = Unsafe.Add(ref baseRef, (nint)(uint)pPos++);
                byte reason = Unsafe.Add(ref baseRef, (nint)(uint)pPos++);
                if ((ackFlags & 0xFE) != 0) goto fail;
                if (!TryReadPropertiesSpan(ref baseRef, ref pPos, bodyEnd, out ByteSlice props) || pPos != bodyEnd) goto fail;
                cursor.Position = bodyEnd;
                packet = new MqttConnAckPacket((ackFlags & 1) != 0, reason, props);
                return FastStatus.Parsed;
            }

            case MqttPacketType.PubAck:
            {
                if (flags != 0 || remainingLength < 2) goto fail;
                ushort pid = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref baseRef, (nint)(uint)pPos));
                if (BitConverter.IsLittleEndian) pid = BinaryPrimitives.ReverseEndianness(pid);
                pPos += 2;
                if (pid == 0) goto fail;
                byte rc = 0;
                ByteSlice props = ByteSlice.Empty;
                if (pPos < bodyEnd)
                {
                    rc = Unsafe.Add(ref baseRef, (nint)(uint)pPos++);
                    if (pPos < bodyEnd && !TryReadPropertiesSpan(ref baseRef, ref pPos, bodyEnd, out props)) goto fail;
                }
                if (pPos != bodyEnd) goto fail;
                cursor.Position = bodyEnd;
                packet = new MqttPubAckPacket(pid, rc, props);
                return FastStatus.Parsed;
            }

            case MqttPacketType.Subscribe:
            {
                if (flags != 0x02 || remainingLength < 2) goto fail;
                ushort pid = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref baseRef, (nint)(uint)pPos));
                if (BitConverter.IsLittleEndian) pid = BinaryPrimitives.ReverseEndianness(pid);
                pPos += 2;
                if (pid == 0 || !TryReadPropertiesSpan(ref baseRef, ref pPos, bodyEnd, out ByteSlice props) || pPos >= bodyEnd) goto fail;
                cursor.Position = bodyEnd;
                packet = new MqttSubscribePacket(pid, props, new ByteSlice(pPos, bodyEnd - pPos));
                return FastStatus.Parsed;
            }

            case MqttPacketType.SubAck:
            {
                if (flags != 0 || remainingLength < 2) goto fail;
                ushort pid = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref baseRef, (nint)(uint)pPos));
                if (BitConverter.IsLittleEndian) pid = BinaryPrimitives.ReverseEndianness(pid);
                pPos += 2;
                if (pid == 0 || !TryReadPropertiesSpan(ref baseRef, ref pPos, bodyEnd, out ByteSlice props) || pPos >= bodyEnd) goto fail;
                cursor.Position = bodyEnd;
                packet = new MqttSubAckPacket(pid, props, new ByteSlice(pPos, bodyEnd - pPos));
                return FastStatus.Parsed;
            }

            case MqttPacketType.PingReq:
                if (flags != 0 || remainingLength != 0) goto fail;
                cursor.Position = bodyEnd;
                packet = new MqttPingReqPacket();
                return FastStatus.Parsed;

            case MqttPacketType.PingResp:
                if (flags != 0 || remainingLength != 0) goto fail;
                cursor.Position = bodyEnd;
                packet = new MqttPingRespPacket();
                return FastStatus.Parsed;

            case MqttPacketType.Disconnect:
            {
                if (flags != 0) goto fail;
                byte rc = 0;
                ByteSlice props = ByteSlice.Empty;
                if (pPos < bodyEnd)
                {
                    rc = Unsafe.Add(ref baseRef, (nint)(uint)pPos++);
                    if (pPos < bodyEnd && !TryReadPropertiesSpan(ref baseRef, ref pPos, bodyEnd, out props)) goto fail;
                }
                if (pPos != bodyEnd) goto fail;
                cursor.Position = bodyEnd;
                packet = new MqttDisconnectPacket(rc, props);
                return FastStatus.Parsed;
            }

            default:
                goto fail;
        }

    fail:
        cursor.Position = start;
        Unsafe.SkipInit(out packet);
        return FastStatus.Failure;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryReadPropertiesSpan(ref byte baseRef, ref int pos, int end, out ByteSlice properties)
    {
        if (!TryReadVbiCore(ref baseRef, pos, end, InputMode.Complete, out int propLen, out int propStart).IsParsed
            || end - propStart < propLen)
        {
            properties = default;
            return false;
        }

        properties = new ByteSlice(propStart, propLen);
        pos = propStart + propLen;
        return true;
    }

    public static ParseResult<MqttPacket> ParsePacket(ref ByteCursor cursor)
    {
        var cp = cursor.Save();
        var hdrRes = cursor.ReadU8();
        if (!hdrRes.TryGetValue(out Parsed<byte> hdrOk))
        {
            return hdrRes.PropagateNonParsed<MqttPacket>();
        }

        byte firstByte = hdrOk.Value;
        var packetType = (MqttPacketType)(firstByte >> 4);
        byte flags = (byte)(firstByte & 0x0F);

        var remLenRes = ReadVariableByteInt(ref cursor);
        if (!remLenRes.TryGetValue(out Parsed<int> remLenOk))
        {
            cursor.Reset(cp);
            return remLenRes.PropagateNonParsed<MqttPacket>();
        }

        int remainingLength = remLenOk.Value;
        var bodySliceRes = cursor.Take(remainingLength);
        if (!bodySliceRes.TryGetValue(out Parsed<ByteSlice> bodySliceOk))
        {
            cursor.Reset(cp);
            return bodySliceRes.PropagateNonParsed<MqttPacket>();
        }

        int bodyBase = bodySliceOk.Value.Offset;
        ReadOnlySpan<byte> body = cursor.Buffer.Slice(bodyBase, remainingLength);
        var sub = new ByteCursor(body, Endianness.Big, InputMode.Complete);

        switch (packetType)
        {
            case MqttPacketType.Connect:
            {
                if (flags != 0)
                    return FailRollback(ref cursor, cp, "CONNECT fixed-header flags must be 0x0");

                var protoRes = sub.ReadLengthPrefixedBytes(LengthPrefix.U16);
                if (!protoRes.TryGetValue(out Parsed<ByteSlice> protoOk) || !body[protoOk.Value].SequenceEqual("MQTT"u8))
                    return FailRollback(ref cursor, cp, "CONNECT protocol name must be 'MQTT'");

                var lvlRes = sub.ReadU8();
                var cfRes = sub.ReadU8();
                var kaRes = sub.ReadU16BE();
                if (!lvlRes.TryGetValue(out Parsed<byte> lvl) ||
                    !cfRes.TryGetValue(out Parsed<byte> cf) ||
                    !kaRes.TryGetValue(out Parsed<ushort> ka))
                    return FailRollback(ref cursor, cp, "Truncated CONNECT variable header");

                if (lvl.Value != 5)
                    return FailRollback(ref cursor, cp, $"Expected MQTT v5 protocol level 5, got {lvl.Value}");
                if ((cf.Value & 0x01) != 0)
                    return FailRollback(ref cursor, cp, "CONNECT reserved flag bit 0 must be 0");

                if (!TryReadProperties(ref sub, bodyBase, out ByteSlice props, out string propErr))
                    return FailRollback(ref cursor, cp, propErr);

                var payload = new ByteSlice(bodyBase + sub.Position, sub.RemainingLength);
                return new Parsed<MqttPacket>(new MqttConnectPacket(lvl.Value, cf.Value, ka.Value, props, payload));
            }

            case MqttPacketType.ConnAck:
            {
                if (flags != 0)
                    return FailRollback(ref cursor, cp, "CONNACK fixed-header flags must be 0x0");

                var ackFlagsRes = sub.ReadU8();
                var reasonRes = sub.ReadU8();
                if (!ackFlagsRes.TryGetValue(out Parsed<byte> ackFlags) ||
                    !reasonRes.TryGetValue(out Parsed<byte> reason))
                    return FailRollback(ref cursor, cp, "Truncated CONNACK variable header");

                if ((ackFlags.Value & 0xFE) != 0)
                    return FailRollback(ref cursor, cp, "CONNACK reserved acknowledge bits must be 0");

                if (!TryReadProperties(ref sub, bodyBase, out ByteSlice props, out string propErr))
                    return FailRollback(ref cursor, cp, propErr);

                if (!sub.IsAtEnd)
                    return FailRollback(ref cursor, cp, "CONNACK contains unexpected trailing payload");

                return new Parsed<MqttPacket>(new MqttConnAckPacket((ackFlags.Value & 1) != 0, reason.Value, props));
            }

            case MqttPacketType.Publish:
            {
                bool dup = (flags & 0x08) != 0;
                byte qos = (byte)((flags >> 1) & 0x03);
                bool retain = (flags & 0x01) != 0;
                if (qos == 3)
                    return FailRollback(ref cursor, cp, "PUBLISH QoS 3 is forbidden by MQTT 5.0");
                if (qos == 0 && dup)
                    return FailRollback(ref cursor, cp, "PUBLISH DUP flag must be 0 when QoS is 0");

                var topicRes = sub.ReadLengthPrefixedBytes(LengthPrefix.U16);
                if (!topicRes.TryGetValue(out Parsed<ByteSlice> topicOk))
                    return FailRollback(ref cursor, cp, "Truncated PUBLISH topic name");

                var topicAbs = new ByteSlice(bodyBase + topicOk.Value.Offset, topicOk.Value.Length);
                Option<ushort> packetId = Option<ushort>.Nothing;
                if (qos > 0)
                {
                    var pidRes = sub.ReadU16BE();
                    if (!pidRes.TryGetValue(out Parsed<ushort> pidOk) || pidOk.Value == 0)
                        return FailRollback(ref cursor, cp, "PUBLISH QoS > 0 requires non-zero packet identifier");
                    packetId = Option<ushort>.Of(pidOk.Value);
                }

                if (!TryReadProperties(ref sub, bodyBase, out ByteSlice props, out string propErr))
                    return FailRollback(ref cursor, cp, propErr);

                var payload = new ByteSlice(bodyBase + sub.Position, sub.RemainingLength);
                return new Parsed<MqttPacket>(new MqttPublishPacket(dup, qos, retain, topicAbs, packetId, props, payload));
            }

            case MqttPacketType.PubAck:
            {
                if (flags != 0)
                    return FailRollback(ref cursor, cp, "PUBACK fixed-header flags must be 0x0");

                var pidRes = sub.ReadU16BE();
                if (!pidRes.TryGetValue(out Parsed<ushort> pidOk) || pidOk.Value == 0)
                    return FailRollback(ref cursor, cp, "PUBACK requires non-zero packet identifier");

                byte reasonCode = 0;
                ByteSlice props = ByteSlice.Empty;
                if (!sub.IsAtEnd)
                {
                    var rcRes = sub.ReadU8();
                    if (!rcRes.TryGetValue(out Parsed<byte> rcOk))
                        return FailRollback(ref cursor, cp, "Truncated PUBACK reason code");
                    reasonCode = rcOk.Value;

                    if (!sub.IsAtEnd && !TryReadProperties(ref sub, bodyBase, out props, out string propErr))
                        return FailRollback(ref cursor, cp, propErr);
                }

                if (!sub.IsAtEnd)
                    return FailRollback(ref cursor, cp, "PUBACK contains unexpected trailing bytes");

                return new Parsed<MqttPacket>(new MqttPubAckPacket(pidOk.Value, reasonCode, props));
            }

            case MqttPacketType.Subscribe:
            {
                if (flags != 0x02)
                    return FailRollback(ref cursor, cp, "SUBSCRIBE fixed-header flags must be 0x2");

                var pidRes = sub.ReadU16BE();
                if (!pidRes.TryGetValue(out Parsed<ushort> pidOk) || pidOk.Value == 0)
                    return FailRollback(ref cursor, cp, "SUBSCRIBE requires non-zero packet identifier");

                if (!TryReadProperties(ref sub, bodyBase, out ByteSlice props, out string propErr))
                    return FailRollback(ref cursor, cp, propErr);

                if (sub.IsAtEnd)
                    return FailRollback(ref cursor, cp, "SUBSCRIBE must contain at least one topic filter");

                var filters = new ByteSlice(bodyBase + sub.Position, sub.RemainingLength);
                return new Parsed<MqttPacket>(new MqttSubscribePacket(pidOk.Value, props, filters));
            }

            case MqttPacketType.SubAck:
            {
                if (flags != 0)
                    return FailRollback(ref cursor, cp, "SUBACK fixed-header flags must be 0x0");

                var pidRes = sub.ReadU16BE();
                if (!pidRes.TryGetValue(out Parsed<ushort> pidOk) || pidOk.Value == 0)
                    return FailRollback(ref cursor, cp, "SUBACK requires non-zero packet identifier");

                if (!TryReadProperties(ref sub, bodyBase, out ByteSlice props, out string propErr))
                    return FailRollback(ref cursor, cp, propErr);

                if (sub.IsAtEnd)
                    return FailRollback(ref cursor, cp, "SUBACK must contain at least one reason code");

                var codes = new ByteSlice(bodyBase + sub.Position, sub.RemainingLength);
                return new Parsed<MqttPacket>(new MqttSubAckPacket(pidOk.Value, props, codes));
            }

            case MqttPacketType.PingReq:
            {
                if (flags != 0 || remainingLength != 0)
                    return FailRollback(ref cursor, cp, "PINGREQ must have flags=0 and remaining length 0");
                return new Parsed<MqttPacket>(new MqttPingReqPacket());
            }

            case MqttPacketType.PingResp:
            {
                if (flags != 0 || remainingLength != 0)
                    return FailRollback(ref cursor, cp, "PINGRESP must have flags=0 and remaining length 0");
                return new Parsed<MqttPacket>(new MqttPingRespPacket());
            }

            case MqttPacketType.Disconnect:
            {
                if (flags != 0)
                    return FailRollback(ref cursor, cp, "DISCONNECT fixed-header flags must be 0x0");

                byte reasonCode = 0;
                ByteSlice props = ByteSlice.Empty;
                if (!sub.IsAtEnd)
                {
                    var rcRes = sub.ReadU8();
                    if (!rcRes.TryGetValue(out Parsed<byte> rcOk))
                        return FailRollback(ref cursor, cp, "Truncated DISCONNECT reason code");
                    reasonCode = rcOk.Value;

                    if (!sub.IsAtEnd && !TryReadProperties(ref sub, bodyBase, out props, out string propErr))
                        return FailRollback(ref cursor, cp, propErr);
                }

                if (!sub.IsAtEnd)
                    return FailRollback(ref cursor, cp, "DISCONNECT contains unexpected trailing bytes");

                return new Parsed<MqttPacket>(new MqttDisconnectPacket(reasonCode, props));
            }

            default:
                return FailRollback(ref cursor, cp, $"Unsupported/reserved MQTT packet type {(byte)packetType}");
        }
    }

    public static void WriteVariableByteInt(List<byte> output, int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaxVariableByteInt);

        int x = value;
        do
        {
            byte encoded = (byte)(x & 0x7F);
            x >>= 7;
            if (x > 0) encoded |= 0x80;
            output.Add(encoded);
        }
        while (x > 0);
    }

    public static byte[] BuildPacket(MqttPacketType type, byte flags, ReadOnlySpan<byte> body)
    {
        List<byte> packet = [with(capacity: 5 + body.Length)];
        packet.Add((byte)(((byte)type << 4) | (flags & 0x0F)));
        WriteVariableByteInt(packet, body.Length);
        foreach (byte b in body) packet.Add(b);
        return packet.ToArray();
    }

    public static byte[] BuildPublish(
        ReadOnlySpan<byte> topic,
        ReadOnlySpan<byte> payload,
        byte qos = 0,
        ushort packetId = 0,
        bool dup = false,
        bool retain = false,
        ReadOnlySpan<byte> properties = default)
    {
        List<byte> body = [with(capacity: 8 + topic.Length + properties.Length + payload.Length)];
        body.Add((byte)(topic.Length >> 8));
        body.Add((byte)topic.Length);
        foreach (byte b in topic) body.Add(b);
        if (qos > 0)
        {
            body.Add((byte)(packetId >> 8));
            body.Add((byte)packetId);
        }
        WriteVariableByteInt(body, properties.Length);
        foreach (byte b in properties) body.Add(b);
        foreach (byte b in payload) body.Add(b);

        byte flags = (byte)((dup ? 8 : 0) | ((qos & 3) << 1) | (retain ? 1 : 0));
        return BuildPacket(MqttPacketType.Publish, flags, body.ToArray());
    }

    private static bool TryReadProperties(
        ref ByteCursor sub,
        int bodyBase,
        out ByteSlice properties,
        out string error)
    {
        var propLenRes = ReadVariableByteInt(ref sub);
        if (!propLenRes.TryGetValue(out Parsed<int> propLenOk))
        {
            properties = default;
            error = "Invalid or truncated MQTT v5 property length";
            return false;
        }

        var sliceRes = sub.Take(propLenOk.Value);
        if (!sliceRes.TryGetValue(out Parsed<ByteSlice> sliceOk))
        {
            properties = default;
            error = "MQTT v5 property length exceeds remaining packet body";
            return false;
        }

        properties = new ByteSlice(bodyBase + sliceOk.Value.Offset, sliceOk.Value.Length);
        error = string.Empty;
        return true;
    }

    private static ParseResult<MqttPacket> FailRollback(ref ByteCursor cursor, Checkpoint cp, string reason)
    {
        cursor.Reset(cp);
        return Failure.At(cp.Position, ErrorKind.Protocol, reason);
    }
}
