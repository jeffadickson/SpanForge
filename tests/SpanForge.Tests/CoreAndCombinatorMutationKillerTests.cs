using System.Buffers;
using System.Runtime.InteropServices;
using SpanForge.Streaming;
using Xunit;

namespace SpanForge.Tests;

/// <summary>
/// Comprehensive boundary, rollback, streaming, and error-path tests for <see cref="ByteCursor"/>,
/// <see cref="Parse"/>, <see cref="StructParsers"/>, <see cref="FastParsers"/>, and <see cref="StreamingDecoder{T}"/>.
/// </summary>
public sealed class CoreAndCombinatorMutationKillerTests
{
    private static readonly SearchValues<byte> Digits = SearchValues.Create("0123456789"u8);
    private static readonly SearchValues<byte> Seps = SearchValues.Create(",;"u8);

    [Fact]
    public void ByteCursor_AllNumericReaders_Endianness_PositionGuard_And_Shortages()
    {
        // Position setter bounds guards
        byte[] small = [1, 2, 3];
        Xunit.Assert.Throws<ArgumentOutOfRangeException>(() => { var c = small.AsCursor(); c.Position = -1; });
        Xunit.Assert.Throws<ArgumentOutOfRangeException>(() => { var c = small.AsCursor(); c.Position = 4; });

        var cp = small.AsCursor();
        cp.Position = 3;
        Assert.Equal(3, cp.Position);
        Assert.True(cp.IsAtEnd);

        // ReadBool strict vs non-strict & shortage
        byte[] bools = [0x00, 0x01, 0x02];
        var cb1 = bools.AsCursor();
        Assert.False(cb1.ReadBool().Unwrap());
        Assert.True(cb1.ReadBool().Unwrap());
        Assert.True(cb1.ReadBool(strict: false).Unwrap());
        Assert.True(cb1.ReadBool().IsBacktrack);

        var cb2 = bools.AsCursor();
        Assert.False(cb2.ReadBool(strict: true).Unwrap());
        Assert.True(cb2.ReadBool(strict: true).Unwrap());
        Assert.True(cb2.ReadBool(strict: true).TryGetValue(out Backtrack btBool) && btBool.Error.Kind == ErrorKind.Verify);
        Assert.Equal(2, cb2.Position);

        var cbStream = ReadOnlySpan<byte>.Empty.AsStreamingCursor();
        Assert.True(cbStream.ReadBool().IsIncomplete);

        // Signed & unsigned integers + floats in BE and LE + ReadI16/U24/I32/U64/I64/F32/F64
        byte[] nums =
        [
            0x80, 0x01,                                     // I16 BE = -32767
            0x01, 0x80,                                     // I16 LE = -32767
            0x01, 0x02, 0x03,                               // U24 BE = 0x010203
            0x03, 0x02, 0x01,                               // U24 LE = 0x010203
            0x80, 0x00, 0x00, 0x01,                         // I32 BE
            0x01, 0x00, 0x00, 0x80,                         // I32 LE
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, // U64 BE
            0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, // I64 BE
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, // I64 LE
            0x00, 0x00, 0x80, 0x3F,                         // F32 LE = 1.0f
            0x40, 0x09, 0x21, 0xFB, 0x54, 0x44, 0x2D, 0x18, // F64 BE (~pi)
            0x18, 0x2D, 0x44, 0x54, 0xFB, 0x21, 0x09, 0x40, // F64 LE (~pi)
        ];

        var c = nums.AsCursor(Endianness.Big);
        Assert.Equal((short)-32767, c.ReadI16().Unwrap());
        c.Endianness = Endianness.Little;
        Assert.Equal((short)-32767, c.ReadI16().Unwrap());
        c.Endianness = Endianness.Big;
        Assert.Equal(0x010203u, c.ReadU24().Unwrap());
        c.Endianness = Endianness.Little;
        Assert.Equal(0x010203u, c.ReadU24().Unwrap());
        c.Endianness = Endianness.Big;
        Assert.Equal(unchecked((int)0x80000001u), c.ReadI32().Unwrap());
        c.Endianness = Endianness.Little;
        Assert.Equal(unchecked((int)0x80000001u), c.ReadI32().Unwrap());
        c.Endianness = Endianness.Big;
        Assert.Equal(0x0102030405060708UL, c.ReadU64().Unwrap());
        Assert.Equal(unchecked((long)0x8000000000000001UL), c.ReadI64().Unwrap());
        c.Endianness = Endianness.Little;
        Assert.Equal(unchecked((long)0x8000000000000001UL), c.ReadI64().Unwrap());
        Assert.Equal(1.0f, c.ReadF32().Unwrap());
        c.Endianness = Endianness.Big;
        Xunit.Assert.Equal(Math.PI, c.ReadF64().Unwrap(), 12);
        c.Endianness = Endianness.Little;
        Xunit.Assert.Equal(Math.PI, c.ReadF64().Unwrap(), 12);
        Assert.True(c.IsAtEnd);

        // Shortages in Complete & Streaming across every width
        byte[] one = [0x01];
        var cs = one.AsStreamingCursor();
        Assert.True(cs.ReadU16BE().TryGetValue(out Incomplete inc16) && inc16.Needed == Needed.Bytes(1));
        Assert.True(cs.ReadU16LE().TryGetValue(out Incomplete inc16l) && inc16l.Needed == Needed.Bytes(1));
        Assert.True(cs.ReadI16BE().IsIncomplete);
        Assert.True(cs.ReadI16LE().IsIncomplete);
        Assert.True(cs.ReadU24BE().TryGetValue(out Incomplete inc24) && inc24.Needed == Needed.Bytes(2));
        Assert.True(cs.ReadU24LE().IsIncomplete);
        Assert.True(cs.ReadU32BE().TryGetValue(out Incomplete inc32) && inc32.Needed == Needed.Bytes(3));
        Assert.True(cs.ReadU32LE().IsIncomplete);
        Assert.True(cs.ReadI32BE().IsIncomplete);
        Assert.True(cs.ReadI32LE().IsIncomplete);
        Assert.True(cs.ReadU64BE().TryGetValue(out Incomplete inc64) && inc64.Needed == Needed.Bytes(7));
        Assert.True(cs.ReadU64LE().IsIncomplete);
        Assert.True(cs.ReadI64BE().IsIncomplete);
        Assert.True(cs.ReadI64LE().IsIncomplete);
        Assert.True(cs.ReadF32BE().IsIncomplete);
        Assert.True(cs.ReadF32LE().IsIncomplete);
        Assert.True(cs.ReadF64BE().IsIncomplete);
        Assert.True(cs.ReadF64LE().IsIncomplete);
        Assert.True(cs.ReadBlittable<int>().IsIncomplete);

        var c0 = ReadOnlySpan<byte>.Empty.AsCursor();
        Assert.True(c0.ReadU8().IsBacktrack);
        Assert.True(c0.ReadI8().IsBacktrack);
        Assert.True(c0.ReadVarUInt64().IsBacktrack);
        Assert.True(c0.ReadVarInt64().IsBacktrack);
        Assert.True(c0.ReadQuicVarInt64().IsBacktrack);
        Assert.True(c0.PeekU8().IsBacktrack);

        var cs0 = ReadOnlySpan<byte>.Empty.AsStreamingCursor();
        Assert.True(cs0.ReadVarUInt64().IsIncomplete);
        Assert.True(cs0.ReadVarInt64().IsIncomplete);
        Assert.True(cs0.ReadQuicVarInt64().IsIncomplete);

        // Truncated multi-byte LEB128 & QUIC varints + >10 byte LEB128
        byte[] truncLeb = [0x80];
        var ctlC = truncLeb.AsCursor();
        Assert.True(ctlC.ReadVarUInt64().IsBacktrack);
        Assert.Equal(0, ctlC.Position);
        var ctlS = truncLeb.AsStreamingCursor();
        Assert.True(ctlS.ReadVarUInt64().IsIncomplete);

        byte[] over10 = [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01];
        var co10 = over10.AsCursor();
        Assert.True(co10.ReadVarUInt64().TryGetValue(out Backtrack btO10) && btO10.Error.Kind == ErrorKind.VarInt);

        byte[] truncQuic = [0x40]; // 2-byte QUIC prefix, only 1 byte available
        var ctqS = truncQuic.AsStreamingCursor();
        Assert.True(ctqS.ReadQuicVarInt64().TryGetValue(out Incomplete incQ) && incQ.Needed == Needed.Bytes(1));
    }

