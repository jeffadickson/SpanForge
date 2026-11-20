using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SpanForge.Protocols.Png;
using SpanForge.Protocols.WebSocket;
using Xunit;

namespace SpanForge.Tests;

public sealed class TryParserAndOptimizationTests
{
    [Fact]
    public void FastStatus_AllStates_Equality_Cut_And_UnpackNeeded()
    {
        var parsed = FastStatus.Parsed;
        var bt = FastStatus.Backtrack;
        var fail = FastStatus.Failure;
        var incUnknown = FastStatus.IncompleteUnknown;
        var inc5 = FastStatus.IncompleteBytes(5);
        var incZero = FastStatus.IncompleteBytes(0);
        var incNeg = FastStatus.IncompleteBytes(-42);

        Assert.True(parsed.IsParsed);
        Assert.False(parsed.IsIncomplete);
        Assert.False(parsed.IsBacktrack);
        Assert.False(parsed.IsFailure);
        Assert.False(parsed.IsError);
        Assert.Equal(parsed, parsed.Cut());

        Assert.True(bt.IsBacktrack);
        Assert.True(bt.IsError);
        Assert.False(bt.IsParsed);
        Assert.False(bt.IsIncomplete);
        Assert.False(bt.IsFailure);
        Assert.Equal(fail, bt.Cut());

        Assert.True(fail.IsFailure);
        Assert.True(fail.IsError);
        Assert.False(fail.IsBacktrack);
        Assert.Equal(fail, fail.Cut());

        Assert.True(incUnknown.IsIncomplete);
        Assert.False(incUnknown.IsError);
        Assert.False(incUnknown.IsParsed);
        Assert.Equal(Needed.Unknown, incUnknown.UnpackNeeded());
        Assert.Equal(incUnknown, incUnknown.Cut());

        Assert.True(inc5.IsIncomplete);
        Assert.Equal(Needed.Bytes(5), inc5.UnpackNeeded());
        Assert.Equal(Needed.Bytes(1), incZero.UnpackNeeded());
        Assert.Equal(Needed.Bytes(1), incNeg.UnpackNeeded());

        Assert.True(parsed == FastStatus.Parsed);
        Assert.True(parsed != bt);
        Assert.True(parsed.Equals((object)FastStatus.Parsed));
        Assert.False(parsed.Equals((object)bt));
        Assert.False(parsed.Equals("not a status"));
        Assert.Equal(parsed.GetHashCode(), FastStatus.Parsed.GetHashCode());
        Xunit.Assert.NotEqual(parsed.GetHashCode(), bt.GetHashCode());
    }

    [Fact]
    public void FastResult_ExhaustiveUnion_AllVariants_And_Unwrap()
    {
        FastResult<int> empty = default;
        Assert.False(empty.HasValue);
        Assert.False(empty.IsParsed);
        Assert.False(empty.IsIncomplete);
        Assert.False(empty.IsBacktrack);
        Assert.False(empty.IsFailure);
        Assert.False(empty.IsError);
        Assert.True(empty.Value is null);
        Assert.False(empty.TryGetValue(out Parsed<int> _));
        Assert.False(empty.TryGetValue(out Incomplete _));
        Assert.False(empty.TryGetValue(out Backtrack<NoError> _));
        Assert.False(empty.TryGetValue(out Failure<NoError> _));

        FastResult<int> ok = new Parsed<int>(42);
        FastResult<int> inc = Incomplete.AtLeast(7);
        FastResult<int> incUnk = Incomplete.Unknown;
        FastResult<int> bt = new Backtrack<NoError>(default);
        FastResult<int> fail = new Failure<NoError>(default);

        Assert.True(ok.HasValue && ok.IsParsed && !ok.IsError);
        Assert.Equal(42, ok.Unwrap());
        Assert.True(ok.Value is Parsed<int>(42));

        Assert.True(inc.HasValue && inc.IsIncomplete && !inc.IsError);
        Assert.True(inc.TryGetValue(out Incomplete incVal) && incVal.Needed.Equals(Needed.Bytes(7)));
        Assert.True(inc.Value is Incomplete);
        Xunit.Assert.Throws<InvalidOperationException>(() => inc.Unwrap());

        Assert.True(incUnk.IsIncomplete);
        Assert.True(incUnk.TryGetValue(out Incomplete incUnkVal) && incUnkVal.Needed.Equals(Needed.Unknown));

        Assert.True(bt.HasValue && bt.IsBacktrack && bt.IsError);
        Assert.True(bt.TryGetValue(out Backtrack<NoError> _));
        Assert.True(bt.Value is Backtrack<NoError>);
        Xunit.Assert.Throws<InvalidOperationException>(() => bt.Unwrap());

        Assert.True(fail.HasValue && fail.IsFailure && fail.IsError);
        Assert.True(fail.TryGetValue(out Failure<NoError> _));
        Assert.True(fail.Value is Failure<NoError>);
        Xunit.Assert.Throws<InvalidOperationException>(() => fail.Unwrap());

        // FastStatus constructor round-trip
        var fromOk = new FastResult<int>(FastStatus.Parsed, 99);
        var fromBt = new FastResult<int>(FastStatus.Backtrack);
        var fromFail = new FastResult<int>(FastStatus.Failure);
        var fromInc = new FastResult<int>(FastStatus.IncompleteBytes(3));
        Assert.Equal(99, fromOk.Unwrap());
        Assert.True(fromBt.IsBacktrack);
        Assert.True(fromFail.IsFailure);
        Assert.True(fromInc.TryGetValue(out Incomplete i3) && i3.Needed.Equals(Needed.Bytes(3)));

        // Exhaustive C# 15 union switch
        static string Describe(FastResult<int> r) => r switch
        {
            Parsed<int>(var v) => $"ok:{v}",
            Incomplete(var n) => $"inc:{n}",
            Backtrack<NoError> => "bt",
            Failure<NoError> => "fail",
        };
        Assert.Equal("ok:42", Describe(ok));
        Assert.Equal("bt", Describe(bt));
        Assert.Equal("fail", Describe(fail));
        Assert.True(Describe(inc).StartsWith("inc:", StringComparison.Ordinal));
    }

