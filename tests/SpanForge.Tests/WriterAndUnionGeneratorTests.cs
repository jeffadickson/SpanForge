using System.Buffers;
using System.Runtime.CompilerServices;
using CsCheck;
using Xunit;

namespace SpanForge.Tests;

[BinaryStruct(Endianness.Big)]
public readonly partial record struct UnionPingPayload(
    uint Nonce,
    ulong Timestamp);

[BinaryStruct(Endianness.Big)]
public readonly partial record struct UnionDataPayload(
    ushort Channel,
    [property: LengthPrefixed(LengthPrefix.U8)] ByteSlice Body);

[BinaryUnion(Endianness.Big, TagWidth.U8)]
[Magic(0x55, 0x4E)] // "UN"
[UnionCase(0x01, typeof(UnionPingPayload))]
[UnionCase(0x02, typeof(UnionDataPayload))]
[UnionCase(0x03, typeof(ushort))]
[UnionCase(0x04, typeof(bool))]
public readonly partial struct GeneratedWireMessage;

[BinaryUnion(Endianness.Little, TagWidth.U16)]
[UnionCase(0x1001, typeof(uint))]
[UnionCase(0x1002, typeof(double))]
public readonly partial struct GeneratedU16LeUnion;

[BinaryUnion(Endianness.Big, TagWidth.U32)]
[UnionCase(0xAABBCCDDu, typeof(long))]
[UnionCase(0x11223344u, typeof(UnionPingPayload))]
public readonly partial struct GeneratedU32BeUnion;

[BinaryStruct(Endianness.Big)]
public readonly partial record struct EnvelopeWithUnion(
    byte Version,
    GeneratedWireMessage Body);

public readonly struct BlittablePoint
{
    public readonly int X;
    public readonly int Y;
    public BlittablePoint(int x, int y) { X = x; Y = y; }
}

