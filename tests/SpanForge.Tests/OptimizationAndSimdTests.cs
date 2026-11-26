using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using CsCheck;
using SpanForge.Pipelines;
using SpanForge.Protocols.Http2;
using SpanForge.Protocols.Mqtt;
using SpanForge.Protocols.Png;
using SpanForge.Protocols.Protobuf;
using SpanForge.Protocols.WebSocket;
using Xunit;

namespace SpanForge.Tests;

[BinaryStruct(Endianness.Big)]
[Magic(0xDE, 0xAD)]
public readonly partial record struct CoalescedAllPrimitivesStruct(
    bool Flag,
    byte U8,
    sbyte I8,
    ushort U16Be,
    [property: Endian(Endianness.Little)] ushort U16Le,
    short I16Be,
    [property: Endian(Endianness.Little)] short I16Le,
    uint U32Be,
    [property: Endian(Endianness.Little)] uint U32Le,
    int I32Be,
    [property: Endian(Endianness.Little)] int I32Le,
    ulong U64Be,
    [property: Endian(Endianness.Little)] ulong U64Le,
    long I64Be,
    [property: Endian(Endianness.Little)] long I64Le,
    float F32Be,
    [property: Endian(Endianness.Little)] float F32Le,
    double F64Be,
    [property: Endian(Endianness.Little)] double F64Le,
    [property: Pad(2)] [property: Magic(0x77)] [property: VarInt] ulong VarU,
    [property: VarInt(VarIntEncoding.Leb128SignedZigZag)] long VarI,
    [property: VarInt(VarIntEncoding.Quic)] ulong QuicV,
    [property: FixedBytes(3)] ByteSlice Fixed3,
    [property: LengthPrefixed(LengthPrefix.U8)] ByteSlice LpU8,
    [property: Endian(Endianness.Little)] [property: LengthPrefixed(LengthPrefix.U16)] ByteSlice LpU16Le);