    [Fact]
    public void ByteCursor_TryPrimitives_CompleteAndStreaming_AllEdges()
    {
        // Big-endian buffer: tag(0xAA), u8(0x55), u16(0x1234), u32(0x89ABCDEF), f64(3.25), lenPrefixedU16("hi")
        byte[] buf = new byte[1 + 1 + 2 + 4 + 8 + 4];
        buf[0] = 0xAA;
        buf[1] = 0x55;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), 0x1234);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), 0x89ABCDEFu);
        BinaryPrimitives.WriteDoubleBigEndian(buf.AsSpan(8, 8), 3.25);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(16, 2), 2);
        buf[18] = (byte)'h';
        buf[19] = (byte)'i';

        var c = buf.AsCursor();
        Assert.True(c.TryTagByte(0xBB, out _).IsBacktrack);
        Assert.Equal(0, c.Position);
        Assert.True(c.TryTagByte(0xAA, out byte tag).IsParsed && tag == 0xAA);
        Assert.True(c.TryReadU8(out byte u8).IsParsed && u8 == 0x55);
        Assert.True(c.TryReadU16BE(out ushort u16).IsParsed && u16 == 0x1234);
        Assert.True(c.TryReadU32BE(out uint u32).IsParsed && u32 == 0x89ABCDEFu);
        Assert.True(c.TryReadF64BE(out double f64).IsParsed && f64 == 3.25);
        Assert.True(c.TryReadLengthPrefixedU16BE(out ByteSlice sl).IsParsed);
        Assert.SequenceEqual<byte>("hi"u8, buf.AsSpan()[sl]);
        Assert.True(c.IsAtEnd);

        // Complete EOF -> Backtrack without advancing
        Assert.True(c.TryTagByte(0x01, out _).IsBacktrack);
        Assert.True(c.TryReadU8(out _).IsBacktrack);
        Assert.True(c.TryReadU16BE(out _).IsBacktrack);
        Assert.True(c.TryReadU32BE(out _).IsBacktrack);
        Assert.True(c.TryReadF64BE(out _).IsBacktrack);
        Assert.True(c.TryReadLengthPrefixedU16BE(out _).IsBacktrack);

        // Streaming shortages -> exact Needed bytes
        byte[] oneByte = [0x00];
        var s0 = ReadOnlySpan<byte>.Empty.AsStreamingCursor();
        var stTag = s0.TryTagByte(0x01, out _);
        Assert.True(stTag.IsIncomplete && stTag.UnpackNeeded().Equals(Needed.Bytes(1)));
        var stU8 = s0.TryReadU8(out _);
        Assert.True(stU8.IsIncomplete && stU8.UnpackNeeded().Equals(Needed.Bytes(1)));

        var s1 = oneByte.AsStreamingCursor();
        var stU16 = s1.TryReadU16BE(out _);
        Assert.True(stU16.IsIncomplete && stU16.UnpackNeeded().Equals(Needed.Bytes(1)));
        var stU32 = s1.TryReadU32BE(out _);
        Assert.True(stU32.IsIncomplete && stU32.UnpackNeeded().Equals(Needed.Bytes(3)));
        var stF64 = s1.TryReadF64BE(out _);
        Assert.True(stF64.IsIncomplete && stF64.UnpackNeeded().Equals(Needed.Bytes(7)));
        var stLpHeader = s1.TryReadLengthPrefixedU16BE(out _);
        Assert.True(stLpHeader.IsIncomplete && stLpHeader.UnpackNeeded().Equals(Needed.Bytes(1)));

        byte[] partialPayload = [0x00, 0x05, 0x41, 0x42]; // length 5, only 2 payload bytes
        var sLp = partialPayload.AsStreamingCursor();
        var stLpBody = sLp.TryReadLengthPrefixedU16BE(out _);
        Assert.True(stLpBody.IsIncomplete && stLpBody.UnpackNeeded().Equals(Needed.Bytes(3)));
        Assert.Equal(0, sLp.Position);
    }

    [Fact]
    public void ByteCursor_TryReadVarUInt64_AllByteLengths_MaxValue_Truncation_And_Overflow()
    {
        ulong[] values =
        [
            0UL,
            127UL,
            128UL,
            (1UL << 14) - 1,
            1UL << 14,
            (1UL << 21) - 1,
            1UL << 21,
            (1UL << 28) - 1,
            1UL << 28,
            (1UL << 35) - 1,
            1UL << 35,
            (1UL << 42) - 1,
            1UL << 42,
            (1UL << 49) - 1,
            1UL << 49,
            (1UL << 56) - 1,
            1UL << 56,
            (1UL << 63) - 1,
            1UL << 63,
            ulong.MaxValue,
        ];

        foreach (ulong expected in values)
        {
            byte[] encoded = EncodeLeb128(expected);

            // Standalone exact-length buffer
            var c = encoded.AsCursor();
            var st = c.TryReadVarUInt64(out ulong actual);
            Assert.True(st.IsParsed);
            Assert.Equal(expected, actual);
            Assert.Equal(encoded.Length, c.Position);

            // Every proper prefix must backtrack (Complete) or return Incomplete(1) (Streaming) with pos=0
            for (int prefixLen = 0; prefixLen < encoded.Length; prefixLen++)
            {
                var cc = encoded.AsSpan(0, prefixLen).AsCursor();
                Assert.True(cc.TryReadVarUInt64(out _).IsBacktrack);
                Assert.Equal(0, cc.Position);

                var sc = encoded.AsSpan(0, prefixLen).AsStreamingCursor();
                var inc = sc.TryReadVarUInt64(out _);
                Assert.True(inc.IsIncomplete && inc.UnpackNeeded().Equals(Needed.Bytes(1)));
                Assert.Equal(0, sc.Position);
            }
        }

        // 10th byte > 1 (overflow) -> Backtrack in both Complete and Streaming modes
        byte[] overflow = [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x02];
        var co = overflow.AsCursor();
        Assert.True(co.TryReadVarUInt64(out _).IsBacktrack);
        Assert.Equal(0, co.Position);
        var so = overflow.AsStreamingCursor();
        Assert.True(so.TryReadVarUInt64(out _).IsBacktrack);
        Assert.Equal(0, so.Position);

        static byte[] EncodeLeb128(ulong v)
        {
            List<byte> outBuf = [];
            do
            {
                byte b = (byte)(v & 0x7F);
                v >>= 7;
                if (v != 0) b |= 0x80;
                outBuf.Add(b);
            } while (v != 0);
            return outBuf.ToArray();
        }
    }

    [Fact]
    public void TryParsers_Preceded_Delimited_Alt_Dispatch_And_FoldDispatch_Rollbacks()
    {
        // Preceded
        var prec = TryParsers.Preceded<TryParsers.TagByteParser, TryParsers.U16BEParser, byte, ushort>(
            TryParsers.TagByte((byte)'#'), TryParsers.U16BE);
        byte[] goodPrec = [(byte)'#', 0x01, 0x02];
        var c1 = goodPrec.AsCursor();
        Assert.True(prec.TryParse(ref c1, out ushort pv).IsParsed && pv == 0x0102 && c1.Position == 3);

        byte[] badPrec = [(byte)'#', 0x01];
        var c2 = badPrec.AsCursor();
        Assert.True(prec.TryParse(ref c2, out _).IsBacktrack && c2.Position == 0);

        // Delimited
        var delim = TryParsers.Delimited<TryParsers.TagByteParser, TryParsers.U16BEParser, TryParsers.TagByteParser, byte, ushort, byte>(
            TryParsers.TagByte((byte)'['), TryParsers.U16BE, TryParsers.TagByte((byte)']'));
        byte[] goodDelim = [(byte)'[', 0xAB, 0xCD, (byte)']'];
        var c3 = goodDelim.AsCursor();
        Assert.True(delim.TryParse(ref c3, out ushort dv).IsParsed && dv == 0xABCD && c3.Position == 4);

        byte[] badClose = [(byte)'[', 0xAB, 0xCD, (byte)'!'];
        var c4 = badClose.AsCursor();
        Assert.True(delim.TryParse(ref c4, out _).IsBacktrack && c4.Position == 0);

        // Alt3 (happy + failure/incomplete propagation without falling through)
        var alt = TryParsers.Alt<FailingByteParser, Tag10Parser, Tag20Parser, byte>(default, default, default);
        byte[] b10 = [10];
        var ca1 = b10.AsCursor();
        Assert.True(alt.TryParse(ref ca1, out byte av1).IsParsed && av1 == 10);

        byte[] b20 = [20];
        var ca2 = b20.AsCursor();
        Assert.True(alt.TryParse(ref ca2, out byte av2).IsParsed && av2 == 20);

        byte[] bFail = [0xFF];
        var ca3 = bFail.AsCursor();
        Assert.True(alt.TryParse(ref ca3, out _).IsFailure && ca3.Position == 0);

        byte[] bMiss = [99];
        var ca4 = bMiss.AsCursor();
        Assert.True(alt.TryParse(ref ca4, out _).IsBacktrack && ca4.Position == 0);

        // Dispatch + FoldDispatch
        var disp = TryParsers.Dispatch<byte, TryParsers.U8Parser, SumDispatchHandler, uint>(TryParsers.U8, default);
        byte[] stream = [0x01, 0x00, 0x00, 0x00, 0x05, 0x02, 0x00, 0x0A];
        var cd = stream.AsCursor();
        Assert.True(disp.TryParse(ref cd, out uint d1).IsParsed && d1 == 5u);
        Assert.True(disp.TryParse(ref cd, out uint d2).IsParsed && d2 == 10u);
        Assert.True(disp.TryParse(ref cd, out _).IsBacktrack && cd.IsAtEnd);

        // Dispatch rollback when payload fails after reading key
        byte[] truncDisp = [0x01, 0x00];
        var cdt = truncDisp.AsStreamingCursor();
        var stInc = disp.TryParse(ref cdt, out _);
        Assert.True(stInc.IsIncomplete && cdt.Position == 0);

        // FoldDispatch over complete stream + trailing unknown tag + streaming incomplete + failure
        var cf = stream.AsCursor();
        ulong total = 0;
        Assert.True(TryParsers.FoldDispatch<byte, TryParsers.U8Parser, SumDispatchFolder, ulong>(ref cf, TryParsers.U8, default, ref total).IsParsed);
        Assert.Equal(15UL, total);
        Assert.True(cf.IsAtEnd);

        byte[] streamWithFailure = [0x02, 0x00, 0x03, 0xFF];
        var cff = streamWithFailure.AsCursor();
        ulong partial = 0;
        var failStatus = TryParsers.FoldDispatch<byte, TryParsers.U8Parser, SumDispatchFolder, ulong>(ref cff, TryParsers.U8, default, ref partial);
        Assert.True(failStatus.IsFailure);
        Assert.Equal(3UL, partial);
        Assert.Equal(3, cff.Position); // rolled back to start of failing item 0xFF

        byte[] streamWithInc = [0x02, 0x00, 0x03, 0x01, 0x00];
        var cfi = streamWithInc.AsStreamingCursor();
        ulong partial2 = 0;
        var incStatus = TryParsers.FoldDispatch<byte, TryParsers.U8Parser, SumDispatchFolder, ulong>(ref cfi, TryParsers.U8, default, ref partial2);
        Assert.True(incStatus.IsIncomplete);
        Assert.Equal(3UL, partial2);
        Assert.Equal(3, cfi.Position); // rolled back to start of incomplete item
    }

    [Fact]
    public void WebSocket_TryParseFrame_AllPaths_UnmaskInto_And_ZeroAlloc()
    {
        // Masked TextFrame ("Hello")
        byte[] maskedText = [0x81, 0x85, 0x37, 0xFA, 0x21, 0x3D, 0x7F, 0x9F, 0x4D, 0x51, 0x58];
        var c1 = maskedText.AsCursor();
        Assert.True(WebSocketFrameParser.TryParseFrame(ref c1, out var f1).IsParsed);
        Assert.True(f1.TryGetValue(out TextFrame tf) && tf.Fin && tf.Payload.Length == 5);
        Span<byte> dest = stackalloc byte[64];
        WebSocketFrameParser.UnmaskInto(maskedText, tf.Payload, tf.MaskKey, dest);
        Assert.SequenceEqual<byte>("Hello"u8, dest[..5]);

        // Unmasked BinaryFrame, ContinuationFrame, PingFrame, PongFrame
        byte[] unmaskedBin = [0x82, 0x03, 1, 2, 3];
        var c2 = unmaskedBin.AsCursor();
        Assert.True(WebSocketFrameParser.TryParseFrame(ref c2, out var f2).IsParsed);
        Assert.True(f2.TryGetValue(out BinaryFrame bf));
        WebSocketFrameParser.UnmaskInto(unmaskedBin, bf.Payload, bf.MaskKey, dest);
        Assert.SequenceEqual<byte>([1, 2, 3], dest[..3]);

        byte[] cont = [0x00, 0x01, 0x77];
        var c3 = cont.AsCursor();
        Assert.True(WebSocketFrameParser.TryParseFrame(ref c3, out var f3).IsParsed && f3.TryGetValue(out ContinuationFrame cf) && !cf.Fin);

        byte[] ping = [0x89, 0x02, (byte)'O', (byte)'K'];
        var c4 = ping.AsCursor();
        Assert.True(WebSocketFrameParser.TryParseFrame(ref c4, out var f4).IsParsed && f4.TryGetValue(out PingFrame _));

        byte[] pong = [0x8A, 0x00];
        var c5 = pong.AsCursor();
        Assert.True(WebSocketFrameParser.TryParseFrame(ref c5, out var f5).IsParsed && f5.TryGetValue(out PongFrame _));

        // Slow path: CloseFrame with status code 1000
        byte[] closeFrame = [0x88, 0x04, 0x03, 0xE8, (byte)'O', (byte)'K'];
        var c6 = closeFrame.AsCursor();
        Assert.True(WebSocketFrameParser.TryParseFrame(ref c6, out var f6).IsParsed);
        Assert.True(f6.TryGetValue(out CloseFrame clf) && clf.StatusCode.TryGetValue(out Some<ushort> code) && code.Value == 1000);

        // Slow path: 16-bit extended payload (126 bytes)
        byte[] ext16 = new byte[4 + 126];
        ext16[0] = 0x82;
        ext16[1] = 126;
        BinaryPrimitives.WriteUInt16BigEndian(ext16.AsSpan(2, 2), 126);
        var c7 = ext16.AsCursor();
        Assert.True(WebSocketFrameParser.TryParseFrame(ref c7, out var f7).IsParsed && f7.TryGetValue(out BinaryFrame bf16) && bf16.Payload.Length == 126);

        // Error paths: fragmented control frame (Failure), reserved opcode (Failure), streaming Incomplete, complete Backtrack
        byte[] fragPing = [0x09, 0x00];
        var ce1 = fragPing.AsCursor();
        Assert.True(WebSocketFrameParser.TryParseFrame(ref ce1, out _).IsFailure && ce1.Position == 0);

        byte[] badOp = [0x83, 0x00];
        var ce2 = badOp.AsCursor();
        Assert.True(WebSocketFrameParser.TryParseFrame(ref ce2, out _).IsFailure && ce2.Position == 0);

        var ce3 = maskedText.AsSpan(0, 7).AsStreamingCursor();
        var inc = WebSocketFrameParser.TryParseFrame(ref ce3, out _);
        Assert.True(inc.IsIncomplete && inc.UnpackNeeded().Equals(Needed.Bytes(4)) && ce3.Position == 0);

        var ce4 = maskedText.AsSpan(0, 1).AsCursor();
        Assert.True(WebSocketFrameParser.TryParseFrame(ref ce4, out _).IsBacktrack && ce4.Position == 0);

        // UnmaskInto word-at-a-time verification across lengths 0..33 and maskOffset 0..3
        uint maskKey = 0x12345678u;
        byte[] maskBytes = [0x12, 0x34, 0x56, 0x78];
        byte[] src = new byte[34];
        for (int i = 0; i < src.Length; i++) src[i] = (byte)(i * 37 + 11);

        for (int len = 0; len <= 33; len++)
        {
            for (int offset = 0; offset < 4; offset++)
            {
                WebSocketFrameParser.UnmaskInto(src, new ByteSlice(0, len), Option<uint>.Of(maskKey), dest, offset);
                for (int i = 0; i < len; i++)
                {
                    byte expected = (byte)(src[i] ^ maskBytes[(i + offset) & 3]);
                    Assert.Equal(expected, dest[i]);
                }
            }
        }

        Xunit.Assert.Throws<ArgumentException>(() =>
        {
            Span<byte> tooSmall = stackalloc byte[2];
            WebSocketFrameParser.UnmaskInto(maskedText, new ByteSlice(6, 5), Option<uint>.Of(maskKey), tooSmall);
        });

        // Strict zero-allocation check for TryParseFrame + UnmaskInto
        _ = RunTryWsLoop(maskedText, 100);
        long before = GC.GetAllocatedBytesForCurrentThread();
        ulong sum = RunTryWsLoop(maskedText, 5_000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(5_000UL * ('H' + 'e' + 'l' + 'l' + 'o'), sum);
        Assert.Equal(0L, allocated);

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        static ulong RunTryWsLoop(byte[] buf, int iterations)
        {
            Span<byte> scratch = stackalloc byte[16];
            ulong acc = 0;
            ReadOnlySpan<byte> span = buf;
            for (int i = 0; i < iterations; i++)
            {
                var c = span.AsCursor();
                if (WebSocketFrameParser.TryParseFrame(ref c, out var frame).IsParsed && frame.TryGetValue(out TextFrame t))
                {
                    WebSocketFrameParser.UnmaskInto(span, t.Payload, t.MaskKey, scratch);
                    for (int k = 0; k < t.Payload.Length; k++) acc += scratch[k];
                }
            }
            return acc;
        }
    }

    [Fact]
    public void BitCursor_TryReadBool_And_TryReadU8_AllBoundaries()
    {
        byte[] data = [0b1011_0100, 0b1100_1010];
        var bc = new BitCursor(data);
        Assert.True(bc.TryReadBool(out bool b0) && b0);
        Assert.True(bc.TryReadBool(out bool b1) && !b1);
        Assert.True(bc.TryReadU8(0, out byte z) && z == 0);
        Assert.True(bc.TryReadU8(4, out byte mid) && mid == 0b1101);
        // Cross-byte read: 2 bits left in byte 0 (00) + 5 bits from byte 1 (11001) = 0b0011001 = 25
        Assert.True(bc.TryReadU8(7, out byte cross) && cross == 0b0011001);
        Assert.True(bc.TryReadU8(3, out byte tail) && tail == 0b010);
        Assert.False(bc.TryReadBool(out _));
        Assert.False(bc.TryReadU8(1, out _));
        Assert.False(bc.TryReadU8(9, out _));
        Assert.False(bc.TryReadU8(-1, out _));
    }

    [Fact]
    public void Png_FlatCrc32_MatchesReferenceAcrossAllLengthsAndAlignments()
    {
        byte[] sample = new byte[65];
        for (int i = 0; i < sample.Length; i++) sample[i] = (byte)((i * 73 + 19) & 0xFF);

        for (int len = 0; len <= sample.Length; len++)
        {
            uint expected = RefCrc32(sample.AsSpan(0, len));
            Assert.Equal(expected, PngParser.ComputeCrc32(sample.AsSpan(0, len)));
        }

        static uint RefCrc32(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFF_FFFFu;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++)
                    crc = (crc & 1) != 0 ? 0xEDB8_8320u ^ (crc >> 1) : crc >> 1;
            }
            return ~crc;
        }
    }

    [Fact]
    public void Png_And_WebSocket_NonZeroOffset_Shortages_ExtendedFrames_And_UnionConstructors()
    {
        // 1. Direct WebSocketFrame union constructors (fin true/false, masked/unmasked, status Some/None)
        WebSocketFrame[] frames =
        [
            new ContinuationFrame(false, Option<uint>.Nothing, new ByteSlice(0, 1)),
            new ContinuationFrame(true, Option<uint>.Of(0x11223344u), new ByteSlice(1, 2)),
            new TextFrame(false, Option<uint>.Nothing, new ByteSlice(0, 0)),
            new TextFrame(true, Option<uint>.Of(0xAABBCCDDu), new ByteSlice(2, 3)),
            new BinaryFrame(false, Option<uint>.Of(0x01020304u), new ByteSlice(0, 4)),
            new CloseFrame(Option<uint>.Nothing, Option<ushort>.Of(1001), new ByteSlice(0, 2)),
            new CloseFrame(Option<uint>.Of(0x55667788u), Option<ushort>.Nothing, ByteSlice.Empty),
            new PingFrame(Option<uint>.Nothing, new ByteSlice(0, 1)),
            new PongFrame(Option<uint>.Of(0x99AABBCCu), new ByteSlice(0, 1)),
        ];
        Assert.True(frames[0].TryGetValue(out ContinuationFrame c0) && !c0.Fin && c0.MaskKey.IsNone);
        Assert.True(frames[1].TryGetValue(out ContinuationFrame c1) && c1.Fin && c1.MaskKey.TryGetValue(out Some<uint> m1) && m1.Value == 0x11223344u);
        Assert.True(frames[2].TryGetValue(out TextFrame t0) && !t0.Fin && t0.MaskKey.IsNone);
        Assert.True(frames[3].TryGetValue(out TextFrame t1) && t1.Fin && t1.MaskKey.TryGetValue(out Some<uint> m3) && m3.Value == 0xAABBCCDDu);
        Assert.True(frames[4].TryGetValue(out BinaryFrame b0) && !b0.Fin && b0.MaskKey.TryGetValue(out Some<uint> m4) && m4.Value == 0x01020304u);
        Assert.True(frames[5].TryGetValue(out CloseFrame cl0) && cl0.MaskKey.IsNone && cl0.StatusCode.TryGetValue(out Some<ushort> sc0) && sc0.Value == 1001);
        Assert.True(frames[6].TryGetValue(out CloseFrame cl1) && cl1.MaskKey.IsSome && cl1.StatusCode.IsNone);
        Assert.True(frames[7].TryGetValue(out PingFrame p0) && p0.MaskKey.IsNone);
        Assert.True(frames[8].TryGetValue(out PongFrame po0) && po0.MaskKey.IsSome);

        // 2. Back-to-back TryParseFrame (non-zero start offset, masked & unmasked, fin true & false)
        byte[] twoFrames =
        [
            0x00, 0x02, 0x41, 0x42,                                     // unmasked non-fin Continuation (len 2)
            0x81, 0x83, 0x11, 0x22, 0x33, 0x44, 0x50, 0x51, 0x52,       // masked fin Text (len 3)
        ];
        var multi = twoFrames.AsCursor();
        Assert.True(WebSocketFrameParser.TryParseFrame(ref multi, out var mf1).IsParsed);
        Assert.Equal(4, multi.Position);
        Assert.True(mf1.TryGetValue(out ContinuationFrame mcf1) && !mcf1.Fin && mcf1.MaskKey.IsNone && mcf1.Payload.Offset == 2 && mcf1.Payload.Length == 2);

        Assert.True(WebSocketFrameParser.TryParseFrame(ref multi, out var mf2).IsParsed);
        Assert.Equal(twoFrames.Length, multi.Position);
        Assert.True(mf2.TryGetValue(out TextFrame mtf2) && mtf2.Fin && mtf2.MaskKey.TryGetValue(out Some<uint> mk2) && mk2.Value == 0x11223344u && mtf2.Payload.Offset == 10 && mtf2.Payload.Length == 3);

        // 3. ParseFrame exact shortage positions & ErrorKind at non-zero start
        byte[] prefixMasked = [0xEE, 0xEE, 0x81, 0x85, 0x11, 0x22]; // start=2, rem=4 (< headerLen 6) -> Eof at start+2=4
        var cShortMask = prefixMasked.AsCursor();
        cShortMask.Position = 2;
        var resShortMask = WebSocketFrameParser.ParseFrame(ref cShortMask);
        Assert.True(resShortMask.TryGetValue(out Backtrack btMask) && btMask.Error.Kind == ErrorKind.Eof && btMask.Error.Offset == 4);

        byte[] prefixPayload = [0xEE, 0xEE, 0x81, 0x85, 0x11, 0x22, 0x33, 0x44, 0xAA]; // start=2, rem=7 (>= headerLen 6, < totalLen 11) -> Take at start+6=8
        var cShortPayload = prefixPayload.AsCursor();
        cShortPayload.Position = 2;
        var resShortPayload = WebSocketFrameParser.ParseFrame(ref cShortPayload);
        Assert.True(resShortPayload.TryGetValue(out Backtrack btPayload) && btPayload.Error.Kind == ErrorKind.Take && btPayload.Error.Offset == 8);

        var cStreamHeader = prefixMasked.AsSpan(0, 3).AsStreamingCursor(); // start=2, rem=1 -> Incomplete(1)
        cStreamHeader.Position = 2;
        Assert.True(WebSocketFrameParser.ParseFrame(ref cStreamHeader).TryGetValue(out Incomplete incH) && incH.Needed.Equals(Needed.Bytes(1)));

        var cStreamBody = prefixPayload.AsStreamingCursor(); // start=2, rem=7, totalLen=11 -> Incomplete(4)
        cStreamBody.Position = 2;
        Assert.True(WebSocketFrameParser.ParseFrame(ref cStreamBody).TryGetValue(out Incomplete incB) && incB.Needed.Equals(Needed.Bytes(4)));

        // 4. Extended 16-bit masked frames (Text, Ping len 125, Pong len 125, Close masked, truncated mask in extended)
        byte[] extMaskedText = new byte[8 + 126];
        extMaskedText[0] = 0x81; extMaskedText[1] = 0x80 | 126;
        BinaryPrimitives.WriteUInt16BigEndian(extMaskedText.AsSpan(2, 2), 126);
        BinaryPrimitives.WriteUInt32BigEndian(extMaskedText.AsSpan(4, 4), 0xCAFEBABEu);
        var ceText = extMaskedText.AsCursor();
        Assert.True(WebSocketFrameParser.ParseFrame(ref ceText).TryGetValue(out Parsed<WebSocketFrame> pExtText));
        Assert.True(pExtText.Value.TryGetValue(out TextFrame extTf) && extTf.MaskKey.TryGetValue(out Some<uint> extMk) && extMk.Value == 0xCAFEBABEu);

        byte[] extTruncMask = [0x81, 0x80 | 126, 0x00, 0x7E, 0xAA, 0xBB]; // extended 16-bit, masked, truncated mask -> Backtrack + rollback
        var ceTruncMask = extTruncMask.AsCursor();
        Assert.True(WebSocketFrameParser.ParseFrame(ref ceTruncMask).IsBacktrack && ceTruncMask.Position == 0);

        // Extended 16-bit encoding of Ping/Pong/Close with length <= 125 (covers extended switch arms 0x9, 0xA, 0x8)
        byte[] extPing = new byte[4 + 125];
        extPing[0] = 0x89; extPing[1] = 126; BinaryPrimitives.WriteUInt16BigEndian(extPing.AsSpan(2, 2), 125);
        var cePing = extPing.AsCursor();
        Assert.True(WebSocketFrameParser.ParseFrame(ref cePing).TryGetValue(out Parsed<WebSocketFrame> pExtPing) && pExtPing.Value.TryGetValue(out PingFrame _));

        byte[] extPong = new byte[4 + 2];
        extPong[0] = 0x8A; extPong[1] = 126; BinaryPrimitives.WriteUInt16BigEndian(extPong.AsSpan(2, 2), 2);
        var cePong = extPong.AsCursor();
        Assert.True(WebSocketFrameParser.ParseFrame(ref cePong).TryGetValue(out Parsed<WebSocketFrame> pExtPong) && pExtPong.Value.TryGetValue(out PongFrame _));

        byte[] extClose = [0x88, 0x80 | 126, 0x00, 0x02, 0x10, 0x20, 0x30, 0x40, 0x13, 0xC8]; // masked close code 0x03E8 = 1000
        var ceClose = extClose.AsCursor();
        Assert.True(WebSocketFrameParser.ParseFrame(ref ceClose).TryGetValue(out Parsed<WebSocketFrame> pExtClose));
        Assert.True(pExtClose.Value.TryGetValue(out CloseFrame extCl) && extCl.MaskKey.IsSome && extCl.StatusCode.TryGetValue(out Some<ushort> extSc) && extSc.Value == 1000);

        byte[] extCloseUnmasked = [0x88, 126, 0x00, 0x00];
        var ceCloseUnm = extCloseUnmasked.AsCursor();
        Assert.True(WebSocketFrameParser.ParseFrame(ref ceCloseUnm).TryGetValue(out Parsed<WebSocketFrame> pExtCloseU));
        Assert.True(pExtCloseU.Value.TryGetValue(out CloseFrame extClU) && extClU.MaskKey.IsNone);

        // 5. PNG shortages at non-zero start + exact Streaming Needed bytes (rem = 1, 5, 9, and body truncation)
        byte[] pngFull = [.. PngParser.Signature, .. PngParser.BuildChunk("IDAT"u8, [1, 2, 3, 4, 5, 6, 7, 8])];
        // Truncate inside length (rem=1), type (rem=5), crc/header (rem=9), and payload (rem=14 < 20)
        int sigLen = PngParser.Signature.Length;
        var ps1 = pngFull.AsSpan(0, sigLen + 1).AsStreamingCursor(); ps1.Position = sigLen;
        Assert.True(PngParser.ParseChunk(ref ps1).TryGetValue(out Incomplete pi1) && pi1.Needed.Equals(Needed.Bytes(3)));

        var ps5 = pngFull.AsSpan(0, sigLen + 5).AsStreamingCursor(); ps5.Position = sigLen;
        Assert.True(PngParser.ParseChunk(ref ps5).TryGetValue(out Incomplete pi5) && pi5.Needed.Equals(Needed.Bytes(3)));

        var ps9 = pngFull.AsSpan(0, sigLen + 9).AsStreamingCursor(); ps9.Position = sigLen;
        Assert.True(PngParser.ParseChunk(ref ps9).TryGetValue(out Incomplete pi9) && pi9.Needed.Equals(Needed.Bytes(3)));

        var ps14 = pngFull.AsSpan(0, sigLen + 14).AsStreamingCursor(); ps14.Position = sigLen;
        Assert.True(PngParser.ParseChunk(ref ps14).TryGetValue(out Incomplete pi14) && pi14.Needed.Equals(Needed.Bytes(6)));

        // Complete mode truncation at non-zero start -> exact Backtrack offset = start + 8
        var pc14 = pngFull.AsSpan(0, sigLen + 14).AsCursor(); pc14.Position = sigLen;
        Assert.True(PngParser.ParseChunk(ref pc14).TryGetValue(out Backtrack pbt) && pbt.Error.Kind == ErrorKind.Take && pbt.Error.Offset == sigLen + 8);

        // Huge length (>= int.MaxValue - 11) in both >= 12 bytes and 4..11 bytes -> Failure(LengthValue)
        byte[] huge12 = [0x7F, 0xFF, 0xFF, 0xF4, (byte)'I', (byte)'D', (byte)'A', (byte)'T', 0, 0, 0, 0]; // int.MaxValue - 11
        var ch12 = huge12.AsCursor();
        Assert.True(PngParser.ParseChunk(ref ch12).TryGetValue(out Failure fh12) && fh12.Error.Kind == ErrorKind.LengthValue && fh12.Error.Context is { Length: > 0 });

        byte[] huge4 = [0x7F, 0xFF, 0xFF, 0xF4]; // exactly 4 bytes with rawLen == int.MaxValue - 11
        var ch4 = huge4.AsCursor();
        Assert.True(PngParser.ParseChunk(ref ch4).TryGetValue(out Failure fh4) && fh4.Error.Kind == ErrorKind.LengthValue);

        // tEXt with NUL at index 0 (empty keyword, valid per PNG spec)
        byte[] emptyKw = PngParser.BuildChunk("tEXt"u8, "\0hello"u8);
        var cek = emptyKw.AsCursor();
        Assert.True(PngParser.ParseChunk(ref cek).TryGetValue(out Parsed<PngChunk> pek) && pek.Value.TryGetValue(out TextChunk tk) && tk.Keyword.Length == 0 && tk.Text.Length == 5);
    }

    private readonly struct FailingByteParser : ITryParser<byte>
    {
        public FastStatus TryParse(ref ByteCursor cursor, out byte value)
        {
            if (cursor.TryTagByte(0xFF, out value).IsParsed) return FastStatus.Failure;
            return FastStatus.Backtrack;
        }
    }

    private readonly struct Tag10Parser : ITryParser<byte>
    {
        public FastStatus TryParse(ref ByteCursor cursor, out byte value) => cursor.TryTagByte(10, out value);
    }

    private readonly struct Tag20Parser : ITryParser<byte>
    {
        public FastStatus TryParse(ref ByteCursor cursor, out byte value) => cursor.TryTagByte(20, out value);
    }

    private readonly struct SumDispatchHandler : ITryDispatchHandler<byte, uint>
    {
        public FastStatus TryParse(byte key, ref ByteCursor cursor, out uint value)
        {
            if (key == 0x01) return cursor.TryReadU32BE(out value);
            if (key == 0x02)
            {
                var s = cursor.TryReadU16BE(out ushort u16);
                value = u16;
                return s;
            }
            value = 0;
            return FastStatus.Backtrack;
        }
    }

    private readonly struct SumDispatchFolder : ITryDispatchFolder<byte, ulong>
    {
        public FastStatus TryFold(byte key, ref ByteCursor cursor, ref ulong acc)
        {
            if (key == 0x01)
            {
                var s = cursor.TryReadU32BE(out uint u);
                if (!s.IsParsed) return s;
                acc += u;
                return FastStatus.Parsed;
            }
            if (key == 0x02)
            {
                var s = cursor.TryReadU16BE(out ushort u);
                if (!s.IsParsed) return s;
                acc += u;
                return FastStatus.Parsed;
            }
            if (key == 0xFF) return FastStatus.Failure;
            return FastStatus.Backtrack;
        }
    }
}
