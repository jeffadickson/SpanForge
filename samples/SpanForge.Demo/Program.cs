using System.Diagnostics;
using System.Text;
using SpanForge;
using SpanForge.Protocols.Dns;
using SpanForge.Protocols.Png;
using SpanForge.Protocols.Resp;
using SpanForge.Protocols.WebSocket;
using SpanForge.Streaming;

Console.WriteLine("=================================================================");
Console.WriteLine("  SpanForge — C# 15 Union-Powered Zero-Copy Span & Binary Parser");
Console.WriteLine("  Combining Rust (nom + winnow + binrw + zerocopy) with .NET 11");
Console.WriteLine("=================================================================");
Console.WriteLine();

// 1. PNG Protocol Showcase (zero-boxing C# 15 `[Union] PngChunk` + slicing-by-8 CRC-32)
{
    Console.WriteLine("[1] PNG Document Parser (zero-boxing C# 15 `[Union] PngChunk`)");
    byte[] png =
    [
        .. PngParser.Signature,
        .. PngParser.BuildChunk("IHDR"u8, [0, 0, 0x07, 0x80, 0, 0, 0x04, 0x38, 8, 6, 0, 0, 0]), // 1920x1080 RGBA
        .. PngParser.BuildChunk("tEXt"u8, "Software\0SpanForge on .NET 11"u8),
        .. PngParser.BuildChunk("IDAT"u8, [0x78, 0x9C, 0xED, 0xC1, 0x01, 0x01]),
        .. PngParser.BuildChunk("IEND"u8, []),
    ];

    var cursor = png.AsCursor();
    var doc = PngParser.ParseDocument(ref cursor).Unwrap();
    foreach (var chunk in doc.Chunks)
    {
        string line = chunk switch
        {
            IhdrChunk h => $"  - IHDR: {h.Width}x{h.Height}, {h.BitDepth}-bit {h.ColorType}",
            PlteChunk p => $"  - PLTE: {p.EntryCount} RGB entries",
            TextChunk(var kw, var txt) => $"  - tEXt: {kw.AsUtf8String(png)} = \"{txt.AsUtf8String(png)}\"",
            IdatChunk d => $"  - IDAT: {d.Data.Length} compressed bytes at {d.Data}",
            UnknownChunk u => $"  - {u.TypeName}: {u.Data.Length} bytes",
            IendChunk => "  - IEND (end of stream)",
        };
        Console.WriteLine(line);
    }

    Console.WriteLine();
}

// 2. WebSocket Streaming Trickle Showcase (BitCursor + zero-boxing `[Union] WebSocketFrame`)
{
    Console.WriteLine("[2] WebSocket RFC 6455 Byte-by-Byte Streaming Decoder");
    byte[] wire = [0x81, 0x85, 0x37, 0xFA, 0x21, 0x3D, 0x7F, 0x9F, 0x4D, 0x51, 0x58];
    var decoder = new StreamingDecoder<WebSocketFrame>(WebSocketFrameParser.ParseFrame);
    int polls = 0;

    for (int i = 0; i < wire.Length; i++)
    {
        decoder.Append(wire.AsSpan(i, 1));
        polls++;
        var res = decoder.TryDecodeNext(out var frameBytes);
        switch (res)
        {
            case Parsed<WebSocketFrame>(var frame):
                string decoded = frame switch
                {
                    TextFrame(var fin, var mask, var payload) => Unmask(frameBytes, payload, mask, fin),
                    BinaryFrame => "Binary",
                    PingFrame => "Ping",
                    PongFrame => "Pong",
                    CloseFrame => "Close",
                    ContinuationFrame => "Continuation",
                };
                Console.WriteLine($"  Decoded after {polls} single-byte chunks: {decoded}");
                break;
            case Incomplete:
                break;
            case Backtrack(var err):
                Console.WriteLine($"  Backtrack: {err}");
                break;
            case Failure(var err):
                Console.WriteLine($"  Failure: {err}");
                break;
        }
    }

    Console.WriteLine();

    static string Unmask(ReadOnlySpan<byte> buf, ByteSlice payload, Option<uint> mask, bool fin)
    {
        Span<byte> dst = stackalloc byte[payload.Length];
        WebSocketFrameParser.UnmaskInto(buf, payload, mask, dst);
        return $"TextFrame(Fin={fin}, Payload=\"{Encoding.UTF8.GetString(dst)}\")";
    }
}

