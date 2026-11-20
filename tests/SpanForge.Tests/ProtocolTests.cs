using System.Text;
using SpanForge.Protocols.Dns;
using SpanForge.Protocols.Png;
using SpanForge.Protocols.Resp;
using SpanForge.Protocols.WebSocket;

namespace SpanForge.Tests;

internal static class ProtocolTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("Protocols/PNG full document + CRC-32 validation + corruption detection", TestPngProtocol);
        suite.Add("Protocols/WebSocket RFC 6455 frames + unmasking + RFC violation checks", TestWebSocketProtocol);
        suite.Add("Protocols/Redis RESP2 closed hierarchy + committed Cut errors", TestRespProtocol);
        suite.Add("Protocols/DNS RFC 1035 flags, pointer compression, CNAME/A/TXT & cycle guard", TestDnsProtocol);
    }

    private static void TestPngProtocol()
    {
        byte[] ihdrPayload = [0, 0, 0x01, 0x00, 0, 0, 0x00, 0x80, 8, 2, 0, 0, 0]; // 256x128 Truecolor
        byte[] png =
        [
            .. PngParser.Signature,
            .. PngParser.BuildChunk("IHDR"u8, ihdrPayload),
            .. PngParser.BuildChunk("PLTE"u8, [255, 0, 0, 0, 255, 0]),
            .. PngParser.BuildChunk("tEXt"u8, "Title\0SpanForge PNG"u8),
            .. PngParser.BuildChunk("IDAT"u8, [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]),
            .. PngParser.BuildChunk("gAMA"u8, [0x00, 0x00, 0xB1, 0x8F]),
            .. PngParser.BuildChunk("IEND"u8, []),
        ];

        var cursor = png.AsCursor();
        var doc = PngParser.ParseDocument(ref cursor).Unwrap();

        Assert.Equal(256u, doc.Header.Width);
        Assert.Equal(128u, doc.Header.Height);
        Assert.Equal(PngColorType.Truecolor, doc.Header.ColorType);
        Assert.Equal(6, doc.Chunks.Count);

        // Exhaustive C# 15 switch over `public union PngChunk`!
        var kinds = new List<string>();
        foreach (var chunk in doc.Chunks)
        {
            string label = chunk switch
            {
                IhdrChunk h => $"IHDR({h.Width}x{h.Height})",
                PlteChunk p => $"PLTE({p.EntryCount})",
                TextChunk(var kw, var txt) => $"tEXt({kw.AsUtf8String(png)}={txt.AsUtf8String(png)})",
                IdatChunk d => $"IDAT({d.Data.Length})",
                UnknownChunk u => $"UNK({u.TypeName})",
                IendChunk => "IEND",
            };
            kinds.Add(label);
        }

        Assert.Equal("IHDR(256x128)", kinds[0]);
        Assert.Equal("PLTE(2)", kinds[1]);
        Assert.Equal("tEXt(Title=SpanForge PNG)", kinds[2]);
        Assert.Equal("IDAT(8)", kinds[3]);
        Assert.Equal("UNK(gAMA)", kinds[4]);
        Assert.Equal("IEND", kinds[5]);

        // Corrupt a single payload byte and verify CRC-32 Failure!
        byte[] corrupted = [.. png];
        corrupted[12] ^= 0x01;
        var badCursor = corrupted.AsCursor();
        var badRes = PngParser.ParseDocument(ref badCursor);
        Assert.True(badRes.TryGetValue(out Failure f) && f.Error.Kind == ErrorKind.CrcMismatch);
    }

    private static void TestWebSocketProtocol()
    {
        // Masked Text frame "Hello"
        ReadOnlySpan<byte> textWire = [0x81, 0x85, 0x37, 0xFA, 0x21, 0x3D, 0x7F, 0x9F, 0x4D, 0x51, 0x58];
        var c1 = textWire.AsCursor();
        var f1 = WebSocketFrameParser.ParseFrame(ref c1).Unwrap();
        Assert.True(f1 is TextFrame { Fin: true });
        if (f1 is TextFrame tf)
        {
            Span<byte> unmasked = stackalloc byte[tf.Payload.Length];
            WebSocketFrameParser.UnmaskInto(textWire, tf.Payload, tf.MaskKey, unmasked);
            Assert.Equal("Hello", Encoding.UTF8.GetString(unmasked));
        }

        // 16-bit extended Binary frame (130 bytes)
        byte[] binWire = new byte[4 + 130];
        binWire[0] = 0x82; // FIN=1, opcode=Binary
        binWire[1] = 126;  // 16-bit length follows
        binWire[2] = 0x00;
        binWire[3] = 130;
        binWire[4] = 0xCA;
        binWire[133] = 0xFE;

        var c2 = binWire.AsCursor();
        var f2 = WebSocketFrameParser.ParseFrame(ref c2).Unwrap();
        Assert.True(f2 is BinaryFrame { Fin: true, Payload.Length: 130 });

        // Close frame with status code 1000 ("bye")
        ReadOnlySpan<byte> closeWire = [0x88, 0x05, 0x03, 0xE8, (byte)'b', (byte)'y', (byte)'e'];
        var c3 = closeWire.AsCursor();
        var f3 = WebSocketFrameParser.ParseFrame(ref c3).Unwrap();
        Assert.True(f3 is CloseFrame { StatusCode.IsSome: true });
        if (f3 is CloseFrame cf)
        {
            Assert.Equal((ushort)1000, cf.StatusCode.UnwrapOr(0));
            Assert.Equal("bye", cf.Reason.AsUtf8String(closeWire));
        }

        // Fragmented Ping frame (FIN=0, opcode=9) must fail per RFC 6455 §5.5!
        ReadOnlySpan<byte> badPing = [0x09, 0x00];
        var c4 = badPing.AsCursor();
        Assert.True(WebSocketFrameParser.ParseFrame(ref c4).IsFailure);
    }

    private static void TestRespProtocol()
    {
        ReadOnlySpan<byte> resp = "*4\r\n+OK\r\n-ERR boom\r\n:-12345\r\n$-1\r\n"u8;
        var c = resp.AsCursor();
        var val = RespParser.ParseValue(ref c).Unwrap();
        Assert.Equal("[+OK, -ERR boom, :-12345, null]", RespParser.Format(val, resp));
        Assert.True(c.IsAtEnd);

        // Malformed integer after ':' must produce committed Failure (via Cut) with exact offset!
        ReadOnlySpan<byte> bad = ":12x4\r\n"u8;
        var cb = bad.AsCursor();
        var res = RespParser.ParseValue(ref cb);
        Assert.True(res.TryGetValue(out Failure f) && f.Error.Offset == 1);
    }

    private static void TestDnsProtocol()
    {
        // Craft a real RFC 1035 DNS response packet:
        // Header (12 bytes): ID=0x1A2B, Flags=0x8180 (QR=1, RD=1, RA=1, RCODE=0), QD=1, AN=2, NS=0, AR=0
        // Question at offset 12: "www.example.com" (3 www 7 example 3 com 0), Type=A (1), Class=IN (1)
        // Answer 1: Name=0xC0 0x0C (pointer to offset 12!), Type=CNAME (5), Class=IN, TTL=300, RDATA = "cdn" + ptr to "example.com" (offset 16!)
        // Answer 2: Name=0xC0 0x0C, Type=A (1), Class=IN, TTL=60, RDATA = 93.184.216.34
        byte[] packet =
        [
            // Header (0..11)
            0x1A, 0x2B,
            0x81, 0x80,
            0x00, 0x01,
            0x00, 0x02,
            0x00, 0x00,
            0x00, 0x00,
            // Question (12..32): "www.example.com"
            3, (byte)'w', (byte)'w', (byte)'w',               // 12..15
            7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', // 16..23
            3, (byte)'c', (byte)'o', (byte)'m',               // 24..27
            0,                                                // 28
            0x00, 0x01,                                       // QTYPE=A
            0x00, 0x01,                                       // QCLASS=IN
            // Answer 1: CNAME -> "cdn.example.com" (uses pointer to offset 16)
            0xC0, 0x0C,                                       // ptr -> www.example.com
            0x00, 0x05,                                       // TYPE=CNAME
            0x00, 0x01,                                       // CLASS=IN
            0x00, 0x00, 0x01, 0x2C,                           // TTL=300
            0x00, 0x06,                                       // RDLENGTH=6
            3, (byte)'c', (byte)'d', (byte)'n', 0xC0, 0x10,   // "cdn" + ptr(16 = "example.com")
            // Answer 2: A -> 93.184.216.34
            0xC0, 0x0C,                                       // ptr -> www.example.com
            0x00, 0x01,                                       // TYPE=A
            0x00, 0x01,                                       // CLASS=IN
            0x00, 0x00, 0x00, 0x3C,                           // TTL=60
            0x00, 0x04,                                       // RDLENGTH=4
            93, 184, 216, 34,
        ];

        var c = packet.AsCursor();
        var parsed = DnsParser.ParsePacket(ref c).Unwrap();

        Assert.Equal((ushort)0x1A2B, parsed.Header.TransactionId);
        Assert.True(parsed.Header.Flags.IsResponse);
        Assert.True(parsed.Header.Flags.RecursionDesired);
        Assert.True(parsed.Header.Flags.RecursionAvailable);
        Assert.Equal((byte)0, parsed.Header.Flags.ResponseCode);

        Assert.Equal(1, parsed.Questions.Count);
        Assert.Equal("www.example.com", parsed.Questions[0].Name);

        Assert.Equal(2, parsed.Answers.Count);
        Assert.Equal("www.example.com", parsed.Answers[0].Name);
        Assert.Equal(300u, parsed.Answers[0].Ttl);

        // Exhaustive C# 15 switch over `public union DnsRecordData`!
        string ans0 = parsed.Answers[0].Data switch
        {
            CNameRecord(var target) => $"CNAME:{target}",
            ARecord a => $"A:{a.ToIPAddress()}",
            AaaaRecord => "AAAA",
            TxtRecord => "TXT",
            UnknownRecord => "UNK",
        };
        string ans1 = parsed.Answers[1].Data switch
        {
            CNameRecord(var target) => $"CNAME:{target}",
            ARecord a => $"A:{a.ToIPAddress()}",
            AaaaRecord => "AAAA",
            TxtRecord => "TXT",
            UnknownRecord => "UNK",
        };

        Assert.Equal("CNAME:cdn.example.com", ans0);
        Assert.Equal("A:93.184.216.34", ans1);

        // Self-referential compression pointer loop must be rejected safely!
        byte[] loopPacket = [0xC0, 0x00];
        var cl = loopPacket.AsCursor();
        Assert.True(DnsParser.ParseName(ref cl).IsFailure);
    }
}