public sealed class OptimizationAndSimdTests
{
    private static uint ReferenceCrc32Bitwise(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFF_FFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB8_8320u : (crc >> 1);
            }
        }
        return ~crc;
    }

    [Fact]
    public void PngParser_ComputeCrc32_Pclmulqdq_MatchesBitwiseReference_AllLengths()
    {
        // Exhaustively test every length from 0..260 (crossing 16, 32, 48, 63, 64, 65, 80, 127, 128, 129, 192, 256)
        byte[] master = new byte[4096];
        for (int i = 0; i < master.Length; i++)
        {
            master[i] = (byte)((i * 131 + 17) ^ (i >> 3));
        }

        for (int len = 0; len <= 260; len++)
        {
            ReadOnlySpan<byte> slice = master.AsSpan(0, len);
            Assert.Equal(ReferenceCrc32Bitwise(slice), PngParser.ComputeCrc32(slice));
        }

        int[] largeLengths = [511, 512, 513, 1000, 1024, 2049, 4096];
        foreach (int len in largeLengths)
        {
            ReadOnlySpan<byte> slice = master.AsSpan(0, len);
            Assert.Equal(ReferenceCrc32Bitwise(slice), PngParser.ComputeCrc32(slice));
        }
    }

    [Fact]
    public void PngParser_TryParseSignature_And_TryParseChunk_AllVariants_And_Errors()
    {
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), 320);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), 240);
        ihdr[8] = 8;
        ihdr[9] = 6; // TruecolorAlpha

        byte[] idat = new byte[96]; // >= 64 bytes exercises PCLMULQDQ CRC-32 inside TryParseChunk!
        for (int i = 0; i < idat.Length; i++) idat[i] = (byte)(i * 7 + 3);

        byte[] doc =
        [
            .. PngParser.Signature,
            .. PngParser.BuildChunk("IHDR"u8, ihdr),
            .. PngParser.BuildChunk("PLTE"u8, [1, 2, 3, 4, 5, 6]),
            .. PngParser.BuildChunk("tEXt"u8, "Key\0Val"u8),
            .. PngParser.BuildChunk("IDAT"u8, idat),
            .. PngParser.BuildChunk("gAMA"u8, [0, 0, 0xB1, 0x8F]),
            .. PngParser.BuildChunk("IEND"u8, []),
        ];

        var c = doc.AsCursor();
        Assert.True(PngParser.TryParseSignature(ref c).IsParsed);
        Assert.True(PngParser.TryParseChunk(ref c, out var ch1).IsParsed && ch1.TryGetValue(out IhdrChunk h) && h.Width == 320 && h.Height == 240);
        Assert.True(PngParser.TryParseChunk(ref c, out var ch2).IsParsed && ch2.TryGetValue(out PlteChunk pl) && pl.EntryCount == 2);
        Assert.True(PngParser.TryParseChunk(ref c, out var ch3).IsParsed && ch3.TryGetValue(out TextChunk tx) && doc.AsSpan()[tx.Keyword].SequenceEqual("Key"u8) && doc.AsSpan()[tx.Text].SequenceEqual("Val"u8));
        Assert.True(PngParser.TryParseChunk(ref c, out var ch4).IsParsed && ch4.TryGetValue(out IdatChunk id) && id.Data.Length == 96);
        Assert.True(PngParser.TryParseChunk(ref c, out var ch5).IsParsed && ch5.TryGetValue(out UnknownChunk uk) && uk.TypeName == "gAMA");
        Assert.True(PngParser.TryParseChunk(ref c, out var ch6).IsParsed && ch6.TryGetValue(out IendChunk _));
        Assert.True(c.IsAtEnd);

        // Streaming shortages at <4, 4..7, 8..11, and payload shortage
        byte[] ihdrChunk = PngParser.BuildChunk("IHDR"u8, ihdr);
        var s2 = ihdrChunk.AsSpan(0, 2).AsStreamingCursor();
        Assert.Equal(Needed.Bytes(2), PngParser.TryParseChunk(ref s2, out _).UnpackNeeded());
        var s6 = ihdrChunk.AsSpan(0, 6).AsStreamingCursor();
        Assert.Equal(Needed.Bytes(2), PngParser.TryParseChunk(ref s6, out _).UnpackNeeded());
        var s10 = ihdrChunk.AsSpan(0, 10).AsStreamingCursor();
        Assert.Equal(Needed.Bytes(2), PngParser.TryParseChunk(ref s10, out _).UnpackNeeded());
        var s15 = ihdrChunk.AsSpan(0, 15).AsStreamingCursor();
        Assert.Equal(Needed.Bytes(ihdrChunk.Length - 15), PngParser.TryParseChunk(ref s15, out _).UnpackNeeded());

        var c10 = ihdrChunk.AsSpan(0, 10).AsCursor();
        Assert.True(PngParser.TryParseChunk(ref c10, out _).IsBacktrack);
        var c15 = ihdrChunk.AsSpan(0, 15).AsCursor();
        Assert.True(PngParser.TryParseChunk(ref c15, out _).IsBacktrack);

        // Overflow length, CRC mismatch, invalid IHDR/PLTE/tEXt/IEND
        byte[] hugeLen = [0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0];
        var chHuge = hugeLen.AsCursor();
        Assert.True(PngParser.TryParseChunk(ref chHuge, out _).IsFailure);
        var chHugeShort = hugeLen.AsSpan(0, 5).AsCursor();
        Assert.True(PngParser.TryParseChunk(ref chHugeShort, out _).IsFailure);

        byte[] badCrc = (byte[])ihdrChunk.Clone();
        badCrc[^1] ^= 0xFF;
        var cBadCrc = badCrc.AsCursor();
        Assert.True(PngParser.TryParseChunk(ref cBadCrc, out _).IsFailure);

        byte[][] invalidChunks =
        [
            PngParser.BuildChunk("IHDR"u8, [1, 2, 3]), // wrong length
            PngParser.BuildChunk("IHDR"u8, new byte[13]), // width=0
            PngParser.BuildChunk("IHDR"u8, [0, 0, 0, 1, 0, 0, 0, 0, 8, 2, 0, 0, 0]), // height=0
            PngParser.BuildChunk("IHDR"u8, [0, 0, 0, 1, 0, 0, 0, 1, 8, 99, 0, 0, 0]), // bad color type
            PngParser.BuildChunk("PLTE"u8, []), // empty PLTE
            PngParser.BuildChunk("PLTE"u8, [1, 2]), // PLTE % 3 != 0
            PngParser.BuildChunk("tEXt"u8, "NoNull"u8), // missing null
            PngParser.BuildChunk("IEND"u8, [1]), // non-empty IEND
        ];
        foreach (byte[] bad in invalidChunks)
        {
            var cb = bad.AsCursor();
            Assert.True(PngParser.TryParseChunk(ref cb, out _).IsFailure);
            Assert.Equal(0, cb.Position);
        }
    }

    [Fact]
    public void WebSocket_UnmaskInto_Simd256_Simd128_Word_Tail_And_Offsets()
    {
        uint maskKey = 0x37FA_213Du;
        byte[] maskBytes = [0x37, 0xFA, 0x21, 0x3D];
        int[] lengths = [0, 1, 3, 4, 7, 8, 13, 15, 16, 17, 31, 32, 33, 48, 63, 64, 65, 99, 256, 1024];

        foreach (int len in lengths)
        {
            byte[] src = new byte[len];
            for (int i = 0; i < len; i++) src[i] = (byte)(i * 29 + 5);

            for (int offset = 0; offset < 4; offset++)
            {
                byte[] dst = new byte[len];
                WebSocketFrameParser.UnmaskInto(src, new ByteSlice(0, len), Option<uint>.Of(maskKey), dst, maskOffset: offset);
                for (int i = 0; i < len; i++)
                {
                    byte expected = (byte)(src[i] ^ maskBytes[(i + offset) & 3]);
                    Assert.Equal(expected, dst[i]);
                }
            }
        }
    }

    [Fact]
    public void ByteCursor_TryPrimitives_And_ByteWriter_TryReserve_And_LengthPrefixed()
    {
        Span<byte> buf = stackalloc byte[128];
        var w = buf.AsWriter(Endianness.Big);
        Assert.False(w.TryReserve(-1, out _));
        Assert.False(w.TryReserve(129, out _));
        Assert.True(w.TryReserve(4, out Span<byte> r4));
        r4[0] = 0x11; r4[1] = 0x22; r4[2] = 0x33; r4[3] = 0x44;
        Assert.True(w.TryWriteI8(-9));
        Assert.True(w.TryWriteBool(true));
        Assert.True(w.TryWriteU16LE(0x1234));
        Assert.True(w.TryWriteI16BE(-500));
        Assert.True(w.TryWriteI16LE(-600));
        Assert.True(w.TryWriteU32LE(0xAABBCCDDu));
        Assert.True(w.TryWriteI32BE(-100000));
        Assert.True(w.TryWriteI32LE(-200000));
        Assert.True(w.TryWriteU64BE(0x0102030405060708UL));
        Assert.True(w.TryWriteU64LE(0x1112131415161718UL));
        Assert.True(w.TryWriteI64BE(-123456789012L));
        Assert.True(w.TryWriteI64LE(-987654321098L));
        Assert.True(w.TryWriteF32BE(1.25f));
        Assert.True(w.TryWriteF32LE(-2.5f));
        Assert.True(w.TryWriteF64BE(3.5));
        Assert.True(w.TryWriteF64LE(-4.25));
        Assert.True(w.TryWriteVarInt64(-777777L));
        Assert.True(w.TryWriteQuicVarInt64(0x12));
        Assert.True(w.TryWriteQuicVarInt64(0x1234));
        Assert.True(w.TryWriteQuicVarInt64(0x1234_5678));
        Assert.True(w.TryWriteQuicVarInt64(0x1234_5678_9ABC_DEF0UL));
        Assert.True(w.TryWriteLengthPrefixedU8("ab"u8));
        Assert.True(w.TryWriteLengthPrefixedU16BE("cde"u8));
        Assert.True(w.TryWriteLengthPrefixedU16LE("fghi"u8));

        var c = w.Written.AsCursor();
        Assert.True(c.TryTag([0x11, 0x22]).IsParsed);
        Assert.True(c.TryTakeSpan(2, out var sp2).IsParsed && sp2[0] == 0x33 && sp2[1] == 0x44);
        Assert.True(c.TryReadI8(out sbyte i8).IsParsed && i8 == -9);
        Assert.True(c.TryReadBool(out bool b).IsParsed && b);
        Assert.True(c.TryReadU16LE(out ushort u16le).IsParsed && u16le == 0x1234);
        Assert.True(c.TryReadI16BE(out short i16be).IsParsed && i16be == -500);
        Assert.True(c.TryReadI16LE(out short i16le).IsParsed && i16le == -600);
        Assert.True(c.TryReadU32LE(out uint u32le).IsParsed && u32le == 0xAABBCCDDu);
        Assert.True(c.TryReadI32BE(out int i32be).IsParsed && i32be == -100000);
        Assert.True(c.TryReadI32LE(out int i32le).IsParsed && i32le == -200000);
        Assert.True(c.TryReadU64BE(out ulong u64be).IsParsed && u64be == 0x0102030405060708UL);
        Assert.True(c.TryReadU64LE(out ulong u64le).IsParsed && u64le == 0x1112131415161718UL);
        Assert.True(c.TryReadI64BE(out long i64be).IsParsed && i64be == -123456789012L);
        Assert.True(c.TryReadI64LE(out long i64le).IsParsed && i64le == -987654321098L);
        Assert.True(c.TryReadF32BE(out float f32be).IsParsed && f32be == 1.25f);
        Assert.True(c.TryReadF32LE(out float f32le).IsParsed && f32le == -2.5f);
        Assert.True(c.TryReadF64BE(out double f64be).IsParsed && f64be == 3.5);
        Assert.True(c.TryReadF64LE(out double f64le).IsParsed && f64le == -4.25);
        Assert.True(c.TryReadVarInt64(out long vi).IsParsed && vi == -777777L);
        Assert.True(c.TryReadQuicVarInt64(out ulong q1).IsParsed && q1 == 0x12);
        Assert.True(c.TryReadQuicVarInt64(out ulong q2).IsParsed && q2 == 0x1234);
        Assert.True(c.TryReadQuicVarInt64(out ulong q4).IsParsed && q4 == 0x1234_5678);
        Assert.True(c.TryReadQuicVarInt64(out ulong q8).IsParsed && q8 == 0x1234_5678_9ABC_DEF0UL);
        Assert.True(c.TryReadLengthPrefixedU8(out var s8).IsParsed && w.Written[s8].SequenceEqual("ab"u8));
        Assert.True(c.TryReadLengthPrefixedU16BE(out var s16b).IsParsed && w.Written[s16b].SequenceEqual("cde"u8));
        Assert.True(c.TryReadLengthPrefixedU16LE(out var s16l).IsParsed && w.Written[s16l].SequenceEqual("fghi"u8));
        Assert.True(c.IsAtEnd);

        // Edge cases on TryTag / TryTake / TryTakeSpan / TrySkip / TryReadQuicVarInt64 / TryReadVarUInt64 (9/10-byte & overflow)
        byte[] two = [0xAA, 0xBB];
        var cs = two.AsStreamingCursor();
        Assert.Equal(Needed.Bytes(2), cs.TryTag([0xAA, 0xBB, 0xCC, 0xDD]).UnpackNeeded());
        Assert.True(cs.TryTag([0xAA, 0x99, 0xCC]).IsBacktrack);
        Assert.True(cs.TryTag([0xAA, 0x99]).IsBacktrack);
        Assert.True(cs.TryTake(-1, out _).IsBacktrack);
        Assert.True(cs.TryTakeSpan(-1, out _).IsBacktrack);
        Assert.True(cs.TrySkip(-1).IsBacktrack);
        Assert.Equal(Needed.Bytes(3), cs.TryTake(5, out _).UnpackNeeded());
        Assert.Equal(Needed.Bytes(3), cs.TryTakeSpan(5, out _).UnpackNeeded());
        Assert.Equal(Needed.Bytes(3), cs.TrySkip(5).UnpackNeeded());
        Assert.True(cs.TrySkip(1).IsParsed && cs.Position == 1);
        Assert.True(cs.TryTake(1, out var tk1).IsParsed && tk1.Offset == 1 && tk1.Length == 1);

        // Shortage tests for LE readers, length-prefixed, and QUIC varint
        byte[] oneByte = [0x80]; // 4-byte QUIC prefix with only 1 byte
        var cq = oneByte.AsStreamingCursor();
        Assert.Equal(Needed.Bytes(3), cq.TryReadQuicVarInt64(out _).UnpackNeeded());
        var cq0 = ReadOnlySpan<byte>.Empty.AsStreamingCursor();
        Assert.Equal(Needed.Bytes(1), cq0.TryReadQuicVarInt64(out _).UnpackNeeded());
        Assert.Equal(Needed.Bytes(2), cq0.TryReadU16LE(out _).UnpackNeeded());
        Assert.Equal(Needed.Bytes(4), cq0.TryReadU32LE(out _).UnpackNeeded());
        Assert.Equal(Needed.Bytes(8), cq0.TryReadU64BE(out _).UnpackNeeded());
        Assert.Equal(Needed.Bytes(8), cq0.TryReadU64LE(out _).UnpackNeeded());
        Assert.Equal(Needed.Bytes(1), cq0.TryReadLengthPrefixedU8(out _).UnpackNeeded());
        Assert.Equal(Needed.Bytes(2), cq0.TryReadLengthPrefixedU16LE(out _).UnpackNeeded());
        byte[] truncU8 = [0x05, 0x01];
        var ctu8 = truncU8.AsStreamingCursor();
        Assert.Equal(Needed.Bytes(4), ctu8.TryReadLengthPrefixedU8(out _).UnpackNeeded());
        byte[] truncU16Le = [0x05, 0x00, 0x01];
        var ctu16 = truncU16Le.AsStreamingCursor();
        Assert.Equal(Needed.Bytes(4), ctu16.TryReadLengthPrefixedU16LE(out _).UnpackNeeded());

        // 9-byte, 10-byte, and overflowing LEB128 via TryReadVarUInt64 (fast >= 10 and slow < 10 paths)
        byte[] var10Max = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01];
        var cv10 = var10Max.AsCursor();
        Assert.True(cv10.TryReadVarUInt64(out ulong maxV).IsParsed && maxV == ulong.MaxValue);
        byte[] var10Bad = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x02];
        var cv10b = var10Bad.AsCursor();
        Assert.True(cv10b.TryReadVarUInt64(out _).IsBacktrack && cv10b.Position == 0);
        byte[] var9 = [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01, 0x00];
        var cv9 = var9.AsCursor();
        Assert.True(cv9.TryReadVarUInt64(out ulong v9).IsParsed && v9 == (1UL << 56));
        byte[] varSlowTrunc = [0x80, 0x80];
        var cvs = varSlowTrunc.AsStreamingCursor();
        Assert.True(cvs.TryReadVarUInt64(out _).IsIncomplete && cvs.Position == 0);
        var cvc = varSlowTrunc.AsCursor();
        Assert.True(cvc.TryReadVarUInt64(out _).IsBacktrack && cvc.Position == 0);
    }

    [Fact]
    public void Http2FrameParser_TryParseFrame_All11FrameTypes_And_ErrorGuards()
    {
        var cp = Http2FrameParser.ClientConnectionPreface.AsCursor();
        Assert.True(Http2FrameParser.TryParseClientPreface(ref cp).IsParsed && cp.IsAtEnd);

        byte[][] validFrames =
        [
            Http2FrameParser.BuildFrame(Http2FrameType.Data, Http2FrameFlags.None, 1, "raw"u8),
            Http2FrameParser.BuildFrame(Http2FrameType.Data, Http2FrameFlags.Padded | Http2FrameFlags.EndStream, 3, [2, (byte)'O', (byte)'K', 0, 0]),
            Http2FrameParser.BuildFrame(Http2FrameType.Headers, Http2FrameFlags.EndHeaders, 5, [0x82]),
            Http2FrameParser.BuildFrame(Http2FrameType.Headers, Http2FrameFlags.Padded | Http2FrameFlags.Priority | Http2FrameFlags.EndHeaders, 7, [1, 0x80, 0, 0, 5, 16, 0x82, 0]),
            Http2FrameParser.BuildFrame(Http2FrameType.Priority, Http2FrameFlags.None, 9, [0x80, 0, 0, 1, 200]),
            Http2FrameParser.BuildFrame(Http2FrameType.RstStream, Http2FrameFlags.None, 9, [0, 0, 0, 8]),
            Http2FrameParser.BuildFrame(Http2FrameType.Settings, Http2FrameFlags.None, 0, [0, 3, 0, 0, 0, 100]),
            Http2FrameParser.BuildFrame(Http2FrameType.Settings, Http2FrameFlags.Ack, 0, []),
            Http2FrameParser.BuildFrame(Http2FrameType.PushPromise, Http2FrameFlags.Padded | Http2FrameFlags.EndHeaders, 1, [1, 0, 0, 0, 4, 0x82, 0]),
            Http2FrameParser.BuildFrame(Http2FrameType.Ping, Http2FrameFlags.Ack, 0, [1, 2, 3, 4, 5, 6, 7, 8]),
            Http2FrameParser.BuildFrame(Http2FrameType.GoAway, Http2FrameFlags.None, 0, [0, 0, 0, 11, 0, 0, 0, 2, (byte)'x']),
            Http2FrameParser.BuildFrame(Http2FrameType.WindowUpdate, Http2FrameFlags.None, 0, [0, 0, 0x10, 0]),
            Http2FrameParser.BuildFrame(Http2FrameType.Continuation, Http2FrameFlags.EndHeaders, 7, [0x82]),
            Http2FrameParser.BuildFrame((Http2FrameType)0x7F, Http2FrameFlags.None, 5, [1, 2]),
        ];

        foreach (byte[] wire in validFrames)
        {
            var c1 = wire.AsCursor();
            var c2 = wire.AsCursor();
            Assert.True(Http2FrameParser.ParseFrame(ref c1).TryGetValue(out Parsed<Http2Frame> p1));
            Assert.True(Http2FrameParser.TryParseFrame(ref c2, out Http2Frame f2).IsParsed);
            Assert.Equal(c1.Position, c2.Position);
            Assert.Equal(p1.Value.Value, f2.Value);
        }

        // Streaming shortages
        var h2ShortHdr = validFrames[0].AsSpan(0, 4).AsStreamingCursor();
        Assert.Equal(Needed.Bytes(5), Http2FrameParser.TryParseFrame(ref h2ShortHdr, out _).UnpackNeeded());
        var h2ShortHdrC = validFrames[0].AsSpan(0, 4).AsCursor();
        Assert.True(Http2FrameParser.TryParseFrame(ref h2ShortHdrC, out _).IsBacktrack);

        var h2ShortPay = validFrames[0].AsSpan(0, 10).AsStreamingCursor();
        Assert.Equal(Needed.Bytes(2), Http2FrameParser.TryParseFrame(ref h2ShortPay, out _).UnpackNeeded());
        var h2ShortPayC = validFrames[0].AsSpan(0, 10).AsCursor();
        Assert.True(Http2FrameParser.TryParseFrame(ref h2ShortPayC, out _).IsBacktrack);

        // maxFrameSize guard
        var h2OverMax = validFrames[6].AsCursor();
        Assert.True(Http2FrameParser.TryParseFrame(ref h2OverMax, out _, maxFrameSize: 4).IsFailure);

        // All RFC 9113 error guards on TryParseFrame
        byte[][] invalidFrames =
        [
            Http2FrameParser.BuildFrame(Http2FrameType.Data, Http2FrameFlags.None, 0, [1]),
            Http2FrameParser.BuildFrame(Http2FrameType.Data, Http2FrameFlags.Padded, 1, []),
            Http2FrameParser.BuildFrame(Http2FrameType.Data, Http2FrameFlags.Padded, 1, [5, 1, 2]),
            Http2FrameParser.BuildFrame(Http2FrameType.Headers, Http2FrameFlags.None, 0, [0x82]),
            Http2FrameParser.BuildFrame(Http2FrameType.Headers, Http2FrameFlags.Padded, 1, []),
            Http2FrameParser.BuildFrame(Http2FrameType.Headers, Http2FrameFlags.Priority, 1, [1, 2, 3]),
            Http2FrameParser.BuildFrame(Http2FrameType.Headers, Http2FrameFlags.Padded, 1, [3, 0x82]),
            Http2FrameParser.BuildFrame(Http2FrameType.Priority, Http2FrameFlags.None, 0, [0, 0, 0, 1, 1]),
            Http2FrameParser.BuildFrame(Http2FrameType.Priority, Http2FrameFlags.None, 1, [1, 2]),
            Http2FrameParser.BuildFrame(Http2FrameType.RstStream, Http2FrameFlags.None, 0, [0, 0, 0, 1]),
            Http2FrameParser.BuildFrame(Http2FrameType.RstStream, Http2FrameFlags.None, 1, [1, 2]),
            Http2FrameParser.BuildFrame(Http2FrameType.Settings, Http2FrameFlags.None, 1, []),
            Http2FrameParser.BuildFrame(Http2FrameType.Settings, Http2FrameFlags.Ack, 0, [0, 0, 0, 0, 0, 0]),
            Http2FrameParser.BuildFrame(Http2FrameType.Settings, Http2FrameFlags.None, 0, [1, 2, 3]),
            Http2FrameParser.BuildFrame(Http2FrameType.PushPromise, Http2FrameFlags.None, 0, [0, 0, 0, 2]),
            Http2FrameParser.BuildFrame(Http2FrameType.PushPromise, Http2FrameFlags.Padded, 1, []),
            Http2FrameParser.BuildFrame(Http2FrameType.PushPromise, Http2FrameFlags.None, 1, [1, 2]),
            Http2FrameParser.BuildFrame(Http2FrameType.Ping, Http2FrameFlags.None, 1, [1, 2, 3, 4, 5, 6, 7, 8]),
            Http2FrameParser.BuildFrame(Http2FrameType.Ping, Http2FrameFlags.None, 0, [1, 2]),
            Http2FrameParser.BuildFrame(Http2FrameType.GoAway, Http2FrameFlags.None, 1, [0, 0, 0, 1, 0, 0, 0, 0]),
            Http2FrameParser.BuildFrame(Http2FrameType.GoAway, Http2FrameFlags.None, 0, [1, 2, 3]),
            Http2FrameParser.BuildFrame(Http2FrameType.WindowUpdate, Http2FrameFlags.None, 0, [1, 2]),
            Http2FrameParser.BuildFrame(Http2FrameType.WindowUpdate, Http2FrameFlags.None, 0, [0, 0, 0, 0]),
            Http2FrameParser.BuildFrame(Http2FrameType.Continuation, Http2FrameFlags.EndHeaders, 0, [0x82]),
        ];

        foreach (byte[] bad in invalidFrames)
        {
            var cb = bad.AsCursor();
            Assert.True(Http2FrameParser.TryParseFrame(ref cb, out _).IsFailure);
            Assert.Equal(0, cb.Position);
        }
    }

    [Fact]
    public void MqttV5Parser_TryReadVariableByteInt_And_TryParsePacket_AllTypes_And_Errors()
    {
        // 1, 2, 3, 4 byte VBI roundtrips + shortages + >4-byte failure
        int[] vbiValues = [0, 127, 128, 16_383, 16_384, 2_097_151, 2_097_152, MqttV5Parser.MaxVariableByteInt];
        foreach (int expected in vbiValues)
        {
            List<byte> enc = [];
            MqttV5Parser.WriteVariableByteInt(enc, expected);
            byte[] arr = enc.ToArray();
            var c = arr.AsCursor();
            Assert.True(MqttV5Parser.TryReadVariableByteInt(ref c, out int val).IsParsed && val == expected && c.IsAtEnd);

            for (int trunc = 0; trunc < arr.Length; trunc++)
            {
                var cs = arr.AsSpan(0, trunc).AsStreamingCursor();
                Assert.True(MqttV5Parser.TryReadVariableByteInt(ref cs, out _).IsIncomplete);
                var cc = arr.AsSpan(0, trunc).AsCursor();
                Assert.True(MqttV5Parser.TryReadVariableByteInt(ref cc, out _).IsBacktrack);
            }
        }

        byte[] badVbi = [0x80, 0x80, 0x80, 0x80, 0x01];
        var cbv = badVbi.AsCursor();
        Assert.True(MqttV5Parser.TryReadVariableByteInt(ref cbv, out _).IsFailure && cbv.Position == 0);

        byte[] connectBody = [0, 4, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 5, 2, 0, 60, 0, 0, 3, (byte)'c', (byte)'l', (byte)'1'];
        byte[][] validPackets =
        [
            MqttV5Parser.BuildPacket(MqttPacketType.Connect, 0, connectBody),
            MqttV5Parser.BuildPacket(MqttPacketType.ConnAck, 0, [1, 0, 0]),
            MqttV5Parser.BuildPublish("t/1"u8, "hello"u8, qos: 0, retain: true),
            MqttV5Parser.BuildPublish("t/2"u8, "world"u8, qos: 1, packetId: 9, dup: true, properties: [1, 1]),
            MqttV5Parser.BuildPublish("t/3"u8, "bigprops"u8, qos: 0, properties: new byte[130]), // 2-byte VBI properties!
            MqttV5Parser.BuildPacket(MqttPacketType.PubAck, 0, [0, 42]),
            MqttV5Parser.BuildPacket(MqttPacketType.PubAck, 0, [0, 42, 0x10, 0]),
            MqttV5Parser.BuildPacket(MqttPacketType.Subscribe, 2, [0, 7, 0, 0, 1, (byte)'#', 1]),
            MqttV5Parser.BuildPacket(MqttPacketType.SubAck, 0, [0, 7, 0, 1]),
            [0xC0, 0x00], // PingReq
            [0xD0, 0x00], // PingResp
            [0xE0, 0x00], // Disconnect compact
            MqttV5Parser.BuildPacket(MqttPacketType.Disconnect, 0, [0x04, 0]),
        ];

        foreach (byte[] pkt in validPackets)
        {
            var c1 = pkt.AsCursor();
            var c2 = pkt.AsCursor();
            Assert.True(MqttV5Parser.ParsePacket(ref c1).TryGetValue(out Parsed<MqttPacket> p1));
            Assert.True(MqttV5Parser.TryParsePacket(ref c2, out MqttPacket p2).IsParsed);
            Assert.Equal(c1.Position, c2.Position);
            Assert.Equal(p1.Value.Value, p2.Value);
        }

        // Empty / truncated header / truncated body
        var e0 = ReadOnlySpan<byte>.Empty.AsStreamingCursor();
        Assert.True(MqttV5Parser.TryParsePacket(ref e0, out _).IsIncomplete);
        var e0c = ReadOnlySpan<byte>.Empty.AsCursor();
        Assert.True(MqttV5Parser.TryParsePacket(ref e0c, out _).IsBacktrack);
        var truncBodyS = validPackets[2].AsSpan(0, validPackets[2].Length - 1).AsStreamingCursor();
        Assert.Equal(Needed.Bytes(1), MqttV5Parser.TryParsePacket(ref truncBodyS, out _).UnpackNeeded());
        var truncBodyC = validPackets[2].AsSpan(0, validPackets[2].Length - 1).AsCursor();
        Assert.True(MqttV5Parser.TryParsePacket(ref truncBodyC, out _).IsBacktrack);

        // Error guards on TryParsePacket
        byte[][] badPackets =
        [
            MqttV5Parser.BuildPacket(MqttPacketType.Connect, 1, connectBody),
            MqttV5Parser.BuildPacket(MqttPacketType.Connect, 0, [0, 4, (byte)'B', (byte)'A', (byte)'D', (byte)'!', 5, 0, 0, 10, 0]),
            MqttV5Parser.BuildPacket(MqttPacketType.Connect, 0, [0, 4, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 4, 0, 0, 10, 0]),
            MqttV5Parser.BuildPacket(MqttPacketType.Connect, 0, [0, 4, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 5, 1, 0, 10, 0]),
            MqttV5Parser.BuildPacket(MqttPacketType.Connect, 0, [0, 4, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 5]),
            MqttV5Parser.BuildPacket(MqttPacketType.Connect, 0, [0, 4, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 5, 0, 0, 10, 5]),
            MqttV5Parser.BuildPacket(MqttPacketType.ConnAck, 1, [0, 0, 0]),
            MqttV5Parser.BuildPacket(MqttPacketType.ConnAck, 0, [0]),
            MqttV5Parser.BuildPacket(MqttPacketType.ConnAck, 0, [2, 0, 0]),
            MqttV5Parser.BuildPacket(MqttPacketType.ConnAck, 0, [0, 0, 0, 99]),
            MqttV5Parser.BuildPacket(MqttPacketType.ConnAck, 0, [0, 0, 5]),
            MqttV5Parser.BuildPacket(MqttPacketType.Publish, 6, [0, 1, (byte)'a', 0]),
            MqttV5Parser.BuildPacket(MqttPacketType.Publish, 8, [0, 1, (byte)'a', 0]),
            MqttV5Parser.BuildPacket(MqttPacketType.Publish, 0, [0]),
            MqttV5Parser.BuildPacket(MqttPacketType.Publish, 0, [0, 5, (byte)'a']),
            MqttV5Parser.BuildPacket(MqttPacketType.Publish, 2, [0, 1, (byte)'a']),
            MqttV5Parser.BuildPacket(MqttPacketType.Publish, 2, [0, 1, (byte)'a', 0, 0, 0]),
            MqttV5Parser.BuildPacket(MqttPacketType.Publish, 0, [0, 1, (byte)'a', 5]),
            MqttV5Parser.BuildPacket(MqttPacketType.PubAck, 1, [0, 1]),
            MqttV5Parser.BuildPacket(MqttPacketType.PubAck, 0, [0, 0]),
            MqttV5Parser.BuildPacket(MqttPacketType.PubAck, 0, [0, 1, 0, 0, 99]),
            MqttV5Parser.BuildPacket(MqttPacketType.PubAck, 0, [0, 1, 0, 5]),
            MqttV5Parser.BuildPacket(MqttPacketType.Subscribe, 0, [0, 1, 0, 0, 1, (byte)'a', 0]),
            MqttV5Parser.BuildPacket(MqttPacketType.Subscribe, 2, [0, 0, 0, 0, 1, (byte)'a', 0]),
            MqttV5Parser.BuildPacket(MqttPacketType.Subscribe, 2, [0, 1, 0]),
            MqttV5Parser.BuildPacket(MqttPacketType.Subscribe, 2, [0, 1, 5]),
            MqttV5Parser.BuildPacket(MqttPacketType.SubAck, 1, [0, 1, 0, 0]),
            MqttV5Parser.BuildPacket(MqttPacketType.SubAck, 0, [0, 0, 0, 0]),
            MqttV5Parser.BuildPacket(MqttPacketType.SubAck, 0, [0, 1, 0]),
            MqttV5Parser.BuildPacket(MqttPacketType.SubAck, 0, [0, 1, 5]),
            [0xC0, 0x01, 0x00],
            [0xD0, 0x01, 0x00],
            MqttV5Parser.BuildPacket(MqttPacketType.Disconnect, 1, []),
            MqttV5Parser.BuildPacket(MqttPacketType.Disconnect, 0, [0, 0, 99]),
            MqttV5Parser.BuildPacket(MqttPacketType.Disconnect, 0, [0, 5]),
            [0x00, 0x00],
        ];

        foreach (byte[] bad in badPackets)
        {
            var cb = bad.AsCursor();
            Assert.True(MqttV5Parser.TryParsePacket(ref cb, out _).IsFailure);
            Assert.Equal(0, cb.Position);
        }
    }

    [Fact]
    public void BinaryStruct_CoalescedTryWrite_And_TryParse_AllPrimitives_Roundtrip()
    {
        byte[] src = "XYZhelloWORLD"u8.ToArray();
        var item = new CoalescedAllPrimitivesStruct(
            Flag: true,
            U8: 0xAB,
            I8: -42,
            U16Be: 0x1234,
            U16Le: 0x5678,
            I16Be: -1234,
            I16Le: -5678,
            U32Be: 0x11223344u,
            U32Le: 0x55667788u,
            I32Be: -100_000,
            I32Le: -200_000,
            U64Be: 0x0102030405060708UL,
            U64Le: 0x1112131415161718UL,
            I64Be: -1234567890123L,
            I64Le: -9876543210987L,
            F32Be: 1.5f,
            F32Le: -2.25f,
            F64Be: 3.14159265358979,
            F64Le: -2.71828182845904,
            VarU: 300_000UL,
            VarI: -555_555L,
            QuicV: 0x1234_5678UL,
            Fixed3: new ByteSlice(0, 3),
            LpU8: new ByteSlice(3, 5),
            LpU16Le: new ByteSlice(8, 5));

        Span<byte> wire = stackalloc byte[256];
        wire.Fill(0xFF);
        int written = item.TryWriteTo(wire, src);
        Assert.True(written > 80);

        ReadOnlySpan<byte> ro = wire[..written];
        var c1 = ro.AsCursor();
        var c2 = ro.AsCursor();
        var parsed1 = CoalescedAllPrimitivesStruct.Parse(ref c1).Unwrap();
        Assert.True(CoalescedAllPrimitivesStruct.TryParse(ref c2, out var parsed2).IsParsed);
        Assert.Equal(c1.Position, c2.Position);
        Assert.True(c2.IsAtEnd);
        Assert.Equal(parsed1.Flag, parsed2.Flag);
        Assert.Equal(parsed1.U16Le, parsed2.U16Le);
        Assert.Equal(parsed1.I64Le, parsed2.I64Le);
        Assert.Equal(parsed1.F64Le, parsed2.F64Le);
        Assert.Equal(parsed1.VarU, parsed2.VarU);
        Assert.Equal(parsed1.VarI, parsed2.VarI);
        Assert.Equal(parsed1.QuicV, parsed2.QuicV);
        Assert.SequenceEqual<byte>("XYZ"u8, ro[parsed2.Fixed3]);
        Assert.SequenceEqual<byte>("hello"u8, ro[parsed2.LpU8]);
        Assert.SequenceEqual<byte>("WORLD"u8, ro[parsed2.LpU16Le]);

        // Undersized write rolls back
        for (int len = 0; len < written; len++)
        {
            Assert.Equal(-1, item.TryWriteTo(wire[..len], src));
        }
    }

    [Fact]
    public void BinaryUnion_DirectTryParse_AllTagWidths_And_Rollback()
    {
        Span<byte> buf = stackalloc byte[64];

        // U8 + magic + nested struct + bool + ushort
        GeneratedWireMessage[] msgs =
        [
            new UnionPingPayload(0x11223344u, 999UL),
            (ushort)0xBEEF,
            true,
            false,
        ];
        foreach (var m in msgs)
        {
            int n = m.TryWriteTo(buf);
            var c = ((ReadOnlySpan<byte>)buf[..n]).AsCursor();
            Assert.True(GeneratedWireMessage.TryParse(ref c, out var decoded).IsParsed && c.IsAtEnd);
            Assert.Equal(m.Value, decoded.Value);
        }

        // U16 LE union direct TryParse
        GeneratedU16LeUnion u16 = 7.25;
        int n16 = u16.TryWriteTo(buf);
        var c16 = ((ReadOnlySpan<byte>)buf[..n16]).AsCursor();
        Assert.True(GeneratedU16LeUnion.TryParse(ref c16, out var d16).IsParsed && d16.TryGetValue(out double dv) && dv == 7.25);

        // U32 BE union direct TryParse
        GeneratedU32BeUnion u32 = -12345L;
        int n32 = u32.TryWriteTo(buf);
        var c32 = ((ReadOnlySpan<byte>)buf[..n32]).AsCursor();
        Assert.True(GeneratedU32BeUnion.TryParse(ref c32, out var d32).IsParsed && d32.TryGetValue(out long lv) && lv == -12345L);

        // Unknown tag and truncated magic rollback
        byte[] badTag = [0x55, 0x4E, 0x99, 0x00];
        var cBad = badTag.AsCursor();
        Assert.True(GeneratedWireMessage.TryParse(ref cBad, out _).IsBacktrack && cBad.Position == 0);
        var cShort = badTag.AsSpan(0, 1).AsStreamingCursor();
        Assert.True(GeneratedWireMessage.TryParse(ref cShort, out _).IsIncomplete && cShort.Position == 0);
    }

    [Fact]
    public void PipeSequenceAdapter_MultiSegment_StackAllocFastPath_And_ArrayPoolPath()
    {
        // 1. Small multi-segment sequence (<= 256 bytes -> stackalloc fast path)
        var smallSeq = BuildMultiSegment([0x00, 0x05], "hello"u8.ToArray());
        Assert.False(smallSeq.IsSingleSegment);
        Assert.True(smallSeq.Length <= PipeSequenceAdapter.StackAllocThreshold);

        Assert.True(PipeSequenceAdapter.TryParseSequence(
            in smallSeq,
            isCompleted: true,
            static (ref ByteCursor c) => c.ReadLengthPrefixedBytes(LengthPrefix.U16),
            out SequencePosition cons1,
            out ParseResult<ByteSlice> res1));
        Assert.True(res1.IsParsed && smallSeq.GetOffset(cons1) == 7);

        // Projected overload (stackalloc path)
        Assert.True(PipeSequenceAdapter.TryParseSequence(
            in smallSeq,
            isCompleted: true,
            static (ref ByteCursor c) => c.ReadLengthPrefixedBytes(LengthPrefix.U16),
            static (ByteSlice s, ReadOnlySpan<byte> frame) => frame[s].ToArray(),
            out SequencePosition cons2,
            out ParseResult<byte[]> res2));
        Assert.True(res2.TryGetValue(out Parsed<byte[]> ok2) && ok2.Value.AsSpan().SequenceEqual("hello"u8) && smallSeq.GetOffset(cons2) == 7);

        // Monomorphized ITryParser overload (stackalloc path + failure rollback)
        var smallU16 = BuildMultiSegment([0x12], [0x34]);
        Assert.True(PipeSequenceAdapter.TryParseSequence(
            in smallU16,
            isCompleted: true,
            TryParsers.U16BE,
            out SequencePosition cons3,
            out FastStatus st3,
            out ushort val3));
        Assert.True(st3.IsParsed && val3 == 0x1234 && smallU16.GetOffset(cons3) == 2);

        // Failure on small multi-segment leaves consumed at Start
        var smallFail = BuildMultiSegment([0x00], [0x09, 0x01]);
        Assert.False(PipeSequenceAdapter.TryParseSequence(
            in smallFail,
            isCompleted: false,
            static (ref ByteCursor c) => c.ReadLengthPrefixedBytes(LengthPrefix.U16),
            out SequencePosition consFail,
            out ParseResult<ByteSlice> resFail));
        Assert.True(resFail.IsIncomplete && consFail.Equals(smallFail.Start));

        Assert.False(PipeSequenceAdapter.TryParseSequence(
            in smallFail,
            isCompleted: false,
            static (ref ByteCursor c) => c.ReadLengthPrefixedBytes(LengthPrefix.U16),
            static (ByteSlice s, ReadOnlySpan<byte> frame) => frame[s].Length,
            out SequencePosition consFail2,
            out ParseResult<int> resFail2));
        Assert.True(resFail2.IsIncomplete && consFail2.Equals(smallFail.Start));

        Assert.False(PipeSequenceAdapter.TryParseSequence(
            in smallFail,
            isCompleted: false,
            TryParsers.U32BE,
            out SequencePosition consFail3,
            out FastStatus stFail3,
            out uint _));
        Assert.True(stFail3.IsIncomplete && consFail3.Equals(smallFail.Start));

        // 2. Large multi-segment sequence (> 256 bytes -> ArrayPool path)
        byte[] payload300 = new byte[300];
        for (int i = 0; i < 300; i++) payload300[i] = (byte)(i & 0xFF);
        var largeSeq = BuildMultiSegment([0x01, 0x2C], payload300); // 2 + 300 = 302 > 256
        Assert.True(largeSeq.Length > PipeSequenceAdapter.StackAllocThreshold);

        Assert.True(PipeSequenceAdapter.TryParseSequence(
            in largeSeq,
            isCompleted: true,
            static (ref ByteCursor c) => c.ReadLengthPrefixedBytes(LengthPrefix.U16),
            static (ByteSlice s, ReadOnlySpan<byte> frame) => frame[s].ToArray(),
            out SequencePosition consLarge,
            out ParseResult<byte[]> resLarge));
        Assert.True(resLarge.TryGetValue(out Parsed<byte[]> okLarge) && okLarge.Value.AsSpan().SequenceEqual(payload300) && largeSeq.GetOffset(consLarge) == 302);

        Assert.True(PipeSequenceAdapter.TryParseSequence(
            in largeSeq,
            isCompleted: true,
            TryParsers.U16BE,
            out SequencePosition consLargeTry,
            out FastStatus stLargeTry,
            out ushort hdrLen));
        Assert.True(stLargeTry.IsParsed && hdrLen == 300 && largeSeq.GetOffset(consLargeTry) == 2);
    }

    [Fact]
    public void WebSocketFrame_And_Protobuf_EdgeBranches_MutationKillers()
    {
        // Unmasked non-final Text/Binary/Continuation + Ping/Pong + Close (empty payload, unmasked, masked)
        byte[][] wsFrames =
        [
            [0x00, 0x02, (byte)'h', (byte)'i'],                         // Continuation, fin=false, unmasked
            [0x01, 0x02, (byte)'a', (byte)'b'],                         // Text, fin=false, unmasked
            [0x02, 0x03, 1, 2, 3],                                      // Binary, fin=false, unmasked
            [0x81, 0x03, (byte)'f', (byte)'o', (byte)'o'],              // Text, fin=true, unmasked
            [0x82, 0x02, 0xAA, 0xBB],                                   // Binary, fin=true, unmasked
            [0x88, 0x00],                                               // Close, fin=true, unmasked, no status code
            [0x88, 0x80, 1, 2, 3, 4],                                   // Close, fin=true, masked, no status code
            [0x89, 0x02, 0x11, 0x22],                                   // Ping, fin=true, unmasked
            [0x8A, 0x02, 0x33, 0x44],                                   // Pong, fin=true, unmasked
            [0x81, 0x80, 0xDE, 0xAD, 0xBE, 0xEF],                       // Text, fin=true, masked, 0-byte payload
        ];

        foreach (byte[] raw in wsFrames)
        {
            var c1 = raw.AsCursor();
            var c2 = raw.AsCursor();
            Assert.True(WebSocketFrameParser.ParseFrame(ref c1).TryGetValue(out Parsed<WebSocketFrame> p1));
            Assert.True(WebSocketFrameParser.TryParseFrame(ref c2, out WebSocketFrame f2).IsParsed);
            Assert.Equal(c1.Position, c2.Position);
            Assert.Equal(p1.Value.Value, f2.Value);

            bool expectFin = (raw[0] & 0x80) != 0;
            bool expectMasked = (raw[1] & 0x80) != 0;
            byte opcode = (byte)(raw[0] & 0x0F);
            switch (opcode)
            {
                case 0:
                    Assert.True(f2.TryGetValue(out ContinuationFrame cf));
                    Assert.Equal(expectFin, cf.Fin);
                    Assert.Equal(expectMasked, cf.MaskKey.TryGetValue(out Some<uint> _));
                    break;
                case 1:
                    Assert.True(f2.TryGetValue(out TextFrame tf));
                    Assert.Equal(expectFin, tf.Fin);
                    Assert.Equal(expectMasked, tf.MaskKey.TryGetValue(out Some<uint> _));
                    break;
                case 2:
                    Assert.True(f2.TryGetValue(out BinaryFrame bf));
                    Assert.Equal(expectFin, bf.Fin);
                    Assert.Equal(expectMasked, bf.MaskKey.TryGetValue(out Some<uint> _));
                    break;
                case 8:
                    Assert.True(f2.TryGetValue(out CloseFrame clf));
                    Assert.False(clf.StatusCode.TryGetValue(out Some<ushort> _));
                    Assert.Equal(expectMasked, clf.MaskKey.TryGetValue(out Some<uint> _));
                    break;
                case 9:
                    Assert.True(f2.TryGetValue(out PingFrame pf));
                    Assert.Equal(expectMasked, pf.MaskKey.TryGetValue(out Some<uint> _));
                    break;
                case 10:
                    Assert.True(f2.TryGetValue(out PongFrame pof));
                    Assert.Equal(expectMasked, pof.MaskKey.TryGetValue(out Some<uint> _));
                    break;
            }
        }

        // Unmasked UnmaskInto copies verbatim
        byte[] src = [10, 20, 30, 40, 50];
        byte[] dst = new byte[5];
        WebSocketFrameParser.UnmaskInto(src, new ByteSlice(0, 5), Option<uint>.Nothing, dst);
        Assert.True(dst.AsSpan().SequenceEqual(src));

        // Protobuf TryParseField Complete vs Streaming EOF
        var emptyComplete = ReadOnlySpan<byte>.Empty.AsCursor();
        Assert.True(ProtobufParser.TryParseField(ref emptyComplete, out _).IsBacktrack);
        var emptyStream = ReadOnlySpan<byte>.Empty.AsStreamingCursor();
        Assert.True(ProtobufParser.TryParseField(ref emptyStream, out _).IsIncomplete);
    }

    private sealed class TestSegment : ReadOnlySequenceSegment<byte>
    {
        public TestSegment(ReadOnlyMemory<byte> memory) => Memory = memory;
        public TestSegment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new TestSegment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }

    private static ReadOnlySequence<byte> BuildMultiSegment(params byte[][] chunks)
    {
        var first = new TestSegment(chunks[0]);
        var last = first;
        for (int i = 1; i < chunks.Length; i++) last = last.Append(chunks[i]);
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }
}