// 3. Redis RESP2 Showcase (C# 15 `closed record class RespValue` + Hex-Dump Diagnostics)
{
    Console.WriteLine("[3] Redis RESP2 Parser (C# 15 `closed record class RespValue`)");
    ReadOnlySpan<byte> cmd = "*3\r\n$3\r\nSET\r\n$7\r\nversion\r\n$6\r\nc#15.0\r\n"u8;
    var c = cmd.AsCursor();
    var value = RespParser.ParseValue(ref c).Unwrap();
    Console.WriteLine($"  Parsed command: {RespParser.Format(value, cmd)}");

    // Demonstrate hex-dump diagnostic formatting on a committed Failure:
    ReadOnlySpan<byte> badResp = "*2\r\n$3\r\nGET\r\n:not_a_num\r\n"u8;
    var badCursor = badResp.AsCursor();
    if (RespParser.ParseValue(ref badCursor).TryGetValue(out Failure fail))
    {
        Console.WriteLine("  Diagnostic report on malformed RESP input:");
        Console.WriteLine(fail.Error.FormatDiagnostics(badResp));
    }

    Console.WriteLine();
}

// 4. DNS Wire Packet Showcase (BitCursor + pointer compression + `union DnsRecordData`)
{
    Console.WriteLine("[4] DNS RFC 1035 Packet Parser (BitCursor + Label Compression)");
    byte[] dns =
    [
        0x42, 0x42, 0x81, 0x80, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00,
        6, (byte)'d', (byte)'o', (byte)'t', (byte)'n', (byte)'e', (byte)'t',
        3, (byte)'d', (byte)'e', (byte)'v', 0,
        0x00, 0x01, 0x00, 0x01,
        0xC0, 0x0C, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x78, 0x00, 0x04,
        20, 76, 201, 171,
    ];

    var c = dns.AsCursor();
    var pkt = DnsParser.ParsePacket(ref c).Unwrap();
    var ans = pkt.Answers[0];
    string rdata = ans.Data switch
    {
        ARecord a => a.ToIPAddress().ToString(),
        AaaaRecord => "IPv6",
        CNameRecord(var t) => t,
        TxtRecord(var raw) => raw.AsUtf8String(dns),
        UnknownRecord u => $"Type {u.RecordType}",
    };
    Console.WriteLine($"  Query: {pkt.Questions[0].Name} -> Answer: {ans.Name} IN A {rdata} (TTL={ans.Ttl}s)");
    Console.WriteLine();
}

// 5. Throughput & Zero-Allocation Micro-Benchmark
{
    Console.WriteLine("[5] Throughput & Allocation Micro-Benchmark (Release mode recommended)");
    byte[] wsWire = [0x81, 0x85, 0x37, 0xFA, 0x21, 0x3D, 0x7F, 0x9F, 0x4D, 0x51, 0x58];
    const int iterations = 2_000_000;

    // Warmup
    BenchmarkWebSocket(wsWire, 10_000);

    long allocBefore = GC.GetAllocatedBytesForCurrentThread();
    long startTs = Stopwatch.GetTimestamp();
    long checksum = BenchmarkWebSocket(wsWire, iterations);
    var elapsed = Stopwatch.GetElapsedTime(startTs);
    long allocAfter = GC.GetAllocatedBytesForCurrentThread();

    double framesPerSec = iterations / elapsed.TotalSeconds;
    double mbPerSec = (iterations * (double)wsWire.Length) / (1024.0 * 1024.0) / elapsed.TotalSeconds;
    Console.WriteLine($"  Parsed & unmasked {iterations:N0} WebSocket frames in {elapsed.TotalMilliseconds:F1} ms");
    Console.WriteLine($"  Throughput: {framesPerSec / 1_000_000.0:F2}M frames/sec ({mbPerSec:F1} MiB/s)");
    Console.WriteLine($"  Allocated:  {allocAfter - allocBefore} bytes (checksum={checksum})");
}

static long BenchmarkWebSocket(byte[] wire, int iterations)
{
    Span<byte> scratch = stackalloc byte[16];
    long acc = 0;

    for (int i = 0; i < iterations; i++)
    {
        var cursor = wire.AsCursor();
        if (WebSocketFrameParser.ParseFrame(ref cursor).TryGetValue(out Parsed<WebSocketFrame> ok)
            && ok.Value.TryGetValue(out TextFrame tf))
        {
            WebSocketFrameParser.UnmaskInto(wire, tf.Payload, tf.MaskKey, scratch);
            acc += scratch[0];
        }
    }

    return acc;
}
