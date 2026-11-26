using System.Numerics;
using System.Text;
using CsCheck;
using Dafny;
using SpanForge;
using SpanForge.Protocols.Dns;
using SpanForge.Protocols.Http2;
using SpanForge.Protocols.Mqtt;
using Xunit;
using CodecOracle = SpanForgeCodecBijections.__default;
using DnsOracle = SpanForgeDnsTermination.__default;
using ProtoOracle = SpanForgeStreamingAndProtocols.__default;
using ZigZagOracle = SpanForgeZigZagBijection.__default;

namespace SpanForge.Tests.Formal;

/// <summary>
/// Differential property tests comparing SpanForge's zero-allocation <see cref="Span{T}"/>
/// implementation directly against the C# reference oracle generated via <c>dafny translate cs</c>
/// from the formally verified Dafny specifications in <c>formal/</c>.
/// </summary>
public sealed class DafnyDifferentialTests
{
    private static ISequence<BigInteger> ToBigIntSeq(ReadOnlySpan<byte> bytes)
    {
        var arr = new BigInteger[bytes.Length];
        for (int i = 0; i < bytes.Length; i++) arr[i] = bytes[i];
        return Sequence<BigInteger>.FromArray(arr);
    }

    private static byte[] FromBigIntSeq(ISequence<BigInteger> seq)
    {
        var arr = new byte[seq.Count];
        for (int i = 0; i < seq.Count; i++) arr[i] = (byte)seq.Select(i);
        return arr;
    }

    [Fact]
    public void Differential_ZigZag64_MatchesVerifiedDafnyOracle()
    {
        long[] edgeValues = [0L, 1L, -1L, 2L, -2L, long.MinValue, long.MaxValue, long.MinValue + 1, long.MaxValue - 1];
        var gen = Gen.OneOf(Gen.Long, Gen.Int[0, edgeValues.Length - 1].Select(i => edgeValues[i]));

        gen.Sample(v =>
        {
            BigInteger mathEnc = CodecOracle.EncodeZigZag64(v);
            ulong bvEnc = ZigZagOracle.EncodeZigZag64Bv(unchecked((ulong)v));
            Assert.Equal((ulong)mathEnc, bvEnc);

            Span<byte> buf = stackalloc byte[16];
            var writer = new ByteWriter(buf);
            Assert.True(writer.TryWriteVarInt64(v));

            byte[] expectedWire = FromBigIntSeq(CodecOracle.EncodeLeb128(mathEnc));
            Assert.True(writer.WrittenSpan.SequenceEqual(expectedWire));

            var c1 = new ByteCursor(writer.WrittenSpan);
            var r1 = c1.ReadVarInt64();
            Assert.True(r1.TryGetValue(out Parsed<long> p1));
            Assert.Equal(v, p1.Value);
            Assert.Equal((long)CodecOracle.DecodeZigZag64(mathEnc), p1.Value);
            Assert.Equal(unchecked((long)ZigZagOracle.DecodeZigZag64Bv(bvEnc)), p1.Value);

            var c2 = new ByteCursor(writer.WrittenSpan);
            Assert.True(c2.TryReadVarInt64(out long v2).IsParsed);
            Assert.Equal(v, v2);
            Assert.Equal(expectedWire.Length, c2.Position);
        }, iter: 200);
    }

