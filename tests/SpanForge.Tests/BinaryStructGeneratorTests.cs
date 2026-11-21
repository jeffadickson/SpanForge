using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SpanForge.Streaming;
using Xunit;

namespace SpanForge.Tests;

[BinaryStruct(Endianness.Big)]
[Magic(0x53, 0x46)] // "SF"
public readonly partial record struct GeneratedPacketHeader(
    [property: Bits(1)] bool Compressed,
    [property: Bits(3)] byte Version,
    [property: Bits(4)] byte Flags,
    [property: AssertValue(0x0102)] ushort MagicVersion,
    [property: Endian(Endianness.Little)] uint SequenceLe,
    [property: Pad(1)]
    [property: Align(4)]
    [property: VarInt(VarIntEncoding.Leb128Unsigned)] ulong ItemCount,
    [property: VarInt(VarIntEncoding.Leb128SignedZigZag)] long Delta,
    [property: VarInt(VarIntEncoding.Quic)] ulong QuicId,
    [property: FixedBytes(3)] ByteSlice Tag3,
    [property: LengthPrefixed(LengthPrefix.U8)] ByteSlice Name);

[BinaryStruct(Endianness.Big)]
[Magic(0xAA)]
public readonly partial record struct GeneratedFastRecord(
    [property: AssertValue(7)] byte Kind,
    ushort Port,
    uint Timestamp,
    double Score,
    [property: VarInt] ulong Sequence,
    [property: LengthPrefixed(LengthPrefix.U16)] ByteSlice Payload);

[BinaryStruct(Endianness.Little)]
public partial struct GeneratedMutableInner
{
    public ushort Code { get; init; }
    public bool Enabled { get; init; }
}

[BinaryStruct(Endianness.Big)]
public partial class GeneratedOuterMessage
{
    public GeneratedMutableInner Inner { get; set; }
    [Magic(0xFE)]
    public short SignedVal { get; set; }
    public float FloatVal { get; set; }
    public long LongVal { get; set; }
    public sbyte SignedByte { get; set; }
}

public sealed class BinaryStructGeneratorTests
{
    [Fact]
    public void GeneratedPacketHeader_AllAttributes_Streaming_Rollback_And_Verify()
    {
        // Build wire bytes for GeneratedPacketHeader:
        // [0..2]: Magic "SF" (0x53, 0x46)
        // [2]: Bits(1)=1, Bits(3)=5, Bits(4)=0xA -> (1<<7)|(5<<4)|0xA = 0xDA
        // [3..5]: MagicVersion BE = 0x0102
        // [5..9]: SequenceLe LE = 0x11223344
        // [9]: Pad(1) = 0x00 -> pos=10
        // [10..12]: Align(4) skips 2 bytes (0x00, 0x00) -> pos=12
        // [12..14]: ItemCount LEB128 = 300 (0xAC, 0x02)
        // [14]: Delta ZigZag = -1 (encoded 1 -> 0x01)
        // [15..17]: QuicId = 0x4123 (2-byte QUIC: 0x4000 | 0x0123 = 0x41, 0x23 -> value 0x0123 = 291)
        // [17..20]: Tag3 = "XYZ"
        // [20..26]: Name U8 length-prefixed = 5, "hello"
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
        var res = GeneratedPacketHeader.Parse(ref c);
        Assert.True(res.TryGetValue(out Parsed<GeneratedPacketHeader> ok));
        var h = ok.Value;
        Assert.True(h.Compressed);
        Assert.Equal((byte)5, h.Version);
        Assert.Equal((byte)0xA, h.Flags);
        Assert.Equal((ushort)0x0102, h.MagicVersion);
        Assert.Equal(0x11223344u, h.SequenceLe);
        Assert.Equal(300UL, h.ItemCount);
        Assert.Equal(-1L, h.Delta);
        Assert.Equal(0x0123UL, h.QuicId);
        Assert.SequenceEqual<byte>("XYZ"u8, wire.AsSpan()[h.Tag3]);
        Assert.SequenceEqual<byte>("hello"u8, wire.AsSpan()[h.Name]);
        Assert.True(c.IsAtEnd);

        // TryParse fallback path & TryParser struct trait
        var cTry = wire.AsCursor();
        GeneratedPacketHeader.TryParser tp = default;
        Assert.True(tp.TryParse(ref cTry, out GeneratedPacketHeader h2).IsParsed);
        Assert.Equal(h, h2);
        Assert.True(cTry.IsAtEnd);

        // AssertValue failure -> Backtrack(Verify) + full cursor rollback to 0
        byte[] badAssert = (byte[])wire.Clone();
        badAssert[3] = 0x99;
        var cBad = badAssert.AsCursor();
        var badRes = GeneratedPacketHeader.Parse(ref cBad);
        Assert.True(badRes.TryGetValue(out Backtrack bt) && bt.Error.Kind == ErrorKind.Verify);
        Assert.Equal(0, cBad.Position);

        // Byte-by-byte streaming through StreamingDecoder<T>
        var decoder = new StreamingDecoder<GeneratedPacketHeader>(GeneratedPacketHeader.Parse);
        GeneratedPacketHeader? decoded = null;
        for (int i = 0; i < wire.Length; i++)
        {
            decoder.Append(wire.AsSpan(i, 1));
            var step = decoder.TryDecodeNext(out ReadOnlySpan<byte> frame);
            if (i < wire.Length - 1)
            {
                Assert.True(step.IsIncomplete);
            }
            else
            {
                Assert.True(step.TryGetValue(out Parsed<GeneratedPacketHeader> done));
                Assert.SequenceEqual<byte>("hello"u8, frame[done.Value.Name]);
                decoded = done.Value;
            }
        }
        Assert.True(decoded.HasValue && decoded.Value.ItemCount == 300UL);
    }

