using System.Buffers.Binary;
using System.Text;
using SpanForge.Protocols.Dns;
using SpanForge.Protocols.Png;
using SpanForge.Protocols.Resp;
using SpanForge.Protocols.WebSocket;
using Xunit;

namespace SpanForge.Tests;

/// <summary>
/// Comprehensive boundary, rollback, error-context, and edge-case tests for all 4 protocol parsers
/// in <c>SpanForge.Protocols</c> targeting &gt;85% Stryker mutation score.
/// </summary>
public sealed class ProtocolMutationKillerTests
{
    [Fact]
    public void Png_AllChunkVariants_Guards_Rollback_And_Errors()
    {
        // Non-zero Compression/Filter/Interlace tests all bitfields in zero-boxing [Union] PngChunk
        byte[] ihdrPayload = [0, 0, 0x01, 0x23, 0, 0, 0x00, 0x45, 16, (byte)PngColorType.TruecolorAlpha, 0x07, 0x05, 0x01];
        byte[] validChunk = PngParser.BuildChunk("IHDR"u8, ihdrPayload);
        var c = validChunk.AsCursor();
        var chunk = PngParser.ParseChunk(ref c).Unwrap();
        Assert.True(chunk.HasValue);
        Assert.False(default(PngChunk).HasValue);
        Assert.True(default(PngChunk).Value is null);
        Assert.True(chunk.TryGetValue(out IhdrChunk ihdr));
        Assert.Equal(0x0123u, ihdr.Width);
        Assert.Equal(0x0045u, ihdr.Height);
        Assert.Equal((byte)16, ihdr.BitDepth);
        Assert.Equal(PngColorType.TruecolorAlpha, ihdr.ColorType);
        Assert.Equal((byte)0x07, ihdr.Compression);
        Assert.Equal((byte)0x05, ihdr.Filter);
        Assert.Equal((byte)0x01, ihdr.Interlace);
        Assert.True(chunk.Value is IhdrChunk);

        // Signature mismatch with context & rollback
        byte[] badSig = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        var cSig = badSig.AsCursor();
        cSig.Position = 1;
        var sigRes = PngParser.ParseSignature(ref cSig);
        Assert.True(sigRes.TryGetValue(out Backtrack sigBt) && !string.IsNullOrEmpty(sigBt.Error.Context));
        Assert.Equal(1, cSig.Position);

        // ParseChunk length > int.MaxValue - 12 -> Failure with rollback & non-empty message
        byte[] hugeLenChunk = [0x00, 0x7F, 0xFF, 0xFF, 0xF5, 0x49, 0x44, 0x41, 0x54]; // offset 1 -> 0x7FFFFFF5 = int.MaxValue - 10
        var cHuge = hugeLenChunk.AsCursor();
        cHuge.Position = 1;
        var hugeRes = PngParser.ParseChunk(ref cHuge);
        Assert.True(hugeRes.TryGetValue(out Failure hugeF) && hugeF.Error.Kind == ErrorKind.LengthValue && !string.IsNullOrEmpty(hugeF.Error.Context));
        Assert.Equal(1, cHuge.Position);

        // Truncated ParseChunk at length (2B), type (5B), data (9B), and CRC (11B) — verify rollback to non-zero start
        byte[] prefixChunk = [0xAA, .. validChunk];
        foreach (int len in new[] { 3, 6, 12, 1 + validChunk.Length - 2 })
        {
            var ct = prefixChunk.AsSpan(0, len).AsCursor();
            ct.Position = 1;
            Assert.False(PngParser.ParseChunk(ref ct).IsParsed);
            Assert.Equal(1, ct.Position);
        }

        // Corrupted CRC checks message & rollback
        byte[] badCrc = [0xAA, .. validChunk];
        badCrc[^1] ^= 0xFF;
        var cCrc = badCrc.AsCursor();
        cCrc.Position = 1;
        var crcRes = PngParser.ParseChunk(ref cCrc);
        Assert.True(crcRes.TryGetValue(out Failure crcF) && crcF.Error.Kind == ErrorKind.CrcMismatch && crcF.Error.Context!.Contains("CRC"));
        Assert.Equal(1, cCrc.Position);

        // IHDR wrong length (12 bytes)
        byte[] badIhdrLen = [0xAA, .. PngParser.BuildChunk("IHDR"u8, new byte[12])];
        var cIhdrLen = badIhdrLen.AsCursor();
        cIhdrLen.Position = 1;
        Assert.True(PngParser.ParseChunk(ref cIhdrLen).TryGetValue(out Failure fIhdrLen) && !string.IsNullOrEmpty(fIhdrLen.Error.Context));
        Assert.Equal(1, cIhdrLen.Position);

        // IHDR width == 0 only, and height == 0 only (kills || -> && mutant!)
        byte[] zeroW = PngParser.BuildChunk("IHDR"u8, [0, 0, 0, 0, 0, 0, 0, 10, 8, 2, 0, 0, 0]);
        var czw = zeroW.AsCursor();
        Assert.True(PngParser.ParseChunk(ref czw).TryGetValue(out Failure fzw) && fzw.Error.Kind == ErrorKind.Verify && !string.IsNullOrEmpty(fzw.Error.Context));

        byte[] zeroH = PngParser.BuildChunk("IHDR"u8, [0, 0, 0, 10, 0, 0, 0, 0, 8, 2, 0, 0, 0]);
        var czh = zeroH.AsCursor();
        Assert.True(PngParser.ParseChunk(ref czh).TryGetValue(out Failure fzh) && fzh.Error.Kind == ErrorKind.Verify && !string.IsNullOrEmpty(fzh.Error.Context));

        // IHDR unknown color type (verify exact offset = dataSlice.Offset + 9!)
        byte[] badColor = [0xAA, .. PngParser.BuildChunk("IHDR"u8, [0, 0, 0, 1, 0, 0, 0, 1, 8, 99, 0, 0, 0])];
        var cCol = badColor.AsCursor();
        cCol.Position = 1;
        var colRes = PngParser.ParseChunk(ref cCol);
        Assert.True(colRes.TryGetValue(out Failure fCol) && fCol.Error.Offset == 1 + 8 + 9 && !string.IsNullOrEmpty(fCol.Error.Context));
        Assert.Equal(1, cCol.Position);

        // PLTE empty (len == 0) AND len % 3 != 0 (len == 4) — kills || -> && mutant!
        var cPlte0 = PngParser.BuildChunk("PLTE"u8, []).AsCursor();
        Assert.True(PngParser.ParseChunk(ref cPlte0).TryGetValue(out Failure fPlte0) && !string.IsNullOrEmpty(fPlte0.Error.Context));

        var cPlte4 = PngParser.BuildChunk("PLTE"u8, [1, 2, 3, 4]).AsCursor();
        Assert.True(PngParser.ParseChunk(ref cPlte4).TryGetValue(out Failure fPlte4) && !string.IsNullOrEmpty(fPlte4.Error.Context));

        // tEXt missing null separator
        var cTextBad = PngParser.BuildChunk("tEXt"u8, "NoNullHere"u8).AsCursor();
        Assert.True(PngParser.ParseChunk(ref cTextBad).TryGetValue(out Failure fText) && !string.IsNullOrEmpty(fText.Error.Context));

        // IEND non-empty payload
        var cIendBad = PngParser.BuildChunk("IEND"u8, [0x01]).AsCursor();
        Assert.True(PngParser.ParseChunk(ref cIendBad).TryGetValue(out Failure fIend) && !string.IsNullOrEmpty(fIend.Error.Context));

        // ParseDocument: bad signature rollback, first chunk != IHDR (check offset cp.Position + 8 & rollback), truncated mid-stream
        byte[] badDocSig = [0xAA, 0x00, 0x01, 0x02];
        var cDocSig = badDocSig.AsCursor();
        cDocSig.Position = 1;
        Assert.True(PngParser.ParseDocument(ref cDocSig).IsBacktrack);
        Assert.Equal(1, cDocSig.Position);

        byte[] wrongFirst = [0xAA, .. PngParser.Signature, .. PngParser.BuildChunk("IEND"u8, [])];
        var cWrongFirst = wrongFirst.AsCursor();
        cWrongFirst.Position = 1;
        var wfRes = PngParser.ParseDocument(ref cWrongFirst);
        Assert.True(wfRes.TryGetValue(out Failure fWf) && fWf.Error.Offset == 1 + 8 && !string.IsNullOrEmpty(fWf.Error.Context));
        Assert.Equal(1, cWrongFirst.Position);

        byte[] truncFirst = [0xAA, .. PngParser.Signature, 0x00, 0x00];
        var cTruncFirst = truncFirst.AsCursor();
        cTruncFirst.Position = 1;
        Assert.True(PngParser.ParseDocument(ref cTruncFirst).TryGetValue(out Failure fTf) && !string.IsNullOrEmpty(fTf.Error.Context));
        Assert.Equal(1, cTruncFirst.Position);

        byte[] truncMid = [0xAA, .. PngParser.Signature, .. validChunk, 0x00, 0x00];
        var cTruncMid = truncMid.AsCursor();
        cTruncMid.Position = 1;
        Assert.True(PngParser.ParseDocument(ref cTruncMid).TryGetValue(out Failure fTm) && !string.IsNullOrEmpty(fTm.Error.Context));
        Assert.Equal(1, cTruncMid.Position);

        // BuildChunk guard: invalid 4CC length throws ArgumentException
        Xunit.Assert.Throws<ArgumentException>(() => PngParser.BuildChunk("ABC"u8, []));
    }

