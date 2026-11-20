using System.Buffers;
using System.Runtime.InteropServices;
using SpanForge.Streaming;
using Xunit;

namespace SpanForge.Tests;

/// <summary>
/// Precision mutation-killing tests covering exact-boundary reads, <see cref="UInt128"/> / <see cref="Half"/>,
/// advance-then-fail rollback verification across <see cref="Parse"/>, <see cref="StructParsers"/>,
/// <see cref="FastParsers"/>, <see cref="ByteCursor"/>, and <see cref="StreamingDecoder{T}"/>.
/// </summary>
public sealed class CoreMutationSweepTests
{
    private static readonly SearchValues<byte> Seps = SearchValues.Create(",;"u8);

    // Helper struct parsers that advance cursor by 1 byte BEFORE failing/backtracking/returning Incomplete
    // so every `cursor.Reset(cp)` statement in StructParsers & FastParsers is strictly verified!
    private readonly struct AdvanceBacktrackParser : IParser<byte>
    {
        public ParseResult<byte> Parse(ref ByteCursor cursor)
        {
            _ = cursor.ReadU8();
            return Backtrack.At(cursor.Position, ErrorKind.Custom);
        }
    }

    private readonly struct AdvanceFailureParser : IParser<byte>
    {
        public ParseResult<byte> Parse(ref ByteCursor cursor)
        {
            _ = cursor.ReadU8();
            return Failure.At(cursor.Position, ErrorKind.Protocol);
        }
    }

    private readonly struct AdvanceIncompleteParser : IParser<byte>
    {
        public ParseResult<byte> Parse(ref ByteCursor cursor)
        {
            _ = cursor.ReadU8();
            return Incomplete.AtLeast(2);
        }
    }

    private readonly struct FastAdvanceBacktrackParser : IParser<byte, KindError>
    {
        public ParseResult<byte, KindError> Parse(ref ByteCursor cursor)
        {
            _ = cursor.ReadU8<KindError>();
            return new Backtrack<KindError>(new KindError(ErrorKind.Custom));
        }
    }

    private readonly struct FastAdvanceFailureParser : IParser<byte, KindError>
    {
        public ParseResult<byte, KindError> Parse(ref ByteCursor cursor)
        {
            _ = cursor.ReadU8<KindError>();
            return new Failure<KindError>(new KindError(ErrorKind.Protocol));
        }
    }

    private readonly struct FastAdvanceIncompleteParser : IParser<byte, KindError>
    {
        public ParseResult<byte, KindError> Parse(ref ByteCursor cursor)
        {
            _ = cursor.ReadU8<KindError>();
            return Incomplete.AtLeast(2);
        }
    }

    [Fact]
    public void ByteCursor_ExactLengths_U128_Half_ZeroIndexDelimiters_And_ErrorMessages()
    {
        // Exact-length buffers kill every `RemainingLength <= N` boundary mutant!
        var cI8 = new byte[] { 0xFE }.AsCursor();
        Assert.Equal((sbyte)-2, cI8.ReadI8().Unwrap());

        var cI16Be = new byte[] { 0xFF, 0xFE }.AsCursor();
        Assert.Equal((short)-2, cI16Be.ReadI16BE().Unwrap());
        var cI16Le = new byte[] { 0xFE, 0xFF }.AsCursor();
        Assert.Equal((short)-2, cI16Le.ReadI16LE().Unwrap());

        var cU24Be = new byte[] { 0x01, 0x02, 0x03 }.AsCursor();
        Assert.Equal(0x010203u, cU24Be.ReadU24BE().Unwrap());
        var cU24Le = new byte[] { 0x03, 0x02, 0x01 }.AsCursor();
        Assert.Equal(0x010203u, cU24Le.ReadU24LE().Unwrap());

        var cU32Le = new byte[] { 0x04, 0x03, 0x02, 0x01 }.AsCursor();
        Assert.Equal(0x01020304u, cU32Le.ReadU32LE().Unwrap());
        var cI32Be = new byte[] { 0x00, 0x00, 0x00, 0x07 }.AsCursor();
        Assert.Equal(7, cI32Be.ReadI32BE().Unwrap());
        var cI32Le = new byte[] { 0x07, 0x00, 0x00, 0x00 }.AsCursor();
        Assert.Equal(7, cI32Le.ReadI32LE().Unwrap());

        var cU64Be = new byte[] { 0, 0, 0, 0, 0, 0, 0, 9 }.AsCursor();
        Assert.Equal(9UL, cU64Be.ReadU64BE().Unwrap());
        var cI64Be = new byte[] { 0, 0, 0, 0, 0, 0, 0, 9 }.AsCursor();
        Assert.Equal(9L, cI64Be.ReadI64BE().Unwrap());
        var cI64Le = new byte[] { 9, 0, 0, 0, 0, 0, 0, 0 }.AsCursor();
        Assert.Equal(9L, cI64Le.ReadI64LE().Unwrap());

        var cF32Be = new byte[] { 0x3F, 0x80, 0x00, 0x00 }.AsCursor(Endianness.Big);
        Assert.Equal(1.0f, cF32Be.ReadF32().Unwrap());
        var cF32Le = new byte[] { 0x00, 0x00, 0x80, 0x3F }.AsCursor(Endianness.Little);
        Assert.Equal(1.0f, cF32Le.ReadF32().Unwrap());
        var cF64Be = new byte[] { 0x3F, 0xF0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }.AsCursor();
        Assert.Equal(1.0, cF64Be.ReadF64BE().Unwrap());
        var cF64Le = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF0, 0x3F }.AsCursor();
        Assert.Equal(1.0, cF64Le.ReadF64LE().Unwrap());