    [Fact]
    public void ByteCursor_Scans_Framing_Align_Peek_And_LengthPrefixed()
    {
        // Take/Skip/PeekSlice negative & shortage + Align <= 0 & already aligned
        byte[] buf = "123abc\rX\r\n"u8.ToArray();
        var c = buf.AsCursor();
        Assert.True(c.Take(-1).IsBacktrack);
        Assert.True(c.Take(100).IsBacktrack);
        Assert.True(c.Skip(-1).IsBacktrack);
        Assert.True(c.Skip(100).IsBacktrack);
        Assert.True(c.PeekSlice(-1).IsBacktrack);
        Assert.True(c.PeekSlice(100).IsBacktrack);
        Assert.Equal("123", c.PeekSlice(3).Unwrap().AsUtf8String(buf));
        Assert.Equal((byte)'1', c.PeekU8().Unwrap());
        Assert.True(c.StartsWith("123"u8));
        Assert.False(c.StartsWith("999"u8));
        Assert.True(c.ExpectEof().IsBacktrack);

        Assert.True(c.Align(0).IsBacktrack);
        Assert.True(c.Align(-4).IsBacktrack);
        Assert.Equal(0, c.Align(4).Unwrap()); // already aligned at 0

        // TakeWhile / TakeWhile1 (SearchValues & predicate) in Complete & Streaming
        byte[] allDigits = "12345"u8.ToArray();
        var cdS = allDigits.AsStreamingCursor();
        Assert.True(cdS.TakeWhile(Digits).IsIncomplete);
        Assert.True(cdS.TakeWhile1(Digits).IsIncomplete);
        Assert.True(cdS.TakeWhile(static b => b is >= (byte)'0' and <= (byte)'9').IsIncomplete);
        Assert.True(cdS.TakeWhile1(static b => b is >= (byte)'0' and <= (byte)'9').IsIncomplete);

        var cdC = allDigits.AsCursor();
        Assert.Equal("12345", cdC.TakeWhile(Digits).Unwrap().AsUtf8String(allDigits));
        Assert.True(cdC.IsAtEnd);

        var cdPred = "123abc"u8.AsCursor();
        Assert.Equal("123", cdPred.TakeWhile1(static b => b is >= (byte)'0' and <= (byte)'9').Unwrap().AsUtf8String("123abc"u8));
        Assert.True(cdPred.TakeWhile1(Digits).IsBacktrack);
        Assert.True(cdPred.TakeWhile1(static b => b is >= (byte)'0' and <= (byte)'9').IsBacktrack);

        // TakeUntil(byte), TakeUntil(span), TakeUntilAny, ReadCString, TakeCrlfLine
        ReadOnlySpan<byte> line = "abc\rdef\r\nghi"u8;
        var cl = line.AsCursor();
        Assert.Equal("abc", cl.TakeUntil((byte)'\r').Unwrap().AsUtf8String(line));
        Assert.Equal(3, cl.Position);
        cl.Position = 0;
        Assert.Equal("abc\rdef", cl.TakeCrlfLine().Unwrap().AsUtf8String(line));
        Assert.Equal("ghi", cl.TakeRest().Unwrap().AsUtf8String(line));

        var cUntilSpan = "hello--world"u8.AsCursor();
        Assert.Equal("hello", cUntilSpan.TakeUntil("--"u8).Unwrap().AsUtf8String("hello--world"u8));
        Assert.Equal("", cUntilSpan.TakeUntil(""u8).Unwrap().AsUtf8String("hello--world"u8));

        // Not-found cases in Complete vs Streaming
        var noMatchC = "abcdef"u8.AsCursor();
        Assert.True(noMatchC.TakeUntil((byte)'z').IsBacktrack);
        Assert.True(noMatchC.TakeUntil("zz"u8).IsBacktrack);
        Assert.True(noMatchC.TakeUntilAny(Seps).IsBacktrack);
        Assert.True(noMatchC.ReadCString().IsBacktrack);
        Assert.True(noMatchC.TakeCrlfLine().IsBacktrack);

        var noMatchS = "abcdef"u8.AsStreamingCursor();
        Assert.True(noMatchS.TakeUntil((byte)'z').IsIncomplete);
        Assert.True(noMatchS.TakeUntil("zz"u8).IsIncomplete);
        Assert.True(noMatchS.TakeUntilAny(Seps).IsIncomplete);
        Assert.True(noMatchS.ReadCString().IsIncomplete);
        Assert.True(noMatchS.TakeCrlfLine().IsIncomplete);

        // ReadLengthPrefixedBytes: U16, U32, VarUInt64, invalid prefix, > int.MaxValue, truncated payload
        byte[] lp =
        [
            0x00, 0x02, (byte)'a', (byte)'b',
            0x00, 0x00, 0x00, 0x01, (byte)'c',
            0x02, (byte)'d', (byte)'e',
        ];
        var clp = lp.AsCursor();
        Assert.Equal("ab", clp.ReadLengthPrefixedBytes(LengthPrefix.U16).Unwrap().AsUtf8String(lp));
        Assert.Equal("c", clp.ReadLengthPrefixedBytes(LengthPrefix.U32).Unwrap().AsUtf8String(lp));
        Assert.Equal("de", clp.ReadLengthPrefixedBytes(LengthPrefix.VarUInt64).Unwrap().AsUtf8String(lp));
        Assert.True(clp.ReadLengthPrefixedBytes((LengthPrefix)99).IsBacktrack);

        byte[] lpHuge = [0xAA, 0xFF, 0xFF, 0xFF, 0xFF];
        var cLpHuge = lpHuge.AsCursor(); cLpHuge.Position = 1;
        Assert.True(cLpHuge.ReadLengthPrefixedBytes(LengthPrefix.U32).IsBacktrack);
        Assert.Equal(1, cLpHuge.Position);

        byte[] lpTrunc = [0xAA, 0x05, 0x01, 0x02];
        var cLpTrunc = lpTrunc.AsCursor(); cLpTrunc.Position = 1;
        Assert.True(cLpTrunc.ReadLengthPrefixedBytes(LengthPrefix.U8).IsBacktrack);
        Assert.Equal(1, cLpTrunc.Position);

        // ReadUtf8String shortage in streaming mode
        var cUtf8S = new byte[] { 0x41 }.AsStreamingCursor();
        Assert.True(cUtf8S.ReadUtf8String(3).IsIncomplete);
    }