    [Fact]
    public void WebSocket_ExtendedLengths_MaskedClose_UnmaskOffset_And_Rollbacks()
    {
        // Uninitialized WebSocketFrame & TryGetValue false branches
        WebSocketFrame uninit = default;
        Assert.False(uninit.HasValue);
        Assert.True(uninit.Value is null);
        Assert.False(uninit.TryGetValue(out CloseFrame _));
        Assert.False(uninit.TryGetValue(out PongFrame _));

        // Continuation, Ping, Pong, Close (0-byte), Close (2-byte masked!)
        ReadOnlySpan<byte> pongWire = [0x8A, 0x02, (byte)'o', (byte)'k'];
        var cPong = pongWire.AsCursor();
        var pong = WebSocketFrameParser.ParseFrame(ref cPong).Unwrap();
        Assert.True(pong.HasValue);
        Assert.True(pong.TryGetValue(out PongFrame pf) && pf.Payload.AsUtf8String(pongWire) == "ok");
        Assert.False(pong.TryGetValue(out CloseFrame _));

        ReadOnlySpan<byte> pingWire = [0x89, 0x01, 0x42];
        var cPing = pingWire.AsCursor();
        var ping = WebSocketFrameParser.ParseFrame(ref cPing).Unwrap();
        Assert.True(ping.TryGetValue(out PingFrame _));
        Assert.False(ping.TryGetValue(out PongFrame _));

        // Masked Close frame with EXACTLY 2 payload bytes (status code 0x03E8 = 1000 masked with 0xA55A_1234)
        uint mask = 0xA55A_1234u;
        byte m0 = (byte)(0x03 ^ (mask >> 24));
        byte m1 = (byte)(0xE8 ^ (mask >> 16));
        ReadOnlySpan<byte> maskedClose2 = [0x88, 0x82, 0xA5, 0x5A, 0x12, 0x34, m0, m1];
        var cMc = maskedClose2.AsCursor();
        var mc = WebSocketFrameParser.ParseFrame(ref cMc).Unwrap();
        Assert.True(mc.TryGetValue(out CloseFrame mcf));
        Assert.Equal((ushort)1000, mcf.StatusCode.UnwrapOr(0));
        Assert.Equal(0, mcf.Reason.Length);

        // Close frame with 0-byte payload (no status code)
        ReadOnlySpan<byte> close0 = [0x88, 0x00];
        var cC0 = close0.AsCursor();
        Assert.True(WebSocketFrameParser.ParseFrame(ref cC0).Unwrap().TryGetValue(out CloseFrame cf0) && cf0.StatusCode.IsNone);

        // Close frame with 1-byte payload -> Failure with rollback & non-empty message
        byte[] close1 = [0xFF, 0x88, 0x01, 0x00];
        var cC1 = close1.AsCursor();
        cC1.Position = 1;
        Assert.True(WebSocketFrameParser.ParseFrame(ref cC1).TryGetValue(out Failure fC1) && !string.IsNullOrEmpty(fC1.Error.Context));
        Assert.Equal(1, cC1.Position);

        // 64-bit extended payload length (valid 130 bytes)
        byte[] len64Valid = new byte[10 + 130];
        len64Valid[0] = 0x82; // FIN=1, Binary
        len64Valid[1] = 127;  // 64-bit length
        BinaryPrimitives.WriteUInt64BigEndian(len64Valid.AsSpan(2, 8), 130);
        var c64 = len64Valid.AsCursor();
        Assert.True(WebSocketFrameParser.ParseFrame(ref c64).Unwrap().TryGetValue(out BinaryFrame bf64) && bf64.Payload.Length == 130);

        // 64-bit extended length truncated -> rollback
        byte[] len64Trunc = [0xAA, 0x82, 127, 0, 0, 0];
        var c64T = len64Trunc.AsCursor();
        c64T.Position = 1;
        Assert.True(WebSocketFrameParser.ParseFrame(ref c64T).IsBacktrack);
        Assert.Equal(1, c64T.Position);

        // 64-bit extended length with MSB=1 -> Failure at cp.Position + 2, rollback, message
        byte[] len64Msb = [0xAA, 0x82, 127, 0x80, 0, 0, 0, 0, 0, 0, 5];
        var c64Msb = len64Msb.AsCursor();
        c64Msb.Position = 1;
        Assert.True(WebSocketFrameParser.ParseFrame(ref c64Msb).TryGetValue(out Failure fMsb)
            && fMsb.Error.Offset == 3 && !string.IsNullOrEmpty(fMsb.Error.Context));
        Assert.Equal(1, c64Msb.Position);

        // 64-bit extended length > int.MaxValue (0x0000_0001_0000_0000) -> Failure at cp.Position + 2, rollback, message
        byte[] len64Over = [0xAA, 0x82, 127, 0, 0, 0, 1, 0, 0, 0, 0];
        var c64Over = len64Over.AsCursor();
        c64Over.Position = 1;
        Assert.True(WebSocketFrameParser.ParseFrame(ref c64Over).TryGetValue(out Failure fOver)
            && fOver.Error.Offset == 3 && fOver.Error.Kind == ErrorKind.LengthValue && !string.IsNullOrEmpty(fOver.Error.Context));
        Assert.Equal(1, c64Over.Position);

        // 16-bit extended length truncated, header truncated, mask truncated, payload truncated -> all rollback to pos 1
        byte[][] truncCases =
        [
            [0xAA, 0x81],                     // 1-byte header
            [0xAA, 0x82, 126, 0x00],          // truncated 16-bit length
            [0xAA, 0x81, 0x82, 0x11, 0x22],   // truncated mask key
            [0xAA, 0x81, 0x05, 0x41, 0x42],   // truncated payload
        ];
        foreach (var wire in truncCases)
        {
            var ct = wire.AsCursor();
            ct.Position = 1;
            Assert.False(WebSocketFrameParser.ParseFrame(ref ct).IsParsed);
            Assert.Equal(1, ct.Position);
        }

        // Control frame > 125 bytes & fragmented control frame -> rollback + message
        byte[] bigPing = [0xAA, 0x89, 126, 0x00, 126];
        var cBigPing = bigPing.AsCursor();
        cBigPing.Position = 1;
        Assert.True(WebSocketFrameParser.ParseFrame(ref cBigPing).TryGetValue(out Failure fBp) && !string.IsNullOrEmpty(fBp.Error.Context));
        Assert.Equal(1, cBigPing.Position);

        // Reserved opcode 0x3 -> rollback + message
        byte[] badOp = [0xAA, 0x83, 0x00];
        var cBadOp = badOp.AsCursor();
        cBadOp.Position = 1;
        Assert.True(WebSocketFrameParser.ParseFrame(ref cBadOp).TryGetValue(out Failure fOp) && !string.IsNullOrEmpty(fOp.Error.Context));
        Assert.Equal(1, cBadOp.Position);

        // UnmaskInto: unmasked copy, destination too small throws, and non-zero maskOffset!
        byte[] plain = "ABCD"u8.ToArray();
        byte[] dest = new byte[4];
        WebSocketFrameParser.UnmaskInto(plain, new ByteSlice(0, 4), Option<uint>.Nothing, dest);
        Assert.SequenceEqual(plain, dest);

        Xunit.Assert.Throws<ArgumentException>(() =>
            WebSocketFrameParser.UnmaskInto(plain, new ByteSlice(0, 4), Option<uint>.Nothing, new byte[2]));

        // maskOffset = 1 uses mask byte 1 (0x5A) for index 0, byte 2 (0x12) for index 1, etc.
        byte[] maskedOff = [(byte)('A' ^ 0x5A), (byte)('B' ^ 0x12), (byte)('C' ^ 0x34), (byte)('D' ^ 0xA5)];
        WebSocketFrameParser.UnmaskInto(maskedOff, new ByteSlice(0, 4), Option<uint>.Of(mask), dest, maskOffset: 1);
        Assert.Equal("ABCD", Encoding.ASCII.GetString(dest));
    }