        // ReadU128BE, ReadU128LE, ReadU128 (exact 16 bytes + shortage + position advance)
        byte[] u128BeBytes = [0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 2];
        var c128Be = u128BeBytes.AsCursor(Endianness.Big);
        Assert.Equal(new UInt128(1UL, 2UL), c128Be.ReadU128().Unwrap());
        Assert.Equal(16, c128Be.Position);
        Assert.True(c128Be.ReadU128BE().IsBacktrack);

        byte[] u128LeBytes = [2, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0];
        var c128Le = u128LeBytes.AsCursor(Endianness.Little);
        Assert.Equal(new UInt128(1UL, 2UL), c128Le.ReadU128().Unwrap());
        Assert.Equal(16, c128Le.Position);
        Assert.True(c128Le.ReadU128LE().IsBacktrack);

        // ReadF16 (BE, LE, exact 2 bytes + shortage + position advance)
        byte[] halfBe = [0x3C, 0x00]; // 1.0 in IEEE 754 binary16 BE
        var cHalfBe = halfBe.AsCursor(Endianness.Big);
        Assert.Equal((Half)1.0f, cHalfBe.ReadF16().Unwrap());
        Assert.Equal(2, cHalfBe.Position);
        Assert.True(cHalfBe.ReadF16().IsBacktrack);

        byte[] halfLe = [0x00, 0x3C]; // 1.0 in IEEE 754 binary16 LE
        var cHalfLe = halfLe.AsCursor(Endianness.Little);
        Assert.Equal((Half)1.0f, cHalfLe.ReadF16().Unwrap());
        Assert.Equal(2, cHalfLe.Position);

        // Exact 1-byte QUIC varint & PeekU8 & PeekSlice(exact & 0) & Skip(exact) & ReadBlittable position check
        var cQuic1 = new byte[] { 0x25 }.AsCursor();
        Assert.Equal(37UL, cQuic1.ReadQuicVarInt64().Unwrap());
        var cPeek1 = new byte[] { 0x55 }.AsCursor();
        Assert.Equal((byte)0x55, cPeek1.PeekU8().Unwrap());
        Assert.Equal(0, cPeek1.PeekSlice(0).Unwrap().Length);
        Assert.Equal(1, cPeek1.PeekSlice(1).Unwrap().Length);
        Assert.Equal(1, cPeek1.Skip(1).Unwrap());
        Assert.True(cPeek1.IsAtEnd);

        Span<byte> blitBytes = stackalloc byte[4];
        int val = 0x12345678;
        MemoryMarshal.Write(blitBytes, in val);
        var cBlit = new ByteCursor(blitBytes);
        Assert.Equal(val, cBlit.ReadBlittable<int>().Unwrap());
        Assert.Equal(4, cBlit.Position);

        // Index-0 delimiter matches kill all `idx <= 0` mutants, and non-zero idx checks `_position += idx`
        var cIdx0 = ",abc"u8.AsCursor();
        Assert.Equal(0, cIdx0.TakeUntil((byte)',').Unwrap().Length);
        Assert.Equal(0, cIdx0.TakeUntil(","u8).Unwrap().Length);
        Assert.Equal(0, cIdx0.TakeUntilAny(Seps).Unwrap().Length);

        var cIdxPos = "ab;cd"u8.AsCursor();
        Assert.Equal(2, cIdxPos.TakeUntilAny(Seps).Unwrap().Length);
        Assert.Equal(2, cIdxPos.Position);

        var cCstr0 = new byte[] { 0x00, 0x41 }.AsCursor();
        Assert.Equal(0, cCstr0.ReadCString().Unwrap().Length);
        Assert.Equal(1, cCstr0.Position);