    [Fact]
    public void Differential_Leb128_EncodeDecodeAndRandomBytes_MatchVerifiedDafnyOracle()
    {
        ulong[] edges =
        [
            0UL, 1UL, 127UL, 128UL, 16_383UL, 16_384UL,
            (1UL << 21) - 1, 1UL << 21,
            (1UL << 56) - 1, 1UL << 56,
            (1UL << 63) - 1, 1UL << 63, ulong.MaxValue
        ];
        var valGen = Gen.OneOf(Gen.ULong, Gen.Int[0, edges.Length - 1].Select(i => edges[i]));

        valGen.Sample(v =>
        {
            byte[] oracleBytes = FromBigIntSeq(CodecOracle.EncodeLeb128(new BigInteger(v)));

            Span<byte> buf = stackalloc byte[16];
            var writer = new ByteWriter(buf);
            Assert.True(writer.TryWriteVarUInt64(v));
            Assert.True(writer.WrittenSpan.SequenceEqual(oracleBytes));

            var c1 = new ByteCursor(writer.WrittenSpan);
            Assert.True(c1.TryReadVarUInt64(out ulong parsed).IsParsed);
            Assert.Equal(v, parsed);
            Assert.Equal(oracleBytes.Length, c1.Position);

            var oracleDec = CodecOracle.DecodeLeb128(ToBigIntSeq(writer.WrittenSpan), BigInteger.Zero);
            Assert.True(oracleDec.is_Parsed);
            Assert.Equal(new BigInteger(v), oracleDec.dtor_value);
            Assert.Equal(oracleBytes.Length, (int)oracleDec.dtor_nextPos);
        }, iter: 200);

        // Arbitrary random & adversarial byte sequences (truncation + 10-byte overflow)
        var bytesGen = Gen.OneOf(
            Gen.Byte.Array[0, 14],
            Gen.Byte[0x80, 0xFF].Array[10, 12],
            Gen.Byte[0x80, 0xFF].Array[1, 9]);

        bytesGen.Sample(data =>
        {
            var oracle = CodecOracle.DecodeLeb128(ToBigIntSeq(data), BigInteger.Zero);

            var cStream = new ByteCursor(data, mode: InputMode.Streaming);
            var res = cStream.ReadVarUInt64();

            var cTry = new ByteCursor(data, mode: InputMode.Streaming);
            FastStatus fast = cTry.TryReadVarUInt64(out ulong fastVal);

            if (oracle.is_Parsed)
            {
                Assert.True(res.TryGetValue(out Parsed<ulong> p));
                Assert.Equal((ulong)oracle.dtor_value, p.Value);
                Assert.Equal((int)oracle.dtor_nextPos, cStream.Position);

                Assert.True(fast.IsParsed);
                Assert.Equal((ulong)oracle.dtor_value, fastVal);
                Assert.Equal((int)oracle.dtor_nextPos, cTry.Position);
            }
            else if (oracle.is_Incomplete)
            {
                Assert.True(res.TryGetValue(out Incomplete _));
                Assert.Equal(0, cStream.Position);
                Assert.True(fast.IsIncomplete);
                Assert.Equal(0, cTry.Position);
            }
            else
            {
                Assert.True(oracle.is_Backtrack);
                Assert.True(res.TryGetValue(out Backtrack _));
                Assert.Equal(0, cStream.Position);
                Assert.True(fast.IsBacktrack);
                Assert.Equal(0, cTry.Position);
            }
        }, iter: 200);
    }

    [Fact]
    public void Differential_QuicVarInt_MatchesVerifiedDafnyOracle()
    {
        const ulong quicMax = (1UL << 62) - 1;
        ulong[] edges = [0UL, 63UL, 64UL, 16_383UL, 16_384UL, 0x3FFF_FFFFUL, 0x4000_0000UL, quicMax];
        var valGen = Gen.OneOf(Gen.ULong[0, quicMax], Gen.Int[0, edges.Length - 1].Select(i => edges[i]));

        valGen.Sample(v =>
        {
            byte[] oracleBytes = FromBigIntSeq(CodecOracle.EncodeQuicVarInt(new BigInteger(v)));

            Span<byte> buf = stackalloc byte[16];
            var writer = new ByteWriter(buf);
            Assert.True(writer.TryWriteQuicVarInt64(v));
            Assert.True(writer.WrittenSpan.SequenceEqual(oracleBytes));

            var c1 = new ByteCursor(writer.WrittenSpan);
            var r1 = c1.ReadQuicVarInt64();
            Assert.True(r1.TryGetValue(out Parsed<ulong> p1));
            Assert.Equal(v, p1.Value);
            Assert.Equal(oracleBytes.Length, c1.Position);

            var c2 = new ByteCursor(writer.WrittenSpan);
            Assert.True(c2.TryReadQuicVarInt64(out ulong v2).IsParsed);
            Assert.Equal(v, v2);
            Assert.Equal(oracleBytes.Length, c2.Position);
        }, iter: 200);

        Gen.Byte.Array[0, 12].Sample(data =>
        {
            var oracle = CodecOracle.DecodeQuicVarInt(ToBigIntSeq(data), BigInteger.Zero);

            var c1 = new ByteCursor(data, mode: InputMode.Streaming);
            var r1 = c1.ReadQuicVarInt64();
            var c2 = new ByteCursor(data, mode: InputMode.Streaming);
            FastStatus s2 = c2.TryReadQuicVarInt64(out ulong v2);

            if (oracle.is_Parsed)
            {
                Assert.True(r1.TryGetValue(out Parsed<ulong> p1));
                Assert.Equal((ulong)oracle.dtor_value, p1.Value);
                Assert.Equal((int)oracle.dtor_nextPos, c1.Position);

                Assert.True(s2.IsParsed);
                Assert.Equal((ulong)oracle.dtor_value, v2);
                Assert.Equal((int)oracle.dtor_nextPos, c2.Position);
            }
            else
            {
                Assert.True(oracle.is_Incomplete);
                Assert.True(r1.TryGetValue(out Incomplete _));
                Assert.Equal(0, c1.Position);
                Assert.True(s2.IsIncomplete);
                Assert.Equal(0, c2.Position);
            }
        }, iter: 200);
    }

