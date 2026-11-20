using SpanNom.Streaming;
using Xunit;

namespace SpanNom.Tests;

/// <summary>
/// Targeted boundary, equality, error-path, and buffer-compaction tests written in response
/// to Stryker.NET 5.1.0 mutation testing findings.
/// </summary>
public sealed class MutationKillerTests
{
    [Fact]
    public void Needed_Equality_HashCode_ToString_And_Guards()
    {
        var b4a = Needed.Bytes(4);
        var b4b = Needed.Bytes(4);
        var b8 = Needed.Bytes(8);
        var unk = Needed.Unknown;
        Needed uninit = default;

        Assert.True(b4a == b4b);
        Assert.False(b4a != b4b);
        Assert.True(b4a != b8);
        Assert.True(b4a != unk);
        Assert.True(b4a.Equals((object)b4b));
        Assert.False(b4a.Equals("not a needed"));
        Assert.Equal(b4a.GetHashCode(), b4b.GetHashCode());
        Assert.True(b4a.HasValue);
        Assert.True(unk.HasValue);
        Assert.False(uninit.HasValue);
        Assert.Equal(new ExactBytes(4), b4a.Value);
        Assert.Equal(default(UnknownSize), unk.Value);
        Assert.True(uninit.Value is null);
        Assert.Equal("Needed(4 bytes)", b4a.ToString());
        Assert.Equal("Needed(Unknown)", unk.ToString());
        Assert.Equal("Needed(Uninitialized)", uninit.ToString());

        bool threwZero = false;
        try { _ = Needed.Bytes(0); } catch (ArgumentOutOfRangeException) { threwZero = true; }
        Assert.True(threwZero);

        bool threwNeg = false;
        try { _ = Needed.Bytes(-3); } catch (ArgumentOutOfRangeException) { threwNeg = true; }
        Assert.True(threwNeg);
    }

    [Fact]
    public void Option_Equality_HashCode_Value_Map_ToString()
    {
        var s1 = Option<int>.Of(42);
        var s2 = Option<int>.Of(42);
        var s3 = Option<int>.Of(99);
        var n1 = Option<int>.Nothing;
        var n2 = Option<int>.Nothing;
        Option<int> uninit = default;

        Assert.True(s1 == s2);
        Assert.False(s1 != s2);
        Assert.True(s1 != s3);
        Assert.True(s1 != n1);
        Assert.True(n1 == n2);
        Assert.True(s1.Equals((object)s2));
        Assert.False(s1.Equals("nope"));
        Assert.Equal(s1.GetHashCode(), s2.GetHashCode());
        Assert.Equal(n1.GetHashCode(), n2.GetHashCode());
        Assert.True(s1.HasValue);
        Assert.True(n1.HasValue);
        Assert.False(uninit.HasValue);
        Assert.Equal(new Some<int>(42), s1.Value);
        Assert.Equal(default(None), n1.Value);
        Assert.True(uninit.Value is null);
        Assert.True(s1.IsSome && !s1.IsNone);
        Assert.True(n1.IsNone && !n1.IsSome);
        Assert.Equal(42, s1.UnwrapOr(0));
        Assert.Equal(7, n1.UnwrapOr(7));
        Assert.True(n1.Map(static x => x + 1).IsNone);
        Assert.Equal("Some(42)", s1.ToString());
        Assert.Equal("None", n1.ToString());
    }

    [Fact]
    public void ParseResult_AllTracks_Value_Context_Cut_StateMap_UnwrapThrows()
    {
        var ok = ParseResult.Ok(10);
        var inc = ParseResult.Need<int>(5);
        var incUnk = ParseResult.NeedUnknown<int>();
        var bt = ParseResult.Backtrack<int>(3, ErrorKind.Tag, "ctx");
        var fail = ParseResult.Fail<int>(7, ErrorKind.Protocol, "fatal");
        ParseResult<int> uninit = default;

        Assert.True(ok.HasValue && ok.IsParsed && !ok.IsError);
        Assert.True(inc.HasValue && inc.IsIncomplete && !inc.IsError);
        Assert.True(bt.HasValue && bt.IsBacktrack && bt.IsError);
        Assert.True(fail.HasValue && fail.IsFailure && fail.IsError);
        Assert.False(uninit.HasValue);

        Assert.Equal(new Parsed<int>(10), ok.Value);
        Assert.Equal(new Incomplete(Needed.Bytes(5)), inc.Value);
        Assert.Equal(new Incomplete(Needed.Unknown), incUnk.Value);
        Assert.True(bt.Value is Backtrack { Error.Offset: 3, Error.Context: "ctx" });
        Assert.True(fail.Value is Failure { Error.Offset: 7, Error.Context: "fatal" });
        Assert.True(uninit.Value is null);

        Assert.Equal(30, ok.Map(3, static (s, v) => s * v).Unwrap());
        Assert.True(bt.Map(3, static (s, v) => s * v).IsBacktrack);
        Assert.True(fail.Cut("outer").TryGetValue(out Failure fOuter) && fOuter.Error.Context == "outer");
        Assert.True(ok.Cut("ignored").IsParsed);
        Assert.True(inc.Cut("ignored").IsIncomplete);
        Assert.True(ok.WithContext("ignored").IsParsed);
        Assert.True(inc.WithContext("ignored").IsIncomplete);
        Assert.True(fail.WithContext("wrapped").TryGetValue(out Failure fWrapped) && fWrapped.Error.Context == "wrapped");

        Assert.Contains("Parsed(10)", ok.ToString());
        Assert.Contains("Incomplete(", inc.ToString());
        Assert.Contains("Backtrack(", bt.ToString());
        Assert.Contains("Failure(", fail.ToString());
        Assert.Equal("ParseResult(Uninitialized)", uninit.ToString());

        AssertThrows<InvalidOperationException>(() => _ = inc.Unwrap());
        AssertThrows<InvalidOperationException>(() => _ = bt.Unwrap());
        AssertThrows<InvalidOperationException>(() => _ = fail.Unwrap());
    }