        var cCrlf0 = "\r\nab"u8.AsCursor();
        Assert.Equal(0, cCrlf0.TakeCrlfLine().Unwrap().Length);
        Assert.Equal(2, cCrlf0.Position);

        // TakeWhile(predicate) complete mode consumes all without Incomplete
        var cPredAll = "123"u8.AsCursor();
        Assert.Equal("123", cPredAll.TakeWhile(static b => b is >= (byte)'0' and <= (byte)'9').Unwrap().AsUtf8String("123"u8));

        // ReadLengthPrefixedBytes header failures at non-zero offset (kills NoCoverage rollback mutants!)
        byte[] prefixOnly = [0xAA];
        foreach (var lp in new[] { LengthPrefix.U8, LengthPrefix.U16, LengthPrefix.U32, LengthPrefix.VarUInt64 })
        {
            var clp = prefixOnly.AsCursor();
            clp.Position = 1;
            Assert.True(clp.ReadLengthPrefixedBytes(lp).IsBacktrack);
            Assert.Equal(1, clp.Position);
        }

        // Assert non-empty error messages across ByteCursor error paths
        Assert.True(new byte[] { 2 }.AsCursor().ReadBool(strict: true).TryGetValue(out Backtrack btB) && !string.IsNullOrEmpty(btB.Error.Context));
        Assert.True(ReadOnlySpan<byte>.Empty.AsCursor().ReadVarUInt64().TryGetValue(out Backtrack btV1) && !string.IsNullOrEmpty(btV1.Error.Context));
        Assert.True(new byte[] { 0x80 }.AsCursor().ReadVarUInt64().TryGetValue(out Backtrack btV2) && !string.IsNullOrEmpty(btV2.Error.Context));
        Assert.True(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x02 }.AsCursor().ReadVarUInt64().TryGetValue(out Backtrack btV3) && !string.IsNullOrEmpty(btV3.Error.Context));
        Assert.True(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 }.AsCursor().ReadVarUInt64().TryGetValue(out Backtrack btV4) && !string.IsNullOrEmpty(btV4.Error.Context));
        Assert.True(new byte[] { 1 }.AsCursor().Take(-1).TryGetValue(out Backtrack btTk) && !string.IsNullOrEmpty(btTk.Error.Context));
        Assert.True(new byte[] { 1 }.AsCursor().Skip(-1).TryGetValue(out Backtrack btSk) && !string.IsNullOrEmpty(btSk.Error.Context));
        Assert.True(new byte[] { 1 }.AsCursor().Align(0).TryGetValue(out Backtrack btAl) && !string.IsNullOrEmpty(btAl.Error.Context));
        Assert.True(new byte[] { 1 }.AsCursor().ReadLengthPrefixedBytes((LengthPrefix)99).TryGetValue(out Backtrack btLp1) && !string.IsNullOrEmpty(btLp1.Error.Context));
        Assert.True(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }.AsCursor().ReadLengthPrefixedBytes(LengthPrefix.U32).TryGetValue(out Backtrack btLp2) && !string.IsNullOrEmpty(btLp2.Error.Context));
        Assert.True(new byte[] { 0xFF, 0xFE }.AsCursor().ReadUtf8String(2).TryGetValue(out Backtrack btUtf) && !string.IsNullOrEmpty(btUtf.Error.Context));
        Assert.True(new byte[] { 1 }.AsCursor().PeekSlice(-1).TryGetValue(out Backtrack btPk) && !string.IsNullOrEmpty(btPk.Error.Context));
        Assert.True(new byte[] { 1 }.AsCursor().ExpectEof().TryGetValue(out Backtrack btEof) && !string.IsNullOrEmpty(btEof.Error.Context));
    }

    [Fact]
    public void ByteCursor_Fast_ExactLengths_MaxVarInt_And_LengthPrefixedRollbacks()
    {
        // Exact-length buffers for every Fast reader
        var c8 = new byte[] { 0x42 }.AsCursor();
        Assert.Equal((byte)0x42, c8.ReadU8<KindError>().Unwrap());
        Assert.True(c8.ReadU8<KindError>().IsBacktrack);

        var c16Be = new byte[] { 0x12, 0x34 }.AsCursor();
        Assert.Equal((ushort)0x1234, c16Be.ReadU16BE<KindError>().Unwrap());
        var c16Le = new byte[] { 0x34, 0x12 }.AsCursor();
        Assert.Equal((ushort)0x1234, c16Le.ReadU16LE<KindError>().Unwrap());

        var c32Le = new byte[] { 4, 3, 2, 1 }.AsCursor();
        Assert.Equal(0x01020304u, c32Le.ReadU32LE<KindError>().Unwrap());

        var c64Be = new byte[] { 0, 0, 0, 0, 0, 0, 0, 7 }.AsCursor();
        Assert.Equal(7UL, c64Be.ReadU64BE<KindError>().Unwrap());
        var c64Le = new byte[] { 7, 0, 0, 0, 0, 0, 0, 0 }.AsCursor();
        Assert.Equal(7UL, c64Le.ReadU64LE<KindError>().Unwrap());

        var cF64Be = new byte[] { 0x3F, 0xF0, 0, 0, 0, 0, 0, 0 }.AsCursor();
        Assert.Equal(1.0, cF64Be.ReadF64BE<KindError>().Unwrap());
        var cF64Le = new byte[] { 0, 0, 0, 0, 0, 0, 0xF0, 0x3F }.AsCursor();
        Assert.Equal(1.0, cF64Le.ReadF64LE<KindError>().Unwrap());

        // Tag<TError> at non-zero start with exact remaining length
        byte[] tagBuf = [0xAA, (byte)'O', (byte)'K'];
        var cTag = tagBuf.AsCursor(); cTag.Position = 1;
        Assert.Equal("OK", cTag.Tag<KindError>("OK"u8).Unwrap().AsUtf8String(tagBuf));

        // Take<TError>(0) succeeds
        Assert.Equal(0, cTag.Take<KindError>(0).Unwrap().Length);

        // ReadVarUInt64<TError>: empty buffer, 3-byte varint (kills shift -= 7), and ulong.MaxValue (10 bytes, b == 1!)
        var cEmpty = ReadOnlySpan<byte>.Empty.AsCursor();
        Assert.True(cEmpty.ReadVarUInt64<KindError>().IsBacktrack);

        var cMax = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 }.AsCursor();
        Assert.Equal(ulong.MaxValue, cMax.ReadVarUInt64<KindError>().Unwrap());

        var c3Byte = new byte[] { 0x80, 0x80, 0x01 }.AsCursor(); // 16384
        Assert.Equal(16384UL, c3Byte.ReadVarUInt64<KindError>().Unwrap());

        // ReadLengthPrefixedBytes<TError> header failure & payload shortage at non-zero offset
        byte[] oneByte = [0xAA];
        foreach (var lp in new[] { LengthPrefix.U8, LengthPrefix.U16, LengthPrefix.U32, LengthPrefix.VarUInt64 })
        {
            var clp = oneByte.AsCursor(); clp.Position = 1;
            Assert.True(clp.ReadLengthPrefixedBytes<KindError>(lp).IsBacktrack);
            Assert.Equal(1, clp.Position);
        }

        byte[] truncBody = [0xAA, 0x05, 0x01];
        var ctb = truncBody.AsCursor(); ctb.Position = 1;
        Assert.True(ctb.ReadLengthPrefixedBytes<KindError>(LengthPrefix.U8).IsBacktrack);
        Assert.Equal(1, ctb.Position);
    }

    [Fact]
    public void Parse_StructParsers_FastParsers_AdvanceThenFail_Rollbacks_And_Messages()
    {
        static ParseResult<byte> AdvBt(ref ByteCursor c) { _ = c.ReadU8(); return Backtrack.At(c.Position, ErrorKind.Custom); }
        static ParseResult<byte> AdvFail(ref ByteCursor c) { _ = c.ReadU8(); return Failure.At(c.Position, ErrorKind.Protocol); }

        byte[] buf = [0xAA, 0x01, 0x02, 0x03, 0x04, 0x05];

        // Parse.Cut with null context vs non-null context
        var cCut = buf.AsCursor(); cCut.Position = 1;
        Assert.True(Parse.Cut(ref cCut, static (ref ByteCursor x) => x.TagByte(9)).TryGetValue(out Failure fNull) && fNull.Error.Context is null);
        Assert.True(Parse.Cut(ref cCut, static (ref ByteCursor x) => x.TagByte(9), "msg").TryGetValue(out Failure fMsg) && fMsg.Error.Context == "msg");

        // Parse.Alt 2/3/4/span: advance-then-backtrack resets before next branch; advance-then-fail/inc resets on short-circuit!
        var c = buf.AsCursor();
        c.Position = 1; Assert.True(Parse.Alt(ref c, AdvFail, static (ref ByteCursor x) => x.ReadU8()).IsFailure); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.Equal((byte)1, Parse.Alt(ref c, AdvBt, static (ref ByteCursor x) => x.ReadU8()).Unwrap()); Assert.Equal(2, c.Position);

        c.Position = 1; Assert.True(Parse.Alt(ref c, AdvFail, AdvBt, AdvBt).IsFailure); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.Equal((byte)1, Parse.Alt(ref c, AdvBt, AdvBt, static (ref ByteCursor x) => x.ReadU8()).Unwrap()); Assert.Equal(2, c.Position);

        c.Position = 1; Assert.True(Parse.Alt(ref c, AdvFail, AdvBt, AdvBt, AdvBt).IsFailure); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(Parse.Alt(ref c, AdvBt, AdvFail, AdvBt, AdvBt).IsFailure); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.Equal((byte)1, Parse.Alt(ref c, AdvBt, AdvBt, AdvBt, static (ref ByteCursor x) => x.ReadU8()).Unwrap()); Assert.Equal(2, c.Position);

        c.Position = 1; Assert.Equal((byte)1, Parse.Alt(ref c, AdvBt, AdvBt, AdvBt, AdvBt, static (ref ByteCursor x) => x.ReadU8()).Unwrap()); Assert.Equal(2, c.Position);

        // Parse.WithSlice at non-zero start kills `cursor.Position + cp.Position`!
        c.Position = 2;
        var (wsVal, wsSlice) = Parse.WithSlice(ref c, static (ref ByteCursor x) => x.ReadU16BE()).Unwrap();
        Assert.Equal((ushort)0x0203, wsVal);
        Assert.Equal(new ByteSlice(2, 2), wsSlice);

        // Preceded, Terminated, Delimited, Pair, SeparatedPair, AllConsuming with AdvanceThenBacktrack
        c.Position = 1; Assert.True(Parse.Preceded(ref c, AdvBt, static (ref ByteCursor x) => x.ReadU8()).IsBacktrack); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(Parse.Preceded(ref c, static (ref ByteCursor x) => x.ReadU8(), AdvBt).IsBacktrack); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(Parse.Terminated(ref c, AdvBt, static (ref ByteCursor x) => x.ReadU8()).IsBacktrack); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(Parse.Delimited(ref c, AdvBt, static (ref ByteCursor x) => x.ReadU8(), static (ref ByteCursor x) => x.ReadU8()).IsBacktrack); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(Parse.Pair(ref c, AdvBt, static (ref ByteCursor x) => x.ReadU8()).IsBacktrack); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(Parse.SeparatedPair(ref c, AdvBt, static (ref ByteCursor x) => x.ReadU8(), static (ref ByteCursor x) => x.ReadU8()).IsBacktrack); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(Parse.AllConsuming(ref c, AdvBt).IsBacktrack); Assert.Equal(1, c.Position);

        // ScopedEndian restores endianness on success
        var cEnd = new byte[] { 0x01, 0x02 }.AsCursor(Endianness.Big);
        Assert.Equal((ushort)0x0201, Parse.ScopedEndian(ref cEnd, Endianness.Little, static (ref ByteCursor x) => x.ReadU16()).Unwrap());
        Assert.Equal(Endianness.Big, cEnd.Endianness);

        // LengthSlice: len == 0 succeeds (kills <= 0!), lenParser AdvBt resets, Take failure resets, negative has message
        c.Position = 1; Assert.Equal(0, Parse.LengthSlice(ref c, static (ref ByteCursor _) => ParseResult.Ok(0)).Unwrap().Length);
        c.Position = 1; Assert.True(Parse.LengthSlice(ref c, static (ref ByteCursor x) => { _ = x.ReadU8(); return Backtrack.At(x.Position, ErrorKind.Custom); }).IsBacktrack); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(Parse.LengthSlice(ref c, static (ref ByteCursor x) => { _ = x.ReadU8(); return ParseResult.Ok(99); }).IsBacktrack); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(Parse.LengthSlice(ref c, static (ref ByteCursor x) => { _ = x.ReadU8(); return ParseResult.Ok(-1); }).TryGetValue(out Backtrack btNeg) && !string.IsNullOrEmpty(btNeg.Error.Context)); Assert.Equal(1, c.Position);

        // LengthValue: LengthSlice fails after advancing + trailing error message non-empty
        c.Position = 1; Assert.True(Parse.LengthValue(ref c, static (ref ByteCursor x) => { _ = x.ReadU8(); return ParseResult.Ok(99); }, static (ref ByteCursor x) => x.ReadU8()).IsBacktrack); Assert.Equal(1, c.Position);
        var cLv = new byte[] { 0x00, 0x03, 1, 2, 3 }.AsCursor();
        Assert.True(Parse.LengthValue(ref cLv, static (ref ByteCursor x) => x.ReadU16BE().Map(static u => (int)u), static (ref ByteCursor s) => s.ReadU8())
            .TryGetValue(out Backtrack btLv) && !string.IsNullOrEmpty(btLv.Error.Context));

        // Count negative message
        Assert.True(Parse.Count(ref c, -1, static (ref ByteCursor x) => x.ReadU8()).TryGetValue(out Backtrack btCnt) && !string.IsNullOrEmpty(btCnt.Error.Context));

        // Many0 / FoldMany0 / RepeatInto: inner advances then backtracks (resets iterCp!), zero-progress resets cp & has message
        c.Position = 1;
        Assert.Equal(0, Parse.Many0(ref c, AdvBt).Unwrap().Count);
        Assert.Equal(1, c.Position);
        Assert.True(Parse.Many0(ref c, static (ref ByteCursor _) => ParseResult.Ok((byte)0)).TryGetValue(out Failure fM0) && !string.IsNullOrEmpty(fM0.Error.Context));

        c.Position = 1;
        Assert.Equal(0, Parse.FoldMany0(ref c, AdvBt, 0, static (a, b) => a + b).Unwrap());
        Assert.Equal(1, c.Position);
        Assert.True(Parse.FoldMany0(ref c, static (ref ByteCursor _) => ParseResult.Ok((byte)0), 0, static (a, b) => a + b).TryGetValue(out Failure fFm0) && !string.IsNullOrEmpty(fFm0.Error.Context));

        Span<byte> dest2 = stackalloc byte[2];
        c.Position = 1;
        Assert.Equal(0, Parse.RepeatInto(ref c, dest2, AdvBt).Unwrap());
        Assert.Equal(1, c.Position);
        // More bytes available (5) than destination.Length (2) -> kills `written <= destination.Length`!
        Assert.Equal(2, Parse.RepeatInto(ref c, dest2, static (ref ByteCursor x) => x.ReadU8(), minCount: 2).Unwrap());
        Assert.Equal(3, c.Position);
        Assert.True(Parse.RepeatInto(ref c, dest2, static (ref ByteCursor _) => ParseResult.Ok((byte)0)).TryGetValue(out Failure fRp) && !string.IsNullOrEmpty(fRp.Error.Context));
        c.Position = 5;
        Assert.True(Parse.RepeatInto(ref c, dest2, static (ref ByteCursor x) => x.ReadU8(), minCount: 2).TryGetValue(out Backtrack btRp) && !string.IsNullOrEmpty(btRp.Error.Context));

        // Separated0: AdvBt on first resets, AdvBt on sep resets, AdvFail on item after sep resets cp!
        c.Position = 1;
        Assert.Equal(0, Parse.Separated0(ref c, AdvBt, static (ref ByteCursor x) => x.ReadU8()).Unwrap().Count);
        Assert.Equal(1, c.Position);
        Assert.Equal(1, Parse.Separated0(ref c, static (ref ByteCursor x) => x.TagByte(1), AdvBt).Unwrap().Count);
        Assert.Equal(2, c.Position);
        c.Position = 1;
        int itemCalls = 0;
        Assert.True(Parse.Separated0(ref c, (ref ByteCursor x) => itemCalls++ == 0 ? x.ReadU8() : AdvFail(ref x), static (ref ByteCursor x) => x.ReadU8()).IsFailure);
        Assert.Equal(1, c.Position);

        // StructParsers: ALL advance-then-fail/backtrack/incomplete rollback checks!
        c.Position = 1;
        Assert.True(StructParsers.Cut<AdvanceBacktrackParser, byte>(default).Parse(ref c).TryGetValue(out Failure sCutNull) && sCutNull.Error.Context is null);
        c.Position = 1; Assert.True(StructParsers.Opt<AdvanceBacktrackParser, byte>(default).Parse(ref c).Unwrap().IsNone); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(StructParsers.Preceded<AdvanceBacktrackParser, StructParsers.U8Parser, byte, byte>(default, default).Parse(ref c).IsBacktrack); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(StructParsers.Delimited<AdvanceBacktrackParser, StructParsers.U8Parser, StructParsers.U8Parser, byte, byte, byte>(default, default, default).Parse(ref c).IsBacktrack); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(StructParsers.Delimited<StructParsers.U8Parser, AdvanceBacktrackParser, StructParsers.U8Parser, byte, byte, byte>(default, default, default).Parse(ref c).IsBacktrack); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(StructParsers.Delimited<StructParsers.U8Parser, StructParsers.U8Parser, AdvanceBacktrackParser, byte, byte, byte>(default, default, default).Parse(ref c).IsBacktrack); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(StructParsers.Pair<AdvanceBacktrackParser, StructParsers.U8Parser, byte, byte>(default, default).Parse(ref c).IsBacktrack); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(StructParsers.Alt<AdvanceFailureParser, StructParsers.U8Parser, byte>(default, default).Parse(ref c).IsFailure); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.Equal((byte)1, StructParsers.Alt<AdvanceBacktrackParser, StructParsers.U8Parser, byte>(default, default).Parse(ref c).Unwrap()); Assert.Equal(2, c.Position);
        c.Position = 1; Assert.True(StructParsers.Alt<AdvanceFailureParser, StructParsers.U8Parser, StructParsers.U8Parser, byte>(default, default, default).Parse(ref c).IsFailure); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(StructParsers.Alt<AdvanceBacktrackParser, AdvanceFailureParser, StructParsers.U8Parser, byte>(default, default, default).Parse(ref c).IsFailure); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.Equal((byte)1, StructParsers.Alt<AdvanceBacktrackParser, AdvanceBacktrackParser, StructParsers.U8Parser, byte>(default, default, default).Parse(ref c).Unwrap()); Assert.Equal(2, c.Position);

        // FastParsers: ALL advance-then-fail/backtrack/incomplete rollback checks!
        c.Position = 1; Assert.True(FastParsers.Opt<FastAdvanceBacktrackParser, byte, KindError>(default).Parse(ref c).Unwrap().IsNone); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(FastParsers.Opt<FastAdvanceFailureParser, byte, KindError>(default).Parse(ref c).IsFailure); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(FastParsers.Opt<FastAdvanceIncompleteParser, byte, KindError>(default).Parse(ref c).IsIncomplete); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(FastParsers.Preceded<FastAdvanceBacktrackParser, FastParsers.U8Parser<KindError>, byte, byte, KindError>(default, default).Parse(ref c).IsBacktrack); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(FastParsers.Preceded<FastParsers.U8Parser<KindError>, FastAdvanceBacktrackParser, byte, byte, KindError>(default, default).Parse(ref c).IsBacktrack); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(FastParsers.Delimited<FastAdvanceBacktrackParser, FastParsers.U8Parser<KindError>, FastParsers.U8Parser<KindError>, byte, byte, byte, KindError>(default, default, default).Parse(ref c).IsBacktrack); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(FastParsers.Delimited<FastParsers.U8Parser<KindError>, FastAdvanceBacktrackParser, FastParsers.U8Parser<KindError>, byte, byte, byte, KindError>(default, default, default).Parse(ref c).IsBacktrack); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(FastParsers.Delimited<FastParsers.U8Parser<KindError>, FastParsers.U8Parser<KindError>, FastAdvanceBacktrackParser, byte, byte, byte, KindError>(default, default, default).Parse(ref c).IsBacktrack); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(FastParsers.Pair<FastAdvanceBacktrackParser, FastParsers.U8Parser<KindError>, byte, byte, KindError>(default, default).Parse(ref c).IsBacktrack); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(FastParsers.Pair<FastParsers.U8Parser<KindError>, FastAdvanceBacktrackParser, byte, byte, KindError>(default, default).Parse(ref c).IsBacktrack); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(FastParsers.Alt<FastAdvanceFailureParser, FastParsers.U8Parser<KindError>, byte, KindError>(default, default).Parse(ref c).IsFailure); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.Equal((byte)1, FastParsers.Alt<FastAdvanceBacktrackParser, FastParsers.U8Parser<KindError>, byte, KindError>(default, default).Parse(ref c).Unwrap()); Assert.Equal(2, c.Position);
        c.Position = 1; Assert.True(FastParsers.Alt<FastAdvanceBacktrackParser, FastAdvanceFailureParser, byte, KindError>(default, default).Parse(ref c).IsFailure); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(FastParsers.Alt<FastAdvanceBacktrackParser, FastAdvanceBacktrackParser, byte, KindError>(default, default).Parse(ref c).TryGetValue(out Backtrack<KindError> fAlt2Bt) && fAlt2Bt.Error.Kind == ErrorKind.Alt); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(FastParsers.Alt<FastAdvanceFailureParser, FastParsers.U8Parser<KindError>, FastParsers.U8Parser<KindError>, byte, KindError>(default, default, default).Parse(ref c).IsFailure); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(FastParsers.Alt<FastAdvanceBacktrackParser, FastAdvanceFailureParser, FastParsers.U8Parser<KindError>, byte, KindError>(default, default, default).Parse(ref c).IsFailure); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.True(FastParsers.Alt<FastAdvanceBacktrackParser, FastAdvanceBacktrackParser, FastAdvanceFailureParser, byte, KindError>(default, default, default).Parse(ref c).IsFailure); Assert.Equal(1, c.Position);
        c.Position = 1; Assert.Equal((byte)1, FastParsers.Alt<FastAdvanceBacktrackParser, FastAdvanceBacktrackParser, FastParsers.U8Parser<KindError>, byte, KindError>(default, default, default).Parse(ref c).Unwrap()); Assert.Equal(2, c.Position);
    }

    [Fact]
    public void Option_ParseResult_FastParseResult_And_StreamingDecoder_RemainingMutants()
    {
        // Option TryGetValue(out None) false on Some & GetHashCode
        var s0 = Option<int>.Of(0);
        Assert.False(s0.TryGetValue(out None _));
        Assert.True(Option<int>.Nothing.TryGetValue(out None _));
        Xunit.Assert.NotEqual(s0.GetHashCode(), Option<int>.Nothing.GetHashCode());

        // ParseResult & FastParseResult Unwrap exception messages + Verify on non-parsed + WithContext cause chain
        var inc = ParseResult.Need<int>(4);
        var bt = ParseResult.Backtrack<int>(2, ErrorKind.Tag, "inner").WithContext("outer");
        var fail = ParseResult.Fail<int>(3, ErrorKind.Protocol, "bad");
        Assert.True(bt.TryGetValue(out Backtrack btChained) && btChained.Error.Cause is not null);
        Assert.True(bt.Verify(static _ => false, 9).TryGetValue(out Backtrack btSame) && btSame.Error.Kind == ErrorKind.Tag);

        var exInc = Xunit.Assert.Throws<InvalidOperationException>(() => _ = inc.Unwrap());
        Assert.Contains("incomplete", exInc.Message);
        var exBt = Xunit.Assert.Throws<InvalidOperationException>(() => _ = bt.Unwrap());
        Assert.Contains("backtracked", exBt.Message);
        var exFail = Xunit.Assert.Throws<InvalidOperationException>(() => _ = fail.Unwrap());
        Assert.Contains("failed", exFail.Message);

        var fInc = FastParseResult.Need<int, KindError>(4);
        var fBt = FastParseResult.Backtrack<int, KindError>(2, ErrorKind.Tag);
        var fFail = FastParseResult.Fail<int, KindError>(3, ErrorKind.Protocol);
        Assert.Contains("incomplete", Xunit.Assert.Throws<InvalidOperationException>(() => _ = fInc.Unwrap()).Message);
        Assert.Contains("backtracked", Xunit.Assert.Throws<InvalidOperationException>(() => _ = fBt.Unwrap()).Message);
        Assert.Contains("failed", Xunit.Assert.Throws<InvalidOperationException>(() => _ = fFail.Unwrap()).Message);

        // StreamingDecoder: BufferedBytes when _readOffset > 0, exact compaction, and growth > 2x vs <= 2x
        var dec = new StreamingDecoder<byte>(static (ref ByteCursor x) => x.ReadU8(), initialCapacity: 4);
        dec.Append([10, 20, 30]);
        Assert.Equal((byte)10, dec.TryDecodeNext().Unwrap()); // _readOffset = 1, _writeOffset = 3
        Assert.SequenceEqual<byte>([20, 30], dec.BufferedBytes);   // kills _writeOffset + _readOffset!

        // Append 2 bytes: freeAtTail=1 < 2, buffered=2, (4 - 2) == 2 == incoming -> exact in-place compact (no alloc!)
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        dec.Append([40, 50]);
        long allocCompact = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        Assert.Equal(0L, allocCompact); // kills `return;` removal & `>` boundary!
        Assert.SequenceEqual<byte>([20, 30, 40, 50], dec.BufferedBytes);

        // Drain all -> resets _readOffset = 0, _writeOffset = 0, so next 4-byte append allocates 0 bytes!
        Assert.Equal((byte)20, dec.TryDecodeNext().Unwrap());
        Assert.Equal((byte)30, dec.TryDecodeNext().Unwrap());
        Assert.Equal((byte)40, dec.TryDecodeNext().Unwrap());
        Assert.Equal((byte)50, dec.TryDecodeNext().Unwrap());

        long allocBeforeReuse = GC.GetAllocatedBytesForCurrentThread();
        dec.Append([1, 2, 3, 4]);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - allocBeforeReuse); // kills `_readOffset=0; _writeOffset=0` removal!

        // Grow where required (5) <= _buffer.Length * 2 (8) -> grows to 8 (kills Math.Min & _buffer.Length / 2!)
        dec.Append([5]);
        Assert.Equal(5, dec.BufferedCount);
        Assert.SequenceEqual<byte>([1, 2, 3, 4, 5], dec.BufferedBytes);

        // Grow where required > _buffer.Length * 2 (buffered=5 + 20 = 25 > 16) -> grows to 25 (kills buffered - incoming!)
        dec.Append(new byte[20]);
        Assert.Equal(25, dec.BufferedCount);
    }
}