    [Fact]
    public void GeneratedFastRecord_DirectTryParse_ParserTraits_And_ZeroAlloc()
    {
        byte[] wire = new byte[1 + 1 + 2 + 4 + 8 + 2 + 2 + 4];
        wire[0] = 0xAA; // Magic
        wire[1] = 7;    // Kind (AssertValue(7))
        BinaryPrimitives.WriteUInt16BigEndian(wire.AsSpan(2, 2), 8080);
        BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(4, 4), 0x12345678u);
        BinaryPrimitives.WriteDoubleBigEndian(wire.AsSpan(8, 8), 42.5);
        wire[16] = 0x81; wire[17] = 0x02; // LEB128 = 257
        BinaryPrimitives.WriteUInt16BigEndian(wire.AsSpan(18, 2), 4);
        "TEST"u8.CopyTo(wire.AsSpan(20, 4));

        // Direct TryParse fast path + ITryParser trait composition with TryParsers.Delimited
        byte[] wrapped = [(byte)'(', .. wire, (byte)')'];
        var delim = TryParsers.Delimited<TryParsers.TagByteParser, GeneratedFastRecord.TryParser, TryParsers.TagByteParser, byte, GeneratedFastRecord, byte>(
            TryParsers.TagByte((byte)'('),
            default,
            TryParsers.TagByte((byte)')'));

        var c = wrapped.AsCursor();
        Assert.True(delim.TryParse(ref c, out GeneratedFastRecord rec).IsParsed);
        Assert.Equal((byte)7, rec.Kind);
        Assert.Equal((ushort)8080, rec.Port);
        Assert.Equal(0x12345678u, rec.Timestamp);
        Assert.Equal(42.5, rec.Score);
        Assert.Equal(257UL, rec.Sequence);
        Assert.SequenceEqual<byte>("TEST"u8, wrapped.AsSpan()[rec.Payload]);
        Assert.True(c.IsAtEnd);

        // AssertValue failure on direct TryParse rolls back to start
        byte[] badKind = (byte[])wire.Clone();
        badKind[1] = 8;
        var cb = badKind.AsCursor();
        Assert.True(GeneratedFastRecord.TryParse(ref cb, out _).IsBacktrack);
        Assert.Equal(0, cb.Position);

        // Streaming incomplete on direct TryParse rolls back to start
        var cs = wire.AsSpan(0, 10).AsStreamingCursor();
        Assert.True(GeneratedFastRecord.TryParse(ref cs, out _).IsIncomplete);
        Assert.Equal(0, cs.Position);

        // Strict 0 bytes allocation assertion
        _ = RunLoop(wire, 100);
        long before = GC.GetAllocatedBytesForCurrentThread();
        ulong sum = RunLoop(wire, 10_000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(10_000UL * (8080UL + 257UL + 4UL), sum);
        Assert.Equal(0L, allocated);

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        static ulong RunLoop(byte[] buf, int iterations)
        {
            ulong acc = 0;
            ReadOnlySpan<byte> span = buf;
            for (int i = 0; i < iterations; i++)
            {
                var cur = span.AsCursor();
                if (GeneratedFastRecord.TryParse(ref cur, out var r).IsParsed)
                {
                    acc += r.Port + r.Sequence + (ulong)r.Payload.Length;
                }
            }
            return acc;
        }
    }

    [Fact]
    public void GeneratedOuterMessage_NestedBinaryStruct_And_ClassGeneration()
    {
        byte[] wire = new byte[3 + 1 + 2 + 4 + 8 + 1];
        BinaryPrimitives.WriteUInt16LittleEndian(wire.AsSpan(0, 2), 0xCAFE);
        wire[2] = 1; // Enabled = true
        wire[3] = 0xFE; // Member Magic
        BinaryPrimitives.WriteInt16BigEndian(wire.AsSpan(4, 2), -1234);
        BinaryPrimitives.WriteSingleBigEndian(wire.AsSpan(6, 4), 1.5f);
        BinaryPrimitives.WriteInt64BigEndian(wire.AsSpan(10, 8), -9876543210L);
        wire[18] = unchecked((byte)(sbyte)-42);

        var c = wire.AsCursor();
        GeneratedOuterMessage.Parser p = default;
        var res = p.Parse(ref c);
        Assert.True(res.TryGetValue(out Parsed<GeneratedOuterMessage> ok));
        Assert.Equal((ushort)0xCAFE, ok.Value.Inner.Code);
        Assert.True(ok.Value.Inner.Enabled);
        Assert.Equal((short)-1234, ok.Value.SignedVal);
        Assert.Equal(1.5f, ok.Value.FloatVal);
        Assert.Equal(-9876543210L, ok.Value.LongVal);
        Assert.Equal((sbyte)-42, ok.Value.SignedByte);
        Assert.True(c.IsAtEnd);

        // Bad member magic triggers full rollback across nested struct
        byte[] bad = (byte[])wire.Clone();
        bad[3] = 0x00;
        var cb = bad.AsCursor();
        Assert.True(GeneratedOuterMessage.Parse(ref cb).IsBacktrack);
        Assert.Equal(0, cb.Position);
    }
}