    [Fact]
    public void Differential_BitWriterAndBitCursor_MatchVerifiedDafnyOracle()
    {
        var gen = Gen.Select(
            Gen.Byte.Array[12, 16],
            Gen.Int[0, 23],
            Gen.Int[1, 64],
            Gen.ULong);

        gen.Sample((initialDirty, skipBits, count, value) =>
        {
            byte[] sfBuf = (byte[])initialDirty.Clone();
            var bw = new BitWriter(sfBuf);
            var preReader = new BitCursor(initialDirty);
            if (skipBits > 0)
            {
                Assert.True(preReader.ReadU64(skipBits).TryGetValue(out Parsed<ulong> preVal));
                Assert.True(bw.TryWriteU64(skipBits, preVal.Value));
            }

            if (count == 1)
                Assert.True(bw.TryWriteBool((value & 1UL) != 0));
            else if (count <= 8)
                Assert.True(bw.TryWriteU8(count, (byte)value));
            else
                Assert.True(bw.TryWriteU64(count, value));

            ISequence<BigInteger> oracleBuf = CodecOracle.WriteBits(
                ToBigIntSeq(initialDirty),
                new BigInteger(skipBits),
                new BigInteger(count),
                new BigInteger(value));
            byte[] expectedBytes = FromBigIntSeq(oracleBuf);

            Assert.True(sfBuf.AsSpan().SequenceEqual(expectedBytes));

            BigInteger oracleRead = CodecOracle.ReadBits(oracleBuf, new BigInteger(skipBits), new BigInteger(count));

            var bc = new BitCursor(sfBuf);
            if (skipBits > 0) Assert.True(bc.ReadU64(skipBits).TryGetValue(out Parsed<ulong> _));
            var sfRead = bc.ReadU64(count);
            Assert.True(sfRead.TryGetValue(out Parsed<ulong> parsed));
            Assert.Equal((ulong)oracleRead, parsed.Value);
        }, iter: 200);
    }

