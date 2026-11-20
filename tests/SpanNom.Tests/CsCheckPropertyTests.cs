using System.Buffers.Binary;
using CsCheck;
using SpanNom.Protocols.Dns;
using SpanNom.Protocols.Png;
using SpanNom.Protocols.Resp;
using SpanNom.Protocols.WebSocket;
using SpanNom.Streaming;
using Xunit;

namespace SpanNom.Tests;

/// <summary>
/// High-throughput property-based tests powered by <c>CsCheck 5.0.1</c> (with automatic shrinking).
/// Verifies binary roundtrips, streaming chunk-independence, monad/railroad laws, and total no-throw safety.
/// </summary>
public sealed class CsCheckPropertyTests
{
    [Fact]
    public void CsCheck_FixedWidth_BigAndLittleEndian_Roundtrip()
    {
        Gen.Select(Gen.UShort, Gen.Short, Gen.UInt, Gen.Int, Gen.ULong, Gen.Long, Gen.Bool)
            .Sample((u16, i16, u32, i32, u64, i64, bigEndian) =>
            {
                Span<byte> buf = stackalloc byte[28];
                if (bigEndian)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(buf[0..2], u16);
                    BinaryPrimitives.WriteInt16BigEndian(buf[2..4], i16);
                    BinaryPrimitives.WriteUInt32BigEndian(buf[4..8], u32);
                    BinaryPrimitives.WriteInt32BigEndian(buf[8..12], i32);
                    BinaryPrimitives.WriteUInt64BigEndian(buf[12..20], u64);
                    BinaryPrimitives.WriteInt64BigEndian(buf[20..28], i64);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(buf[0..2], u16);
                    BinaryPrimitives.WriteInt16LittleEndian(buf[2..4], i16);
                    BinaryPrimitives.WriteUInt32LittleEndian(buf[4..8], u32);
                    BinaryPrimitives.WriteInt32LittleEndian(buf[8..12], i32);
                    BinaryPrimitives.WriteUInt64LittleEndian(buf[12..20], u64);
                    BinaryPrimitives.WriteInt64LittleEndian(buf[20..28], i64);
                }

                var c = new ByteCursor(buf, bigEndian ? Endianness.Big : Endianness.Little);
                Assert.Equal(u16, c.ReadU16().Unwrap());
                Assert.Equal(i16, c.ReadI16().Unwrap());
                Assert.Equal(u32, c.ReadU32().Unwrap());
                Assert.Equal(i32, c.ReadI32().Unwrap());
                Assert.Equal(u64, c.ReadU64().Unwrap());
                Assert.Equal(i64, c.ReadI64().Unwrap());
                Assert.True(c.IsAtEnd);
            }, iter: 500);
    }

    [Fact]
    public void CsCheck_Leb128_ZigZag_And_QuicVarInt_Roundtrip()
    {
        Gen.ULong.Sample(value =>
        {
            Span<byte> leb = stackalloc byte[10];
            int len = WriteLeb128(value, leb);
            var c = new ByteCursor(leb[..len]);
            Assert.Equal(value, c.ReadVarUInt64().Unwrap());
            Assert.True(c.IsAtEnd);
        }, iter: 500);

        Gen.Long.Sample(value =>
        {
            ulong zigzag = (ulong)((value << 1) ^ (value >> 63));
            Span<byte> leb = stackalloc byte[10];
            int len = WriteLeb128(zigzag, leb);
            var c = new ByteCursor(leb[..len]);
            Assert.Equal(value, c.ReadVarInt64().Unwrap());
            Assert.True(c.IsAtEnd);
        }, iter: 500);

        // QUIC RFC 9000 supports 62-bit unsigned values [0 .. (1<<62)-1]
        Gen.ULong[0, (1UL << 62) - 1].Sample(value =>
        {
            Span<byte> buf = stackalloc byte[8];
            int len = WriteQuicVarInt(value, buf);
            var c = new ByteCursor(buf[..len]);
            Assert.Equal(value, c.ReadQuicVarInt64().Unwrap());
            Assert.True(c.IsAtEnd);
        }, iter: 500);
    }

    [Fact]
    public void CsCheck_BitCursor_ArbitraryBitWidthPacking_Roundtrip()
    {
        var fieldGen = Gen.Select(Gen.Int[1, 16], Gen.UShort)
            .Select(static t => (Width: t.Item1, Value: (ushort)(t.Item2 & ((1 << t.Item1) - 1))));

        fieldGen.Array[1, 16].Sample(fields =>
        {
            int totalBits = 0;
            foreach (var f in fields) totalBits += f.Width;
            byte[] packed = new byte[(totalBits + 7) >> 3];

            int bitPos = 0;
            foreach (var (width, value) in fields)
            {
                for (int b = width - 1; b >= 0; b--)
                {
                    if (((value >> b) & 1) != 0)
                    {
                        packed[bitPos >> 3] |= (byte)(1 << (7 - (bitPos & 7)));
                    }

                    bitPos++;
                }
            }

            var bits = new BitCursor(packed);
            foreach (var (width, expected) in fields)
            {
                ushort actual = width <= 8 ? bits.ReadU8(width).Unwrap() : bits.ReadU16(width).Unwrap();
                Assert.Equal(expected, actual);
            }

            Assert.Equal(totalBits, bits.BitPosition);
            Assert.Equal(packed.Length, bits.ConsumedBytes);
        }, iter: 400);
    }

    [Fact]
    public void CsCheck_WebSocket_EncodeDecodeUnmask_Roundtrip()
    {
        var opGen = Gen.OneOf(Gen.Const((byte)0x0), Gen.Const((byte)0x1), Gen.Const((byte)0x2), Gen.Const((byte)0x9), Gen.Const((byte)0xA));
        Gen.Select(opGen, Gen.Bool, Gen.Bool, Gen.UInt, Gen.Byte.Array[0, 180])
            .Sample((opcode, fin, masked, maskKey, payload) =>
            {
                bool isControl = (opcode & 0x08) != 0;
                bool actualFin = isControl || fin;
                byte[] actualPayload = isControl && payload.Length > 125 ? payload[..125] : payload;

                byte[] wire = EncodeWebSocketFrame(opcode, actualFin, masked, maskKey, actualPayload);
                var cursor = wire.AsCursor();
                var frame = WebSocketFrameParser.ParseFrame(ref cursor).Unwrap();
                Assert.True(cursor.IsAtEnd);

                (Option<uint> parsedMask, ByteSlice slice) = frame switch
                {
                    ContinuationFrame(_, var m, var p) => (m, p),
                    TextFrame(_, var m, var p) => (m, p),
                    BinaryFrame(_, var m, var p) => (m, p),
                    PingFrame(var m, var p) => (m, p),
                    PongFrame(var m, var p) => (m, p),
                    CloseFrame(var m, _, var p) => (m, p),
                };

                byte[] unmasked = new byte[slice.Length];
                WebSocketFrameParser.UnmaskInto(wire, slice, parsedMask, unmasked);
                Assert.SequenceEqual<byte>(actualPayload, unmasked);
            }, iter: 300);
    }

    [Fact]
    public void CsCheck_PngChunk_SingleBitFlip_AlwaysTriggersCrcFailure()
    {
        Gen.Select(Gen.Byte.Array[0, 64], Gen.Int[0, 1000]).Sample((payload, bitSelector) =>
        {
            byte[] chunk = PngParser.BuildChunk("IDAT"u8, payload);

            // Valid chunk parses cleanly
            var okCursor = chunk.AsCursor();
            var parsed = PngParser.ParseChunk(ref okCursor).Unwrap();
            Assert.True(parsed is IdatChunk { Data.Length: var len } && len == payload.Length);

            // Flip any single bit in [Type (4B) || Payload || CRC (4B)] -> must yield Failure(CrcMismatch)
            int crcCoveredStart = 4;
            int totalBits = (chunk.Length - crcCoveredStart) * 8;
            int bitToFlip = bitSelector % totalBits;
            chunk[crcCoveredStart + (bitToFlip >> 3)] ^= (byte)(1 << (bitToFlip & 7));

            var badCursor = chunk.AsCursor();
            var badRes = PngParser.ParseChunk(ref badCursor);
            Assert.True(badRes.TryGetValue(out Failure f) && f.Error.Kind == ErrorKind.CrcMismatch);
        }, iter: 300);
    }

    [Fact]
    public void CsCheck_StreamingDecoder_ArbitraryChunkSplits_EquivalentToComplete()
    {
        Gen.Select(
                Gen.Byte.Array[0, 40].Array[1, 6],
                Gen.Int[1, 13].Array[1, 20])
            .Sample((payloads, chunkSizes) =>
            {
                // Encode a sequence of WebSocket binary frames
                List<byte> wireList = [];
                foreach (var p in payloads)
                {
                    wireList.AddRange(EncodeWebSocketFrame(0x2, fin: true, masked: true, maskKey: 0xA55A_1234u, p));
                }

                byte[] wire = wireList.ToArray();
                var decoder = new StreamingDecoder<WebSocketFrame>(WebSocketFrameParser.ParseFrame, initialCapacity: 16);
                var decodedPayloads = new List<byte[]>();

                int pos = 0;
                int splitIdx = 0;
                while (pos < wire.Length)
                {
                    int take = Math.Min(chunkSizes[splitIdx % chunkSizes.Length], wire.Length - pos);
                    splitIdx++;
                    decoder.Append(wire.AsSpan(pos, take));
                    pos += take;

                    while (true)
                    {
                        var res = decoder.TryDecodeNext(out var frameBytes);
                        if (res.TryGetValue(out Parsed<WebSocketFrame> ok))
                        {
                            Assert.True(ok.Value.TryGetValue(out BinaryFrame bf));
                            byte[] unmasked = new byte[bf.Payload.Length];
                            WebSocketFrameParser.UnmaskInto(frameBytes, bf.Payload, bf.MaskKey, unmasked);
                            decodedPayloads.Add(unmasked);
                        }
                        else if (res.IsIncomplete)
                        {
                            break;
                        }
                        else
                        {
                            throw new AssertionException($"Unexpected streaming error: {res}");
                        }
                    }
                }

                Assert.Equal(payloads.Length, decodedPayloads.Count);
                for (int i = 0; i < payloads.Length; i++)
                {
                    Assert.SequenceEqual<byte>(payloads[i], decodedPayloads[i]);
                }
            }, iter: 200);
    }

    [Fact]
    public void CsCheck_RailroadTotalSafety_ArbitraryGarbageBytes_NeverThrow()
    {
        // The core Railroad Policy guarantee: NO arbitrary untrusted input may throw an exception!
        Gen.Byte.Array[0, 128].Sample(garbage =>
        {
            var c1 = garbage.AsCursor();
            _ = PngParser.ParseDocument(ref c1);

            var c2 = garbage.AsCursor();
            _ = WebSocketFrameParser.ParseFrame(ref c2);

            var c3 = garbage.AsCursor();
            _ = RespParser.ParseValue(ref c3);

            var c4 = garbage.AsCursor();
            _ = DnsParser.ParsePacket(ref c4);

            var c5 = garbage.AsStreamingCursor();
            _ = WebSocketFrameParser.ParseFrame(ref c5);
        }, iter: 1_000);
    }

    [Fact]
    public void CsCheck_Railroad_FunctorAndMonadLaws()
    {
        var resultGen = Gen.OneOf(
            Gen.Int.Select(ParseResult.Ok),
            Gen.Int[1, 64].Select(ParseResult.Need<int>),
            Gen.Int[0, 100].Select(static o => ParseResult.Backtrack<int>(o, ErrorKind.Tag)),
            Gen.Int[0, 100].Select(static o => ParseResult.Fail<int>(o, ErrorKind.Protocol)));

        static ParseResult<int> F(int x) => (x & 1) == 0 ? ParseResult.Ok(unchecked(x * 3 + 1)) : Backtrack.At(1, ErrorKind.Verify);
        static ParseResult<int> G(int x) => x >= 0 ? ParseResult.Ok(unchecked(x - 7)) : Failure.At(2, ErrorKind.Protocol);

        resultGen.Sample(r =>
        {
            // Functor identity: r.Map(id) == r
            Assert.Equal(r.ToString(), r.Map(static x => x).ToString());

            // Functor composition: r.Map(f).Map(g) == r.Map(x => g(f(x)))
            Assert.Equal(
                r.Map(static x => unchecked(x + 5)).Map(static x => unchecked(x * 2)).ToString(),
                r.Map(static x => unchecked((x + 5) * 2)).ToString());

            // Monad right identity: r.Bind(Ok) == r
            Assert.Equal(r.ToString(), r.Bind(ParseResult.Ok).ToString());

            // Monad associativity: r.Bind(F).Bind(G) == r.Bind(x => F(x).Bind(G))
            Assert.Equal(
                r.Bind(F).Bind(G).ToString(),
                r.Bind(static x => F(x).Bind(G)).ToString());

            // Railroad Recover law: only Backtrack is recovered; Failure and Incomplete stay off-track
            var recovered = r.Recover(static _ => 999);
            if (r.IsBacktrack) Assert.Equal(999, recovered.Unwrap());
            else Assert.Equal(r.ToString(), recovered.ToString());
        }, iter: 500);
    }

    private static int WriteLeb128(ulong value, Span<byte> dest)
    {
        int i = 0;
        while (true)
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) b |= 0x80;
            dest[i++] = b;
            if (value == 0) return i;
        }
    }

    private static int WriteQuicVarInt(ulong value, Span<byte> dest)
    {
        if (value <= 63)
        {
            dest[0] = (byte)value;
            return 1;
        }

        if (value <= 16_383)
        {
            BinaryPrimitives.WriteUInt16BigEndian(dest, (ushort)(value | 0x4000));
            return 2;
        }

        if (value <= 1_073_741_823)
        {
            BinaryPrimitives.WriteUInt32BigEndian(dest, (uint)(value | 0x8000_0000u));
            return 4;
        }

        BinaryPrimitives.WriteUInt64BigEndian(dest, value | 0xC000_0000_0000_0000UL);
        return 8;
    }

    private static byte[] EncodeWebSocketFrame(byte opcode, bool fin, bool masked, uint maskKey, byte[] payload)
    {
        List<byte> outBuf = [with(capacity: payload.Length + 10)];
        outBuf.Add((byte)((fin ? 0x80 : 0x00) | (opcode & 0x0F)));

        byte maskBit = masked ? (byte)0x80 : (byte)0x00;
        if (payload.Length < 126)
        {
            outBuf.Add((byte)(maskBit | payload.Length));
        }
        else
        {
            outBuf.Add((byte)(maskBit | 126));
            outBuf.Add((byte)(payload.Length >> 8));
            outBuf.Add((byte)(payload.Length & 0xFF));
        }

        if (masked)
        {
            Span<byte> mk =
            [
                (byte)(maskKey >> 24),
                (byte)(maskKey >> 16),
                (byte)(maskKey >> 8),
                (byte)maskKey,
            ];
            for (int i = 0; i < 4; i++) outBuf.Add(mk[i]);
            for (int i = 0; i < payload.Length; i++) outBuf.Add((byte)(payload[i] ^ mk[i & 3]));
        }
        else
        {
            outBuf.AddRange(payload);
        }

        return outBuf.ToArray();
    }
}
