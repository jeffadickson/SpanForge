namespace SpanNom.Tests;

internal static class CombinatorTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("Combinators/Alt backtracking vs Cut commit semantics", TestAltAndCut);
        suite.Add("Combinators/Opt, Peek, Recognize, WithSlice, Delimited, Pair", TestCoreCombinators);
        suite.Add("Combinators/ScopedEndian & LengthValue bounded sub-cursor", TestScopedEndianAndLengthValue);
        suite.Add("Combinators/Count, Many0, FoldMany0, RepeatInto, Separated0", TestRepetitionCombinators);
        suite.Add("Combinators/Monomorphized StructParsers pipelines", TestStructParsers);
    }

    private static void TestAltAndCut()
    {
        // Branch 1 matches 'A' then commits with Cut for a U16; Branch 2 matches 'B' then U8.
        static ParseResult<int> BranchA(ref ByteCursor c) =>
            Parse.Preceded(
                ref c,
                static (ref ByteCursor x) => x.TagByte((byte)'A'),
                static (ref ByteCursor x) => x.ReadU16BE().Cut("expected U16 after 'A'").Map(static v => (int)v));

        static ParseResult<int> BranchB(ref ByteCursor c) =>
            Parse.Preceded(
                ref c,
                static (ref ByteCursor x) => x.TagByte((byte)'B'),
                static (ref ByteCursor x) => x.ReadU8().Map(static v => (int)v));

        // Normal backtrack from A -> succeeds on B
        ReadOnlySpan<byte> inputB = [(byte)'B', 0x2A];
        var cb = inputB.AsCursor();
        Assert.Equal(42, Parse.Alt(ref cb, BranchA, BranchB).Unwrap());

        // Matches 'A', then truncated payload -> Cut produces Failure, which MUST NOT backtrack to BranchB!
        ReadOnlySpan<byte> badA = [(byte)'A', 0x01];
        var ca = badA.AsCursor();
        var failRes = Parse.Alt(ref ca, BranchA, BranchB);
        Assert.True(failRes.TryGetValue(out Failure f));
        Assert.Equal("expected U16 after 'A'", f.Error.Context);
        Assert.Equal(0, ca.Position);
    }

    private static void TestCoreCombinators()
    {
        ReadOnlySpan<byte> buf = "[(42)]"u8;
        var c = buf.AsCursor();

        // Peek does not advance
        Assert.Equal((byte)'[', Parse.Peek(ref c, static (ref ByteCursor x) => x.ReadU8()).Unwrap());
        Assert.Equal(0, c.Position);

        // Opt returns None on backtrack, Some on match
        Assert.True(Parse.Opt(ref c, static (ref ByteCursor x) => x.TagByte((byte)'!')).Unwrap().IsNone);
        Assert.Equal(0, c.Position);

        // Delimited + Recognize + WithSlice
        var (val, slice) = Parse.WithSlice(
            ref c,
            static (ref ByteCursor x) => Parse.Delimited(
                ref x,
                static (ref ByteCursor o) => o.Tag("[("u8),
                static (ref ByteCursor v) => v.Take(2),
                static (ref ByteCursor cl) => cl.Tag(")]"u8))).Unwrap();

        Assert.Equal("42", val.AsUtf8String(buf));
        Assert.Equal("[(42)]", slice.AsUtf8String(buf));
        Assert.True(c.IsAtEnd);
    }

    private static void TestScopedEndianAndLengthValue()
    {
        // Outer BigEndian u16 (0x0004 = length 4) -> inner LittleEndian pair of u16 (0x0102, 0x0304) -> outer BigEndian u16 (0xABCD)
        ReadOnlySpan<byte> data = [0x00, 0x04, 0x02, 0x01, 0x04, 0x03, 0xAB, 0xCD];
        var c = data.AsCursor(Endianness.Big);

        var innerPair = Parse.LengthValue(
            ref c,
            static (ref ByteCursor x) => x.ReadU16().Map(static u => (int)u),
            static (ref ByteCursor sub) => Parse.ScopedEndian(
                ref sub,
                Endianness.Little,
                static (ref ByteCursor le) => Parse.Pair(
                    ref le,
                    static (ref ByteCursor a) => a.ReadU16(),
                    static (ref ByteCursor b) => b.ReadU16()))).Unwrap();

        Assert.Equal((ushort)0x0102, innerPair.First);
        Assert.Equal((ushort)0x0304, innerPair.Second);
        Assert.Equal(Endianness.Big, c.Endianness);
        Assert.Equal((ushort)0xABCD, c.ReadU16().Unwrap());

        // LengthValue rejects unconsumed trailing bytes inside the frame when requireFullConsumption=true
        ReadOnlySpan<byte> trailingFrame = [0x00, 0x03, 0x11, 0x22, 0x33];
        var ct = trailingFrame.AsCursor(Endianness.Big);
        var bad = Parse.LengthValue(
            ref ct,
            static (ref ByteCursor x) => x.ReadU16().Map(static u => (int)u),
            static (ref ByteCursor sub) => sub.ReadU16());
        Assert.True(bad.TryGetValue(out Backtrack bt) && bt.Error.Kind == ErrorKind.LengthValue);
        Assert.Equal(0, ct.Position);
    }

    private static void TestRepetitionCombinators()
    {
        ReadOnlySpan<byte> nums = [10, 20, 30, 40];
        var c1 = nums.AsCursor();
        var list = Parse.Count(ref c1, 4, static (ref ByteCursor x) => x.ReadU8()).Unwrap();
        Assert.Equal(4, list.Count);
        Assert.Equal((byte)40, list[3]);

        // FoldMany0 zero-allocation sum
        var c2 = nums.AsCursor();
        int sum = Parse.FoldMany0(ref c2, static (ref ByteCursor x) => x.ReadU8(), 0, static (acc, b) => acc + b).Unwrap();
        Assert.Equal(100, sum);

        // RepeatInto stackalloc buffer
        var c3 = nums.AsCursor();
        Span<byte> dest = stackalloc byte[4];
        int written = Parse.RepeatInto(ref c3, dest, static (ref ByteCursor x) => x.ReadU8(), minCount: 2).Unwrap();
        Assert.Equal(4, written);
        Assert.SequenceEqual(nums, dest);

        // Separated0: "1,2,3"
        ReadOnlySpan<byte> csv = "1,2,3"u8;
        var c4 = csv.AsCursor();
        var items = Parse.Separated0(
            ref c4,
            static (ref ByteCursor x) => x.ReadU8().Verify(static b => b is >= (byte)'0' and <= (byte)'9', x.Position),
            static (ref ByteCursor x) => x.TagByte((byte)',')).Unwrap();
        Assert.Equal(3, items.Count);
        Assert.Equal((byte)'1', items[0]);
        Assert.Equal((byte)'2', items[1]);
        Assert.Equal((byte)'3', items[2]);
    }

    private static void TestStructParsers()
    {
        // Monomorphized struct pipeline: '<' + LengthPrefixed(U8) + '>'
        var pipeline = StructParsers.Delimited<
            StructParsers.TagByteParser,
            StructParsers.LengthPrefixedParser,
            StructParsers.TagByteParser,
            byte, ByteSlice, byte>(
            StructParsers.TagByte((byte)'<'),
            StructParsers.LengthPrefixed(LengthPrefix.U8),
            StructParsers.TagByte((byte)'>'));

        ReadOnlySpan<byte> data = [(byte)'<', 4, (byte)'r', (byte)'u', (byte)'s', (byte)'t', (byte)'>'];
        var c = data.AsCursor();
        var slice = pipeline.Parse(ref c).Unwrap();
        Assert.Equal("rust", slice.AsUtf8String(data));
        Assert.True(c.IsAtEnd);
    }
}