    [Fact]
    public void Differential_MqttVbiAndPublish_MatchVerifiedDafnyOracle()
    {
        Gen.Int[0, 268_435_455].Sample(v =>
        {
            byte[] oracleWire = FromBigIntSeq(ProtoOracle.MqttEncodeVbi(new BigInteger(v)));

            var c = new ByteCursor(oracleWire, mode: InputMode.Streaming);
            var res = MqttV5Parser.ReadVariableByteInt(ref c);
            Assert.True(res.TryGetValue(out Parsed<int> p));
            Assert.Equal(v, p.Value);
            Assert.Equal(oracleWire.Length, c.Position);
        }, iter: 200);

        Gen.Select(Gen.Byte.Array[0, 6], Gen.Bool).Sample((data, streaming) =>
        {
            var mode = streaming ? InputMode.Streaming : InputMode.Complete;
            var dMode = streaming
                ? SpanForgeStreamingAndProtocols.InputMode.create_Streaming()
                : SpanForgeStreamingAndProtocols.InputMode.create_Complete();

            var oracle = ProtoOracle.MqttDecodeVbi(Sequence<byte>.FromArray(data), BigInteger.Zero, dMode);
            var c = new ByteCursor(data, mode: mode);
            var res = MqttV5Parser.ReadVariableByteInt(ref c);

            if (oracle.is_FParsed)
            {
                Assert.True(res.TryGetValue(out Parsed<int> p));
                Assert.Equal((int)oracle.dtor_value, p.Value);
                Assert.Equal((int)oracle.dtor_consumed, c.Position);
            }
            else if (oracle.is_FIncomplete)
            {
                Assert.True(res.TryGetValue(out Incomplete _));
                Assert.Equal(0, c.Position);
            }
            else
            {
                Assert.True(res.TryGetValue(out Backtrack _) || res.TryGetValue(out Failure _));
                Assert.Equal(0, c.Position);
            }
        }, iter: 200);

        Gen.Select(Gen.Byte[0x30, 0x3F], Gen.Byte.Array[0, 24], Gen.Bool).Sample((hdr, rest, streaming) =>
        {
            byte[] packetBytes = new byte[1 + rest.Length];
            packetBytes[0] = hdr;
            Buffer.BlockCopy(rest, 0, packetBytes, 1, rest.Length);

            var mode = streaming ? InputMode.Streaming : InputMode.Complete;
            var dMode = streaming
                ? SpanForgeStreamingAndProtocols.InputMode.create_Streaming()
                : SpanForgeStreamingAndProtocols.InputMode.create_Complete();

            var oracle = ProtoOracle.MqttParsePublish(Sequence<byte>.FromArray(packetBytes), BigInteger.Zero, dMode);
            var c1 = new ByteCursor(packetBytes, mode: mode);
            var r1 = MqttV5Parser.ParsePacket(ref c1);
            var c2 = new ByteCursor(packetBytes, mode: mode);
            FastStatus s2 = MqttV5Parser.TryParsePacket(ref c2, out MqttPacket p2);

            if (oracle.is_FParsed)
            {
                Assert.True(r1.TryGetValue(out Parsed<MqttPacket> parsed));
                Assert.True(parsed.Value.TryGetValue(out MqttPublishPacket pub));
                var model = oracle.dtor_value;
                Assert.Equal((byte)model.dtor_qos, pub.Qos);
                Assert.Equal((int)model.dtor_topic.dtor_offset, pub.TopicName.Offset);
                Assert.Equal((int)model.dtor_topic.dtor_len, pub.TopicName.Length);
                Assert.Equal((int)model.dtor_properties.dtor_offset, pub.Properties.Offset);
                Assert.Equal((int)model.dtor_properties.dtor_len, pub.Properties.Length);
                Assert.Equal((int)model.dtor_payload.dtor_offset, pub.Payload.Offset);
                Assert.Equal((int)model.dtor_payload.dtor_len, pub.Payload.Length);
                Assert.Equal((int)oracle.dtor_consumed, c1.Position);

                Assert.True(s2.IsParsed);
                Assert.True(p2.TryGetValue(out MqttPublishPacket pub2));
                Assert.Equal(pub.Payload, pub2.Payload);
                Assert.Equal(c1.Position, c2.Position);
            }
            else if (oracle.is_FIncomplete)
            {
                Assert.True(r1.TryGetValue(out Incomplete _));
                Assert.Equal(0, c1.Position);
                Assert.True(s2.IsIncomplete);
                Assert.Equal(0, c2.Position);
            }
            else
            {
                Assert.True(r1.TryGetValue(out Backtrack _) || r1.TryGetValue(out Failure _));
                Assert.Equal(0, c1.Position);
                Assert.True(s2.IsBacktrack || s2.IsFailure);
                Assert.Equal(0, c2.Position);
            }
        }, iter: 200);
    }

