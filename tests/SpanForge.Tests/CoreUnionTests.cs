namespace SpanForge.Tests;

internal static class CoreUnionTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("Core/ParseResult exhaustive C# 15 union switch & conversions", TestParseResultUnion);
        suite.Add("Core/Needed & Option non-boxing unions", TestNeededAndOption);
        suite.Add("Core/ByteSlice & C# 15 extension indexer", TestByteSliceExtensionIndexer);
        suite.Add("Core/ParseError hex-dump diagnostics & breadcrumbs", TestErrorDiagnostics);
    }

    private static void TestParseResultUnion()
    {
        ParseResult<int> ok = new Parsed<int>(42);
        ParseResult<int> inc = Incomplete.AtLeast(4);
        ParseResult<int> bt = Backtrack.At(3, ErrorKind.Tag, "expected magic");
        ParseResult<int> fail = Failure.At(7, ErrorKind.CrcMismatch, "bad crc");

        static string Describe(ParseResult<int> r) => r switch
        {
            Parsed<int>(var v) => $"ok:{v}",
            Incomplete(ExactBytes(var n)) => $"need:{n}",
            Incomplete(UnknownSize) => "need:?",
            Backtrack(var e) => $"bt:{e.Offset}:{e.Kind}",
            Failure(var e) => $"fail:{e.Offset}:{e.Kind}",
        };

        Assert.Equal("ok:42", Describe(ok));
        Assert.Equal("need:4", Describe(inc));
        Assert.Equal("bt:3:Tag", Describe(bt));
        Assert.Equal("fail:7:CrcMismatch", Describe(fail));

        // Cut escalates Backtrack -> Failure but leaves Parsed/Incomplete unchanged.
        Assert.True(bt.Cut().IsFailure);
        Assert.True(ok.Cut().IsParsed);
        Assert.True(inc.Cut().IsIncomplete);

        // Map preserves non-parsed states.
        Assert.Equal("ok:84", Describe(ok.Map(static x => x * 2)));
        Assert.True(bt.Map(static x => x * 2).IsBacktrack);
        Assert.True(inc.Map(static x => x * 2).IsIncomplete);
    }

    private static void TestNeededAndOption()
    {
        Needed exact = new ExactBytes(8);
        Needed unknown = default(UnknownSize);

        static int Resolve(Needed n) => n switch
        {
            ExactBytes(var count) => count,
            UnknownSize => -1,
        };

        Assert.Equal(8, Resolve(exact));
        Assert.Equal(-1, Resolve(unknown));

        Option<ushort> some = new Some<ushort>(0xCAFE);
        Option<ushort> none = default(None);

        static ushort Extract(Option<ushort> o) => o switch
        {
            Some<ushort>(var v) => v,
            None => 0,
        };

        Assert.Equal((ushort)0xCAFE, Extract(some));
        Assert.Equal((ushort)0, Extract(none));
        Assert.Equal((ushort)99, none.UnwrapOr(99));
        Assert.Equal(0xCAFE + 1, some.Map(static x => x + 1).UnwrapOr(0));
    }

    private static void TestByteSliceExtensionIndexer()
    {
        byte[] backing = "Hello, SpanForge!"u8.ToArray();
        ReadOnlySpan<byte> span = backing;

        var slice = new ByteSlice(7, 9);
        // C# 15 extension indexer on ReadOnlySpan<byte>, plus native Range slice on byte[]
        Assert.SequenceEqual("SpanForge"u8, span[slice]);
        Assert.SequenceEqual("SpanForge"u8, backing[slice.AsRange]);
        Assert.Equal("SpanForge", slice.AsUtf8String(span));
        Assert.Equal(16, slice.End);
    }

    private static void TestErrorDiagnostics()
    {
        ReadOnlySpan<byte> data = [0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF, 0xAA, 0xBB];
        var err = new ParseError(5, ErrorKind.Verify, "invalid flag")
            .WithContext("header decode", offset: 4);

        string report = err.FormatDiagnostics(data);
        Assert.Contains("header decode", report);
        Assert.Contains("invalid flag", report);
        Assert.Contains("89 50 4E 47 00 FF AA BB", report);
        Assert.Contains("^^", report);
    }
}
