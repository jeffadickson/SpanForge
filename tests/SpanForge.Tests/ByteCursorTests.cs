using System.Buffers;
using System.Runtime.InteropServices;

namespace SpanForge.Tests;

internal static class ByteCursorTests
{
    private static readonly SearchValues<byte> AsciiDigits = SearchValues.Create("0123456789"u8);
    private static readonly SearchValues<byte> Delims = SearchValues.Create(",;:"u8);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct BlittablePoint(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }

    public static void Register(TestSuite suite)
    {
        suite.Add("ByteCursor/Fixed-width integers & floats (BE & LE)", TestFixedWidthPrimitives);
        suite.Add("ByteCursor/LEB128, ZigZag, and QUIC RFC 9000 varints", TestVarInts);
        suite.Add("ByteCursor/Tag complete vs streaming early mismatch", TestTagStreamingSemantics);
        suite.Add("ByteCursor/SIMD TakeWhile, TakeUntil, CString, CRLF, Align", TestSimdAndFraming);
        suite.Add("ByteCursor/ReadUtf8String validation & ReadBlittable zero-copy", TestUtf8AndBlittable);
    }

    private static void TestFixedWidthPrimitives()
    {
        ReadOnlySpan<byte> data =
        [
            0xAB,                                           // u8
            0xFE,                                           // i8 (-2)
            0x12, 0x34,                                     // u16 BE = 0x1234
            0x34, 0x12,                                     // u16 LE = 0x1234
            0x01, 0x02, 0x03,                               // u24 BE = 0x010203
            0xDE, 0xAD, 0xBE, 0xEF,                         // u32 BE
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // u64 LE = 1
            0x3F, 0x80, 0x00, 0x00,                         // f32 BE = 1.0f
        ];

        var c = data.AsCursor(Endianness.Big);
        Assert.Equal((byte)0xAB, c.ReadU8().Unwrap());
        Assert.Equal((sbyte)-2, c.ReadI8().Unwrap());
        Assert.Equal((ushort)0x1234, c.ReadU16().Unwrap());
        Assert.Equal((ushort)0x1234, c.ReadU16LE().Unwrap());
        Assert.Equal(0x010203u, c.ReadU24BE().Unwrap());
        Assert.Equal(0xDEADBEEFu, c.ReadU32().Unwrap());
        Assert.Equal(1UL, c.ReadU64LE().Unwrap());
        Assert.Equal(1.0f, c.ReadF32BE().Unwrap());
        Assert.True(c.IsAtEnd);
    }

    private static void TestVarInts()
    {
        // LEB128: 0, 127, 300 (0xAC 0x02), ulong.MaxValue (9x 0xFF + 0x01)
        ReadOnlySpan<byte> leb = [0x00, 0x7F, 0xAC, 0x02, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01];
        var c = leb.AsCursor();
        Assert.Equal(0UL, c.ReadVarUInt64().Unwrap());
        Assert.Equal(127UL, c.ReadVarUInt64().Unwrap());
        Assert.Equal(300UL, c.ReadVarUInt64().Unwrap());
        Assert.Equal(ulong.MaxValue, c.ReadVarUInt64().Unwrap());

        // Overflow (10th byte = 0x02) must backtrack and restore position
        ReadOnlySpan<byte> overflow = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x02];
        var co = overflow.AsCursor();
        Assert.True(co.ReadVarUInt64().IsBacktrack);
        Assert.Equal(0, co.Position);

        // ZigZag: 0->0, 1->-1, 2->1, 3->-2, 4->2
        ReadOnlySpan<byte> zz = [0x00, 0x01, 0x02, 0x03, 0x04];
        var cz = zz.AsCursor();
        Assert.Equal(0L, cz.ReadVarInt64().Unwrap());
        Assert.Equal(-1L, cz.ReadVarInt64().Unwrap());
        Assert.Equal(1L, cz.ReadVarInt64().Unwrap());
        Assert.Equal(-2L, cz.ReadVarInt64().Unwrap());
        Assert.Equal(2L, cz.ReadVarInt64().Unwrap());