    [Fact]
    public void Differential_Http2FrameParser_MatchesVerifiedDafnyOracle()
    {
        var frameGen = Gen.Select(
            Gen.Int[0, 32],
            Gen.Byte[0, 12],
            Gen.Byte,
            Gen.UInt,
            Gen.Byte.Array[0, 36],
            Gen.Bool);

        frameGen.Sample((declaredLen, rawType, flags, streamId, payloadBytes, streaming) =>
        {
            byte[] buf = new byte[9 + payloadBytes.Length];
            buf[0] = (byte)(declaredLen >> 16);
            buf[1] = (byte)(declaredLen >> 8);
            buf[2] = (byte)declaredLen;
            buf[3] = rawType;
            buf[4] = flags;
            buf[5] = (byte)(streamId >> 24);
            buf[6] = (byte)(streamId >> 16);
            buf[7] = (byte)(streamId >> 8);
            buf[8] = (byte)streamId;
            Buffer.BlockCopy(payloadBytes, 0, buf, 9, payloadBytes.Length);

            var mode = streaming ? InputMode.Streaming : InputMode.Complete;
            var dMode = streaming
                ? SpanForgeStreamingAndProtocols.InputMode.create_Streaming()
                : SpanForgeStreamingAndProtocols.InputMode.create_Complete();

            var oracle = ProtoOracle.Http2ParseFrame(
                Sequence<byte>.FromArray(buf), BigInteger.Zero, dMode, new BigInteger(16_384));

            var c1 = new ByteCursor(buf, mode: mode);
            var r1 = Http2FrameParser.ParseFrame(ref c1);
            var c2 = new ByteCursor(buf, mode: mode);
            FastStatus s2 = Http2FrameParser.TryParseFrame(ref c2, out Http2Frame f2);

            if (oracle.is_FParsed)
            {
                Assert.True(r1.TryGetValue(out Parsed<Http2Frame> p1));
                Assert.Equal((int)oracle.dtor_consumed, c1.Position);
                Assert.True(s2.IsParsed);
                Assert.Equal(c1.Position, c2.Position);

                var m = oracle.dtor_value;
                if (m.is_H2Data)
                {
                    Assert.True(p1.Value.TryGetValue(out Http2DataFrame df));
                    Assert.Equal((int)m.dtor_data.dtor_offset, df.Data.Offset);
                    Assert.Equal((int)m.dtor_data.dtor_len, df.Data.Length);
                    Assert.True(f2.TryGetValue(out Http2DataFrame df2));
                    Assert.Equal(df.Data, df2.Data);
                }
                else if (m.is_H2Headers)
                {
                    Assert.True(p1.Value.TryGetValue(out Http2HeadersFrame hf));
                    Assert.Equal((int)m.dtor_headerBlock.dtor_offset, hf.HeaderBlockFragment.Offset);
                    Assert.Equal((int)m.dtor_headerBlock.dtor_len, hf.HeaderBlockFragment.Length);
                }
            }
            else if (oracle.is_FIncomplete)
            {
                Assert.True(r1.TryGetValue(out Incomplete _));
                Assert.Equal(0, c1.Position);
                Assert.True(s2.IsIncomplete);
                Assert.Equal(0, c2.Position);
            }
            else
            {
                Assert.True(r1.TryGetValue(out Backtrack _) || r1.TryGetValue(out Failure _));
                Assert.Equal(0, c1.Position);
                Assert.True(s2.IsBacktrack || s2.IsFailure);
                Assert.Equal(0, c2.Position);
            }
        }, iter: 250);
    }

    [Fact]
    public void Differential_DnsCompressionPointerTermination_MatchesVerifiedDafnyOracle()
    {
        byte[][] adversarialCases =
        [
            [0xC0, 0x00],
            [0xC0, 0x02, 0xC0, 0x00],
            [3, (byte)'w', (byte)'w', (byte)'w', 0],
            [3, (byte)'c', (byte)'o', (byte)'m', 0, 3, (byte)'f', (byte)'o', (byte)'o', 0xC0, 0x00],
            [0xC0, 0xFF],
            [0x40, 0x00],
            [0x80, 0x00]
        ];
        var dnsGen = Gen.OneOf(
            Gen.Byte.Array[0, 32],
            Gen.Int[0, adversarialCases.Length - 1].Select(i => adversarialCases[i]));

        Gen.Select(dnsGen, Gen.Int[0, 12], Gen.Bool).Sample((buf, rawStart, streaming) =>
        {
            int start = buf.Length == 0 ? 0 : rawStart % (buf.Length + 1);
            var mode = streaming ? InputMode.Streaming : InputMode.Complete;
            var dMode = streaming
                ? SpanForgeDnsTermination.InputMode.create_Streaming()
                : SpanForgeDnsTermination.InputMode.create_Complete();

            var oracleTuple = DnsOracle.ParseDnsName(Sequence<byte>.FromArray(buf), new BigInteger(start), dMode);
            var outcome = oracleTuple.dtor__0;
            int expectedPos = (int)oracleTuple.dtor__1;

            var c = new ByteCursor(buf, mode: mode) { Position = start };
            var res = DnsParser.ParseName(ref c);

            if (outcome.is_NameParsed)
            {
                Assert.True(res.TryGetValue(out Parsed<string> parsed));
                Assert.Equal(expectedPos, c.Position);

                var sb = new StringBuilder();
                var labels = outcome.dtor_labels;
                for (int i = 0; i < labels.Count; i++)
                {
                    if (i > 0) sb.Append('.');
                    sb.Append(Encoding.ASCII.GetString(labels.Select(i).Elements));
                }
                Assert.Equal(sb.ToString(), parsed.Value);
            }
            else if (outcome.is_NameIncomplete)
            {
                Assert.True(res.TryGetValue(out Incomplete _));
                Assert.Equal(start, c.Position);
            }
            else
            {
                Assert.True(outcome.is_NameFailure);
                Assert.True(res.TryGetValue(out Failure _));
                Assert.Equal(start, c.Position);
            }
        }, iter: 250);
    }
}
