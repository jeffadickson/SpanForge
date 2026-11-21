using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using SpanForge.Pipelines;
using SpanForge.Protocols.Http2;
using SpanForge.Protocols.WebSocket;
using Xunit;

namespace SpanForge.Tests;

public sealed class PipelinesTests
{
    private sealed class ChunkedSegment : ReadOnlySequenceSegment<byte>
    {
        public ChunkedSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public ChunkedSegment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new ChunkedSegment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }

    private static ReadOnlySequence<byte> BuildMultiSegment(byte[] data, int chunkSize)
    {
        if (data.Length == 0) return ReadOnlySequence<byte>.Empty;
        var first = new ChunkedSegment(data.AsMemory(0, Math.Min(chunkSize, data.Length)));
        var last = first;
        for (int offset = chunkSize; offset < data.Length; offset += chunkSize)
        {
            int take = Math.Min(chunkSize, data.Length - offset);
            last = last.Append(data.AsMemory(offset, take));
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    [Fact]
    public void PipeParseOutcome_ExhaustiveUnion_AllVariants()
    {
        PipeParseOutcome<int> empty = default;
        Assert.False(empty.HasValue);
        Assert.True(empty.Value is null);

        PipeParseOutcome<int> parsed = new PipeParsed<int>(7);
        PipeParseOutcome<int> eof = new PipeEndOfStream();
        PipeParseOutcome<int> inc = new PipeIncompleteAtEof(Needed.Bytes(3));
        PipeParseOutcome<int> fail = new PipeFailed(new ParseError(2, ErrorKind.Protocol, "bad"));

        Assert.True(parsed.HasValue && parsed.IsParsed && parsed.Value is PipeParsed<int>(7));
        Assert.True(eof.HasValue && eof.IsEndOfStream && eof.Value is PipeEndOfStream);
        Assert.True(inc.HasValue && inc.IsIncompleteAtEof && inc.Value is PipeIncompleteAtEof);
        Assert.True(fail.HasValue && fail.IsFailed && fail.Value is PipeFailed);

        static string Describe(PipeParseOutcome<int> o) => o switch
        {
            PipeParsed<int>(var v) => $"ok:{v}",
            PipeEndOfStream => "eof",
            PipeIncompleteAtEof(var n) => $"inc:{n}",
            PipeFailed(var e) => $"fail:{e.Kind}",
        };

        Assert.Equal("ok:7", Describe(parsed));
        Assert.Equal("eof", Describe(eof));
        Assert.True(Describe(inc).StartsWith("inc:", StringComparison.Ordinal));
        Assert.Equal("fail:Protocol", Describe(fail));
    }

    [Fact]
    public void PipeSequenceAdapter_SingleAndMultiSegment_AllOverloads()
    {
        // Two length-prefixed U16 big-endian frames: "Hi" + "SpanForge"
        byte[] wire =
        [
            0x00, 0x02, (byte)'H', (byte)'i',
            0x00, 0x09, (byte)'S', (byte)'p', (byte)'a', (byte)'n', (byte)'F', (byte)'o', (byte)'r', (byte)'g', (byte)'e',
        ];

        // Single-segment projector path
        var single = new ReadOnlySequence<byte>(wire);
        Assert.True(PipeSequenceAdapter.TryParseSequence(
            in single,
            isCompleted: false,
            static (ref ByteCursor c) => c.ReadLengthPrefixedBytes(LengthPrefix.U16),
            static (slice, frame) => Encoding.UTF8.GetString(frame[slice]),
            out SequencePosition consumed1,
            out ParseResult<string> r1));
        Assert.Equal("Hi", r1.Unwrap());

        var restSingle = single.Slice(consumed1);
        Assert.True(PipeSequenceAdapter.TryParseSequence(
            in restSingle,
            isCompleted: true,
            static (ref ByteCursor c) => c.ReadLengthPrefixedBytes(LengthPrefix.U16),
            static (slice, frame) => Encoding.UTF8.GetString(frame[slice]),
            out SequencePosition consumed2,
            out ParseResult<string> r2));
        Assert.Equal("SpanForge", r2.Unwrap());
        Assert.True(restSingle.Slice(consumed2).IsEmpty);

        // Multi-segment (2 bytes per segment!) spanning boundaries
        var multi = BuildMultiSegment(wire, chunkSize: 2);
        Assert.False(multi.IsSingleSegment);
        Assert.True(PipeSequenceAdapter.TryParseSequence(
            in multi,
            isCompleted: false,
            static (ref ByteCursor c) => c.ReadLengthPrefixedBytes(LengthPrefix.U16),
            static (slice, frame) => Encoding.UTF8.GetString(frame[slice]),
            out SequencePosition mConsumed1,
            out ParseResult<string> mr1));
        Assert.Equal("Hi", mr1.Unwrap());

        var restMulti = multi.Slice(mConsumed1);
        Assert.True(PipeSequenceAdapter.TryParseSequence(
            in restMulti,
            isCompleted: true,
            static (ref ByteCursor c) => c.ReadLengthPrefixedBytes(LengthPrefix.U16),
            static (slice, frame) => Encoding.UTF8.GetString(frame[slice]),
            out SequencePosition mConsumed2,
            out ParseResult<string> mr2));
        Assert.Equal("SpanForge", mr2.Unwrap());
        Assert.True(restMulti.Slice(mConsumed2).IsEmpty);

        // Monomorphized ITryParser<T> overload over multi-segment sequence
        byte[] delimBytes = [(byte)'[', 0x12, 0x34, (byte)']'];
        var delimMulti = BuildMultiSegment(delimBytes, chunkSize: 1);
        var delimParser = TryParsers.Delimited<TryParsers.TagByteParser, TryParsers.U16BEParser, TryParsers.TagByteParser, byte, ushort, byte>(
            TryParsers.TagByte((byte)'['),
            TryParsers.U16BE,
            TryParsers.TagByte((byte)']'));
        Assert.True(PipeSequenceAdapter.TryParseSequence(
            in delimMulti,
            isCompleted: true,
            delimParser,
            out SequencePosition dConsumed,
            out FastStatus dStatus,
            out ushort dVal));
        Assert.True(dStatus.IsParsed);
        Assert.Equal((ushort)0x1234, dVal);
        Assert.True(delimMulti.Slice(dConsumed).IsEmpty);
    }

    [Fact]
    public async Task PipeParser_StreamsWebSocketAndHttp2Frames_ByteByByte_WithBackpressure()
    {
        // Build 3 masked WebSocket Text frames ("Hello", "Pipe", "World")
        byte[] wsStream =
        [
            .. BuildMaskedTextFrame("Hello"u8, 0x37FA213Du),
            .. BuildMaskedTextFrame("Pipe"u8, 0x11223344u),
            .. BuildMaskedTextFrame("World"u8, 0xAABBCCDDu),
        ];

        var pipe = new Pipe();
        Task writerTask = Task.Run(async () =>
        {
            // Trickle 2 bytes at a time to exercise PipeReader backpressure & Incomplete loops
            for (int i = 0; i < wsStream.Length; i += 2)
            {
                int count = Math.Min(2, wsStream.Length - i);
                wsStream.AsSpan(i, count).CopyTo(pipe.Writer.GetSpan(count));
                pipe.Writer.Advance(count);
                await pipe.Writer.FlushAsync();
            }
            await pipe.Writer.CompleteAsync();
        });

        List<string> messages = [];
        await foreach (var outcome in PipeParser.ReadAllAsync(
            pipe.Reader,
            static (ref ByteCursor c) => WebSocketFrameParser.ParseFrame(ref c),
            static (WebSocketFrame frame, ReadOnlySpan<byte> frameBytes) =>
            {
                if (frame.TryGetValue(out TextFrame tf))
                {
                    Span<byte> unmasked = stackalloc byte[tf.Payload.Length];
                    WebSocketFrameParser.UnmaskInto(frameBytes, tf.Payload, tf.MaskKey, unmasked);
                    return Encoding.UTF8.GetString(unmasked);
                }
                return string.Empty;
            }))
        {
            Assert.True(outcome.TryGetValue(out PipeParsed<string> parsed));
            messages.Add(parsed.Value);
        }

        await writerTask;
        await pipe.Reader.CompleteAsync();
        Assert.Equal(3, messages.Count);
        Assert.Equal("Hello", messages[0]);
        Assert.Equal("Pipe", messages[1]);
        Assert.Equal("World", messages[2]);

        // Also test HTTP/2 frames over PipeParser.ReadAllAsync
        byte[] h2Stream =
        [
            .. Http2FrameParser.BuildFrame(Http2FrameType.Settings, Http2FrameFlags.Ack, 0, []),
            .. Http2FrameParser.BuildFrame(Http2FrameType.Ping, Http2FrameFlags.None, 0, [1, 2, 3, 4, 5, 6, 7, 8]),
            .. Http2FrameParser.BuildFrame(Http2FrameType.WindowUpdate, Http2FrameFlags.None, 1, [0, 0, 0x04, 0x00]),
        ];

        var h2Pipe = new Pipe();
        h2Stream.CopyTo(h2Pipe.Writer.GetSpan(h2Stream.Length));
        h2Pipe.Writer.Advance(h2Stream.Length);
        await h2Pipe.Writer.FlushAsync();
        await h2Pipe.Writer.CompleteAsync();

        List<string> h2Kinds = [];
        await foreach (var outcome in PipeParser.ReadAllAsync(
            h2Pipe.Reader,
            static (ref ByteCursor c) => Http2FrameParser.ParseFrame(ref c)))
        {
            Assert.True(outcome.TryGetValue(out PipeParsed<Http2Frame> ok));
            h2Kinds.Add(ok.Value switch
            {
                Http2SettingsFrame => "SETTINGS",
                Http2PingFrame(_, var opaque) => $"PING:{opaque:X16}",
                Http2WindowUpdateFrame(var sid, var inc) => $"WU:{sid}:{inc}",
                _ => "OTHER",
            });
        }
        await h2Pipe.Reader.CompleteAsync();
        Assert.Equal(3, h2Kinds.Count);
        Assert.Equal("SETTINGS", h2Kinds[0]);
        Assert.Equal("PING:0102030405060708", h2Kinds[1]);
        Assert.Equal("WU:1:1024", h2Kinds[2]);
    }

    [Fact]
    public async Task PipeParser_Handles_EndOfStream_IncompleteAtEof_Malformed_MaxBufferSize_And_TryParser()
    {
        // 1. Empty completed pipe -> PipeEndOfStream
        var emptyPipe = new Pipe();
        await emptyPipe.Writer.CompleteAsync();
        var oEmpty = await PipeParser.ReadOneAsync(emptyPipe.Reader, static (ref ByteCursor c) => c.ReadU32BE());
        Assert.True(oEmpty.IsEndOfStream);
        await emptyPipe.Reader.CompleteAsync();

        // 2. Truncated frame at completed EOF -> PipeFailed (Complete mode on final buffer) or PipeIncompleteAtEof
        var truncPipe = new Pipe();
        byte[] twoBytes = [0x01, 0x02];
        twoBytes.CopyTo(truncPipe.Writer.GetSpan(2));
        truncPipe.Writer.Advance(2);
        await truncPipe.Writer.FlushAsync();
        await truncPipe.Writer.CompleteAsync();
        var oTrunc = await PipeParser.ReadOneAsync(truncPipe.Reader, static (ref ByteCursor c) => c.ReadU32BE());
        Assert.True(oTrunc.IsFailed || oTrunc.IsIncompleteAtEof);
        await truncPipe.Reader.CompleteAsync();

        // 3. DoS maxBufferSize guard (unfinished 100-byte frame when maxBufferSize = 16)
        var dosPipe = new Pipe();
        byte[] hugeHeader = new byte[20];
        hugeHeader[0] = 0x00;
        hugeHeader[1] = 100; // promises 100 bytes, only 18 follow
        hugeHeader.CopyTo(dosPipe.Writer.GetSpan(hugeHeader.Length));
        dosPipe.Writer.Advance(hugeHeader.Length);
        await dosPipe.Writer.FlushAsync(); // NOT completed!
        var oDos = await PipeParser.ReadOneAsync(
            dosPipe.Reader,
            static (ref ByteCursor c) => c.ReadLengthPrefixedBytes(LengthPrefix.U16),
            maxBufferSize: 16);
        Assert.True(oDos.TryGetValue(out PipeFailed dosFail) && dosFail.Error.Kind == ErrorKind.LengthValue);
        await dosPipe.Writer.CompleteAsync();
        await dosPipe.Reader.CompleteAsync();

        // 4. Monomorphized ITryParser<T> async ReadOneAsync with generated [BinaryStruct] TryParser
        var tryPipe = new Pipe();
        byte[] delimited = [(byte)'[', 0xAB, 0xCD, (byte)']'];
        delimited.CopyTo(tryPipe.Writer.GetSpan(delimited.Length));
        tryPipe.Writer.Advance(delimited.Length);
        await tryPipe.Writer.FlushAsync();
        await tryPipe.Writer.CompleteAsync();

        var parser = TryParsers.Delimited<TryParsers.TagByteParser, TryParsers.U16BEParser, TryParsers.TagByteParser, byte, ushort, byte>(
            TryParsers.TagByte((byte)'['),
            TryParsers.U16BE,
            TryParsers.TagByte((byte)']'));

        var oTry = await PipeParser.ReadOneAsync<
            TryParsers.DelimitedParser<TryParsers.TagByteParser, TryParsers.U16BEParser, TryParsers.TagByteParser, byte, ushort, byte>,
            ushort>(tryPipe.Reader, parser);
        Assert.True(oTry.TryGetValue(out PipeParsed<ushort> okTry) && okTry.Value == 0xABCD);
        var oTryEof = await PipeParser.ReadOneAsync<
            TryParsers.DelimitedParser<TryParsers.TagByteParser, TryParsers.U16BEParser, TryParsers.TagByteParser, byte, ushort, byte>,
            ushort>(tryPipe.Reader, parser);
        Assert.True(oTryEof.IsEndOfStream);
        await tryPipe.Reader.CompleteAsync();
    }

    private static byte[] BuildMaskedTextFrame(ReadOnlySpan<byte> payload, uint maskKey)
    {
        byte[] mask = [(byte)(maskKey >> 24), (byte)(maskKey >> 16), (byte)(maskKey >> 8), (byte)maskKey];
        byte[] frame = new byte[6 + payload.Length];
        frame[0] = 0x81;
        frame[1] = (byte)(0x80 | payload.Length);
        mask.CopyTo(frame.AsSpan(2, 4));
        for (int i = 0; i < payload.Length; i++) frame[6 + i] = (byte)(payload[i] ^ mask[i & 3]);
        return frame;
    }
}