    [Fact]
    public void Railroad_AllOverloads_And_ShortCircuitTracks()
    {
        // Bind with state
        Assert.Equal(25, ParseResult.Ok(5).Bind(5, static (s, v) => ParseResult.Ok(s * v)).Unwrap());
        Assert.True(ParseResult.Backtrack<int>(1, ErrorKind.Tag).Bind(5, static (s, v) => ParseResult.Ok(s * v)).IsBacktrack);

        // Then short-circuits on incoming error without calling continuation, and rolls back on failing continuation
        ReadOnlySpan<byte> data = [0xAA, 0xBB];
        var c = data.AsCursor();
        bool called = false;
        var shortCircuited = ParseResult.Backtrack<byte>(0, ErrorKind.Tag)
            .Then(ref c, (byte _, ref ByteCursor cur) => { called = true; return cur.ReadU8(); });
        Assert.False(called);
        Assert.True(shortCircuited.IsBacktrack);
        Assert.Equal(0, c.Position);

        var rolledBack = c.ReadU8()
            .Then(ref c, static (byte first, ref ByteCursor cur) =>
            {
                _ = cur.ReadU8();
                return ParseResult.Backtrack<byte>(cur.Position, ErrorKind.Verify);
            });
        Assert.True(rolledBack.IsBacktrack);
        Assert.Equal(1, c.Position); // rolled back second step to position 1

        // MapError on all tracks + TapError on Failure
        Assert.Equal(10, ParseResult.Ok(10).MapError(static e => e.WithContext("x")).Unwrap());
        Assert.True(ParseResult.Need<int>(2).MapError(static e => e.WithContext("x")).IsIncomplete);
        Assert.True(ParseResult.Fail<int>(2, ErrorKind.Protocol).MapError(static e => e.WithContext("m"))
            .TryGetValue(out Failure fm) && fm.Error.Context == "m");

        ErrorKind seenFail = ErrorKind.None;
        _ = ParseResult.Fail<int>(2, ErrorKind.CrcMismatch).TapError(e => seenFail = e.Kind);
        Assert.Equal(ErrorKind.CrcMismatch, seenFail);

        // Ensure & EnsureCommitted on non-parsed pass through unchanged
        Assert.True(ParseResult.Need<int>(1).Ensure(static _ => false, 0).IsIncomplete);
        Assert.True(ParseResult.Need<int>(1).EnsureCommitted(static _ => false, 0).IsIncomplete);

        // Match on Incomplete and Failure
        Assert.Equal("inc", ParseResult.Need<int>(2).Match(static _ => "ok", static _ => "inc", static _ => "bt", static _ => "fail"));
        Assert.Equal("fail", ParseResult.Fail<int>(2, ErrorKind.Protocol).Match(static _ => "ok", static _ => "inc", static _ => "bt", static _ => "fail"));
    }

