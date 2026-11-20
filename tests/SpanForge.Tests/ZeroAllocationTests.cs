using System.Runtime.CompilerServices;
using SpanForge.Protocols.WebSocket;

namespace SpanForge.Tests;

internal static class ZeroAllocationTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("ZeroAlloc/Non-boxing ParseResult<T> + StructParsers (0 bytes)", TestStructParsersZeroAlloc);
        suite.Add("ZeroAlloc/FoldMany0 + RepeatInto stackalloc (0 bytes)", TestRepetitionZeroAlloc);
        suite.Add("ZeroAlloc/Full RFC 6455 WebSocketFrameParser + UnmaskInto (0 bytes)", TestWebSocketZeroAlloc);
    }

    private static void TestStructParsersZeroAlloc()
    {
        byte[] data = [(byte)'[', 0x12, 0x34, (byte)']', (byte)'X'];
        var parser = StructParsers.Delimited<
            StructParsers.TagByteParser,
            StructParsers.U16Parser,
            StructParsers.TagByteParser,
            byte, ushort, byte>(
            StructParsers.TagByte((byte)'['),
            StructParsers.U16,
            StructParsers.TagByte((byte)']'));

        // Warmup JIT
        _ = RunLoop(data, parser, 100);

        long before = GC.GetAllocatedBytesForCurrentThread();
        long sum = RunLoop(data, parser, 10_000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(10_000L * 0x1234, sum);
        Assert.Equal(0L, allocated);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static long RunLoop<TP>(byte[] buf, TP p, int iterations) where TP : struct, IParser<ushort>
        {
            long acc = 0;
            for (int i = 0; i < iterations; i++)
            {
                var c = buf.AsCursor();
                acc += p.Parse(ref c) switch
                {
                    Parsed<ushort>(var v) => v,
                    Incomplete => -1,
                    Backtrack => -2,
                    Failure => -3,
                };
            }

            return acc;
        }
    }

    private static void TestRepetitionZeroAlloc()
    {
        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8];

        _ = RunLoop(data, 100);

        long before = GC.GetAllocatedBytesForCurrentThread();
        long sum = RunLoop(data, 10_000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(10_000L * 72, sum);
        Assert.Equal(0L, allocated);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static long RunLoop(byte[] buf, int iterations)
        {
            Span<byte> scratch = stackalloc byte[8];
            long total = 0;
            for (int i = 0; i < iterations; i++)
            {
                var c1 = buf.AsCursor();
                total += Parse.FoldMany0(ref c1, static (ref ByteCursor x) => x.ReadU8(), 0, static (a, b) => a + b) switch
                {
                    Parsed<int>(var s) => s,
                    Incomplete => 0,
                    Backtrack => 0,
                    Failure => 0,
                };

                var c2 = buf.AsCursor();
                int n = Parse.RepeatInto(ref c2, scratch, static (ref ByteCursor x) => x.ReadU8()) switch
                {
                    Parsed<int>(var count) => count,
                    Incomplete => 0,
                    Backtrack => 0,
                    Failure => 0,
                };

                for (int k = 0; k < n; k++) total += scratch[k];
            }

            return total;
        }
    }

    private static void TestWebSocketZeroAlloc()
    {
        // RFC 6455 masked TextFrame "Hello"
        byte[] wire = [0x81, 0x85, 0x37, 0xFA, 0x21, 0x3D, 0x7F, 0x9F, 0x4D, 0x51, 0x58];

        _ = RunLoop(wire, 100);

        long before = GC.GetAllocatedBytesForCurrentThread();
        long checksum = RunLoop(wire, 10_000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(10_000L * ('H' + 'e' + 'l' + 'l' + 'o'), checksum);
        Assert.Equal(0L, allocated);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static long RunLoop(byte[] buf, int iterations)
        {
            Span<byte> unmasked = stackalloc byte[16];
            long acc = 0;

            for (int i = 0; i < iterations; i++)
            {
                var cursor = buf.AsCursor();
                var res = WebSocketFrameParser.ParseFrame(ref cursor);
                if (res.TryGetValue(out Parsed<WebSocketFrame> ok))
                {
                    // Exhaustive C# 15 switch over zero-boxing custom [Union] WebSocketFrame!
                    acc += ok.Value switch
                    {
                        TextFrame(_, var mask, var payload) => SumUnmasked(buf, payload, mask, unmasked),
                        BinaryFrame(_, var mask, var payload) => SumUnmasked(buf, payload, mask, unmasked),
                        PingFrame(var mask, var payload) => SumUnmasked(buf, payload, mask, unmasked),
                        PongFrame(var mask, var payload) => SumUnmasked(buf, payload, mask, unmasked),
                        ContinuationFrame(_, var mask, var payload) => SumUnmasked(buf, payload, mask, unmasked),
                        CloseFrame(var mask, _, var reason) => SumUnmasked(buf, reason, mask, unmasked),
                    };
                }
            }

            return acc;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static long SumUnmasked(ReadOnlySpan<byte> src, ByteSlice payload, Option<uint> mask, Span<byte> scratch)
        {
            WebSocketFrameParser.UnmaskInto(src, payload, mask, scratch);
            long s = 0;
            for (int i = 0; i < payload.Length; i++) s += scratch[i];
            return s;
        }
    }
}