    [Fact]
    public void Resp_AllErrorPaths_NullArray_Context_And_Rollbacks()
    {
        // Null array (*-1\r\n) -> RespNull
        ReadOnlySpan<byte> nullArr = "*-1\r\n"u8;
        var cna = nullArr.AsCursor();
        Assert.True(RespParser.ParseValue(ref cna).Unwrap() is RespNull);

        // All committed failure paths starting at non-zero offset (verifies Cut context & rollback!)
        byte[][] failCases =
        [
            "X+NO_CRLF"u8.ToArray(),
            "X-NO_CRLF"u8.ToArray(),
            "X:NO_CRLF"u8.ToArray(),
            "X:12x\r\n"u8.ToArray(),
            "X:abc\r\n"u8.ToArray(),
            "X$NO_CRLF"u8.ToArray(),
            "X$abc\r\n"u8.ToArray(),
            "X$12x\r\n"u8.ToArray(),
            "X$-2\r\n"u8.ToArray(),
            "X$5\r\nhi"u8.ToArray(),
            "X$2\r\nhiXX"u8.ToArray(),
            "X*NO_CRLF"u8.ToArray(),
            "X*abc\r\n"u8.ToArray(),
            "X*1x\r\n"u8.ToArray(),
            "X*-2\r\n"u8.ToArray(),
            "X*2\r\n+OK\r\n"u8.ToArray(),
        ];

        foreach (var wire in failCases)
        {
            var c = wire.AsCursor();
            c.Position = 1;
            var res = RespParser.ParseValue(ref c);
            Assert.True(res.TryGetValue(out Failure f) && !string.IsNullOrEmpty(f.Error.Context));
            Assert.Equal(1, c.Position);
        }

        // Streaming incomplete on each RESP type rolls back cleanly
        byte[][] incompleteCases =
        [
            "X+OK"u8.ToArray(),
            "X-ERR"u8.ToArray(),
            "X:42"u8.ToArray(),
            "X$3\r\nab"u8.ToArray(),
            "X*1\r\n+O"u8.ToArray(),
        ];

        foreach (var wire in incompleteCases)
        {
            var c = wire.AsStreamingCursor();
            c.Position = 1;
            Assert.True(RespParser.ParseValue(ref c).IsIncomplete);
            Assert.Equal(1, c.Position);
        }
    }

