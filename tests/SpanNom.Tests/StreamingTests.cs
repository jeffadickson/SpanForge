using System.Text;
using SpanNom.Protocols.Png;
using SpanNom.Protocols.Resp;
using SpanNom.Protocols.WebSocket;
using SpanNom.Streaming;

namespace SpanNom.Tests;

internal static class StreamingTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("Streaming/Byte-by-byte WebSocket frame trickle decode", TestWebSocketTrickle);
        suite.Add("Streaming/Byte-by-byte PNG chunk stream decode", TestPngChunkTrickle);
        suite.Add("Streaming/Byte-by-byte Redis RESP array trickle decode", TestRespTrickle);
    }

    private static void TestWebSocketTrickle()
    {
        // Two back-to-back WebSocket frames:
        // Frame 1: Masked TextFrame "Hello" (RFC 6455 §5.7 example: 81 85 37 FA 21 3D 7F 9F 4D 51 58)
        // Frame 2: Unmasked PingFrame "OK" (89 02 4F 4B)
        byte[] stream =
        [
            0x81, 0x85, 0x37, 0xFA, 0x21, 0x3D, 0x7F, 0x9F, 0x4D, 0x51, 0x58,
            0x89, 0x02, 0x4F, 0x4B,
        ];

        var decoder = new StreamingDecoder<WebSocketFrame>(WebSocketFrameParser.ParseFrame, initialCapacity: 8);
        var decoded = new List<string>();
        int incompleteCount = 0;

        // Feed 1 byte at a time!
        for (int i = 0; i < stream.Length; i++)
        {
            decoder.Append(stream.AsSpan(i, 1));

            while (true)
            {
                var res = decoder.TryDecodeNext(out var frameBytes);
                if (res.TryGetValue(out Parsed<WebSocketFrame> ok))
                {
                    string summary = ok.Value switch
                    {
                        TextFrame(var fin, var mask, var payload) => UnmaskText(frameBytes, payload, mask, fin),
                        PingFrame(var mask, var payload) => $"PING:{UnmaskRaw(frameBytes, payload, mask)}",
                        BinaryFrame => "BIN",
                        CloseFrame => "CLOSE",
                        PongFrame => "PONG",
                        ContinuationFrame => "CONT",
                    };
                    decoded.Add(summary);
                }
                else if (res.IsIncomplete)
                {
                    incompleteCount++;
                    break;
                }
                else
                {
                    throw new AssertionException($"Unexpected streaming error at byte {i}: {res}");
                }
            }
        }

        Assert.Equal(2, decoded.Count);
        Assert.Equal("TEXT(fin=True):Hello", decoded[0]);
        Assert.Equal("PING:OK", decoded[1]);
        Assert.True(incompleteCount > 10, "Should have yielded Incomplete multiple times during 1-byte trickle");
        Assert.Equal(0, decoder.BufferedCount);

        static string UnmaskText(ReadOnlySpan<byte> buf, ByteSlice payload, Option<uint> mask, bool fin)
        {
            Span<byte> scratch = stackalloc byte[payload.Length];
            WebSocketFrameParser.UnmaskInto(buf, payload, mask, scratch);
            return $"TEXT(fin={fin}):{Encoding.UTF8.GetString(scratch)}";
        }

        static string UnmaskRaw(ReadOnlySpan<byte> buf, ByteSlice payload, Option<uint> mask)
        {
            Span<byte> scratch = stackalloc byte[payload.Length];
            WebSocketFrameParser.UnmaskInto(buf, payload, mask, scratch);
            return Encoding.UTF8.GetString(scratch);
        }
    }

    private static void TestPngChunkTrickle()
    {
        byte[] ihdrPayload = [0, 0, 0, 16, 0, 0, 0, 8, 8, 6, 0, 0, 0];
        byte[] chunk1 = PngParser.BuildChunk("IHDR"u8, ihdrPayload);
        byte[] chunk2 = PngParser.BuildChunk("tEXt"u8, "Author\0SpanNom"u8);
        byte[] chunk3 = PngParser.BuildChunk("IEND"u8, []);
        byte[] wire = [.. chunk1, .. chunk2, .. chunk3];

        var decoder = new StreamingDecoder<PngChunk>(PngParser.ParseChunk, initialCapacity: 16);
        var summaries = new List<string>();

        // Feed in uneven 3-byte chunks!
        for (int offset = 0; offset < wire.Length; offset += 3)
        {
            int len = Math.Min(3, wire.Length - offset);
            decoder.Append(wire.AsSpan(offset, len));

            while (true)
            {
                var res = decoder.TryDecodeNext(out var frame);
                if (res.TryGetValue(out Parsed<PngChunk> ok))
                {
                    string desc = ok.Value switch
                    {
                        IhdrChunk h => $"IHDR:{h.Width}x{h.Height}",
                        TextChunk(var kw, var txt) => $"tEXt:{kw.AsUtf8String(frame)}={txt.AsUtf8String(frame)}",
                        IendChunk => "IEND",
                        PlteChunk p => $"PLTE:{p.EntryCount}",
                        IdatChunk d => $"IDAT:{d.Data.Length}",
                        UnknownChunk u => $"UNK:{u.TypeName}",
                    };
                    summaries.Add(desc);
                }
                else if (res.IsIncomplete)
                {
                    break;
                }
                else
                {
                    throw new AssertionException($"Unexpected PNG streaming error: {res}");
                }
            }
        }

        Assert.Equal(3, summaries.Count);
        Assert.Equal("IHDR:16x8", summaries[0]);
        Assert.Equal("tEXt:Author=SpanNom", summaries[1]);
        Assert.Equal("IEND", summaries[2]);
    }

    private static void TestRespTrickle()
    {
        byte[] wire = "*3\r\n$3\r\nSET\r\n$3\r\nkey\r\n:42\r\n"u8.ToArray();
        var decoder = new StreamingDecoder<RespValue>(RespParser.ParseValue);
        string? parsed = null;

        for (int i = 0; i < wire.Length; i++)
        {
            decoder.Append(wire.AsSpan(i, 1));
            var res = decoder.TryDecodeNext(out var frame);
            if (res.TryGetValue(out Parsed<RespValue> ok))
            {
                parsed = RespParser.Format(ok.Value, frame);
            }
            else
            {
                Assert.True(res.IsIncomplete, $"Expected Incomplete at byte {i}, got {res}");
            }
        }

        Assert.Equal("[\"SET\", \"key\", :42]", parsed);
    }
}