    [Fact]
    public void Parse_AllCombinatorBranches_And_Rollbacks()
    {
        // Cut without context & Context wrapper
        byte[] data = [0xAA, 0x01, 0x02, 0x03];
        var c = data.AsCursor(); c.Position = 1;
        Assert.True(Parse.Cut(ref c, static (ref ByteCursor x) => x.TagByte(0x99)).IsFailure);
        Assert.True(Parse.Context(ref c, "outer", static (ref ByteCursor x) => x.TagByte(0x99))
            .TryGetValue(out Backtrack btCtx) && btCtx.Error.Context == "outer");

        // Opt: Parsed, Incomplete rollback, Failure rollback
        var cOpt = data.AsCursor(); cOpt.Position = 1;
        Assert.Equal((byte)0x01, Parse.Opt(ref cOpt, static (ref ByteCursor x) => x.TagByte(0x01)).Unwrap().UnwrapOr(0));
        var cOptInc = new byte[] { 0xAA }.AsStreamingCursor(); cOptInc.Position = 1;
        Assert.True(Parse.Opt(ref cOptInc, static (ref ByteCursor x) => x.ReadU16BE()).IsIncomplete);
        Assert.Equal(1, cOptInc.Position);
        var cOptFail = data.AsCursor(); cOptFail.Position = 1;
        Assert.True(Parse.Opt(ref cOptFail, static (ref ByteCursor x) => x.TagByte(0x99).Cut()).IsFailure);
        Assert.Equal(1, cOptFail.Position);

        // Alt 2, 3, 4, and params span: success on each branch, all-backtrack, Incomplete & Failure short-circuit
        static ParseResult<byte> T1(ref ByteCursor x) => x.TagByte(1);
        static ParseResult<byte> T2(ref ByteCursor x) => x.TagByte(2);
        static ParseResult<byte> T3(ref ByteCursor x) => x.TagByte(3);
        static ParseResult<byte> T4(ref ByteCursor x) => x.TagByte(4);
        static ParseResult<byte> FailAfter1(ref ByteCursor x) { _ = x.ReadU8(); return Failure.At(x.Position, ErrorKind.Protocol); }
        static ParseResult<byte> IncAfter1(ref ByteCursor x) { _ = x.ReadU8(); return Incomplete.AtLeast(2); }

        var ca3 = new byte[] { 3 }.AsCursor();
        Assert.Equal((byte)3, Parse.Alt(ref ca3, T1, T2, T3).Unwrap());
        var ca3Fail = new byte[] { 9 }.AsCursor();
        Assert.True(Parse.Alt(ref ca3Fail, T1, T2, T3).TryGetValue(out Backtrack bt3) && bt3.Error.Kind == ErrorKind.Alt);
        var ca3Cut = new byte[] { 9, 8 }.AsCursor(); ca3Cut.Position = 1;
        Assert.True(Parse.Alt(ref ca3Cut, T1, FailAfter1, T3).IsFailure);
        Assert.Equal(1, ca3Cut.Position);
        var ca3Inc = new byte[] { 9, 8 }.AsCursor(); ca3Inc.Position = 1;
        Assert.True(Parse.Alt(ref ca3Inc, T1, T2, IncAfter1).IsIncomplete);
        Assert.Equal(1, ca3Inc.Position);

        var ca4 = new byte[] { 4 }.AsCursor();
        Assert.Equal((byte)4, Parse.Alt(ref ca4, T1, T2, T3, T4).Unwrap());
        var ca4All = new byte[] { 9 }.AsCursor();
        Assert.True(Parse.Alt(ref ca4All, T1, T2, T3, T4).TryGetValue(out Backtrack bt4) && bt4.Error.Kind == ErrorKind.Alt);
        var ca4Fail = new byte[] { 9, 8 }.AsCursor(); ca4Fail.Position = 1;
        Assert.True(Parse.Alt(ref ca4Fail, T1, T2, FailAfter1, T4).IsFailure);
        Assert.Equal(1, ca4Fail.Position);
        var ca4Inc = new byte[] { 9, 8 }.AsCursor(); ca4Inc.Position = 1;
        Assert.True(Parse.Alt(ref ca4Inc, T1, T2, T3, IncAfter1).IsIncomplete);
        Assert.Equal(1, ca4Inc.Position);

        var caSpan = new byte[] { 9, 8 }.AsCursor(); caSpan.Position = 1;
        Assert.True(Parse.Alt(ref caSpan, T1, T2, T3, T4, FailAfter1).IsFailure);
        Assert.Equal(1, caSpan.Position);
        var caSpanBt = new byte[] { 9 }.AsCursor();
        Assert.True(Parse.Alt(ref caSpanBt, T1, T2, T3, T4, T1).TryGetValue(out Backtrack btSp) && btSp.Error.Kind == ErrorKind.Alt);

        // Recognize, WithSlice, Verify (true, false, inner error)
        var cRec = data.AsCursor(); cRec.Position = 1;
        Assert.Equal(new ByteSlice(1, 2), Parse.Recognize(ref cRec, static (ref ByteCursor x) => x.ReadU16BE()).Unwrap());
        var cRecErr = data.AsCursor(); cRecErr.Position = 1;
        Assert.True(Parse.Recognize(ref cRecErr, FailAfter1).IsFailure);
        Assert.Equal(1, cRecErr.Position);
        Assert.True(Parse.Recognize(ref cRecErr, IncAfter1).IsIncomplete);
        Assert.Equal(1, cRecErr.Position);
        Assert.True(Parse.WithSlice(ref cRecErr, FailAfter1).IsFailure);
        Assert.Equal(1, cRecErr.Position);
        Assert.True(Parse.WithSlice(ref cRecErr, IncAfter1).IsIncomplete);
        Assert.Equal(1, cRecErr.Position);

        var cVer = data.AsCursor(); cVer.Position = 1;
        Assert.Equal((byte)1, Parse.Verify(ref cVer, static (ref ByteCursor x) => x.ReadU8(), static b => b == 1).Unwrap());
        Assert.True(Parse.Verify(ref cVer, static (ref ByteCursor x) => x.ReadU8(), static b => b == 99, "bad").TryGetValue(out Backtrack btV) && btV.Error.Kind == ErrorKind.Verify);
        Assert.Equal(2, cVer.Position);
        Assert.True(Parse.Verify(ref cVer, FailAfter1, static _ => true).IsFailure);
        Assert.Equal(2, cVer.Position);

        // Terminated, Delimited, Pair, SeparatedPair, AllConsuming rollback paths
        var cSeq = data.AsCursor(); cSeq.Position = 1;
        Assert.Equal((byte)1, Parse.Terminated(ref cSeq, static (ref ByteCursor x) => x.ReadU8(), static (ref ByteCursor x) => x.TagByte(2)).Unwrap());
        Assert.Equal(3, cSeq.Position);
        cSeq.Position = 1;
        Assert.True(Parse.Terminated(ref cSeq, static (ref ByteCursor x) => x.ReadU8(), static (ref ByteCursor x) => x.TagByte(9)).IsBacktrack);
        Assert.Equal(1, cSeq.Position);
        Assert.True(Parse.Terminated(ref cSeq, static (ref ByteCursor x) => x.TagByte(9), static (ref ByteCursor x) => x.ReadU8()).IsBacktrack);

        Assert.True(Parse.Delimited(ref cSeq, static (ref ByteCursor x) => x.TagByte(1), static (ref ByteCursor x) => x.TagByte(9), static (ref ByteCursor x) => x.TagByte(3)).IsBacktrack);
        Assert.Equal(1, cSeq.Position);
        Assert.True(Parse.Delimited(ref cSeq, static (ref ByteCursor x) => x.TagByte(1), static (ref ByteCursor x) => x.TagByte(2), static (ref ByteCursor x) => x.TagByte(9)).IsBacktrack);
        Assert.Equal(1, cSeq.Position);

        Assert.True(Parse.Pair(ref cSeq, static (ref ByteCursor x) => x.TagByte(1), static (ref ByteCursor x) => x.TagByte(9)).IsBacktrack);
        Assert.Equal(1, cSeq.Position);
        Assert.Equal(((byte)1, (byte)3), Parse.SeparatedPair(ref cSeq, static (ref ByteCursor x) => x.TagByte(1), static (ref ByteCursor x) => x.TagByte(2), static (ref ByteCursor x) => x.TagByte(3)).Unwrap());
        cSeq.Position = 1;
        Assert.True(Parse.SeparatedPair(ref cSeq, static (ref ByteCursor x) => x.TagByte(1), static (ref ByteCursor x) => x.TagByte(9), static (ref ByteCursor x) => x.TagByte(3)).IsBacktrack);
        Assert.Equal(1, cSeq.Position);
        Assert.True(Parse.SeparatedPair(ref cSeq, static (ref ByteCursor x) => x.TagByte(1), static (ref ByteCursor x) => x.TagByte(2), static (ref ByteCursor x) => x.TagByte(9)).IsBacktrack);
        Assert.Equal(1, cSeq.Position);

        // AllConsuming success, trailing bytes rollback, inner error rollback
        var cAll = new byte[] { 1, 2 }.AsCursor();
        Assert.Equal((ushort)0x0102, Parse.AllConsuming(ref cAll, static (ref ByteCursor x) => x.ReadU16BE()).Unwrap());
        var cAllTrail = new byte[] { 0xAA, 1, 2 }.AsCursor(); cAllTrail.Position = 1;
        Assert.True(Parse.AllConsuming(ref cAllTrail, static (ref ByteCursor x) => x.ReadU8()).TryGetValue(out Backtrack btEof) && btEof.Error.Kind == ErrorKind.ExpectedEof);
        Assert.Equal(1, cAllTrail.Position);
        Assert.True(Parse.AllConsuming(ref cAllTrail, static (ref ByteCursor x) => x.TagByte(9)).IsBacktrack);

        // ScopedEndian rollback restores previous endianness
        var cEnd = new byte[] { 0x01 }.AsCursor(Endianness.Big);
        Assert.True(Parse.ScopedEndian(ref cEnd, Endianness.Little, static (ref ByteCursor x) => x.ReadU16()).IsBacktrack);
        Assert.Equal(Endianness.Big, cEnd.Endianness);

        // LengthSlice negative length & LengthValue requireFullConsumption=false
        var cNegLen = new byte[] { 0xAA, 1 }.AsCursor(); cNegLen.Position = 1;
        Assert.True(Parse.LengthSlice(ref cNegLen, static (ref ByteCursor _) => ParseResult.Ok(-1)).IsBacktrack);
        Assert.Equal(1, cNegLen.Position);

        var cLvPartial = new byte[] { 0x00, 0x03, 0x11, 0x22, 0x33 }.AsCursor();
        Assert.Equal((ushort)0x1122, Parse.LengthValue(
            ref cLvPartial,
            static (ref ByteCursor x) => x.ReadU16BE().Map(static u => (int)u),
            static (ref ByteCursor sub) => sub.ReadU16BE(),
            requireFullConsumption: false).Unwrap());
        Assert.True(cLvPartial.IsAtEnd);

        // Count negative & mid-failure rollback
        var cCnt = new byte[] { 0xAA, 1 }.AsCursor(); cCnt.Position = 1;
        Assert.True(Parse.Count(ref cCnt, -1, static (ref ByteCursor x) => x.ReadU8()).IsBacktrack);
        Assert.True(Parse.Count(ref cCnt, 2, static (ref ByteCursor x) => x.ReadU8()).IsBacktrack);
        Assert.Equal(1, cCnt.Position);

        // Many0 / Many1 / FoldMany0 / RepeatInto zero-progress guard, Incomplete, Failure, minCount
        static ParseResult<byte> ZeroProgress(ref ByteCursor _) => ParseResult.Ok((byte)0);
        var cZp = new byte[] { 0xAA, 1, 2 }.AsCursor(); cZp.Position = 1;
        Assert.True(Parse.Many0(ref cZp, ZeroProgress).IsFailure);
        Assert.Equal(1, cZp.Position);
        Assert.True(Parse.Many0(ref cZp, FailAfter1).IsFailure);
        Assert.Equal(1, cZp.Position);
        Assert.True(Parse.Many0(ref cZp, IncAfter1).IsIncomplete);
        Assert.Equal(1, cZp.Position);

        Assert.True(Parse.Many1(ref cZp, static (ref ByteCursor x) => x.TagByte(9)).TryGetValue(out Backtrack btM1) && btM1.Error.Kind == ErrorKind.Many1);
        Assert.Equal(1, cZp.Position);
        Assert.True(Parse.Many1(ref cZp, FailAfter1).IsFailure);
        Assert.Equal(2, Parse.Many1(ref cZp, static (ref ByteCursor x) => x.ReadU8()).Unwrap().Count);

        cZp.Position = 1;
        Assert.True(Parse.FoldMany0(ref cZp, ZeroProgress, 0, static (a, b) => a + b).IsFailure);
        Assert.Equal(1, cZp.Position);
        Assert.True(Parse.FoldMany0(ref cZp, FailAfter1, 0, static (a, b) => a + b).IsFailure);
        Assert.Equal(1, cZp.Position);
        Assert.True(Parse.FoldMany0(ref cZp, IncAfter1, 0, static (a, b) => a + b).IsIncomplete);
        Assert.Equal(1, cZp.Position);

        Span<byte> dest = stackalloc byte[4];
        Assert.True(Parse.RepeatInto(ref cZp, dest, ZeroProgress).IsFailure);
        Assert.Equal(1, cZp.Position);
        Assert.True(Parse.RepeatInto(ref cZp, dest, FailAfter1).IsFailure);
        Assert.Equal(1, cZp.Position);
        Assert.True(Parse.RepeatInto(ref cZp, dest, IncAfter1).IsIncomplete);
        Assert.Equal(1, cZp.Position);
        Assert.True(Parse.RepeatInto(ref cZp, dest, static (ref ByteCursor x) => x.ReadU8(), minCount: 5).IsBacktrack);
        Assert.Equal(1, cZp.Position);

        // Separated0: 0 items, trailing separator rollback, Incomplete/Failure on first/sep/item
        var cSep0 = "x"u8.AsCursor();
        Assert.Equal(0, Parse.Separated0(ref cSep0, static (ref ByteCursor x) => x.TagByte((byte)'1'), static (ref ByteCursor x) => x.TagByte((byte)',')).Unwrap().Count);
        Assert.Equal(0, cSep0.Position);

        var cSepTrail = "1,x"u8.AsCursor();
        var sepItems = Parse.Separated0(ref cSepTrail, static (ref ByteCursor x) => x.TagByte((byte)'1'), static (ref ByteCursor x) => x.TagByte((byte)',')).Unwrap();
        Assert.Equal(1, sepItems.Count);
        Assert.Equal(1, cSepTrail.Position); // rolled back trailing comma!

        var cSepErr = new byte[] { 0xAA, 1, 2 }.AsCursor(); cSepErr.Position = 1;
        Assert.True(Parse.Separated0(ref cSepErr, FailAfter1, static (ref ByteCursor x) => x.TagByte(2)).IsFailure);
        Assert.Equal(1, cSepErr.Position);
        Assert.True(Parse.Separated0(ref cSepErr, static (ref ByteCursor x) => x.TagByte(1), FailAfter1).IsFailure);
        Assert.Equal(1, cSepErr.Position);
        Assert.True(Parse.Separated0(ref cSepErr, static (ref ByteCursor x) => x.TagByte(1), static (ref ByteCursor x) => x.TagByte(2)).Unwrap().Count == 1);
    }

