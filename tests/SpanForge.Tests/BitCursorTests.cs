namespace SpanForge.Tests;

internal static class BitCursorTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("BitCursor/Cross-byte MSB bitfields & signed extension", TestCrossByteBitfields);
        suite.Add("BitCursor/ByteCursor.ParseBits bridge & streaming Incomplete", TestByteCursorBridgeAndStreaming);
    }

    private static void TestCrossByteBitfields()
    {
        // Binary: 1011_0100 1100_1010 1111_0000
        // 1 bit: 1 (true)
        // 3 bits: 011 (3)
        // 6 bits: 0100_11 (19)
        // 4-bit signed: 0010 (+2)
        // 4-bit signed: 1011 (-5 in 4-bit two's complement: 1011 = -8 + 3 = -5) -> wait: remaining bits of byte1 are 10 (2 bits) + first 2 bits of byte2 (11) = 1011 = -5!
        ReadOnlySpan<byte> bytes = [0b1011_0100, 0b1100_1010, 0b1111_0000];
        var bits = new BitCursor(bytes);

        Assert.True(bits.ReadBool().Unwrap());
        Assert.Equal((byte)0b011, bits.ReadU8(3).Unwrap());
        Assert.Equal((byte)0b010011, bits.ReadU8(6).Unwrap());
        Assert.Equal(2, bits.ReadI32(4).Unwrap());
        Assert.Equal(-5, bits.ReadI32(4).Unwrap()); // 1011b = -5

        var cp = bits.Save();
        Assert.Equal((byte)0b1100, bits.ReadU8(4).Unwrap());
        bits.Reset(cp);
        Assert.Equal((byte)0b110000, bits.ReadU8(6).Unwrap());
        Assert.Equal(24, bits.BitPosition);
        Assert.Equal(3, bits.ConsumedBytes);
    }

    private static void TestByteCursorBridgeAndStreaming()
    {
        // 12-bit value + 4-bit tag = 2 bytes, followed by 1 trailing byte 0xEE.
        ReadOnlySpan<byte> full = [0xAB, 0xCD, 0xEE];
        var cursor = full.AsCursor();

        var pair = cursor.ParseBits(static (ref BitCursor b) =>
        {
            var r1 = b.ReadU16(12);
            if (!r1.TryGetValue(out Parsed<ushort> p1)) return r1.PropagateNonParsed<(ushort, byte)>();
            var r2 = b.ReadU8(4);
            if (!r2.TryGetValue(out Parsed<byte> p2)) return r2.PropagateNonParsed<(ushort, byte)>();
            return new Parsed<(ushort, byte)>((p1.Value, p2.Value));
        }).Unwrap();

        Assert.Equal((ushort)0xABC, pair.Item1);
        Assert.Equal((byte)0xD, pair.Item2);
        Assert.Equal(2, cursor.Position);
        Assert.Equal((byte)0xEE, cursor.ReadU8().Unwrap());

        // Streaming shortage: only 1 byte available when 12 bits are needed -> Incomplete(ExactBytes(1))
        ReadOnlySpan<byte> shortBuf = [0xAB];
        var sc = shortBuf.AsStreamingCursor();
        var incRes = sc.ParseBits(static (ref BitCursor b) => b.ReadU16(12));
        Assert.True(incRes.TryGetValue(out Incomplete inc) && inc.Needed == Needed.Bytes(1));
        Assert.Equal(0, sc.Position);
    }
}