    [Fact]
    public void BitCursor_Boundaries_Errors_Alignment_And_WideReads()
    {
        ReadOnlySpan<byte> buf = [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x23, 0x45, 0x67, 0x89];
        var bits = new BitCursor(buf, baseByteOffset: 10);

        Assert.Equal(72, bits.TotalBits);
        Assert.Equal(10, bits.CurrentByteOffset);
        Assert.Equal((byte)0, bits.ReadU8(0).Unwrap());
        Assert.Equal(0UL, bits.ReadU64(0).Unwrap());

        // Range validation
        Assert.True(bits.ReadU8(-1).IsBacktrack);
        Assert.True(bits.ReadU8(9).IsBacktrack);
        Assert.True(bits.ReadU16(-1).IsBacktrack);
        Assert.True(bits.ReadU16(17).IsBacktrack);
        Assert.True(bits.ReadU32(-1).IsBacktrack);
        Assert.True(bits.ReadU32(33).IsBacktrack);
        Assert.True(bits.ReadU64(-1).IsBacktrack);
        Assert.True(bits.ReadU64(65).IsBacktrack);
        Assert.True(bits.ReadI32(0).IsBacktrack);
        Assert.True(bits.ReadI32(33).IsBacktrack);
        Assert.True(bits.SkipBits(-1).IsBacktrack);

        // Wide reads (U32, U64) & AlignToByte (both aligned and unaligned!)
        Assert.Equal(0xDEAD_BEEFu, bits.ReadU32(32).Unwrap());
        Assert.Equal(14, bits.CurrentByteOffset);
        bits.AlignToByte(); // already aligned -> no-op
        Assert.Equal(32, bits.BitPosition);

        Assert.Equal(0x0123_4567UL, bits.ReadU64(32).Unwrap());
        Assert.Equal(3, bits.SkipBits(3).Unwrap());
        bits.AlignToByte(); // unaligned -> advances to 72
        Assert.Equal(72, bits.BitPosition);

        // Complete mode EOF shortage
        Assert.True(bits.ReadBool().IsBacktrack);
        Assert.True(bits.ReadU8(1).IsBacktrack);
        Assert.True(bits.ReadU16(1).IsBacktrack);
        Assert.True(bits.ReadU32(1).IsBacktrack);
        Assert.True(bits.ReadI32(1).IsBacktrack);
        Assert.True(bits.SkipBits(1).IsBacktrack);

        // Streaming mode exact byte shortage
        var sBits = new BitCursor([0xFF], baseByteOffset: 0, mode: InputMode.Streaming);
        Assert.True(sBits.ReadU16(10).TryGetValue(out Incomplete inc1) && inc1.Needed == Needed.Bytes(1));
        Assert.True(sBits.ReadU32(25).TryGetValue(out Incomplete inc2) && inc2.Needed == Needed.Bytes(3));
        Assert.True(sBits.SkipBits(17).TryGetValue(out Incomplete inc3) && inc3.Needed == Needed.Bytes(2));
    }

    [Fact]
    public void StreamingDecoder_Compaction_Growth_Clear_And_FinalChunk()
    {
        AssertThrows<ArgumentOutOfRangeException>(() => _ = new StreamingDecoder<byte>(static (ref ByteCursor c) => c.ReadU8(), initialCapacity: 0));

        // 4-byte frame parser: U32BE
        var decoder = new StreamingDecoder<uint>(static (ref ByteCursor c) => c.ReadU32BE(), initialCapacity: 6);

        // Empty append is a no-op
        decoder.Append([]);
        Assert.Equal(0, decoder.BufferedCount);
        Assert.True(decoder.BufferedBytes.IsEmpty);

        // Append 5 bytes -> decode 1 frame (4 bytes consumed, 1 byte left at _readOffset=4)
        decoder.Append([0x00, 0x00, 0x00, 0x01, 0x00]);
        Assert.Equal(5, decoder.BufferedCount);
        Assert.Equal(1u, decoder.TryDecodeNext().Unwrap());
        Assert.Equal(1, decoder.BufferedCount);

        // Append 3 bytes -> triggers in-place compaction (1 + 3 = 4 <= capacity 6, but tail free was only 1!)
        decoder.Append([0x00, 0x00, 0x02]);
        Assert.Equal(4, decoder.BufferedCount);
        Assert.Equal(2u, decoder.TryDecodeNext().Unwrap());
        Assert.Equal(0, decoder.BufferedCount);

        // Append 10 bytes (> capacity 6) -> triggers buffer growth!
        decoder.Append([0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0xAA, 0xBB]);
        Assert.Equal(3u, decoder.TryDecodeNext().Unwrap());
        Assert.Equal(4u, decoder.TryDecodeNext().Unwrap());
        Assert.Equal(2, decoder.BufferedCount);

        // Streaming returns Incomplete; isFinalChunk=true returns Backtrack(Eof)
        Assert.True(decoder.TryDecodeNext(isFinalChunk: false).IsIncomplete);
        Assert.True(decoder.TryDecodeNext(isFinalChunk: true).IsBacktrack);

        // Clear resets offsets
        decoder.Clear();
        Assert.Equal(0, decoder.BufferedCount);
    }

    private static void AssertThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new AssertionException($"Expected {typeof(TException).Name} to be thrown.");
    }
}