    [Fact]
    public void StructParsers_And_FastParsers_AllCombinators_And_EdgeCases()
    {
        byte[] buf =
        [
            0x01,
            0x00, 0x02,
            0x00, 0x00, 0x00, 0x03,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04,
            0x40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // 2.0
            0x2A,                                           // varint 42
            (byte)'a', (byte)'b',
        ];
        var c = buf.AsCursor();
        Assert.Equal((byte)1, StructParsers.U8.Parse(ref c).Unwrap());
        Assert.Equal((ushort)2, StructParsers.U16.Parse(ref c).Unwrap());
        Assert.Equal(3u, StructParsers.U32.Parse(ref c).Unwrap());
        Assert.Equal(4UL, StructParsers.U64.Parse(ref c).Unwrap());
        Assert.Equal(2.0, StructParsers.F64.Parse(ref c).Unwrap());
        Assert.Equal(42UL, StructParsers.VarUInt64.Parse(ref c).Unwrap());
        Assert.Equal("ab", StructParsers.Take(2).Parse(ref c).Unwrap().AsUtf8String(buf));

        // StructParsers: Cut, Opt, Preceded, Delimited, Pair, Alt (2-way & 3-way) + rollbacks
        byte[] sData = [0xAA, 0x02, 0x03, 0x04];
        var cs = sData.AsCursor(); cs.Position = 1;

        var cutNoCtx = StructParsers.Cut<StructParsers.TagByteParser, byte>(StructParsers.TagByte(0x99));
        Assert.True(cutNoCtx.Parse(ref cs).IsFailure);
        var cutCtx = StructParsers.Cut<StructParsers.TagByteParser, byte>(StructParsers.TagByte(0x99), "ctx");
        Assert.True(cutCtx.Parse(ref cs).TryGetValue(out Failure cf) && cf.Error.Context == "ctx");

        var optHit = StructParsers.Opt<StructParsers.TagByteParser, byte>(StructParsers.TagByte(0x02));
        Assert.Equal((byte)0x02, optHit.Parse(ref cs).Unwrap().UnwrapOr(0));
        var optMiss = StructParsers.Opt<StructParsers.TagByteParser, byte>(StructParsers.TagByte(0x99));
        Assert.True(optMiss.Parse(ref cs).Unwrap().IsNone);
        var optFail = StructParsers.Opt<StructParsers.CutParser<StructParsers.TagByteParser, byte>, byte>(cutNoCtx);
        Assert.True(optFail.Parse(ref cs).IsFailure);
        var cOptInc = new byte[] { 0xAA }.AsStreamingCursor(); cOptInc.Position = 1;
        var optInc = StructParsers.Opt<StructParsers.U16Parser, ushort>(StructParsers.U16);
        Assert.True(optInc.Parse(ref cOptInc).IsIncomplete);

        cs.Position = 1;
        var prec = StructParsers.Preceded<StructParsers.TagByteParser, StructParsers.TagByteParser, byte, byte>(
            StructParsers.TagByte(0x02), StructParsers.TagByte(0x99));
        Assert.True(prec.Parse(ref cs).IsBacktrack);
        Assert.Equal(1, cs.Position);

        var pair = StructParsers.Pair<StructParsers.TagByteParser, StructParsers.TagByteParser, byte, byte>(
            StructParsers.TagByte(0x02), StructParsers.TagByte(0x03));
        Assert.Equal(((byte)2, (byte)3), pair.Parse(ref cs).Unwrap());
        cs.Position = 1;
        var pairFail = StructParsers.Pair<StructParsers.TagByteParser, StructParsers.TagByteParser, byte, byte>(
            StructParsers.TagByte(0x02), StructParsers.TagByte(0x99));
        Assert.True(pairFail.Parse(ref cs).IsBacktrack);
        Assert.Equal(1, cs.Position);

        var alt2 = StructParsers.Alt<StructParsers.TagByteParser, StructParsers.TagByteParser, byte>(
            StructParsers.TagByte(0x01), StructParsers.TagByte(0x02));
        Assert.Equal((byte)2, alt2.Parse(ref cs).Unwrap());
        cs.Position = 1;
        var alt2Miss = StructParsers.Alt<StructParsers.TagByteParser, StructParsers.TagByteParser, byte>(
            StructParsers.TagByte(0x01), StructParsers.TagByte(0x99));
        Assert.True(alt2Miss.Parse(ref cs).TryGetValue(out Backtrack btA2) && btA2.Error.Kind == ErrorKind.Alt);
        Assert.Equal(1, cs.Position);

        var alt3 = StructParsers.Alt<StructParsers.TagByteParser, StructParsers.TagByteParser, StructParsers.TagByteParser, byte>(
            StructParsers.TagByte(0x01), StructParsers.TagByte(0x99), StructParsers.TagByte(0x02));
        Assert.Equal((byte)2, alt3.Parse(ref cs).Unwrap());
        cs.Position = 1;
        var alt3Miss = StructParsers.Alt<StructParsers.TagByteParser, StructParsers.TagByteParser, StructParsers.TagByteParser, byte>(
            StructParsers.TagByte(0x01), StructParsers.TagByte(0x99), StructParsers.TagByte(0x88));
        Assert.True(alt3Miss.Parse(ref cs).TryGetValue(out Backtrack btA3) && btA3.Error.Kind == ErrorKind.Alt);
        Assert.Equal(1, cs.Position);

        // FastParsers remaining primitives & Alt3 & rollbacks
        var cf2 = buf.AsCursor();
        Assert.Equal((byte)1, FastParsers.U8<KindError>().Parse(ref cf2).Unwrap());
        Assert.Equal((ushort)2, FastParsers.U16BE<KindError>().Parse(ref cf2).Unwrap());
        Assert.Equal(3u, FastParsers.U32<KindError>().Parse(ref cf2).Unwrap());
        Assert.Equal(4UL, FastParsers.U64<KindError>().Parse(ref cf2).Unwrap());
        Assert.Equal(2.0, FastParsers.F64<KindError>().Parse(ref cf2).Unwrap());
        Assert.Equal(42UL, FastParsers.VarUInt64<KindError>().Parse(ref cf2).Unwrap());
        Assert.Equal("ab", FastParsers.Take<KindError>(2).Parse(ref cf2).Unwrap().AsUtf8String(buf));

        var cfAlt = new byte[] { 0xAA, 0x03 }.AsCursor(); cfAlt.Position = 1;
        var fAlt3 = FastParsers.Alt<FastParsers.TagByteParser<KindError>, FastParsers.TagByteParser<KindError>, FastParsers.TagByteParser<KindError>, byte, KindError>(
            FastParsers.TagByte<KindError>(1), FastParsers.TagByte<KindError>(2), FastParsers.TagByte<KindError>(3));
        Assert.Equal((byte)3, fAlt3.Parse(ref cfAlt).Unwrap());
        cfAlt.Position = 1;
        var fAlt3Miss = FastParsers.Alt<FastParsers.TagByteParser<KindError>, FastParsers.TagByteParser<KindError>, FastParsers.TagByteParser<KindError>, byte, KindError>(
            FastParsers.TagByte<KindError>(1), FastParsers.TagByte<KindError>(2), FastParsers.TagByte<KindError>(9));
        Assert.True(fAlt3Miss.Parse(ref cfAlt).TryGetValue(out Backtrack<KindError> fBt3) && fBt3.Error.Kind == ErrorKind.Alt);
        Assert.Equal(1, cfAlt.Position);
    }