public sealed class WriterAndUnionGeneratorTests
{
    [Fact]
    public void ByteWriter_AllPrimitives_Endianness_And_Rollback()
    {
        byte[] buf = new byte[256];
        var w = buf.AsWriter(Endianness.Big);
        Assert.Equal(256, w.Length);
        Assert.Equal(256, w.RemainingLength);
        Assert.Equal(0, w.Position);
        Assert.Equal(Endianness.Big, w.Endianness);
        Assert.Equal(256, w.Buffer.Length);
        Assert.Equal(256, w.Remaining.Length);
        Assert.Equal(0, w.Written.Length);
        Assert.Equal(0, w.WrittenSpan.Length);

        Assert.True(w.TryWriteU8(0xAB));
        Assert.True(w.TryWriteI8(-42));
        Assert.True(w.TryWriteBool(true));
        Assert.True(w.TryWriteBool(false));
        Assert.True(w.TryWriteU16BE(0x1234));
        Assert.True(w.TryWriteU16LE(0x5678));
        Assert.True(w.TryWriteI16BE(-1234));
        Assert.True(w.TryWriteI16LE(-5678));
        Assert.True(w.TryWriteU32BE(0x11223344u));
        Assert.True(w.TryWriteU32LE(0x55667788u));
        Assert.True(w.TryWriteI32BE(-100_000));
        Assert.True(w.TryWriteI32LE(-200_000));
        Assert.True(w.TryWriteU64BE(0x0102030405060708UL));
        Assert.True(w.TryWriteU64LE(0x1112131415161718UL));
        Assert.True(w.TryWriteI64BE(-1234567890123L));
        Assert.True(w.TryWriteI64LE(-9876543210987L));
        Assert.True(w.TryWriteF32BE(1.5f));
        Assert.True(w.TryWriteF32LE(-2.25f));
        Assert.True(w.TryWriteF64BE(3.14159265358979));
        Assert.True(w.TryWriteF64LE(-2.71828182845904));

        // Scoped endianness writers (Big then Little)
        w.Endianness = Endianness.Big;
        Assert.True(w.TryWriteU16(0xBEEF));
        Assert.True(w.TryWriteI16(-321));
        Assert.True(w.TryWriteU32(0xCAFEBABEu));
        Assert.True(w.TryWriteI32(-999));
        Assert.True(w.TryWriteU64(0xDEAD_BEEF_CAFE_BABEUL));
        Assert.True(w.TryWriteI64(-777777L));
        Assert.True(w.TryWriteF32(6.5f));
        Assert.True(w.TryWriteF64(9.75));

        var cp = w.Save();
        w.Endianness = Endianness.Little;
        Assert.True(w.TryWriteU16(0xBEEF));
        Assert.True(w.TryWriteI16(-321));
        Assert.True(w.TryWriteU32(0xCAFEBABEu));
        Assert.True(w.TryWriteI32(-999));
        Assert.True(w.TryWriteU64(0xDEAD_BEEF_CAFE_BABEUL));
        Assert.True(w.TryWriteI64(-777777L));
        Assert.True(w.TryWriteF32(6.5f));
        Assert.True(w.TryWriteF64(9.75));
        int totalWritten = w.Position;
        Assert.Equal(totalWritten, w.Written.Length);
        Assert.Equal(totalWritten, w.WrittenSpan.Length);
        Assert.Equal(256 - totalWritten, w.RemainingLength);

        // Verify with ByteCursor roundtrip!
        var c = w.Written.AsCursor(Endianness.Big);
        Assert.Equal((byte)0xAB, c.ReadU8().Unwrap());
        Assert.Equal((sbyte)-42, c.ReadI8().Unwrap());
        Assert.True(c.ReadBool().Unwrap());
        Assert.False(c.ReadBool().Unwrap());
        Assert.Equal((ushort)0x1234, c.ReadU16BE().Unwrap());
        Assert.Equal((ushort)0x5678, c.ReadU16LE().Unwrap());
        Assert.Equal((short)-1234, c.ReadI16BE().Unwrap());
        Assert.Equal((short)-5678, c.ReadI16LE().Unwrap());
        Assert.Equal(0x11223344u, c.ReadU32BE().Unwrap());
        Assert.Equal(0x55667788u, c.ReadU32LE().Unwrap());
        Assert.Equal(-100_000, c.ReadI32BE().Unwrap());
        Assert.Equal(-200_000, c.ReadI32LE().Unwrap());
        Assert.Equal(0x0102030405060708UL, c.ReadU64BE().Unwrap());
        Assert.Equal(0x1112131415161718UL, c.ReadU64LE().Unwrap());
        Assert.Equal(-1234567890123L, c.ReadI64BE().Unwrap());
        Assert.Equal(-9876543210987L, c.ReadI64LE().Unwrap());
        Assert.Equal(1.5f, c.ReadF32BE().Unwrap());
        Assert.Equal(-2.25f, c.ReadF32LE().Unwrap());
        Assert.Equal(3.14159265358979, c.ReadF64BE().Unwrap());
        Assert.Equal(-2.71828182845904, c.ReadF64LE().Unwrap());

        c.Endianness = Endianness.Big;
        Assert.Equal((ushort)0xBEEF, c.ReadU16().Unwrap());
        Assert.Equal((short)-321, c.ReadI16().Unwrap());
        Assert.Equal(0xCAFEBABEu, c.ReadU32().Unwrap());
        Assert.Equal(-999, c.ReadI32().Unwrap());
        Assert.Equal(0xDEAD_BEEF_CAFE_BABEUL, c.ReadU64().Unwrap());
        Assert.Equal(-777777L, c.ReadI64().Unwrap());
        Assert.Equal(6.5f, c.ReadF32().Unwrap());
        Assert.Equal(9.75, c.ReadF64().Unwrap());

        c.Endianness = Endianness.Little;
        Assert.Equal((ushort)0xBEEF, c.ReadU16().Unwrap());
        Assert.Equal((short)-321, c.ReadI16().Unwrap());
        Assert.Equal(0xCAFEBABEu, c.ReadU32().Unwrap());
        Assert.Equal(-999, c.ReadI32().Unwrap());
        Assert.Equal(0xDEAD_BEEF_CAFE_BABEUL, c.ReadU64().Unwrap());
        Assert.Equal(-777777L, c.ReadI64().Unwrap());
        Assert.Equal(6.5f, c.ReadF32().Unwrap());
        Assert.Equal(9.75, c.ReadF64().Unwrap());
        Assert.True(c.IsAtEnd);

        // Save/Reset restores both Position and Endianness
        w.Reset(cp);
        Assert.Equal(cp.Position, w.Position);
        Assert.Equal(Endianness.Big, w.Endianness);

        // Position setter valid and out-of-range
        w.Position = 0;
        Assert.Equal(0, w.Position);
        w.Position = 256;
        Assert.Equal(256, w.Position);
        Xunit.Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            var local = new byte[4].AsWriter();
            local.Position = -1;
        });
        Xunit.Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            var local = new byte[4].AsWriter();
            local.Position = 5;
        });
    }

    [Fact]
    public void ByteWriter_InsufficientCapacity_AllMethods_ReturnFalse_WithoutAdvancing()
    {
        Span<byte> empty = [];
        var w0 = empty.AsWriter();
        Assert.False(w0.TryWriteU8(1));
        Assert.False(w0.TryWriteI8(1));
        Assert.False(w0.TryWriteBool(true));
        Assert.False(w0.TryWriteVarUInt64(0));
        Assert.False(w0.TryWriteVarInt64(0));
        Assert.False(w0.TryWriteQuicVarInt64(0));
        Assert.False(w0.TryWriteBytes([1]));
        Assert.False(w0.TryWritePad(1));
        Assert.False(w0.TryWriteBlittable(new BlittablePoint(1, 2)));
        Assert.Equal(0, w0.Position);

        Span<byte> one = stackalloc byte[1];
        var w1 = one.AsWriter();
        Assert.False(w1.TryWriteU16BE(1));
        Assert.False(w1.TryWriteU16LE(1));
        Assert.False(w1.TryWriteI16BE(1));
        Assert.False(w1.TryWriteI16LE(1));
        Assert.False(w1.TryWriteVarUInt64(128)); // needs 2 bytes -> must roll back!
        Assert.Equal(0, w1.Position);

        Span<byte> three = stackalloc byte[3];
        var w3 = three.AsWriter();
        Assert.False(w3.TryWriteU32BE(1));
        Assert.False(w3.TryWriteU32LE(1));
        Assert.False(w3.TryWriteF32BE(1f));
        Assert.False(w3.TryWriteF32LE(1f));
        Assert.False(w3.TryWriteVarUInt64(0x1000_0000UL)); // needs 5 bytes -> rolls back!
        Assert.Equal(0, w3.Position);

        Span<byte> seven = stackalloc byte[7];
        var w7 = seven.AsWriter();
        Assert.False(w7.TryWriteU64BE(1));
        Assert.False(w7.TryWriteU64LE(1));
        Assert.False(w7.TryWriteF64BE(1.0));
        Assert.False(w7.TryWriteF64LE(1.0));
        Assert.False(w7.TryWriteBlittable(new BlittablePoint(3, 4)));
        Assert.Equal(0, w7.Position);
    }

    [Fact]
    public void ByteWriter_VarInts_Quic_Pad_Align_LengthPrefixed_Blittable()
    {
        Span<byte> buf = stackalloc byte[128];
        var w = buf.AsWriter(Endianness.Big);

        // LEB128 boundary values
        ulong[] varValues = [0, 1, 127, 128, 16383, 16384, ulong.MaxValue];
        foreach (ulong v in varValues) Assert.True(w.TryWriteVarUInt64(v));

        // ZigZag boundary values
        long[] zzValues = [0, -1, 1, -2, 2, long.MinValue, long.MaxValue];
        foreach (long v in zzValues) Assert.True(w.TryWriteVarInt64(v));

        // QUIC varint 1, 2, 4, 8-byte boundaries
        ulong[] quicValues = [0, 0x3FUL, 0x40UL, 0x3FFFUL, 0x4000UL, 0x3FFF_FFFFUL, 0x4000_0000UL, 0x3FFF_FFFF_FFFF_FFFFUL];
        foreach (ulong v in quicValues) Assert.True(w.TryWriteQuicVarInt64(v));
        Assert.False(w.TryWriteQuicVarInt64(0x4000_0000_0000_0000UL)); // exceeds 62-bit QUIC max!

        var c = w.Written.AsCursor();
        foreach (ulong v in varValues) Assert.Equal(v, c.ReadVarUInt64().Unwrap());
        foreach (long v in zzValues) Assert.Equal(v, c.ReadVarInt64().Unwrap());
        foreach (ulong v in quicValues) Assert.Equal(v, c.ReadQuicVarInt64().Unwrap());
        Assert.True(c.IsAtEnd);

        // Pad, Align, LengthPrefixed, Blittable
        Span<byte> buf2 = stackalloc byte[64];
        buf2.Fill(0xEE);
        var w2 = buf2.AsWriter(Endianness.Big);
        Assert.False(w2.TryWritePad(-1));
        Assert.True(w2.TryWriteU8(0x11));
        Assert.False(w2.TryAlign(0));
        Assert.False(w2.TryAlign(-4));
        Assert.False(w2.TryAlign(3));
        Assert.True(w2.TryAlign(4, 0xCC)); // writes 3 bytes of 0xCC -> pos=4
        Assert.Equal(4, w2.Position);
        Assert.True(w2.TryAlign(4, 0xDD)); // already aligned -> writes 0 bytes
        Assert.Equal(4, w2.Position);
        Assert.True(w2.TryWritePad(2, 0xAA)); // pos=6
        Assert.Equal(6, w2.Position);

        Assert.True(w2.TryWriteLengthPrefixedBytes(LengthPrefix.U8, "hi"u8));
        Assert.True(w2.TryWriteLengthPrefixedBytes(LengthPrefix.U16, "span"u8));
        Assert.True(w2.TryWriteLengthPrefixedBytes(LengthPrefix.U32, "forge"u8));
        Assert.True(w2.TryWriteLengthPrefixedBytes(LengthPrefix.VarUInt64, "ok"u8));
        Assert.False(w2.TryWriteLengthPrefixedBytes((LengthPrefix)99, "bad"u8));
        Assert.True(w2.TryWriteBlittable(new BlittablePoint(42, -99)));

        var c2 = w2.Written.AsCursor();
        Assert.Equal((byte)0x11, c2.ReadU8().Unwrap());
        Assert.Equal((byte)0xCC, c2.ReadU8().Unwrap());
        Assert.Equal((byte)0xCC, c2.ReadU8().Unwrap());
        Assert.Equal((byte)0xCC, c2.ReadU8().Unwrap());
        Assert.Equal((byte)0xAA, c2.ReadU8().Unwrap());
        Assert.Equal((byte)0xAA, c2.ReadU8().Unwrap());
        Assert.SequenceEqual<byte>("hi"u8, c2[c2.ReadLengthPrefixedBytes(LengthPrefix.U8).Unwrap()]);
        Assert.SequenceEqual<byte>("span"u8, c2[c2.ReadLengthPrefixedBytes(LengthPrefix.U16).Unwrap()]);
        Assert.SequenceEqual<byte>("forge"u8, c2[c2.ReadLengthPrefixedBytes(LengthPrefix.U32).Unwrap()]);
        Assert.SequenceEqual<byte>("ok"u8, c2[c2.ReadLengthPrefixedBytes(LengthPrefix.VarUInt64).Unwrap()]);
        var pt = c2.ReadBlittable<BlittablePoint>().Unwrap();
        Assert.Equal(42, pt.X);
        Assert.Equal(-99, pt.Y);
        Assert.True(c2.IsAtEnd);

        // LengthPrefixed overflow & insufficient capacity rollback
        byte[] tooBigU8 = new byte[256];
        byte[] tooBigU16 = new byte[65536];
        Span<byte> small = stackalloc byte[5];
        var ws = small.AsWriter();
        Assert.False(ws.TryWriteLengthPrefixedBytes(LengthPrefix.U8, tooBigU8));
        Assert.False(ws.TryWriteLengthPrefixedBytes(LengthPrefix.U16, tooBigU16));
        Assert.False(ws.TryWriteLengthPrefixedBytes(LengthPrefix.U8, [1, 2, 3, 4, 5])); // 1+5 > 5 -> rollback
        Assert.Equal(0, ws.Position);
        Assert.False(ws.TryWriteLengthPrefixedBytes(LengthPrefix.U16, [1, 2, 3, 4])); // 2+4 > 5 -> rollback
        Assert.Equal(0, ws.Position);
        Assert.False(ws.TryWriteLengthPrefixedBytes(LengthPrefix.U32, [1, 2])); // 4+2 > 5 -> rollback
        Assert.Equal(0, ws.Position);
        Assert.False(ws.TryWriteLengthPrefixedBytes(LengthPrefix.VarUInt64, [1, 2, 3, 4, 5])); // 1+5 > 5 -> rollback
        Assert.Equal(0, ws.Position);
    }

    [Fact]
    public void BitWriter_Roundtrip_DirtyBuffer_Bounds_And_Checkpoints()
    {
        // Fill with 0xFF to verify BitWriter clears every written bit and AlignToByte zeros trailing bits!
        byte[] raw = new byte[16];
        raw.AsSpan().Fill(0xFF);

        var bw = new BitWriter(raw, baseByteOffset: 0);
        Assert.Equal(0, bw.BitPosition);
        Assert.Equal(128, bw.TotalBits);
        Assert.Equal(128, bw.RemainingBits);
        Assert.Equal(0, bw.WrittenBytes);
        Assert.Equal(0, bw.CurrentByteOffset);

        Assert.True(bw.TryWriteBool(true));          // 1 bit
        Assert.True(bw.TryWriteBool(false));         // 1 bit
        Assert.True(bw.TryWriteU8(0, 0xFF));         // 0 bits (no-op)
        Assert.True(bw.TryWriteU8(6, 0b10_1010));    // 6 bits -> total 8 bits (1 byte)
        Assert.Equal(8, bw.BitPosition);
        Assert.Equal(1, bw.WrittenBytes);
        Assert.Equal(1, bw.CurrentByteOffset);

        var cp = bw.Save();
        Assert.True(bw.TryWriteU16(12, 0xABC));      // 12 bits
        Assert.True(bw.TryWriteI32(5, -7));          // 5 bits signed (11001)
        Assert.True(bw.TryWriteU32(19, 0x5A5A5u));   // 19 bits
        Assert.True(bw.TryWriteU64(40, 0xAB_CDEF_1234UL)); // 40 bits
        // Reset and rewrite to prove Save/Reset works cleanly
        bw.Reset(cp);
        Assert.Equal(8, bw.BitPosition);
        Assert.True(bw.TryWriteU16(12, 0xABC));
        Assert.True(bw.TryWriteI32(5, -7));
        Assert.True(bw.TryWriteU32(19, 0x5A5A5u));
        Assert.True(bw.TryWriteU64(40, 0xAB_CDEF_1234UL));
        Assert.True(bw.TryWriteU8(3, 0b101));        // 3 bits into partial byte (total 87 bits)
        Assert.Equal(87, bw.BitPosition);
        Assert.Equal(11, bw.WrittenBytes);
        Assert.Equal(10, bw.CurrentByteOffset);

        bw.AlignToByte(); // zeros low 1 bit of byte 10 -> BitPosition = 88 (byte 11)
        Assert.Equal(88, bw.BitPosition);
        Assert.Equal(11, bw.CurrentByteOffset);
        bw.AlignToByte(); // already aligned -> no-op
        Assert.Equal(88, bw.BitPosition);

        // Verify exact bit-for-bit decode via BitCursor
        var bc = new BitCursor(raw.AsSpan(0, bw.WrittenBytes));
        Assert.True(bc.ReadBool().Unwrap());
        Assert.False(bc.ReadBool().Unwrap());
        Assert.Equal((byte)0b10_1010, bc.ReadU8(6).Unwrap());
        Assert.Equal((ushort)0xABC, bc.ReadU16(12).Unwrap());
        Assert.Equal(-7, bc.ReadI32(5).Unwrap());
        Assert.Equal(0x5A5A5u, bc.ReadU32(19).Unwrap());
        Assert.Equal(0xAB_CDEF_1234UL, bc.ReadU64(40).Unwrap());
        Assert.Equal((byte)0b101, bc.ReadU8(3).Unwrap());
        Assert.Equal((byte)0, bc.ReadU8(1).Unwrap()); // trailing pad bit was zeroed even on 0xFF buffer!

        // Invalid bit counts and overflow checks
        Span<byte> tiny = stackalloc byte[1];
        var bt = new BitWriter(tiny, baseByteOffset: 4);
        Assert.Equal(4, bt.CurrentByteOffset);
        Assert.False(bt.TryWriteU8(-1, 0));
        Assert.False(bt.TryWriteU8(9, 0));
        Assert.False(bt.TryWriteU16(-1, 0));
        Assert.False(bt.TryWriteU16(17, 0));
        Assert.False(bt.TryWriteU32(-1, 0));
        Assert.False(bt.TryWriteU32(33, 0));
        Assert.False(bt.TryWriteI32(0, 0));
        Assert.False(bt.TryWriteI32(33, 0));
        Assert.False(bt.TryWriteU64(-1, 0));
        Assert.False(bt.TryWriteU64(65, 0));
        Assert.False(bt.TryWriteU16(9, 1)); // exceeds 8 remaining bits
        Assert.True(bt.TryWriteU8(8, 0x55));
        Assert.False(bt.TryWriteBool(true)); // 0 remaining bits
        Assert.Equal(5, bt.CurrentByteOffset);
    }

    [Fact]
    public void GeneratedBinaryStruct_TryWrite_TryWriteTo_And_IBufferWriter_Roundtrip()
    {
        byte[] wire =
        [
            0x53, 0x46,
            0xDA,
            0x01, 0x02,
            0x44, 0x33, 0x22, 0x11,
            0x00,
            0x00, 0x00,
            0xAC, 0x02,
            0x01,
            0x41, 0x23,
            (byte)'X', (byte)'Y', (byte)'Z',
            0x05, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o',
        ];

        var c = wire.AsCursor();
        var header = GeneratedPacketHeader.Parse(ref c).Unwrap();

        // 1. TryWriteTo exact buffer (pre-filled with 0xFF to verify zeroed padding/alignment/bitfields!)
        byte[] outBuf = new byte[wire.Length];
        outBuf.AsSpan().Fill(0xFF);
        int written = header.TryWriteTo(outBuf, wire);
        Assert.Equal(wire.Length, written);
        Assert.SequenceEqual<byte>(wire, outBuf);

        // 2. Undersized buffers return -1 and roll back ByteWriter position to 0
        for (int len = 0; len < wire.Length; len++)
        {
            byte[] small = new byte[len];
            var w = small.AsWriter();
            Assert.False(header.TryWrite(ref w, wire));
            Assert.Equal(0, w.Position);
            Assert.Equal(-1, header.TryWriteTo(small, wire));
        }

        // 3. Invalid sourceBuffer (missing slice data) fails cleanly without throwing
        Assert.Equal(-1, header.TryWriteTo(outBuf, ReadOnlySpan<byte>.Empty));

        // 4. WriteTo(IBufferWriter<byte>) with tiny initialHint=1 exercises growth loop
        var abw = new ArrayBufferWriter<byte>();
        header.WriteTo(abw, wire, initialHint: 1);
        Assert.SequenceEqual<byte>(wire, abw.WrittenSpan);

        // 5. GeneratedFastRecord roundtrip
        byte[] fastWire =
        [
            0xAA, 7, 0x1F, 0x90, 0x00, 0x01, 0x02, 0x03,
            0x40, 0x09, 0x21, 0xFB, 0x54, 0x44, 0x2D, 0x18,
            0x96, 0x01, 0x00, 0x04, (byte)'d', (byte)'a', (byte)'t', (byte)'a',
        ];
        var fc = fastWire.AsCursor();
        var fastRec = GeneratedFastRecord.Parse(ref fc).Unwrap();
        Span<byte> fastOut = stackalloc byte[fastWire.Length];
        Assert.Equal(fastWire.Length, fastRec.TryWriteTo(fastOut, fastWire));
        Assert.SequenceEqual<byte>(fastWire, fastOut);

        // 6. GeneratedOuterMessage (nested struct + class + magic + LE/BE) roundtrip
        var outer = new GeneratedOuterMessage
        {
            Inner = new GeneratedMutableInner { Code = 0x1234, Enabled = true },
            SignedVal = -1234,
            FloatVal = 2.5f,
            LongVal = -9876543210L,
            SignedByte = -12,
        };
        var outerAbw = new ArrayBufferWriter<byte>();
        outer.WriteTo(outerAbw);
        var oc = outerAbw.WrittenSpan.AsCursor();
        var parsedOuter = GeneratedOuterMessage.Parse(ref oc).Unwrap();
        Assert.Equal(outer.Inner, parsedOuter.Inner);
        Assert.Equal(outer.SignedVal, parsedOuter.SignedVal);
        Assert.Equal(outer.FloatVal, parsedOuter.FloatVal);
        Assert.Equal(outer.LongVal, parsedOuter.LongVal);
        Assert.Equal(outer.SignedByte, parsedOuter.SignedByte);
        Assert.True(oc.IsAtEnd);
    }

    [Fact]
    public void GeneratedBinaryUnion_U8_U16_U32_Nested_ExhaustiveSwitch_And_Streaming()
    {
        // 1. Implicit conversion from case type -> generated C# 15 [Union]
        GeneratedWireMessage pingMsg = new UnionPingPayload(0xDEAD_BEEFu, 123456789UL);
        Assert.True(pingMsg.HasValue);
        Assert.True(pingMsg.TryGetValue(out UnionPingPayload extractedPing));
        Assert.Equal(0xDEAD_BEEFu, extractedPing.Nonce);
        Assert.False(pingMsg.TryGetValue(out UnionDataPayload _));
        Assert.True(pingMsg.Value is UnionPingPayload);

        // Write -> Parse -> exhaustive C# 15 switch!
        Span<byte> buf = stackalloc byte[64];
        int len = pingMsg.TryWriteTo(buf);
        Assert.Equal(2 + 1 + 4 + 8, len); // "UN" + tag 0x01 + u32 + u64
        Assert.Equal((byte)'U', buf[0]);
        Assert.Equal((byte)'N', buf[1]);
        Assert.Equal((byte)0x01, buf[2]);

        var c = ((ReadOnlySpan<byte>)buf[..len]).AsCursor();
        var parsed = GeneratedWireMessage.Parse(ref c).Unwrap();
        Assert.True(c.IsAtEnd);

        string desc = parsed switch
        {
            UnionPingPayload(var nonce, var ts) => $"ping:{nonce:X}:{ts}",
            UnionDataPayload(var ch, var body) => $"data:{ch}:{body.Length}",
            ushort u => $"u16:{u}",
            bool b => $"bool:{b}",
            null => "uninit",
        };
        Assert.Equal("ping:DEADBEEF:123456789", desc);

        // 2. DataPayload with ByteSlice + sourceBuffer roundtrip
        byte[] src = "hello-union"u8.ToArray();
        GeneratedWireMessage dataMsg = new UnionDataPayload(42, new ByteSlice(0, src.Length));
        var abw = new ArrayBufferWriter<byte>();
        dataMsg.WriteTo(abw, src, initialHint: 2);
        var cData = abw.WrittenSpan.AsCursor();
        GeneratedWireMessage.TryParser tp = default;
        Assert.True(tp.TryParse(ref cData, out var dataParsed).IsParsed);
        Assert.True(dataParsed.TryGetValue(out UnionDataPayload dp));
        Assert.Equal((ushort)42, dp.Channel);
        Assert.SequenceEqual<byte>(src, abw.WrittenSpan[dp.Body]);

        // 3. Primitive cases (ushort, bool)
        GeneratedWireMessage u16Msg = (ushort)0xCAFE;
        int u16Len = u16Msg.TryWriteTo(buf);
        Assert.Equal(5, u16Len);
        var cu16 = ((ReadOnlySpan<byte>)buf[..u16Len]).AsCursor();
        Assert.True(GeneratedWireMessage.Parse(ref cu16).Unwrap().TryGetValue(out ushort u16Val));
        Assert.Equal((ushort)0xCAFE, u16Val);

        GeneratedWireMessage boolMsg = true;
        int bLen = boolMsg.TryWriteTo(buf);
        Assert.Equal(4, bLen);
        var cb = ((ReadOnlySpan<byte>)buf[..bLen]).AsCursor();
        Assert.True(GeneratedWireMessage.Parse(ref cb).Unwrap().TryGetValue(out bool bVal));
        Assert.True(bVal);

        // 4. Unknown tag -> Backtrack(Switch) + cursor reset; Streaming partial -> Incomplete
        byte[] unknownWire = [0x55, 0x4E, 0x7F, 0x00];
        var cUnk = unknownWire.AsCursor();
        var unkRes = GeneratedWireMessage.Parse(ref cUnk);
        Assert.True(unkRes.TryGetValue(out Backtrack bt) && bt.Error.Kind == ErrorKind.Alt);
        Assert.Equal(0, cUnk.Position);

        byte[] partialWire = [0x55, 0x4E, 0x01, 0x00, 0x01]; // missing rest of PingPayload
        var cPart = partialWire.AsStreamingCursor();
        Assert.True(GeneratedWireMessage.Parse(ref cPart).IsIncomplete);
        Assert.Equal(0, cPart.Position);
        Assert.True(GeneratedWireMessage.TryParse(ref cPart, out _).IsIncomplete);

        // 5. Uninitialized default(GeneratedWireMessage) fails TryWrite cleanly
        GeneratedWireMessage uninit = default;
        Assert.False(uninit.HasValue);
        Assert.Equal(-1, uninit.TryWriteTo(buf));

        // 6. U16 LE union & U32 BE union
        GeneratedU16LeUnion u16Le = 3.5;
        int l16 = u16Le.TryWriteTo(buf);
        Assert.Equal(10, l16);
        Assert.Equal((byte)0x02, buf[0]); // 0x1002 LE
        Assert.Equal((byte)0x10, buf[1]);
        var c16 = ((ReadOnlySpan<byte>)buf[..l16]).AsCursor();
        Assert.True(GeneratedU16LeUnion.Parse(ref c16).Unwrap().TryGetValue(out double d16));
        Assert.Equal(3.5, d16);

        GeneratedU32BeUnion u32Be = -42L;
        int l32 = u32Be.TryWriteTo(buf);
        Assert.Equal(12, l32);
        var c32 = ((ReadOnlySpan<byte>)buf[..l32]).AsCursor();
        Assert.True(GeneratedU32BeUnion.Parse(ref c32).Unwrap().TryGetValue(out long lVal));
        Assert.Equal(-42L, lVal);

        // 7. Nested [BinaryUnion] inside [BinaryStruct]
        var env = new EnvelopeWithUnion(9, pingMsg);
        int envLen = env.TryWriteTo(buf);
        Assert.Equal(1 + 15, envLen);
        var cEnv = ((ReadOnlySpan<byte>)buf[..envLen]).AsCursor();
        var envParsed = EnvelopeWithUnion.Parse(ref cEnv).Unwrap();
        Assert.Equal((byte)9, envParsed.Version);
        Assert.True(envParsed.Body.TryGetValue(out UnionPingPayload nestedPing));
        Assert.Equal(extractedPing, nestedPing);
    }

    [Fact]
    public void CsCheck_ByteWriter_And_BitWriter_Roundtrip_Properties()
    {
        Gen.Select(Gen.UShort, Gen.Int, Gen.ULong, Gen.Long, Gen.Double)
            .Sample((u16, i32, u64, i64, f64) =>
            {
                if (double.IsNaN(f64)) return;
                Span<byte> buf = stackalloc byte[128];
                var w = buf.AsWriter(Endianness.Big);
                Assert.True(w.TryWriteU16BE(u16));
                Assert.True(w.TryWriteI32LE(i32));
                Assert.True(w.TryWriteVarUInt64(u64));
                Assert.True(w.TryWriteVarInt64(i64));
                ulong quic = u64 & 0x3FFF_FFFF_FFFF_FFFFUL;
                Assert.True(w.TryWriteQuicVarInt64(quic));
                Assert.True(w.TryWriteF64BE(f64));

                var c = w.Written.AsCursor();
                Assert.Equal(u16, c.ReadU16BE().Unwrap());
                Assert.Equal(i32, c.ReadI32LE().Unwrap());
                Assert.Equal(u64, c.ReadVarUInt64().Unwrap());
                Assert.Equal(i64, c.ReadVarInt64().Unwrap());
                Assert.Equal(quic, c.ReadQuicVarInt64().Unwrap());
                Assert.Equal(f64, c.ReadF64BE().Unwrap());
                Assert.True(c.IsAtEnd);
            });

        Gen.Select(Gen.Bool, Gen.Byte, Gen.UShort, Gen.UInt)
            .Sample((b, u8, u16, u32) =>
            {
                Span<byte> buf = stackalloc byte[16];
                buf.Fill(0xFF);
                byte v5 = (byte)(u8 & 0x1F);
                ushort v11 = (ushort)(u16 & 0x07FF);
                uint v23 = u32 & 0x007F_FFFFu;

                var bw = new BitWriter(buf);
                Assert.True(bw.TryWriteBool(b));
                Assert.True(bw.TryWriteU8(5, v5));
                Assert.True(bw.TryWriteU16(11, v11));
                Assert.True(bw.TryWriteU32(23, v23));
                bw.AlignToByte();

                var bc = new BitCursor(buf[..bw.WrittenBytes]);
                Assert.Equal(b, bc.ReadBool().Unwrap());
                Assert.Equal(v5, bc.ReadU8(5).Unwrap());
                Assert.Equal(v11, bc.ReadU16(11).Unwrap());
                Assert.Equal(v23, bc.ReadU32(23).Unwrap());
            });
    }

    [Fact]
    public void ZeroAllocation_Writer_BinaryStruct_And_BinaryUnion_Roundtrip()
    {
        var ping = new GeneratedWireMessage(new UnionPingPayload(0x12345678u, 0xCAFEBABEUL));
        Span<byte> buf = stackalloc byte[64];

        // Warmup JIT
        _ = RunZeroAllocLoop(ping, buf, 5_000);

        long before = GC.GetAllocatedBytesForCurrentThread();
        ulong checksum = RunZeroAllocLoop(ping, buf, 50_000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(checksum > 0);
        Assert.Equal(0L, allocated);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static ulong RunZeroAllocLoop(GeneratedWireMessage msg, Span<byte> scratch, int iterations)
    {
        ulong acc = 0;
        for (int i = 0; i < iterations; i++)
        {
            int n = msg.TryWriteTo(scratch);
            ReadOnlySpan<byte> ro = scratch[..n];
            var c = ro.AsCursor();
            if (GeneratedWireMessage.Parse(ref c).TryGetValue(out Parsed<GeneratedWireMessage> ok) &&
                ok.Value.TryGetValue(out UnionPingPayload p))
            {
                acc += p.Nonce ^ p.Timestamp;
            }
        }
        return acc;
    }
}