        // QUIC RFC 9000 Appendix A.1 test vectors:
        // 0x25 -> 37, 0x7BBD -> 15293, 0x9D7F3E7D -> 494878333, 0xC2197C5EFF14E88C -> 151288809941952652
        ReadOnlySpan<byte> quic =
        [
            0x25,
            0x7B, 0xBD,
            0x9D, 0x7F, 0x3E, 0x7D,
            0xC2, 0x19, 0x7C, 0x5E, 0xFF, 0x14, 0xE8, 0x8C,
        ];
        var cq = quic.AsCursor();
        Assert.Equal(37UL, cq.ReadQuicVarInt64().Unwrap());
        Assert.Equal(15293UL, cq.ReadQuicVarInt64().Unwrap());
        Assert.Equal(494878333UL, cq.ReadQuicVarInt64().Unwrap());
        Assert.Equal(151288809941952652UL, cq.ReadQuicVarInt64().Unwrap());
        Assert.True(cq.IsAtEnd);
    }

    private static void TestTagStreamingSemantics()
    {
        // Partial matching prefix in streaming mode -> Incomplete(ExactBytes(2))
        ReadOnlySpan<byte> partialOk = "PN"u8;
        var cs1 = partialOk.AsStreamingCursor();
        var r1 = cs1.Tag("PNG!"u8);
        Assert.True(r1.TryGetValue(out Incomplete inc) && inc.Needed == Needed.Bytes(2));

        // Partial mismatching prefix in streaming mode -> Backtrack immediately!
        ReadOnlySpan<byte> partialBad = "PX"u8;
        var cs2 = partialBad.AsStreamingCursor();
        Assert.True(cs2.Tag("PNG!"u8).IsBacktrack);

        // Full match
        ReadOnlySpan<byte> full = "PNG!rest"u8;
        var cs3 = full.AsStreamingCursor();
        var slice = cs3.Tag("PNG!"u8).Unwrap();
        Assert.Equal("PNG!", slice.AsUtf8String(full));
        Assert.Equal(4, cs3.Position);
    }

    private static void TestSimdAndFraming()
    {
        ReadOnlySpan<byte> text = "12345;hello\0line1\r\n"u8;
        var c = text.AsCursor();

        var digits = c.TakeWhile1(AsciiDigits).Unwrap();
        Assert.Equal("12345", digits.AsUtf8String(text));

        Assert.Equal((byte)';', c.TagByte((byte)';').Unwrap());

        var cstr = c.ReadCString().Unwrap();
        Assert.Equal("hello", cstr.AsUtf8String(text));

        var line = c.TakeCrlfLine().Unwrap();
        Assert.Equal("line1", line.AsUtf8String(text));
        Assert.True(c.IsAtEnd);

        // TakeUntilAny + Align + LengthPrefixed
        ReadOnlySpan<byte> framed = [0x01, 0x00, 0x00, 0x00, 0x03, (byte)'a', (byte)'b', (byte)'c'];
        var cf = framed.AsCursor();
        Assert.Equal((byte)0x01, cf.ReadU8().Unwrap());
        Assert.Equal(3, cf.Align(4).Unwrap());
        Assert.Equal(4, cf.Position);
        var payload = cf.ReadLengthPrefixedBytes(LengthPrefix.U8).Unwrap();
        Assert.Equal("abc", payload.AsUtf8String(framed));

        ReadOnlySpan<byte> delimInput = "alpha:beta"u8;
        var cd = delimInput.AsCursor();
        Assert.Equal("alpha", cd.TakeUntilAny(Delims).Unwrap().AsUtf8String(delimInput));
    }

    private static void TestUtf8AndBlittable()
    {
        ReadOnlySpan<byte> validUtf8 = "C#15 🚀"u8;
        var c1 = validUtf8.AsCursor();
        Assert.Equal("C#15 🚀", c1.ReadUtf8String(validUtf8.Length).Unwrap());

        ReadOnlySpan<byte> invalidUtf8 = [0xFF, 0xFE, 0xFD];
        var c2 = invalidUtf8.AsCursor();
        var badRes = c2.ReadUtf8String(3);
        Assert.True(badRes.TryGetValue(out Backtrack bt) && bt.Error.Kind == ErrorKind.InvalidUtf8);
        Assert.Equal(0, c2.Position);

        Span<byte> pointBytes = stackalloc byte[8];
        var expected = new BlittablePoint(100, -250);
        MemoryMarshal.Write(pointBytes, in expected);
        var c3 = new ByteCursor(pointBytes);
        var parsed = c3.ReadBlittable<BlittablePoint>().Unwrap();
        Assert.Equal(100, parsed.X);
        Assert.Equal(-250, parsed.Y);
    }
}