    [Fact]
    public void StreamingDecoder_ExactBoundaries_And_CompactionOrder()
    {
        // Test exact boundary: freeAtTail == incomingBytes (no compact/grow needed),
        // exact compaction boundary (_buffer.Length - buffered == incomingBytes),
        // and growth when buffered > 0 copies existing unread bytes accurately.
        var dec = new StreamingDecoder<ushort>(static (ref ByteCursor c) => c.ReadU16BE(), initialCapacity: 4);
        dec.Append([0x11, 0x22, 0x33]); // writeOffset=3, freeAtTail=1
        Assert.Equal((ushort)0x1122, dec.TryDecodeNext(out var frame1).Unwrap());
        Assert.Equal(2, frame1.Length);
        Assert.Equal(1, dec.BufferedCount); // [0x33] at readOffset=2, writeOffset=3

        // freeAtTail=1 == incomingBytes=1 -> appends cleanly without realloc
        dec.Append([0x44]); // [0x33, 0x44] at readOffset=2, writeOffset=4
        Assert.Equal((ushort)0x3344, dec.TryDecodeNext().Unwrap());
        Assert.Equal(0, dec.BufferedCount); // resets readOffset=0, writeOffset=0

        // Fill 3 bytes, consume 2 -> readOffset=2, buffered=1, capacity=4, freeAtTail=1
        // Append 3 bytes -> (_buffer.Length - buffered) == 3 == incomingBytes -> compacts in place!
        dec.Append([0xAA, 0xBB, 0xCC]);
        Assert.Equal((ushort)0xAABB, dec.TryDecodeNext().Unwrap());
        dec.Append([0xDD, 0xEE, 0xFF]);
        Assert.Equal(4, dec.BufferedCount);
        Assert.Equal((ushort)0xCCDD, dec.TryDecodeNext().Unwrap());
        Assert.Equal((ushort)0xEEFF, dec.TryDecodeNext().Unwrap());

        // Grow while buffered > 0 (buffered = 1, incoming = 5 -> required = 6 > capacity 4)
        dec.Append([0x01]);
        dec.Append([0x02, 0x03, 0x04, 0x05, 0x06]);
        Assert.Equal(6, dec.BufferedCount);
        Assert.Equal((ushort)0x0102, dec.TryDecodeNext().Unwrap());
        Assert.Equal((ushort)0x0304, dec.TryDecodeNext().Unwrap());
        Assert.Equal((ushort)0x0506, dec.TryDecodeNext().Unwrap());
        Assert.Equal(0, dec.BufferedCount);
    }
}