    [Fact]
    public void Dns_AllRecordTypes_CompressionEdgeCases_Streaming_And_Rollbacks()
    {
        // ParseHeader truncated at every length 0..11 rolls back to non-zero start
        byte[] hdrWire = [0xAA, 0x12, 0x34, 0x81, 0x80, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        for (int len = 1; len <= 12; len++)
        {
            var ch = hdrWire.AsSpan(0, len).AsCursor();
            ch.Position = 1;
            Assert.False(DnsParser.ParseHeader(ref ch).IsParsed);
            Assert.Equal(1, ch.Position);
        }

        // ParseName streaming vs complete: empty, truncated ptr, truncated label (verify exact Needed bytes!)
        byte[] empty = [0xAA];
        var cEmptyC = empty.AsCursor(); cEmptyC.Position = 1;
        Assert.True(DnsParser.ParseName(ref cEmptyC).TryGetValue(out Failure fEmpty) && !string.IsNullOrEmpty(fEmpty.Error.Context));
        Assert.Equal(1, cEmptyC.Position);
        var cEmptyS = empty.AsStreamingCursor(); cEmptyS.Position = 1;
        Assert.True(DnsParser.ParseName(ref cEmptyS).TryGetValue(out Incomplete incEmpty) && incEmpty.Needed == Needed.Bytes(1));

        byte[] truncPtr = [0xAA, 0xC0];
        var cPtrC = truncPtr.AsCursor(); cPtrC.Position = 1;
        Assert.True(DnsParser.ParseName(ref cPtrC).TryGetValue(out Failure fPtr) && !string.IsNullOrEmpty(fPtr.Error.Context));
        Assert.Equal(1, cPtrC.Position);
        var cPtrS = truncPtr.AsStreamingCursor(); cPtrS.Position = 1;
        Assert.True(DnsParser.ParseName(ref cPtrS).TryGetValue(out Incomplete incPtr) && incPtr.Needed == Needed.Bytes(1));

        byte[] truncLabel = [0xAA, 5, (byte)'a', (byte)'b'];
        var cLblC = truncLabel.AsCursor(); cLblC.Position = 1;
        Assert.True(DnsParser.ParseName(ref cLblC).TryGetValue(out Failure fLbl) && !string.IsNullOrEmpty(fLbl.Error.Context));
        Assert.Equal(1, cLblC.Position);
        var cLblS = truncLabel.AsStreamingCursor(); cLblS.Position = 1;
        Assert.True(DnsParser.ParseName(ref cLblS).TryGetValue(out Incomplete incLbl) && incLbl.Needed == Needed.Bytes(3));

        // Pointer out-of-bounds exactly at target == cursor.Length
        byte[] oobPtr = [0xAA, 0xC0, 0x03];
        var cOob = oobPtr.AsCursor(); cOob.Position = 1;
        Assert.True(DnsParser.ParseName(ref cOob).TryGetValue(out Failure fOob) && !string.IsNullOrEmpty(fOob.Error.Context));
        Assert.Equal(1, cOob.Position);

        // Invalid label header byte (0x40 / 0x80)
        byte[] badLbl = [0xAA, 0x40, 0x00];
        var cBadLbl = badLbl.AsCursor(); cBadLbl.Position = 1;
        Assert.True(DnsParser.ParseName(ref cBadLbl).TryGetValue(out Failure fBadLbl) && !string.IsNullOrEmpty(fBadLbl.Error.Context));
        Assert.Equal(1, cBadLbl.Position);

        // Compression pointer at offset 0 pointing to offset 2 (kills endPosAfterFirstJump <= 0 / > 0!)
        byte[] ptrAtZero = [0xC0, 0x02, 2, (byte)'g', (byte)'o', 0];
        var cPtr0 = ptrAtZero.AsCursor();
        Assert.Equal("go", DnsParser.ParseName(ref cPtr0).Unwrap());
        Assert.Equal(2, cPtr0.Position);

        // High compression pointer target >= 256 (0xC1 0x00 -> offset 256!) kills (len & 0x3F) >> 8!
        byte[] highPtrBuf = new byte[264];
        highPtrBuf[0] = 0xC1;
        highPtrBuf[1] = 0x00;
        highPtrBuf[256] = 2;
        highPtrBuf[257] = (byte)'h';
        highPtrBuf[258] = (byte)'i';
        highPtrBuf[259] = 0;
        var cHigh = highPtrBuf.AsCursor();
        Assert.Equal("hi", DnsParser.ParseName(ref cHigh).Unwrap());
        Assert.Equal(2, cHigh.Position);

        // Exact jump limit boundary: 16 pointer jumps succeed; 17 pointer jumps fail!
        byte[] chain16 = new byte[16 * 2 + 3];
        for (int i = 0; i < 16; i++)
        {
            chain16[i * 2] = 0xC0;
            chain16[i * 2 + 1] = (byte)((i + 1) * 2);
        }
        chain16[32] = 1; chain16[33] = (byte)'a'; chain16[34] = 0;
        var c16 = chain16.AsCursor();
        Assert.Equal("a", DnsParser.ParseName(ref c16).Unwrap());

        byte[] chain17 = new byte[17 * 2 + 3];
        for (int i = 0; i < 17; i++)
        {
            chain17[i * 2] = 0xC0;
            chain17[i * 2 + 1] = (byte)((i + 1) * 2);
        }
        chain17[34] = 1; chain17[35] = (byte)'a'; chain17[36] = 0;
        var c17 = chain17.AsCursor();
        Assert.True(DnsParser.ParseName(ref c17).TryGetValue(out Failure f17) && !string.IsNullOrEmpty(f17.Error.Context));
        Assert.Equal(0, c17.Position);

        // ParseQuestion & ParseResourceRecord truncation rollbacks + AAAA (16B vs 15B), TXT, Unknown, bad CNAME
        byte[] qWire = [0xAA, 1, (byte)'a', 0, 0x00, 0x01, 0x00, 0x01];
        foreach (int len in new[] { 2, 4, 6 })
        {
            var cq = qWire.AsSpan(0, len).AsCursor(); cq.Position = 1;
            Assert.False(DnsParser.ParseQuestion(ref cq).IsParsed);
            Assert.Equal(1, cq.Position);
        }

        // Valid AAAA (16B), invalid AAAA (15B -> UnknownRecord), TXT, bad CNAME
        byte[] rrAaaa = [0xAA, 1, (byte)'a', 0, 0x00, 0x1C, 0x00, 0x01, 0, 0, 0, 60, 0x00, 16, .. new byte[16]];
        foreach (int len in new[] { 2, 5, 7, 10, 13, 20 })
        {
            var crr = rrAaaa.AsSpan(0, len).AsCursor(); crr.Position = 1;
            Assert.False(DnsParser.ParseResourceRecord(ref crr).IsParsed);
            Assert.Equal(1, crr.Position);
        }
        var cAaaaOk = rrAaaa.AsCursor(); cAaaaOk.Position = 1;
        Assert.True(DnsParser.ParseResourceRecord(ref cAaaaOk).Unwrap().Data is AaaaRecord { Raw16Bytes.Length: 16 });

        byte[] rrAaaaBad = [1, (byte)'a', 0, 0x00, 0x1C, 0x00, 0x01, 0, 0, 0, 60, 0x00, 15, .. new byte[15]];
        var cAaaaBad = rrAaaaBad.AsCursor();
        Assert.True(DnsParser.ParseResourceRecord(ref cAaaaBad).Unwrap().Data is UnknownRecord { RecordType: DnsParser.TypeAaaa });

        byte[] rrTxt = [1, (byte)'a', 0, 0x00, 0x10, 0x00, 0x01, 0, 0, 0, 10, 0x00, 3, 2, (byte)'o', (byte)'k'];
        var cTxt = rrTxt.AsCursor();
        Assert.True(DnsParser.ParseResourceRecord(ref cTxt).Unwrap().Data is TxtRecord { RawTxt.Length: 3 });

        byte[] rrBadCname = [0xAA, 1, (byte)'a', 0, 0x00, 0x05, 0x00, 0x01, 0, 0, 0, 10, 0x00, 1, 0xC0];
        var cBadCname = rrBadCname.AsCursor(); cBadCname.Position = 1;
        Assert.True(DnsParser.ParseResourceRecord(ref cBadCname).IsFailure);
        Assert.Equal(1, cBadCname.Position);

        // ParsePacket failures at header, question, and answer sections — verify rollback + context!
        byte[] badPktHdr = [0xAA, 0x12, 0x34];
        var cpHdr = badPktHdr.AsCursor(); cpHdr.Position = 1;
        Assert.False(DnsParser.ParsePacket(ref cpHdr).IsParsed);
        Assert.Equal(1, cpHdr.Position);

        byte[] badPktQ = [0xAA, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0]; // QD=1, missing question
        var cpQ = badPktQ.AsCursor(); cpQ.Position = 1;
        Assert.True(DnsParser.ParsePacket(ref cpQ).TryGetValue(out Failure fPktQ) && !string.IsNullOrEmpty(fPktQ.Error.Context));
        Assert.Equal(1, cpQ.Position);

        byte[] badPktA = [0xAA, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0]; // QD=0, AN=1, missing answer
        var cpA = badPktA.AsCursor(); cpA.Position = 1;
        Assert.True(DnsParser.ParsePacket(ref cpA).TryGetValue(out Failure fPktA) && !string.IsNullOrEmpty(fPktA.Error.Context));
        Assert.Equal(1, cpA.Position);
    }
}
