using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using CsCheck;
using SpanForge.Protocols.Http2;
using SpanForge.Protocols.Mqtt;
using SpanForge.Protocols.Protobuf;
using SpanForge.Streaming;
using Xunit;

namespace SpanForge.Tests;

public sealed class NewProtocolsTests
{
    // =========================================================================
    // 1. Protobuf Wire Format
    // =========================================================================

    [Fact]
    public void Protobuf_AllWireTypes_ZigZag_Packed_Streaming_And_ZeroAlloc()
    {
        // ZigZag roundtrips
        int[] i32s = [0, -1, 1, -2, 2, int.MinValue, int.MaxValue];
        foreach (int v in i32s)
            Assert.Equal(v, ProtobufParser.DecodeZigZag32(ProtobufParser.EncodeZigZag32(v)));

        long[] i64s = [0, -1, 1, -2, 2, long.MinValue, long.MaxValue];
        foreach (long v in i64s)
            Assert.Equal(v, ProtobufParser.DecodeZigZag64(ProtobufParser.EncodeZigZag64(v)));

        // Build packed varints payload [1, 150, 300]
        List<byte> packed = [];
        ProtobufParser.WriteVarInt(packed, 1);
        ProtobufParser.WriteVarInt(packed, 150);
        ProtobufParser.WriteVarInt(packed, 300);

        byte[] msg =
        [
            .. ProtobufParser.EncodeVarIntField(1, 150UL),
            .. ProtobufParser.EncodeFixed64Field(2, 0x0102_0304_0506_0708UL),
            .. ProtobufParser.EncodeLengthDelimitedField(3, "SpanForge"u8),
            .. ProtobufParser.EncodeFixed32Field(4, 0xDEADBEEFu),
            .. ProtobufParser.EncodeLengthDelimitedField(5, packed.ToArray()),
        ];

        // ParseField & exhaustive [Union] switch
        var c = msg.AsCursor();
        Assert.True(ProtobufParser.ParseField(ref c).TryGetValue(out Parsed<ProtobufField> f1));
        Assert.True(f1.Value.TryGetValue(out ProtobufVarIntField vf) && vf.FieldNumber == 1 && vf.Value == 150UL);

        Assert.True(ProtobufParser.ParseField(ref c).TryGetValue(out Parsed<ProtobufField> f2));
        Assert.True(f2.Value.TryGetValue(out ProtobufFixed64Field f64) && f64.FieldNumber == 2 && f64.Bits == 0x0102_0304_0506_0708UL);

        Assert.True(ProtobufParser.ParseField(ref c).TryGetValue(out Parsed<ProtobufField> f3));
        Assert.True(f3.Value.TryGetValue(out ProtobufLengthDelimitedField ld) && ld.FieldNumber == 3);
        Assert.SequenceEqual<byte>("SpanForge"u8, msg.AsSpan()[ld.Payload]);

        Assert.True(ProtobufParser.ParseField(ref c).TryGetValue(out Parsed<ProtobufField> f4));
        Assert.True(f4.Value.TryGetValue(out ProtobufFixed32Field f32) && f32.FieldNumber == 4 && f32.Bits == 0xDEADBEEFu);

        Assert.True(ProtobufParser.ParseField(ref c).TryGetValue(out Parsed<ProtobufField> f5));
        Assert.True(f5.Value.TryGetValue(out ProtobufLengthDelimitedField ldPacked) && ldPacked.FieldNumber == 5);
        Span<ulong> unpacked = stackalloc ulong[4];
        Assert.True(ProtobufParser.ParsePackedVarInts(msg.AsSpan()[ldPacked.Payload], unpacked).TryGetValue(out Parsed<int> cnt) && cnt.Value == 3);
        Assert.Equal(1UL, unpacked[0]);
        Assert.Equal(150UL, unpacked[1]);
        Assert.Equal(300UL, unpacked[2]);
        Assert.True(c.IsAtEnd);

        // Destination too small on ParsePackedVarInts -> Failure
        Span<ulong> tiny = stackalloc ulong[2];
        Assert.True(ProtobufParser.ParsePackedVarInts(msg.AsSpan()[ldPacked.Payload], tiny).IsFailure);

        // TryParseField across all 4 wire types + streaming Incomplete + errors
        var ct = msg.AsCursor();
        int decodedCount = 0;
        while (!ct.IsAtEnd)
        {
            Assert.True(ProtobufParser.TryParseField(ref ct, out ProtobufField fld).IsParsed);
            Assert.True(fld.HasValue && fld.FieldNumber > 0);
            Assert.True(fld.Value is not null);
            decodedCount++;
        }
        Assert.Equal(5, decodedCount);

        // Byte-by-byte streaming of length-delimited field
        byte[] ldWire = ProtobufParser.EncodeLengthDelimitedField(7, "hello"u8);
        var dec = new StreamingDecoder<ProtobufField>(ProtobufParser.ParseField);
        for (int i = 0; i < ldWire.Length; i++)
        {
            dec.Append(ldWire.AsSpan(i, 1));
            var st = dec.TryDecodeNext(out ReadOnlySpan<byte> frame);
            if (i < ldWire.Length - 1) Assert.True(st.IsIncomplete);
            else Assert.True(st.TryGetValue(out Parsed<ProtobufField> ok) && ok.Value.TryGetValue(out ProtobufLengthDelimitedField pld) && frame[pld.Payload].SequenceEqual("hello"u8));
        }

        // Invalid field number 0 & invalid wire types (3, 4, 6, 7) -> Failure with rollback
        byte[][] invalidCases =
        [
            [0x00, 0x01],       // field 0
            [0x0B],             // field 1, wireType 3 (StartGroup)
            [0x0C],             // field 1, wireType 4 (EndGroup)
            [0x0E],             // field 1, wireType 6 (invalid)
            [0x0F],             // field 1, wireType 7 (invalid)
        ];
        foreach (byte[] bad in invalidCases)
        {
            var cb = bad.AsCursor();
            Assert.True(ProtobufParser.ParseField(ref cb).IsFailure && cb.Position == 0);
            Assert.True(ProtobufParser.TryParseField(ref cb, out _).IsFailure && cb.Position == 0);
        }

        // Strict 0 bytes allocation assertion
        _ = RunPbLoop(msg, 100);
        long before = GC.GetAllocatedBytesForCurrentThread();
        ulong chk = RunPbLoop(msg, 5_000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(chk > 0);
        Assert.Equal(0L, allocated);

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        static ulong RunPbLoop(byte[] buf, int iterations)
        {
            ulong acc = 0;
            ReadOnlySpan<byte> span = buf;
            for (int i = 0; i < iterations; i++)
            {
                var cur = span.AsCursor();
                while (!cur.IsAtEnd)
                {
                    if (!ProtobufParser.TryParseField(ref cur, out ProtobufField f).IsParsed) break;
                    acc += f switch
                    {
                        ProtobufVarIntField(_, var v) => v,
                        ProtobufFixed64Field(_, var b) => b,
                        ProtobufLengthDelimitedField(_, var s) => (ulong)s.Length,
                        ProtobufFixed32Field(_, var b) => b,
                        null => 0UL,
                    };
                }
            }
            return acc;
        }
    }

    // =========================================================================
    // 2. HTTP/2 Framing (RFC 9113)
    // =========================================================================

    [Fact]
    public void Http2_AllFrameTypes_Validation_Streaming_And_ZeroAlloc()
    {
        // Client connection preface
        var cp = Http2FrameParser.ClientConnectionPreface.AsCursor();
        Assert.True(Http2FrameParser.ParseClientPreface(ref cp).IsParsed && cp.IsAtEnd);

        // 1. Padded DATA frame
        byte[] dataPayload = [2, (byte)'O', (byte)'K', 0, 0];
        byte[] dataFrame = Http2FrameParser.BuildFrame(Http2FrameType.Data, Http2FrameFlags.Padded | Http2FrameFlags.EndStream, 3, dataPayload);
        var c1 = dataFrame.AsCursor();
        Assert.True(Http2FrameParser.ParseFrame(ref c1).TryGetValue(out Parsed<Http2Frame> p1));
        Assert.True(p1.Value.TryGetValue(out Http2DataFrame df) && df.StreamId == 3 && df.PadLength == 2 && dataFrame.AsSpan()[df.Data].SequenceEqual("OK"u8));

        // 2. HEADERS frame with Padded + Priority
        byte[] hdrPayload = [1, 0x80, 0x00, 0x00, 0x05, 16, 0x82, 0x86, 0x00];
        byte[] hdrFrame = Http2FrameParser.BuildFrame(Http2FrameType.Headers, Http2FrameFlags.Padded | Http2FrameFlags.Priority | Http2FrameFlags.EndHeaders, 7, hdrPayload);
        var c2 = hdrFrame.AsCursor();
        Assert.True(Http2FrameParser.ParseFrame(ref c2).TryGetValue(out Parsed<Http2Frame> p2));
        Assert.True(p2.Value.TryGetValue(out Http2HeadersFrame hf) && hf.StreamId == 7 && hf.PadLength == 1 && hf.Priority.TryGetValue(out Some<Http2PrioritySpec> ps) && ps.Value.Exclusive && ps.Value.StreamDependency == 5 && ps.Value.Weight == 16);
        Assert.SequenceEqual<byte>([0x82, 0x86], hdrFrame.AsSpan()[hf.HeaderBlockFragment]);

        // 3. PRIORITY, RST_STREAM, SETTINGS, PUSH_PROMISE, PING, GOAWAY, WINDOW_UPDATE, CONTINUATION, UNKNOWN
        byte[] prioFrame = Http2FrameParser.BuildFrame(Http2FrameType.Priority, Http2FrameFlags.None, 9, [0x00, 0x00, 0x00, 0x01, 200]);
        var c3 = prioFrame.AsCursor();
        Assert.True(Http2FrameParser.ParseFrame(ref c3).TryGetValue(out Parsed<Http2Frame> p3) && p3.Value.TryGetValue(out Http2PriorityFrame pf) && !pf.Priority.Exclusive && pf.Priority.StreamDependency == 1 && pf.Priority.Weight == 200);

        byte[] rstFrame = Http2FrameParser.BuildFrame(Http2FrameType.RstStream, Http2FrameFlags.None, 9, [0, 0, 0, 8]);
        var c4 = rstFrame.AsCursor();
        Assert.True(Http2FrameParser.ParseFrame(ref c4).TryGetValue(out Parsed<Http2Frame> p4) && p4.Value.TryGetValue(out Http2RstStreamFrame rf) && rf.ErrorCode == 8);

        byte[] settingsPayload = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(settingsPayload.AsSpan(0, 2), 3); // MAX_CONCURRENT_STREAMS
        BinaryPrimitives.WriteUInt32BigEndian(settingsPayload.AsSpan(2, 4), 100);
        BinaryPrimitives.WriteUInt16BigEndian(settingsPayload.AsSpan(6, 2), 4); // INITIAL_WINDOW_SIZE
        BinaryPrimitives.WriteUInt32BigEndian(settingsPayload.AsSpan(8, 4), 65535);
        byte[] settingsFrame = Http2FrameParser.BuildFrame(Http2FrameType.Settings, Http2FrameFlags.None, 0, settingsPayload);
        var c5 = settingsFrame.AsCursor();
        Assert.True(Http2FrameParser.ParseFrame(ref c5).TryGetValue(out Parsed<Http2Frame> p5));
        Assert.True(p5.Value.TryGetValue(out Http2SettingsFrame sf));
        Assert.True(Http2FrameParser.TryReadSetting(settingsFrame, sf.Entries, 0, out var se0) && se0.Id == 3 && se0.Value == 100);
        Assert.True(Http2FrameParser.TryReadSetting(settingsFrame, sf.Entries, 1, out var se1) && se1.Id == 4 && se1.Value == 65535);
        Assert.False(Http2FrameParser.TryReadSetting(settingsFrame, sf.Entries, 2, out _));

        byte[] ppFrame = Http2FrameParser.BuildFrame(Http2FrameType.PushPromise, Http2FrameFlags.EndHeaders, 1, [0, 0, 0, 2, 0x82]);
        var c6 = ppFrame.AsCursor();
        Assert.True(Http2FrameParser.ParseFrame(ref c6).TryGetValue(out Parsed<Http2Frame> p6) && p6.Value.TryGetValue(out Http2PushPromiseFrame ppf) && ppf.PromisedStreamId == 2);

        byte[] pingFrame = Http2FrameParser.BuildFrame(Http2FrameType.Ping, Http2FrameFlags.Ack, 0, [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]);
        var c7 = pingFrame.AsCursor();
        Assert.True(Http2FrameParser.ParseFrame(ref c7).TryGetValue(out Parsed<Http2Frame> p7) && p7.Value.TryGetValue(out Http2PingFrame pingf) && pingf.OpaqueData == 0x0102030405060708UL);

        byte[] goAwayFrame = Http2FrameParser.BuildFrame(Http2FrameType.GoAway, Http2FrameFlags.None, 0, [0, 0, 0, 11, 0, 0, 0, 0, (byte)'b', (byte)'y', (byte)'e']);
        var c8 = goAwayFrame.AsCursor();
        Assert.True(Http2FrameParser.ParseFrame(ref c8).TryGetValue(out Parsed<Http2Frame> p8) && p8.Value.TryGetValue(out Http2GoAwayFrame gaf) && gaf.LastStreamId == 11 && goAwayFrame.AsSpan()[gaf.DebugData].SequenceEqual("bye"u8));

        byte[] wuFrame = Http2FrameParser.BuildFrame(Http2FrameType.WindowUpdate, Http2FrameFlags.None, 0, [0, 0, 0x10, 0x00]);
        var c9 = wuFrame.AsCursor();
        Assert.True(Http2FrameParser.ParseFrame(ref c9).TryGetValue(out Parsed<Http2Frame> p9) && p9.Value.TryGetValue(out Http2WindowUpdateFrame wuf) && wuf.WindowSizeIncrement == 4096);

        byte[] contFrame = Http2FrameParser.BuildFrame(Http2FrameType.Continuation, Http2FrameFlags.EndHeaders, 7, [0x82]);
        var c10 = contFrame.AsCursor();
        Assert.True(Http2FrameParser.ParseFrame(ref c10).TryGetValue(out Parsed<Http2Frame> p10) && p10.Value.TryGetValue(out Http2ContinuationFrame contf) && contf.StreamId == 7);

        byte[] unkFrame = Http2FrameParser.BuildFrame((Http2FrameType)0x7F, Http2FrameFlags.None, 5, [1, 2]);
        var c11 = unkFrame.AsCursor();
        Assert.True(Http2FrameParser.ParseFrame(ref c11).TryGetValue(out Parsed<Http2Frame> p11) && p11.Value.TryGetValue(out Http2UnknownFrame uf) && uf.RawType == 0x7F && uf.Payload.Length == 2);

        // RFC 9113 protocol error guards
        byte[][] invalidFrames =
        [
            Http2FrameParser.BuildFrame(Http2FrameType.Data, Http2FrameFlags.None, 0, [1]),                  // DATA stream 0
            Http2FrameParser.BuildFrame(Http2FrameType.Data, Http2FrameFlags.Padded, 1, [5, 1, 2]),          // pad >= len
            Http2FrameParser.BuildFrame(Http2FrameType.Settings, Http2FrameFlags.None, 1, []),               // SETTINGS stream != 0
            Http2FrameParser.BuildFrame(Http2FrameType.Settings, Http2FrameFlags.Ack, 0, [0, 0, 0, 0, 0, 0]),// SETTINGS ACK non-empty
            Http2FrameParser.BuildFrame(Http2FrameType.Settings, Http2FrameFlags.None, 0, [1, 2, 3]),        // SETTINGS len % 6 != 0
            Http2FrameParser.BuildFrame(Http2FrameType.Ping, Http2FrameFlags.None, 0, [1, 2, 3]),            // PING len != 8
            Http2FrameParser.BuildFrame(Http2FrameType.WindowUpdate, Http2FrameFlags.None, 0, [0, 0, 0, 0]), // WU increment == 0
            Http2FrameParser.BuildFrame(Http2FrameType.Priority, Http2FrameFlags.None, 1, [1, 2]),           // PRIORITY len != 5
            Http2FrameParser.BuildFrame(Http2FrameType.RstStream, Http2FrameFlags.None, 0, [0, 0, 0, 1]),    // RST stream 0
            Http2FrameParser.BuildFrame(Http2FrameType.GoAway, Http2FrameFlags.None, 0, [1, 2, 3]),          // GOAWAY len < 8
        ];
        foreach (byte[] bad in invalidFrames)
        {
            var cb = bad.AsCursor();
            Assert.True(Http2FrameParser.ParseFrame(ref cb).IsFailure && cb.Position == 0);
        }

        // maxFrameSize guard
        var cMax = settingsFrame.AsCursor();
        Assert.True(Http2FrameParser.ParseFrame(ref cMax, maxFrameSize: 4).IsFailure);

        // Streaming byte-by-byte
        var h2Dec = new StreamingDecoder<Http2Frame>(static (ref ByteCursor x) => Http2FrameParser.ParseFrame(ref x));
        for (int i = 0; i < pingFrame.Length; i++)
        {
            h2Dec.Append(pingFrame.AsSpan(i, 1));
            var st = h2Dec.TryDecodeNext();
            if (i < pingFrame.Length - 1) Assert.True(st.IsIncomplete);
            else Assert.True(st.IsParsed);
        }

        // Strict 0 bytes allocation check
        _ = RunH2Loop(dataFrame, 100);
        long before = GC.GetAllocatedBytesForCurrentThread();
        ulong h2Sum = RunH2Loop(dataFrame, 5_000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(h2Sum > 0);
        Assert.Equal(0L, allocated);

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        static ulong RunH2Loop(byte[] buf, int iterations)
        {
            ulong acc = 0;
            ReadOnlySpan<byte> span = buf;
            for (int i = 0; i < iterations; i++)
            {
                var cur = span.AsCursor();
                if (Http2FrameParser.ParseFrame(ref cur).TryGetValue(out Parsed<Http2Frame> ok))
                {
                    acc += ok.Value switch
                    {
                        Http2DataFrame(var sid, _, var d, _) => sid + (ulong)d.Length,
                        Http2HeadersFrame(var sid, _, _, var h, _) => sid + (ulong)h.Length,
                        Http2PriorityFrame(var sid, _) => sid,
                        Http2RstStreamFrame(var sid, var err) => sid + err,
                        Http2SettingsFrame(_, var e) => (ulong)e.Length,
                        Http2PushPromiseFrame(var sid, _, var pid, _, _) => sid + pid,
                        Http2PingFrame(_, var op) => op,
                        Http2GoAwayFrame(var lsid, var err, _) => lsid + err,
                        Http2WindowUpdateFrame(var sid, var inc) => sid + inc,
                        Http2ContinuationFrame(var sid, _, var h) => sid + (ulong)h.Length,
                        Http2UnknownFrame(_, _, var sid, var p) => sid + (ulong)p.Length,
                    };
                }
            }
            return acc;
        }
    }

    // =========================================================================
    // 3. MQTT v5.0 Control Packets
    // =========================================================================

    [Fact]
    public void MqttV5_AllPacketTypes_VariableByteInt_Validation_Streaming_And_ZeroAlloc()
    {
        // Variable Byte Int boundaries (1, 2, 3, 4 bytes)
        int[] vbiValues = [0, 127, 128, 16_383, 16_384, 2_097_151, 2_097_152, MqttV5Parser.MaxVariableByteInt];
        foreach (int expected in vbiValues)
        {
            List<byte> enc = [];
            MqttV5Parser.WriteVariableByteInt(enc, expected);
            var cv = enc.ToArray().AsCursor();
            Assert.True(MqttV5Parser.ReadVariableByteInt(ref cv).TryGetValue(out Parsed<int> ok) && ok.Value == expected && cv.IsAtEnd);
        }

        // >4-byte VBI -> Failure
        byte[] badVbi = [0x80, 0x80, 0x80, 0x80, 0x01];
        var cbv = badVbi.AsCursor();
        Assert.True(MqttV5Parser.ReadVariableByteInt(ref cbv).IsFailure && cbv.Position == 0);

        // 1. CONNECT v5
        byte[] connectBody =
        [
            0x00, 0x04, (byte)'M', (byte)'Q', (byte)'T', (byte)'T',
            5,          // protocol level 5
            0x02,       // clean start
            0x00, 60,   // keep alive 60s
            0x00,       // properties len 0
            0x00, 0x03, (byte)'c', (byte)'l', (byte)'1', // clientId
        ];
        byte[] connectPkt = MqttV5Parser.BuildPacket(MqttPacketType.Connect, 0, connectBody);
        var c1 = connectPkt.AsCursor();
        Assert.True(MqttV5Parser.ParsePacket(ref c1).TryGetValue(out Parsed<MqttPacket> p1));
        Assert.True(p1.Value.TryGetValue(out MqttConnectPacket conn) && conn.ProtocolLevel == 5 && conn.KeepAlive == 60 && conn.Payload.Length == 5);

        // 2. CONNACK
        byte[] connAckPkt = MqttV5Parser.BuildPacket(MqttPacketType.ConnAck, 0, [0x01, 0x00, 0x00]);
        var c2 = connAckPkt.AsCursor();
        Assert.True(MqttV5Parser.ParsePacket(ref c2).TryGetValue(out Parsed<MqttPacket> p2) && p2.Value.TryGetValue(out MqttConnAckPacket ca) && ca.SessionPresent && ca.ReasonCode == 0);

        // 3. PUBLISH (QoS 0 + QoS 1 with properties & packetId)
        byte[] pub0 = MqttV5Parser.BuildPublish("sensors/temp"u8, "22.5C"u8, qos: 0, retain: true);
        var c3 = pub0.AsCursor();
        Assert.True(MqttV5Parser.ParsePacket(ref c3).TryGetValue(out Parsed<MqttPacket> p3));
        Assert.True(p3.Value.TryGetValue(out MqttPublishPacket pubP0) && pubP0.Qos == 0 && pubP0.Retain && pubP0.PacketId.IsNone);
        Assert.SequenceEqual<byte>("sensors/temp"u8, pub0.AsSpan()[pubP0.TopicName]);
        Assert.SequenceEqual<byte>("22.5C"u8, pub0.AsSpan()[pubP0.Payload]);

        byte[] pub1 = MqttV5Parser.BuildPublish("alerts/1"u8, "OK"u8, qos: 1, packetId: 42, dup: true, properties: [0x01, 0x01]);
        var c4 = pub1.AsCursor();
        Assert.True(MqttV5Parser.ParsePacket(ref c4).TryGetValue(out Parsed<MqttPacket> p4));
        Assert.True(p4.Value.TryGetValue(out MqttPublishPacket pubP1) && pubP1.Dup && pubP1.Qos == 1 && pubP1.PacketId.TryGetValue(out Some<ushort> pid1) && pid1.Value == 42 && pubP1.Properties.Length == 2);

        // 4. PUBACK (compact 2-byte + full with reason & props)
        byte[] pubAckCompact = MqttV5Parser.BuildPacket(MqttPacketType.PubAck, 0, [0x00, 42]);
        var c5 = pubAckCompact.AsCursor();
        Assert.True(MqttV5Parser.ParsePacket(ref c5).TryGetValue(out Parsed<MqttPacket> p5) && p5.Value.TryGetValue(out MqttPubAckPacket pa) && pa.PacketId == 42 && pa.ReasonCode == 0);

        // 5. SUBSCRIBE & SUBACK
        byte[] subPkt = MqttV5Parser.BuildPacket(MqttPacketType.Subscribe, 0x02, [0x00, 0x07, 0x00, 0x00, 0x01, (byte)'#', 0x01]);
        var c6 = subPkt.AsCursor();
        Assert.True(MqttV5Parser.ParsePacket(ref c6).TryGetValue(out Parsed<MqttPacket> p6) && p6.Value.TryGetValue(out MqttSubscribePacket sp) && sp.PacketId == 7 && sp.TopicFilters.Length == 4);

        byte[] subAckPkt = MqttV5Parser.BuildPacket(MqttPacketType.SubAck, 0, [0x00, 0x07, 0x00, 0x01]);
        var c7 = subAckPkt.AsCursor();
        Assert.True(MqttV5Parser.ParsePacket(ref c7).TryGetValue(out Parsed<MqttPacket> p7) && p7.Value.TryGetValue(out MqttSubAckPacket sap) && sap.PacketId == 7 && sap.ReasonCodes.Length == 1);

        // 6. PINGREQ, PINGRESP, DISCONNECT
        byte[] pingReq = [0xC0, 0x00];
        var c8 = pingReq.AsCursor();
        Assert.True(MqttV5Parser.ParsePacket(ref c8).TryGetValue(out Parsed<MqttPacket> p8) && p8.Value.TryGetValue(out MqttPingReqPacket _));

        byte[] pingResp = [0xD0, 0x00];
        var c9 = pingResp.AsCursor();
        Assert.True(MqttV5Parser.ParsePacket(ref c9).TryGetValue(out Parsed<MqttPacket> p9) && p9.Value.TryGetValue(out MqttPingRespPacket _));

        byte[] discCompact = [0xE0, 0x00];
        var c10 = discCompact.AsCursor();
        Assert.True(MqttV5Parser.ParsePacket(ref c10).TryGetValue(out Parsed<MqttPacket> p10) && p10.Value.TryGetValue(out MqttDisconnectPacket dp) && dp.ReasonCode == 0);

        byte[] discFull = MqttV5Parser.BuildPacket(MqttPacketType.Disconnect, 0, [0x04, 0x00]);
        var c11 = discFull.AsCursor();
        Assert.True(MqttV5Parser.ParsePacket(ref c11).TryGetValue(out Parsed<MqttPacket> p11) && p11.Value.TryGetValue(out MqttDisconnectPacket dp2) && dp2.ReasonCode == 0x04);

        // Validation guards -> Failure with cursor rollback
        byte[][] badPackets =
        [
            MqttV5Parser.BuildPacket(MqttPacketType.Connect, 0x01, connectBody),                 // bad CONNECT flags
            MqttV5Parser.BuildPacket(MqttPacketType.Publish, 0x06, [0, 1, (byte)'a', 0]),        // QoS 3 forbidden
            MqttV5Parser.BuildPacket(MqttPacketType.Publish, 0x08, [0, 1, (byte)'a', 0]),        // QoS 0 + DUP forbidden
            MqttV5Parser.BuildPacket(MqttPacketType.Subscribe, 0x00, [0, 1, 0, 0, 1, (byte)'a', 0]), // bad SUBSCRIBE flags != 2
            [0xC0, 0x01, 0x00],                                                                  // PINGREQ non-zero len
            [0x00, 0x00],                                                                        // reserved packet type 0
        ];
        foreach (byte[] bad in badPackets)
        {
            var cb = bad.AsCursor();
            Assert.True(MqttV5Parser.ParsePacket(ref cb).IsFailure && cb.Position == 0);
        }

        // Byte-by-byte streaming
        var mDec = new StreamingDecoder<MqttPacket>(MqttV5Parser.ParsePacket);
        for (int i = 0; i < pub1.Length; i++)
        {
            mDec.Append(pub1.AsSpan(i, 1));
            var st = mDec.TryDecodeNext();
            if (i < pub1.Length - 1) Assert.True(st.IsIncomplete);
            else Assert.True(st.IsParsed);
        }

        // Strict 0 bytes allocation check
        _ = RunMqttLoop(pub1, 100);
        long before = GC.GetAllocatedBytesForCurrentThread();
        ulong mSum = RunMqttLoop(pub1, 5_000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(mSum > 0);
        Assert.Equal(0L, allocated);

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        static ulong RunMqttLoop(byte[] buf, int iterations)
        {
            ulong acc = 0;
            ReadOnlySpan<byte> span = buf;
            for (int i = 0; i < iterations; i++)
            {
                var cur = span.AsCursor();
                if (MqttV5Parser.ParsePacket(ref cur).TryGetValue(out Parsed<MqttPacket> ok))
                {
                    acc += ok.Value switch
                    {
                        MqttConnectPacket(_, _, var ka, _, var pl) => ka + (ulong)pl.Length,
                        MqttConnAckPacket(_, var rc, _) => rc,
                        MqttPublishPacket(_, var q, _, var t, _, _, var pl) => q + (ulong)(t.Length + pl.Length),
                        MqttPubAckPacket(var id, _, _) => id,
                        MqttSubscribePacket(var id, _, var f) => id + (ulong)f.Length,
                        MqttSubAckPacket(var id, _, var rc) => id + (ulong)rc.Length,
                        MqttPingReqPacket => 1UL,
                        MqttPingRespPacket => 2UL,
                        MqttDisconnectPacket(var rc, _) => rc,
                    };
                }
            }
            return acc;
        }
    }

    // =========================================================================
    // 4. CsCheck Property & Fuzz No-Throw Safety for All 3 New Protocols
    // =========================================================================

    [Fact]
    public void CsCheck_NewProtocols_ArbitraryBytes_NeverThrow_And_Roundtrip()
    {
        // Fuzz no-throw safety on completely arbitrary garbage bytes
        Gen.Byte.Array[0, 128].Sample(bytes =>
        {
            var c1 = bytes.AsCursor();
            _ = ProtobufParser.ParseField(ref c1);
            var c1t = bytes.AsCursor();
            _ = ProtobufParser.TryParseField(ref c1t, out _);

            var c2 = bytes.AsCursor();
            _ = Http2FrameParser.ParseFrame(ref c2);

            var c3 = bytes.AsCursor();
            _ = MqttV5Parser.ParsePacket(ref c3);
        }, iter: 1_000);

        // Protobuf field roundtrip
        Gen.Select(Gen.UInt[1, ProtobufParser.MaxFieldNumber], Gen.ULong, Gen.Byte.Array[0, 32])
            .Sample((fieldNum, val, payload) =>
            {
                byte[] vWire = ProtobufParser.EncodeVarIntField(fieldNum, val);
                var c1 = vWire.AsCursor();
                Assert.True(ProtobufParser.ParseField(ref c1).TryGetValue(out Parsed<ProtobufField> p1));
                Assert.True(p1.Value.TryGetValue(out ProtobufVarIntField vf) && vf.FieldNumber == fieldNum && vf.Value == val);

                byte[] lWire = ProtobufParser.EncodeLengthDelimitedField(fieldNum, payload);
                var c2 = lWire.AsCursor();
                Assert.True(ProtobufParser.ParseField(ref c2).TryGetValue(out Parsed<ProtobufField> p2));
                Assert.True(p2.Value.TryGetValue(out ProtobufLengthDelimitedField ldf) && ldf.FieldNumber == fieldNum && lWire.AsSpan()[ldf.Payload].SequenceEqual(payload));
            }, iter: 500);
    }

    [Fact]
    public void NewProtocols_MutationKiller_AllEdgeCases_ValueProperties_And_ErrorContexts()
    {
        // --- 1. Protobuf edges ---
        ProtobufField pbDefault = default;
        Assert.False(pbDefault.HasValue);
        Assert.True(pbDefault.Value is null);
        Assert.False(pbDefault.TryGetValue(out ProtobufVarIntField _));
        Assert.False(pbDefault.TryGetValue(out ProtobufFixed64Field _));
        Assert.False(pbDefault.TryGetValue(out ProtobufLengthDelimitedField _));
        Assert.False(pbDefault.TryGetValue(out ProtobufFixed32Field _));

        // MaxFieldNumber is valid; MaxFieldNumber + 1 fails
        byte[] maxFieldWire = ProtobufParser.EncodeVarIntField(ProtobufParser.MaxFieldNumber, 9UL);
        var cmf = maxFieldWire.AsCursor();
        Assert.True(ProtobufParser.ParseField(ref cmf).TryGetValue(out Parsed<ProtobufField> pmf) && pmf.Value.FieldNumber == ProtobufParser.MaxFieldNumber);
        var cmft = maxFieldWire.AsCursor();
        Assert.True(ProtobufParser.TryParseField(ref cmft, out ProtobufField tmf).IsParsed && tmf.FieldNumber == ProtobufParser.MaxFieldNumber);

        List<byte> overFieldBuf = [];
        ProtobufParser.WriteVarInt(overFieldBuf, ((ulong)ProtobufParser.MaxFieldNumber + 1UL) << 3);
        overFieldBuf.Add(0x01);
        byte[] overField = overFieldBuf.ToArray();
        var cof = overField.AsCursor();
        Assert.True(ProtobufParser.ParseField(ref cof).TryGetValue(out Failure fof) && fof.Error.Context is { Length: > 0 } && cof.Position == 0);
        Assert.True(ProtobufParser.TryParseField(ref cof, out _).IsFailure && cof.Position == 0);

        // LengthDelimited > int.MaxValue -> Failure with context; truncated -> Incomplete(len - rem) in Streaming, Backtrack in Complete
        List<byte> hugeLenBuf = [];
        ProtobufParser.WriteVarInt(hugeLenBuf, (1UL << 3) | 2UL);
        ProtobufParser.WriteVarInt(hugeLenBuf, (ulong)int.MaxValue + 1UL);
        var chl = hugeLenBuf.ToArray().AsCursor();
        Assert.True(ProtobufParser.ParseField(ref chl).TryGetValue(out Failure fhl) && fhl.Error.Context is { Length: > 0 } && chl.Position == 0);
        Assert.True(ProtobufParser.TryParseField(ref chl, out _).IsFailure && chl.Position == 0);

        byte[] truncLd = [0x0A, 0x05, 0x41, 0x42]; // field 1, len 5, only 2 bytes
        var ctlS = truncLd.AsStreamingCursor();
        var stLdInc = ProtobufParser.TryParseField(ref ctlS, out _);
        Assert.True(stLdInc.IsIncomplete && stLdInc.UnpackNeeded().Equals(Needed.Bytes(3)) && ctlS.Position == 0);
        var ctlC = truncLd.AsCursor();
        Assert.True(ProtobufParser.TryParseField(ref ctlC, out _).IsBacktrack && ctlC.Position == 0);
        Assert.True(ProtobufParser.ParseField(ref ctlC).IsBacktrack && ctlC.Position == 0);

        // Truncated Fixed64, Fixed32, VarInt -> rollback to start on ParseField & TryParseField
        byte[] truncF64 = [0x09, 1, 2, 3];
        var cf64S = truncF64.AsStreamingCursor();
        var stF64 = ProtobufParser.TryParseField(ref cf64S, out _);
        Assert.True(stF64.IsIncomplete && stF64.UnpackNeeded().Equals(Needed.Bytes(5)) && cf64S.Position == 0);
        var cf64C = truncF64.AsCursor();
        Assert.True(ProtobufParser.ParseField(ref cf64C).IsBacktrack && cf64C.Position == 0);
        Assert.True(ProtobufParser.TryParseField(ref cf64C, out _).IsBacktrack && cf64C.Position == 0);

        byte[] truncF32 = [0x0D, 1, 2];
        var cf32S = truncF32.AsStreamingCursor();
        var stF32 = ProtobufParser.TryParseField(ref cf32S, out _);
        Assert.True(stF32.IsIncomplete && stF32.UnpackNeeded().Equals(Needed.Bytes(2)) && cf32S.Position == 0);
        var cf32C = truncF32.AsCursor();
        Assert.True(ProtobufParser.ParseField(ref cf32C).IsBacktrack && cf32C.Position == 0);
        Assert.True(ProtobufParser.TryParseField(ref cf32C, out _).IsBacktrack && cf32C.Position == 0);

        byte[] truncVar = [0x08, 0x80];
        var cvC = truncVar.AsCursor();
        Assert.True(ProtobufParser.ParseField(ref cvC).IsBacktrack && cvC.Position == 0);
        Assert.True(ProtobufParser.TryParseField(ref cvC, out _).IsBacktrack && cvC.Position == 0);

        // ParsePackedVarInts on truncated varint + destination too small error context
        Span<ulong> packDst = stackalloc ulong[1];
        Assert.True(ProtobufParser.ParsePackedVarInts([0x80], packDst).IsBacktrack);
        Assert.True(ProtobufParser.ParsePackedVarInts([0x01, 0x02], packDst).TryGetValue(out Failure fPack) && fPack.Error.Context is { Length: > 0 });

        // --- 2. HTTP/2 edges ---
        Http2Frame h2Def = default;
        Assert.False(h2Def.HasValue);
        Assert.True(h2Def.Value is null);
        Assert.False(h2Def.TryGetValue(out Http2DataFrame _));
        Assert.False(h2Def.TryGetValue(out Http2HeadersFrame _));
        Assert.False(h2Def.TryGetValue(out Http2PriorityFrame _));
        Assert.False(h2Def.TryGetValue(out Http2RstStreamFrame _));
        Assert.False(h2Def.TryGetValue(out Http2SettingsFrame _));
        Assert.False(h2Def.TryGetValue(out Http2PushPromiseFrame _));
        Assert.False(h2Def.TryGetValue(out Http2PingFrame _));
        Assert.False(h2Def.TryGetValue(out Http2GoAwayFrame _));
        Assert.False(h2Def.TryGetValue(out Http2WindowUpdateFrame _));
        Assert.False(h2Def.TryGetValue(out Http2ContinuationFrame _));
        Assert.False(h2Def.TryGetValue(out Http2UnknownFrame _));

        // Exercise .Value and .HasValue across all 11 Http2Frame variants
        Http2Frame[] allH2 =
        [
            new Http2DataFrame(1, Http2FrameFlags.None, ByteSlice.Empty, 0),
            new Http2HeadersFrame(1, Http2FrameFlags.None, Option<Http2PrioritySpec>.Nothing, ByteSlice.Empty, 0),
            new Http2HeadersFrame(1, Http2FrameFlags.Priority, Option<Http2PrioritySpec>.Of(new Http2PrioritySpec(false, 2, 10)), ByteSlice.Empty, 0),
            new Http2PriorityFrame(1, new Http2PrioritySpec(true, 2, 10)),
            new Http2RstStreamFrame(1, 0),
            new Http2SettingsFrame(Http2FrameFlags.None, ByteSlice.Empty),
            new Http2PushPromiseFrame(1, Http2FrameFlags.None, 2, ByteSlice.Empty, 0),
            new Http2PingFrame(Http2FrameFlags.None, 123UL),
            new Http2GoAwayFrame(1, 0, ByteSlice.Empty),
            new Http2WindowUpdateFrame(1, 10),
            new Http2ContinuationFrame(1, Http2FrameFlags.None, ByteSlice.Empty),
            new Http2UnknownFrame(0x99, Http2FrameFlags.None, 1, ByteSlice.Empty),
        ];
        foreach (var fr in allH2)
        {
            Assert.True(fr.HasValue && fr.Value is not null);
        }

        // Large 24-bit payload (>= 65536) and exact maxFrameSize boundary
        byte[] bigPayload = new byte[65_600];
        byte[] bigData = Http2FrameParser.BuildFrame(Http2FrameType.Data, Http2FrameFlags.None, 1, bigPayload);
        var cBig = bigData.AsCursor();
        Assert.True(Http2FrameParser.ParseFrame(ref cBig, maxFrameSize: 65_600).TryGetValue(out Parsed<Http2Frame> pBig) && pBig.Value.TryGetValue(out Http2DataFrame bdf) && bdf.Data.Length == 65_600);
        var cBigOver = bigData.AsCursor();
        Assert.True(Http2FrameParser.ParseFrame(ref cBigOver, maxFrameSize: 65_599).TryGetValue(out Failure fBigOver) && fBigOver.Error.Context is { Length: > 0 });

        // Non-zero start offset: truncated header & truncated payload (Complete Backtrack offset + Streaming exact Needed)
        byte[] prefixedH2 = [0xAA, 0xBB, .. Http2FrameParser.BuildFrame(Http2FrameType.Data, Http2FrameFlags.None, 1, [1, 2, 3, 4])];
        var h2ShortHdrC = prefixedH2.AsSpan(0, 2 + 4).AsCursor(); h2ShortHdrC.Position = 2;
        Assert.True(Http2FrameParser.ParseFrame(ref h2ShortHdrC).TryGetValue(out Backtrack h2BtH) && h2BtH.Error.Offset == 2 && h2BtH.Error.Context is { Length: > 0 });
        var h2ShortHdrS = prefixedH2.AsSpan(0, 2 + 4).AsStreamingCursor(); h2ShortHdrS.Position = 2;
        Assert.True(Http2FrameParser.ParseFrame(ref h2ShortHdrS).TryGetValue(out Incomplete h2IncH) && h2IncH.Needed.Equals(Needed.Bytes(5)));

        var h2ShortPayC = prefixedH2.AsSpan(0, 2 + 9 + 1).AsCursor(); h2ShortPayC.Position = 2;
        Assert.True(Http2FrameParser.ParseFrame(ref h2ShortPayC).TryGetValue(out Backtrack h2BtP) && h2BtP.Error.Offset == 11 && h2BtP.Error.Context is { Length: > 0 });
        var h2ShortPayS = prefixedH2.AsSpan(0, 2 + 9 + 1).AsStreamingCursor(); h2ShortPayS.Position = 2;
        Assert.True(Http2FrameParser.ParseFrame(ref h2ShortPayS).TryGetValue(out Incomplete h2IncP) && h2IncP.Needed.Equals(Needed.Bytes(3)));

        // Headers unpadded, PushPromise padded, GoAway exact 8 bytes, plus all error branches with non-empty Context
        byte[] plainHdr = Http2FrameParser.BuildFrame(Http2FrameType.Headers, Http2FrameFlags.EndHeaders, 1, [0x82]);
        var cph = plainHdr.AsCursor();
        Assert.True(Http2FrameParser.ParseFrame(ref cph).TryGetValue(out Parsed<Http2Frame> pph) && pph.Value.TryGetValue(out Http2HeadersFrame phf) && phf.Priority.IsNone && phf.HeaderBlockFragment.Length == 1);

        byte[] paddedPp = Http2FrameParser.BuildFrame(Http2FrameType.PushPromise, Http2FrameFlags.Padded | Http2FrameFlags.EndHeaders, 1, [1, 0, 0, 0, 4, 0x82, 0]);
        var cpp = paddedPp.AsCursor();
        Assert.True(Http2FrameParser.ParseFrame(ref cpp).TryGetValue(out Parsed<Http2Frame> ppp) && ppp.Value.TryGetValue(out Http2PushPromiseFrame ppf) && ppf.PromisedStreamId == 4 && ppf.PadLength == 1 && paddedPp.AsSpan()[ppf.HeaderBlockFragment].SequenceEqual<byte>([0x82]));

        byte[] exactGoAway = Http2FrameParser.BuildFrame(Http2FrameType.GoAway, Http2FrameFlags.None, 0, [0, 0, 0, 3, 0, 0, 0, 1]);
        var cga = exactGoAway.AsCursor();
        Assert.True(Http2FrameParser.ParseFrame(ref cga).TryGetValue(out Parsed<Http2Frame> pga) && pga.Value.TryGetValue(out Http2GoAwayFrame gaf) && gaf.DebugData.Length == 0);

        byte[][] moreInvalidH2 =
        [
            Http2FrameParser.BuildFrame(Http2FrameType.Data, Http2FrameFlags.Padded, 1, []),                          // padded data empty
            Http2FrameParser.BuildFrame(Http2FrameType.Headers, Http2FrameFlags.None, 0, [0x82]),                     // headers stream 0
            Http2FrameParser.BuildFrame(Http2FrameType.Headers, Http2FrameFlags.Padded, 1, []),                       // padded headers empty
            Http2FrameParser.BuildFrame(Http2FrameType.Headers, Http2FrameFlags.Priority, 1, [1, 2, 3]),              // headers priority < 5
            Http2FrameParser.BuildFrame(Http2FrameType.Headers, Http2FrameFlags.Padded, 1, [3, 0x82]),                // headers pad > body
            Http2FrameParser.BuildFrame(Http2FrameType.PushPromise, Http2FrameFlags.None, 0, [0, 0, 0, 2]),           // pp stream 0
            Http2FrameParser.BuildFrame(Http2FrameType.PushPromise, Http2FrameFlags.Padded, 1, []),                   // padded pp empty
            Http2FrameParser.BuildFrame(Http2FrameType.PushPromise, Http2FrameFlags.None, 1, [1, 2]),                 // pp < 4
            Http2FrameParser.BuildFrame(Http2FrameType.Continuation, Http2FrameFlags.EndHeaders, 0, [0x82]),          // continuation stream 0
            Http2FrameParser.BuildFrame(Http2FrameType.WindowUpdate, Http2FrameFlags.None, 0, [1, 2]),                // wu len != 4
            Http2FrameParser.BuildFrame(Http2FrameType.Ping, Http2FrameFlags.None, 1, [1, 2, 3, 4, 5, 6, 7, 8]),      // ping stream != 0
            Http2FrameParser.BuildFrame(Http2FrameType.GoAway, Http2FrameFlags.None, 1, [0, 0, 0, 1, 0, 0, 0, 0]),    // goaway stream != 0
            Http2FrameParser.BuildFrame(Http2FrameType.Priority, Http2FrameFlags.None, 0, [0, 0, 0, 1, 1]),           // priority stream 0
            Http2FrameParser.BuildFrame(Http2FrameType.RstStream, Http2FrameFlags.None, 1, [1, 2]),                   // rst len != 4
        ];
        foreach (byte[] bad in moreInvalidH2)
        {
            var cb = bad.AsCursor();
            Assert.True(Http2FrameParser.ParseFrame(ref cb).TryGetValue(out Failure f) && f.Error.Context is { Length: > 0 } && cb.Position == 0);
        }

        // --- 3. MQTT v5 edges ---
        MqttPacket mqttDef = default;
        Assert.False(mqttDef.HasValue);
        Assert.True(mqttDef.Value is null);
        Assert.False(mqttDef.TryGetValue(out MqttConnectPacket _));
        Assert.False(mqttDef.TryGetValue(out MqttConnAckPacket _));
        Assert.False(mqttDef.TryGetValue(out MqttPublishPacket _));
        Assert.False(mqttDef.TryGetValue(out MqttPubAckPacket _));
        Assert.False(mqttDef.TryGetValue(out MqttSubscribePacket _));
        Assert.False(mqttDef.TryGetValue(out MqttSubAckPacket _));
        Assert.False(mqttDef.TryGetValue(out MqttPingReqPacket _));
        Assert.False(mqttDef.TryGetValue(out MqttPingRespPacket _));
        Assert.False(mqttDef.TryGetValue(out MqttDisconnectPacket _));

        MqttPacket[] allMqtt =
        [
            new MqttConnectPacket(5, 2, 30, ByteSlice.Empty, ByteSlice.Empty),
            new MqttConnAckPacket(false, 0, ByteSlice.Empty),
            new MqttConnAckPacket(true, 1, ByteSlice.Empty),
            new MqttPublishPacket(false, 0, false, ByteSlice.Empty, Option<ushort>.Nothing, ByteSlice.Empty, ByteSlice.Empty),
            new MqttPublishPacket(true, 2, true, ByteSlice.Empty, Option<ushort>.Of(9), ByteSlice.Empty, ByteSlice.Empty),
            new MqttPubAckPacket(1, 0, ByteSlice.Empty),
            new MqttSubscribePacket(1, ByteSlice.Empty, ByteSlice.Empty),
            new MqttSubAckPacket(1, ByteSlice.Empty, ByteSlice.Empty),
            new MqttPingReqPacket(),
            new MqttPingRespPacket(),
            new MqttDisconnectPacket(0, ByteSlice.Empty),
        ];
        foreach (var pkt in allMqtt)
        {
            Assert.True(pkt.HasValue && pkt.Value is not null);
        }

        // Full PubAck with reason code + properties; ConnAck sessionPresent=false; Publish dup=false, retain=false, QoS 0
        byte[] pubAckFull = MqttV5Parser.BuildPacket(MqttPacketType.PubAck, 0, [0x00, 0x05, 0x10, 0x02, 0x1F, 0x00]);
        var cpa = pubAckFull.AsCursor();
        Assert.True(MqttV5Parser.ParsePacket(ref cpa).TryGetValue(out Parsed<MqttPacket> ppa) && ppa.Value.TryGetValue(out MqttPubAckPacket paf) && paf.PacketId == 5 && paf.ReasonCode == 0x10 && paf.Properties.Length == 2);

        byte[] connAckFalse = MqttV5Parser.BuildPacket(MqttPacketType.ConnAck, 0, [0x00, 0x00, 0x00]);
        var cca = connAckFalse.AsCursor();
        Assert.True(MqttV5Parser.ParsePacket(ref cca).TryGetValue(out Parsed<MqttPacket> pca) && pca.Value.TryGetValue(out MqttConnAckPacket caf) && !caf.SessionPresent);

        byte[] pubPlain = MqttV5Parser.BuildPublish("a"u8, "b"u8, qos: 0, dup: false, retain: false);
        var cpp0 = pubPlain.AsCursor();
        Assert.True(MqttV5Parser.ParsePacket(ref cpp0).TryGetValue(out Parsed<MqttPacket> ppp0) && ppp0.Value.TryGetValue(out MqttPublishPacket pub0) && !pub0.Dup && !pub0.Retain && pub0.Qos == 0);

        // Non-zero start offset rollback + slice offset verification
        byte[] prefixedPub = [0x99, 0x99, .. pubPlain];
        var cPre = prefixedPub.AsCursor(); cPre.Position = 2;
        Assert.True(MqttV5Parser.ParsePacket(ref cPre).TryGetValue(out Parsed<MqttPacket> pPre));
        Assert.True(pPre.Value.TryGetValue(out MqttPublishPacket pubPre));
        Assert.SequenceEqual<byte>("a"u8, prefixedPub.AsSpan()[pubPre.TopicName]);
        Assert.SequenceEqual<byte>("b"u8, prefixedPub.AsSpan()[pubPre.Payload]);

        // All MQTT v5 validation failures with non-empty Context and rollback
        byte[][] moreBadMqtt =
        [
            MqttV5Parser.BuildPacket(MqttPacketType.Connect, 0, [0, 4, (byte)'B', (byte)'A', (byte)'D', (byte)'!', 5, 0, 0, 10, 0]),  // bad proto name
            MqttV5Parser.BuildPacket(MqttPacketType.Connect, 0, [0, 4, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 4, 0, 0, 10, 0]),  // bad proto level
            MqttV5Parser.BuildPacket(MqttPacketType.Connect, 0, [0, 4, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 5, 1, 0, 10, 0]),  // reserved bit 0
            MqttV5Parser.BuildPacket(MqttPacketType.Connect, 0, [0, 4, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 5]),              // truncated header
            MqttV5Parser.BuildPacket(MqttPacketType.Connect, 0, [0, 4, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 5, 0, 0, 10, 5]),  // bad properties len
            MqttV5Parser.BuildPacket(MqttPacketType.ConnAck, 1, [0, 0, 0]),                                                           // bad flags
            MqttV5Parser.BuildPacket(MqttPacketType.ConnAck, 0, [0]),                                                                 // truncated
            MqttV5Parser.BuildPacket(MqttPacketType.ConnAck, 0, [2, 0, 0]),                                                           // reserved ack bits
            MqttV5Parser.BuildPacket(MqttPacketType.ConnAck, 0, [0, 0, 0, 99]),                                                       // trailing byte
            MqttV5Parser.BuildPacket(MqttPacketType.ConnAck, 0, [0, 0, 5]),                                                           // bad props
            MqttV5Parser.BuildPacket(MqttPacketType.Publish, 0, [0, 5, (byte)'a']),                                                   // truncated topic
            MqttV5Parser.BuildPacket(MqttPacketType.Publish, 2, [0, 1, (byte)'a', 0, 0, 0]),                                          // QoS 1 pid 0
            MqttV5Parser.BuildPacket(MqttPacketType.Publish, 0, [0, 1, (byte)'a', 5]),                                                // bad props
            MqttV5Parser.BuildPacket(MqttPacketType.PubAck, 1, [0, 1]),                                                               // bad flags
            MqttV5Parser.BuildPacket(MqttPacketType.PubAck, 0, [0, 0]),                                                               // pid 0
            MqttV5Parser.BuildPacket(MqttPacketType.PubAck, 0, [0, 1, 0, 0, 99]),                                                     // trailing byte
            MqttV5Parser.BuildPacket(MqttPacketType.PubAck, 0, [0, 1, 0, 5]),                                                         // bad props
            MqttV5Parser.BuildPacket(MqttPacketType.Subscribe, 2, [0, 0, 0, 0, 1, (byte)'a', 0]),                                     // pid 0
            MqttV5Parser.BuildPacket(MqttPacketType.Subscribe, 2, [0, 1, 0]),                                                         // empty filters
            MqttV5Parser.BuildPacket(MqttPacketType.Subscribe, 2, [0, 1, 5]),                                                         // bad props
            MqttV5Parser.BuildPacket(MqttPacketType.SubAck, 1, [0, 1, 0, 0]),                                                         // bad flags
            MqttV5Parser.BuildPacket(MqttPacketType.SubAck, 0, [0, 0, 0, 0]),                                                         // pid 0
            MqttV5Parser.BuildPacket(MqttPacketType.SubAck, 0, [0, 1, 0]),                                                            // empty reason codes
            MqttV5Parser.BuildPacket(MqttPacketType.SubAck, 0, [0, 1, 5]),                                                            // bad props
            [0xC1, 0x00],                                                                                                             // PINGREQ bad flags
            [0xD1, 0x00],                                                                                                             // PINGRESP bad flags
            [0xD0, 0x01, 0x00],                                                                                                       // PINGRESP non-zero len
            MqttV5Parser.BuildPacket(MqttPacketType.Disconnect, 1, []),                                                               // DISCONNECT bad flags
            MqttV5Parser.BuildPacket(MqttPacketType.Disconnect, 0, [0, 0, 99]),                                                       // DISCONNECT trailing byte
            MqttV5Parser.BuildPacket(MqttPacketType.Disconnect, 0, [0, 5]),                                                           // DISCONNECT bad props
        ];
        foreach (byte[] bad in moreBadMqtt)
        {
            var cb = bad.AsCursor();
            Assert.True(MqttV5Parser.ParsePacket(ref cb).TryGetValue(out Failure fm) && fm.Error.Context is { Length: > 0 } && cb.Position == 0);
        }

        // WriteVariableByteInt guard checks
        Xunit.Assert.Throws<ArgumentOutOfRangeException>(() => MqttV5Parser.WriteVariableByteInt([], -1));
        Xunit.Assert.Throws<ArgumentOutOfRangeException>(() => MqttV5Parser.WriteVariableByteInt([], MqttV5Parser.MaxVariableByteInt + 1));

        // Streaming incomplete VBI rollback
        var vbiInc = new byte[] { 0x80 }.AsStreamingCursor();
        Assert.True(MqttV5Parser.ReadVariableByteInt(ref vbiInc).IsIncomplete && vbiInc.Position == 0);
    }
}
